using MailServer.Infrastructure;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailServer.Tests;

public sealed class ConfigurationCacheTests
{
    [Fact]
    public async Task ConcurrentMissesShareOneLoadAndThenHit()
    {
        using var cache = CreateCache();
        var loads = 0;
        async Task<(Snapshot Value, long Size)> Load(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref loads);
            await Task.Delay(30, cancellationToken);
            return (new Snapshot("value"), 128);
        }

        var values = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => cache.GetAsync("rules:one", ConfigurationScopes.Rules, Load, CancellationToken.None)));
        var warm = await cache.GetAsync("rules:one", ConfigurationScopes.Rules, Load, CancellationToken.None);

        Assert.All(values, value => Assert.Equal("value", value.Value));
        Assert.Equal("value", warm.Value);
        Assert.Equal(1, loads);
        Assert.True(cache.Hits >= 1);
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public async Task FailedRefreshUsesLastGoodSnapshot()
    {
        using var cache = CreateCache();
        var first = await cache.GetAsync(
            "firewall",
            ConfigurationScopes.Firewall,
            _ => Task.FromResult((new Snapshot("safe"), 128L)),
            CancellationToken.None);
        cache.ObserveCommittedVersion(ConfigurationScopes.Firewall, 1);

        var stale = await cache.GetAsync<Snapshot>(
            "firewall",
            ConfigurationScopes.Firewall,
            _ => throw new IOException("database unavailable"),
            CancellationToken.None);

        Assert.Equal(first, stale);
        Assert.Equal(1, cache.ReloadFailures);
        Assert.Contains("database unavailable", cache.LastError);
    }

    [Fact]
    public async Task DisabledCacheLoadsEveryTime()
    {
        using var cache = CreateCache(enabled: false);
        var loads = 0;
        Task<(Snapshot Value, long Size)> Load(CancellationToken _)
        {
            loads++;
            return Task.FromResult((new Snapshot(loads.ToString()), 128L));
        }

        _ = await cache.GetAsync("settings", ConfigurationScopes.Settings, Load, CancellationToken.None);
        _ = await cache.GetAsync("settings", ConfigurationScopes.Settings, Load, CancellationToken.None);

        Assert.Equal(2, loads);
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public async Task StaleDeliveryLeaseCannotRenewOrComplete()
    {
        var dbOptions = new DbContextOptionsBuilder<MailDbContext>()
            .UseInMemoryDatabase($"delivery-lease-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var factory = new PooledDbContextFactory<MailDbContext>(dbOptions);
        var settings = new SystemSettingsService(
            factory,
            Options.Create(new DeliveryOptions()),
            Options.Create(new MailboxDefaultsOptions()),
            Options.Create(new CertificatesOptions()));
        var queue = new DeliveryQueue(
            factory,
            settings,
            Options.Create(new DatabaseOptions { Provider = DatabaseProvider.Sqlite }),
            NullLogger<DeliveryQueue>.Instance);
        DeliveryJob job;
        await using (var db = await factory.CreateDbContextAsync())
        {
            job = new DeliveryJob
            {
                Message = new StoredMessage { ContentKey = "lease", EnvelopeFrom = "from@example.com" },
                Sender = "from@example.com",
                Recipient = "to@example.net",
                Status = DeliveryStatus.Processing,
                Attempts = 1,
                LeaseOwner = "worker-a",
                LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1)
            };
            db.DeliveryJobs.Add(job);
            await db.SaveChangesAsync();
        }
        var stale = new DeliveryLease(job, "worker-a", 1, job.LeaseUntil!.Value);
        Assert.True(await queue.RenewAsync(stale, TimeSpan.FromMinutes(1), CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var current = await db.DeliveryJobs.SingleAsync();
            current.LeaseOwner = "worker-b";
            current.Attempts = 2;
            await db.SaveChangesAsync();
        }

        var completion = await queue.CompleteAsync(stale, DeliveryResult.Success("250 accepted"), CancellationToken.None);

        Assert.False(completion.Applied);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(await verify.DeliveryAttempts.ToListAsync());
        Assert.Equal("worker-b", (await verify.DeliveryJobs.SingleAsync()).LeaseOwner);
    }

    private static ConfigurationSnapshotCache CreateCache(bool enabled = true) => new(
        Options.Create(new ConfigurationCacheOptions
        {
            Enabled = enabled,
            SizeLimitBytes = 4096,
            MaximumEntryBytes = 2048,
            AbsoluteRefreshSeconds = 60,
            MaximumConcurrentLoads = 2
        }),
        NullLogger<ConfigurationSnapshotCache>.Instance);

    private sealed record Snapshot(string Value);
}
