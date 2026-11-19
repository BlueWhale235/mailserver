using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

/// <summary>Immutable hash-addressed objects; bounded disk staging keeps MIME streams seekable.</summary>
public sealed class S3MessageContentStore : IMessageContentStore, IMessageStorageMaintenance, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly IDbContextFactory<MailDbContext> _factory;
    private readonly StorageOptions _storage;
    private readonly S3StorageOptions _options;
    private readonly string _root, _prefix;
    private readonly SemaphoreSlim _transfers;
    private readonly SemaphoreSlim _readers;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly AsyncKeyedLock<string> _loads = new();
    private readonly FileStream _directoryLease;

    public S3MessageContentStore(IAmazonS3 client, IDbContextFactory<MailDbContext> factory, IOptions<StorageOptions> storage)
    {
        _client = client; _factory = factory; _storage = storage.Value; _options = _storage.S3;
        if (string.IsNullOrWhiteSpace(_options.Bucket) || _options.CacheBytes < 0 || _storage.MaximumMessageBytes <= 0
            || _options.MaximumConcurrentTransfers is < 1 or > 32 || _options.MaximumOpenReaders is < 1 or > 1024)
            throw new InvalidOperationException("Invalid S3 bucket, cache or transfer limits.");
        _prefix = _options.Prefix.Trim('/');
        if (_prefix.Length > 0) _prefix += "/";
        _root = Path.GetFullPath(_options.CachePath);
        Directory.CreateDirectory(_root);
        // A cache directory belongs to exactly one process; use separate paths for colocated roles.
        _directoryLease = new FileStream(Path.Combine(_root, ".owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _transfers = new(_options.MaximumConcurrentTransfers);
        _readers = new(_options.MaximumOpenReaders);
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*.partial")) File.Delete(file);
            TrimCache(0);
        }
        catch { _directoryLease.Dispose(); _transfers.Dispose(); _readers.Dispose(); throw; }
    }

    private string ObjectKey(string key)
    {
        FileMessageContentStore.ValidateKey(key);
        return _prefix + key[..2] + "/" + key[2..4] + "/" + key + ".eml";
    }

    public async Task<ContentDescriptor> SaveAsync(Stream content, CancellationToken token)
    {
        await _transfers.WaitAsync(token);
        var path = Path.Combine(_root, Guid.NewGuid() + ".partial");
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var (size, hash) = await CopyBoundedAsync(content, file, token);
            file.Position = 0;
            await using var mutation = await ContentMutationLease.AcquireAsync(_factory, hash, token);
            // Overwrite identical bytes to refresh LastModified, preserving the orphan grace period.
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _options.Bucket, Key = ObjectKey(hash), InputStream = file,
                AutoCloseStream = false, UseChunkEncoding = false, ContentType = "message/rfc822",
                ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(hash))
            }, token);
            return new(hash, size); // No successful acceptance before the remote PUT completes.
        }
        finally { try { File.Delete(path); } finally { _transfers.Release(); } }
    }

    private async Task<(long Size, string Hash)> CopyBoundedAsync(Stream input, Stream output, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        long size = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, 65536), token)) > 0)
            {
                size += read;
                if (size > _storage.MaximumMessageBytes) throw new InvalidDataException("Message exceeds the configured storage limit.");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), token);
            }
            await output.FlushAsync(token);
            return (size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken token)
    {
        await _readers.WaitAsync(token);
        try { return new ReaderLease(await OpenCoreAsync(key, token), _readers); }
        catch { _readers.Release(); throw; }
    }

    private async Task<Stream> OpenCoreAsync(string key, CancellationToken token)
    {
        var objectKey = ObjectKey(key);
        using var load = await _loads.AcquireAsync(key, token);
        // A local hit must not resurrect content removed from the authoritative store.
        var metadata = await _client.GetObjectMetadataAsync(_options.Bucket, objectKey, token);
        if (metadata.ContentLength > _storage.MaximumMessageBytes)
            throw new InvalidDataException("Message exceeds the configured storage limit.");
        var cached = Path.Combine(_root, key + ".eml");
        await _cacheGate.WaitAsync(token);
        try
        {
            if (File.Exists(cached) && new FileInfo(cached).Length == metadata.ContentLength)
            {
                File.SetLastWriteTimeUtc(cached, DateTime.UtcNow);
                return Open(cached);
            }
        }
        finally { _cacheGate.Release(); }
        await _transfers.WaitAsync(token);
        var partial = Path.Combine(_root, Guid.NewGuid() + ".partial");
        try
        {
            using var response = await _client.GetObjectAsync(_options.Bucket, objectKey, token);
            long size;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var result = await CopyBoundedAsync(response.ResponseStream, output, token);
                size = result.Size;
                if (!string.Equals(result.Hash, key, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Object content hash mismatch.");
            }
            await _cacheGate.WaitAsync(token);
            try
            {
                if (size <= _options.CacheBytes)
                {
                    TrimCache(size);
                    File.Move(partial, cached, overwrite: true);
                    return Open(cached);
                }
                // Large objects bypass retention; their temporary file is deleted with the stream.
                return new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            }
            finally { _cacheGate.Release(); }
        }
        catch { File.Delete(partial); throw; }
        finally { _transfers.Release(); }
    }

    private static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);

    private void TrimCache(long incoming)
    {
        var files = new DirectoryInfo(_root).EnumerateFiles("*.eml").OrderBy(x => x.LastWriteTimeUtc).ToArray();
        var bytes = files.Sum(x => x.Length);
        var count = files.Length;
        foreach (var file in files)
        {
            if (bytes + incoming <= _options.CacheBytes && count < 4096) break;
            var length = file.Length;
            file.Delete(); bytes -= length; count--;
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken token)
    {
        try { await _client.GetObjectMetadataAsync(_options.Bucket, ObjectKey(key), token); return true; }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { return false; }
    }

    public async Task<ContentStoragePage> ListAsync(string? continuationToken, int pageSize, CancellationToken token)
    {
        var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _options.Bucket, Prefix = _prefix, ContinuationToken = continuationToken,
            MaxKeys = Math.Clamp(pageSize, 1, 1000)
        }, token);
        var items = new List<ContentStorageObject>();
        foreach (var item in response.S3Objects ?? [])
        {
            var key = Path.GetFileNameWithoutExtension(item.Key);
            if (FileMessageContentStore.IsValidKey(key) && item.Key == ObjectKey(key))
                items.Add(new(key, item.Size ?? 0, item.LastModified ?? DateTime.UtcNow));
        }
        return new(items, response.IsTruncated == true ? response.NextContinuationToken : null);
    }

    public async Task DeleteIfUnreferencedAsync(string key, CancellationToken token) =>
        await TryDeleteUnreferencedAsync(key, DateTimeOffset.UtcNow.AddMinutes(-5), token);

    public async Task<bool> TryDeleteUnreferencedAsync(string key, DateTimeOffset olderThan, CancellationToken token)
    {
        if (!FileMessageContentStore.IsValidKey(key)) return false;
        var objectKey = ObjectKey(key);
        await using var mutation = await ContentMutationLease.AcquireAsync(_factory, key, token);
        GetObjectMetadataResponse metadata;
        try { metadata = await _client.GetObjectMetadataAsync(_options.Bucket, objectKey, token); }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { return false; }
        var cutoff = olderThan < DateTimeOffset.UtcNow.AddMinutes(-5) ? olderThan : DateTimeOffset.UtcNow.AddMinutes(-5);
        if (metadata.LastModified is null || metadata.LastModified > cutoff.UtcDateTime) return false;
        await using var db = await _factory.CreateDbContextAsync(token);
        if (await db.Messages.AnyAsync(x => x.ContentKey == key, token)) return false;
        await _client.DeleteObjectAsync(_options.Bucket, objectKey, token);
        await _cacheGate.WaitAsync(token);
        try { File.Delete(Path.Combine(_root, key + ".eml")); }
        finally { _cacheGate.Release(); }
        return true;
    }

    public async Task CheckHealthAsync(CancellationToken token)
    {
        var key = _prefix + ".health/" + Guid.NewGuid();
        try
        {
            await _client.PutObjectAsync(new PutObjectRequest { BucketName = _options.Bucket, Key = key, ContentBody = "ready", UseChunkEncoding = false }, token);
            using var response = await _client.GetObjectAsync(_options.Bucket, key, token);
            using var reader = new StreamReader(response.ResponseStream);
            if (await reader.ReadToEndAsync(token) != "ready") throw new IOException("Object storage probe failed.");
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _client.DeleteObjectAsync(_options.Bucket, key, timeout.Token);
        }
    }

    // The reader permit survives download completion, bounding evicted-but-open files too.
    private sealed class ReaderLease(Stream inner, SemaphoreSlim permits) : Stream
    {
        private int _disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => inner.ReadAsync(buffer, offset, count, token);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() => inner.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                try { inner.Dispose(); } finally { permits.Release(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                try { await inner.DisposeAsync(); } finally { permits.Release(); }
            GC.SuppressFinalize(this);
        }
    }

    public void Dispose() { _directoryLease.Dispose(); _transfers.Dispose(); _cacheGate.Dispose(); }
}
