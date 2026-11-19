using System.Security.Cryptography;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed class FileMessageContentStore(
    IOptions<StorageOptions> options,
    IDbContextFactory<MailDbContext> dbFactory) : IMessageContentStore, IMessageStorageMaintenance
{
    private readonly StorageOptions _options = options.Value;

    public async Task<ContentDescriptor> SaveAsync(Stream content, CancellationToken cancellationToken)
    {
        using var performance = PerformanceTelemetry.Stage("storage.write");
        var root = CheckRoot();
        if (_options.PreparedInboundEnabled && content is PreparedMessageContent prepared)
        {
            var descriptor = prepared.Descriptor;
            ValidateKey(descriptor.Key);
            if (descriptor.Size < 0 || descriptor.Size > _options.MaximumMessageBytes)
                throw new InvalidDataException("Prepared message exceeds the configured size limit.");
            await using var mutation = await ContentMutationLease.AcquireAsync(dbFactory, descriptor.Key, cancellationToken, allowLocal: true);
            var finalDirectory = Path.Combine(root, descriptor.Key[..2], descriptor.Key[2..4]);
            Directory.CreateDirectory(finalDirectory);
            var destination = Path.Combine(finalDirectory, descriptor.Key + ".eml");
            if (File.Exists(destination))
            {
                File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
                return descriptor;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (prepared.TryTransferTo(destination))
            {
                File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
                return descriptor;
            }
            // Cross-device moves and destination races retain the existing verified copy path.
            prepared.Position = 0;
        }
        var temporaryDirectory = Path.Combine(root, ".tmp");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = Path.Combine(temporaryDirectory, $"{System.Guid.NewGuid()}.eml");

        long size = 0;
        byte[] hash;
        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > _options.MaximumMessageBytes)
                    {
                        throw new InvalidDataException($"Message exceeds the configured {_options.MaximumMessageBytes} byte limit.");
                    }

                    sha256.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
                hash = sha256.GetHashAndReset();
            }

            var key = Convert.ToHexStringLower(hash);
            await using var mutation = await ContentMutationLease.AcquireAsync(dbFactory, key, cancellationToken, allowLocal: true);
            var finalDirectory = Path.Combine(root, key[..2], key[2..4]);
            Directory.CreateDirectory(finalDirectory);
            var finalPath = Path.Combine(finalDirectory, $"{key}.eml");

            if (!File.Exists(finalPath))
            {
                try { File.Move(temporaryPath, finalPath); }
                catch (IOException) when (File.Exists(finalPath)) { File.Delete(temporaryPath); }
            }
            else
            {
                File.Delete(temporaryPath);
            }

            File.SetLastWriteTimeUtc(finalPath, DateTime.UtcNow);
            return new ContentDescriptor(key, size);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        var path = GetPath(key);
        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public async Task DeleteIfUnreferencedAsync(string key, CancellationToken cancellationToken)
        => await TryDeleteUnreferencedAsync(key, DateTimeOffset.UtcNow.AddMinutes(-5), cancellationToken);

    public async Task<bool> TryDeleteUnreferencedAsync(string key, DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        if (!IsValidKey(key)) return false;
        await using var mutation = await ContentMutationLease.AcquireAsync(dbFactory, key, cancellationToken, allowLocal: true);
        var path = GetPath(key);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Messages.AnyAsync(x => x.ContentKey == key, cancellationToken))
        {
            return false;
        }

        // Publication precedes the referencing transaction. Give in-flight writers a grace
        // period; abandoned new content is reclaimed by the orphan scan.
        var cutoff = olderThan < DateTimeOffset.UtcNow.AddMinutes(-5) ? olderThan : DateTimeOffset.UtcNow.AddMinutes(-5);
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) <= cutoff.UtcDateTime)
        {
            File.Delete(path);
            return true;
        }
        return false;
    }

    private string CheckRoot()
    {
        var root = Path.GetFullPath(_options.RootPath);
        if (_options.RequireExistingRoot && !Directory.Exists(root)) throw new IOException("Shared message storage is not mounted.");
        if (_options.RequiredMarkerFile is { Length: > 0 } marker)
        {
            if (Path.GetFileName(marker) != marker || marker is "." or "..") throw new InvalidOperationException("Storage marker must be a file name.");
            if (!File.Exists(Path.Combine(root, marker))) throw new IOException("Shared message storage marker is missing.");
        }
        return root;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetPath(key)));
    }

    public Task<ContentStoragePage> ListAsync(string? continuationToken, int pageSize, CancellationToken token)
    {
        var root = CheckRoot();
        if (!Directory.Exists(root)) return Task.FromResult(new ContentStoragePage([], null));
        var take = Math.Clamp(pageSize, 1, 1000);
        // Retain at most one page plus a look-ahead key, even for a very large store.
        var candidates = new SortedSet<(string Key, string Path)>(Comparer<(string Key, string Path)>.Create(
            (left, right) => string.CompareOrdinal(left.Key, right.Key)));
        foreach (var path in Directory.EnumerateFiles(root, "*.eml", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var key = Path.GetFileNameWithoutExtension(path);
            if (!IsValidKey(key) || string.CompareOrdinal(key, continuationToken ?? "") <= 0
                || path != Path.Combine(root, key[..2], key[2..4], key + ".eml")) continue;
            if (candidates.Count == take + 1 && string.CompareOrdinal(key, candidates.Max.Key) >= 0) continue;
            candidates.Add((key, path));
            if (candidates.Count > take + 1) candidates.Remove(candidates.Max);
        }
        var items = candidates.ToArray();
        var rows = new List<ContentStorageObject>();
        foreach (var item in items.Take(take))
        {
            var info = new FileInfo(item.Path);
            if (info.Exists) rows.Add(new(item.Key, info.Length, info.LastWriteTimeUtc));
        }
        return Task.FromResult(new ContentStoragePage(rows, items.Length > take ? items[take - 1].Key : null));
    }

    public async Task CheckHealthAsync(CancellationToken token)
    {
        var root = CheckRoot();
        Directory.CreateDirectory(root);
        await using var file = new FileStream(Path.Combine(root, ".ready-" + Guid.NewGuid()), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        await file.WriteAsync(new byte[] { 1 }, token);
        await file.FlushAsync(token);
        file.Position = 0;
        if (file.ReadByte() != 1) throw new IOException("Storage probe failed.");
    }

    private string GetPath(string key)
    {
        ValidateKey(key);
        return Path.Combine(CheckRoot(), key[..2], key[2..4], $"{key}.eml");
    }

    internal static bool IsValidKey(string key) => key.Length == 64 && key.All(Uri.IsHexDigit);
    internal static void ValidateKey(string key)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException("Invalid content key.", nameof(key));
        }
    }
}
