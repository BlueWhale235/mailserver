using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailServer.Tests;

public sealed class S3IntegrationFactAttribute : FactAttribute
{
    public S3IntegrationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MAILSERVER_TEST_S3_URL")))
            Skip = "Requires isolated MySQL and S3 services; run scripts/test-distributed-storage.sh.";
    }
}

public sealed class S3IntegrationTests
{
    [S3IntegrationFact]
    public async Task IndependentNodesPublishAndReadThroughRealS3AndMySql()
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION")
            ?? throw new InvalidOperationException("Missing test database connection.");
        if (!connection.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Requires isolated mailserver_acceptance database.");
        using var client = new AmazonS3Client(new BasicAWSCredentials("mailserver-test", "mailserver-test-secret"), new AmazonS3Config
        {
            ServiceURL = Environment.GetEnvironmentVariable("MAILSERVER_TEST_S3_URL"),
            AuthenticationRegion = "us-east-1", ForcePathStyle = true
        });
        const string bucket = "mailserver-acceptance";
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        var factory = new TestMailDbContextFactory(new DbContextOptionsBuilder<MailDbContext>()
            .UseMySQL(connection, options => options.MigrationsAssembly("MailServer.Migrations.MySql")).Options);
        await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync();
        var prefix = "test-" + Guid.NewGuid().ToString("N") + "/";
        var root = Path.Combine(Path.GetTempPath(), "mail-s3-" + Guid.NewGuid());
        S3MessageContentStore Store(string role) => new(client, factory, Options.Create(new StorageOptions
        {
            Provider = "S3", S3 = new S3StorageOptions { Bucket = bucket, Prefix = prefix,
                CachePath = Path.Combine(root, role), CacheBytes = 4096, MaximumConcurrentTransfers = 2 }
        }));
        try
        {
            using var smtp = Store("smtp"); using var imap = Store("imap"); using var api = Store("api");
            var bytes = Encoding.UTF8.GetBytes("Subject: distributed acceptance\r\n\r\n" + Guid.NewGuid());
            var saves = await Task.WhenAll(smtp.SaveAsync(new MemoryStream(bytes), default), imap.SaveAsync(new MemoryStream(bytes), default));
            Assert.Equal(saves[0], saves[1]);
            foreach (var reader in new[] { imap, api })
            {
                await using var stream = await reader.OpenReadAsync(saves[0].Key, default);
                Assert.True(stream.CanSeek);
                using var copy = new MemoryStream(); await stream.CopyToAsync(copy);
                Assert.Equal(bytes, copy.ToArray());
                await reader.CheckHealthAsync(default);
            }
            var page = await smtp.ListAsync(null, 10, default);
            Assert.Equal(saves[0].Key, Assert.Single(page.Items).Key);
            Assert.False(await smtp.TryDeleteUnreferencedAsync(saves[0].Key, DateTimeOffset.UtcNow, default));
            var key = saves[0].Key;
            await client.DeleteObjectAsync(bucket, prefix + key[..2] + "/" + key[2..4] + "/" + key + ".eml");
            Assert.False(await api.ExistsAsync(key, default));
            await Assert.ThrowsAsync<AmazonS3Exception>(() => imap.OpenReadAsync(key, default));
        }
        finally
        {
            var remaining = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = prefix });
            foreach (var item in remaining.S3Objects ?? []) await client.DeleteObjectAsync(bucket, item.Key);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
