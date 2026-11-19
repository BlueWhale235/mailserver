using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailServer.Tests;

public sealed class DistributedStorageTests
{
    private static TestMailDbContextFactory Factory() => new(new DbContextOptionsBuilder<MailDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task SharedRootRequiresMountMarkerAndSupportsIndependentReaders()
    {
        using var temp = new Temp();
        var options = Options.Create(new StorageOptions { RootPath = temp.Path, RequireExistingRoot = true, RequiredMarkerFile = ".mounted" });
        var store = new FileMessageContentStore(options, Factory());
        await Assert.ThrowsAsync<IOException>(() => store.CheckHealthAsync(default));
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, ".mounted"), "mailstore");
        await store.CheckHealthAsync(default);
        var descriptor = await store.SaveAsync(new MemoryStream("hello"u8.ToArray()), default);
        var second = new FileMessageContentStore(options, Factory());
        await using var read = await second.OpenReadAsync(descriptor.Key, default);
        Assert.Equal("hello", await new StreamReader(read).ReadToEndAsync());
        Assert.False(await store.TryDeleteUnreferencedAsync(descriptor.Key, DateTimeOffset.UtcNow, default));
        Assert.False(await store.TryDeleteUnreferencedAsync("legacy-invalid-key", DateTimeOffset.UtcNow, default));
        File.Delete(System.IO.Path.Combine(temp.Path, ".mounted"));
        await Assert.ThrowsAsync<IOException>(() => second.ExistsAsync(descriptor.Key, default));
    }

    [Fact]
    public async Task FilePaginationExcludesStagingAndDeletionPreservesReferences()
    {
        using var temp = new Temp();
        var factory = Factory();
        var store = new FileMessageContentStore(Options.Create(new StorageOptions { RootPath = temp.Path }), factory);
        var keys = new List<string>();
        for (var i = 0; i < 3; i++) keys.Add((await store.SaveAsync(new MemoryStream(Encoding.UTF8.GetBytes("body" + i)), default)).Key);
        Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, ".tmp"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, ".tmp", keys[0] + ".eml"), "incomplete");
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await store.ListAsync(cursor, 1, default);
            seen.AddRange(page.Items.Select(x => x.Key)); cursor = page.ContinuationToken;
        } while (cursor is not null);
        Assert.Equal(keys.Order(), seen.Order());
        await using (var db = factory.CreateDbContext())
        {
            db.Messages.Add(new StoredMessage { ContentKey = keys[0], EnvelopeFrom = "sender@example.test" }); await db.SaveChangesAsync();
        }
        foreach (var key in keys) File.SetLastWriteTimeUtc(System.IO.Path.Combine(temp.Path, key[..2], key[2..4], key + ".eml"), DateTime.UtcNow.AddHours(-2));
        Assert.False(await store.TryDeleteUnreferencedAsync(keys[0], DateTimeOffset.UtcNow, default));
        Assert.True(await store.TryDeleteUnreferencedAsync(keys[1], DateTimeOffset.UtcNow, default));
        Assert.True(await store.ExistsAsync(keys[0], default));
        Assert.False(await store.ExistsAsync(keys[1], default));
    }

    [Fact]
    public async Task S3CacheIsSeekableBoundedAndNeverResurrectsDeletedObjects()
    {
        using var temp = new Temp();
        using var client = new FakeS3();
        var key = client.Seed("hello");
        using var store = CreateS3(client, temp.Path, cacheBytes: 5);
        await using (var first = await store.OpenReadAsync(key, default)) { Assert.True(first.CanSeek); Assert.Equal(5, first.Length); }
        await using (var second = await store.OpenReadAsync(key, default)) { second.Position = 1; Assert.Equal((int)'e', second.ReadByte()); }
        Assert.Equal(1, client.Downloads);
        client.Objects.Clear();
        await Assert.ThrowsAsync<AmazonS3Exception>(() => store.OpenReadAsync(key, default));
        Assert.False(await store.ExistsAsync(key, default));
        client.FailRequests = true;
        await Assert.ThrowsAsync<AmazonS3Exception>(() => store.ExistsAsync(key, default));
    }

    [Fact]
    public async Task S3ActiveReadPermitLivesUntilStreamDisposalAndBypassFilesDisappear()
    {
        using var temp = new Temp();
        using var client = new FakeS3();
        var key = client.Seed("larger than cache");
        using var store = CreateS3(client, temp.Path, cacheBytes: 0);
        var first = await store.OpenReadAsync(key, default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenReadAsync(key, timeout.Token));
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial"));
        await using (var next = await store.OpenReadAsync(key, default)) Assert.Equal(17, next.Length);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.eml"));
    }

    [Fact]
    public async Task S3RejectsCorruptionAndOversizeWithoutLeavingStagingFiles()
    {
        using var temp = new Temp();
        using var client = new FakeS3();
        var key = client.Seed("original");
        client.Objects[FakeS3.Path(key)] = "modified"u8.ToArray();
        using var store = CreateS3(client, temp.Path, maximumBytes: 10);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.OpenReadAsync(key, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new MemoryStream(new byte[11]), default));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial"));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.eml"));
        Assert.Throws<IOException>(() => CreateS3(client, temp.Path));
    }

    [Fact]
    public async Task S3MaintenanceFiltersForeignKeysAndHealthProbeCleansUp()
    {
        using var temp = new Temp();
        using var client = new FakeS3();
        var key = client.Seed("hello");
        client.Objects["mailserver/.health/abandoned"] = [];
        using var store = CreateS3(client, temp.Path);
        var page = await store.ListAsync(null, 3, default);
        Assert.Equal(key, Assert.Single(page.Items).Key);
        await store.CheckHealthAsync(default);
        Assert.Equal(2, client.Objects.Count);
    }

    [Theory]
    [InlineData("smtp")]
    [InlineData("imap")]
    [InlineData("api")]
    public void NodeBudgetIsIndependentOfRoleAndOverridesGlobalProfile(string role)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Node:Enabled"] = "true", ["Node:Id"] = "node-a", ["Node:CacheBytes"] = "10485760", ["Node:DeliveryConcurrency"] = "3" }).Build();
        var runtime = new ResourceRuntime(role, config);
        using var cache = new ConfigurationSnapshotCache(Options.Create(new ConfigurationCacheOptions()), NullLogger<ConfigurationSnapshotCache>.Instance);
        runtime.Apply("1gb", cache);
        Assert.Equal(10485760, runtime.Snapshot().CacheBudgetBytes);
        Assert.Equal(3, runtime.Snapshot().DeliveryLimit);
        Assert.Throws<InvalidOperationException>(() => SnowflakeProcess.Acquire(config, role));
    }

    [Fact]
    public async Task NodeStatusSeparatesLiveStaleAndInvalidHeartbeats()
    {
        var factory = Factory();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Node:Enabled"] = "true" }).Build();
        var runtime = new ResourceRuntime("api", config);
        using var cache = new ConfigurationSnapshotCache(Options.Create(new ConfigurationCacheOptions()), NullLogger<ConfigurationSnapshotCache>.Instance);
        using var fetch = new MessageFetchMetadataCache(factory, runtime);
        using var diagnostics = new PerformanceCollector(runtime);
        using var service = new NodeStatusService(factory, runtime, cache, fetch, diagnostics, config, NullLogger<NodeStatusService>.Instance);
        var json = JsonSerializer.Serialize(new NodeRuntimeSnapshot(runtime.Snapshot(), 123, 456, 1, "S3", "node"));
        await using (var db = factory.CreateDbContext())
        {
            db.ConfigurationProcessStatuses.AddRange(
                new ConfigurationProcessStatus { ProcessId = "live", InstanceName = "smtp-a", Roles = "smtp", LastSeenAt = DateTimeOffset.UtcNow, ResourceStatusJson = json },
                new ConfigurationProcessStatus { ProcessId = "old", InstanceName = "imap-a", Roles = "imap", LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-2), ResourceStatusJson = json },
                new ConfigurationProcessStatus { ProcessId = "invalid", InstanceName = "worker-a", Roles = "worker", LastSeenAt = DateTimeOffset.UtcNow, ResourceStatusJson = "{" });
            await db.SaveChangesAsync();
        }
        var nodes = await service.ListAsync(default);
        Assert.Equal("online", nodes.Single(x => x.InstanceId == "live").State);
        Assert.Equal("offline", nodes.Single(x => x.InstanceId == "old").State);
        Assert.Equal("unknown", nodes.Single(x => x.InstanceId == "invalid").State);
    }

    private static S3MessageContentStore CreateS3(IAmazonS3 client, string path, long cacheBytes = 32, long maximumBytes = 1024) => new(client, Factory(),
        Options.Create(new StorageOptions { Provider = "S3", MaximumMessageBytes = maximumBytes,
            S3 = new S3StorageOptions { Bucket = "test", CachePath = path, CacheBytes = cacheBytes, MaximumOpenReaders = 1 } }));

    private sealed class Temp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mail-distributed-" + Guid.NewGuid());
        public Temp() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class FakeS3() : AmazonS3Client("test", "test", RegionEndpoint.USEast1)
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public int Downloads { get; private set; }
        public bool FailRequests { get; set; }
        public static string Path(string key) => $"mailserver/{key[..2]}/{key[2..4]}/{key}.eml";
        public string Seed(string body) { var bytes = Encoding.UTF8.GetBytes(body); var key = Convert.ToHexStringLower(SHA256.HashData(bytes)); Objects[Path(key)] = bytes; return key; }
        private byte[] Read(string key)
        {
            if (FailRequests) throw new AmazonS3Exception("Unavailable") { StatusCode = HttpStatusCode.ServiceUnavailable };
            return Objects.TryGetValue(key, out var value) ? value : throw new AmazonS3Exception("Missing") { StatusCode = HttpStatusCode.NotFound };
        }
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(string bucket, string key, CancellationToken token = default)
        { var data = Read(key); return Task.FromResult(new GetObjectMetadataResponse { ContentLength = data.Length, LastModified = DateTime.UtcNow }); }
        public override Task<GetObjectResponse> GetObjectAsync(string bucket, string key, CancellationToken token = default)
        { Downloads++; return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(Read(key)) }); }
        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken token = default)
        { Objects[request.Key] = Encoding.UTF8.GetBytes(request.ContentBody); return Task.FromResult(new PutObjectResponse()); }
        public override Task<DeleteObjectResponse> DeleteObjectAsync(string bucket, string key, CancellationToken token = default)
        { Objects.Remove(key); return Task.FromResult(new DeleteObjectResponse()); }
        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken token = default) =>
            Task.FromResult(new ListObjectsV2Response { S3Objects = Objects.Select(x => new S3Object { Key = x.Key, Size = x.Value.Length, LastModified = DateTime.UtcNow }).ToList() });
    }
}
