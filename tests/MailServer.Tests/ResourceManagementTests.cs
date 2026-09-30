using System.Data.Common;
using System.Text.Json;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailServer.Tests;

public sealed class ResourceManagementTests
{
    [Fact]
    public async Task OversizedFirewallMarkerIsCachedAndBatchedRulesStillMatch()
    {
        await using var fixture = new SqliteFixture();
        await fixture.InitializeAsync();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.FirewallRules.AddRange(
                new FirewallRule { Cidr = "192.0.2.0/25" },
                new FirewallRule { Cidr = "2001:db8::/33" });
            await db.SaveChangesAsync();
        }
        using var cache = Cache();
        cache.Resize(4096, 512); // Estimate exceeds one entry; the marker itself still fits.
        var firewall = new IpFirewallService(fixture.Factory, NullLogger<IpFirewallService>.Instance, cache);
        Assert.True(await firewall.IsBlockedAsync(System.Net.IPAddress.Parse("192.0.2.100"), CancellationToken.None));
        Assert.Equal(1, cache.EntryCount);
        Assert.Equal(256, cache.EstimatedBytes);
        Assert.True(await firewall.IsBlockedAsync(System.Net.IPAddress.Parse("2001:db8:1000::1"), CancellationToken.None));
        Assert.False(await firewall.IsBlockedAsync(System.Net.IPAddress.Parse("192.0.2.200"), CancellationToken.None));
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public async Task ConnectionProtectionStatusUsesAppliedConfigurationAndHeartbeatAge()
    {
        await using var fixture = new SqliteFixture();
        await fixture.InitializeAsync();
        using var cache = Cache();
        var config = new ConfigurationBuilder().Build();
        var settings = new ConnectionProtectionSettingsService(fixture.Factory, cache, config);
        var desired = await settings.SaveAsync(ConnectionProtectionSettings.Default with
        { ImapLogin = new LoginProtection(10, 3, 20) }, "test", CancellationToken.None);
        var runtime = new ResourceRuntime("imap", config);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.ConfigurationProcessStatuses.Add(new ConfigurationProcessStatus
            {
                ProcessId = "imap-status", InstanceName = "mail-1", Roles = "imap",
                LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                ResourceStatusJson = JsonSerializer.Serialize(runtime.Snapshot() with
                { ProtectionConfigurationId = JsonSerializer.Serialize(desired) })
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal("offline", (await settings.StatusAsync(CancellationToken.None)).Processes.Single().State);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var process = await db.ConfigurationProcessStatuses.SingleAsync();
            process.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Equal("applied", (await settings.StatusAsync(CancellationToken.None)).Processes.Single().State);
        await settings.SaveAsync(desired with { MaximumTrackedSources = 256 }, "test", CancellationToken.None);
        Assert.Equal("applying", (await settings.StatusAsync(CancellationToken.None)).Processes.Single().State);
    }

    [Theory]
    [InlineData("512mb", 4, 1, 8, 16)]
    [InlineData("1gb", 8, 2, 16, 32)]
    [InlineData("2gb", 16, 4, 32, 64)]
    [InlineData("4gb", 32, 8, 64, 128)]
    [InlineData("8gb", 64, 16, 1024, 256)]
    [InlineData("16gb", 128, 32, 2048, 512)]
    [InlineData("32gb", 256, 64, 4096, 1024)]
    public void ProfilesApplyWithoutDisconnectingExistingWork(string id, int mib, int workers, int smtp, int imap)
    {
        var config = new ConfigurationBuilder().Build();
        var runtime = new ResourceRuntime("all", config);
        using var cache = Cache();
        runtime.Apply(id, cache);
        Assert.Equal(mib * 1024L * 1024, cache.Options.SizeLimitBytes);
        Assert.Equal(workers, runtime.Delivery.Limit);
        var memoryBound = (int)Math.Clamp(
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (512L * 1024), 8, int.MaxValue);
        Assert.Equal(Math.Min(smtp, memoryBound), runtime.Smtp.Limit);
        Assert.Equal(imap, runtime.Imap.Limit);
        using var first = runtime.Delivery.TryAcquire();
        using var second = runtime.Delivery.TryAcquire();
        runtime.Apply("512mb", cache);
        Assert.Equal(second is null ? 1 : 2, runtime.Delivery.Active);
        Assert.Null(runtime.Delivery.TryAcquire());
        second?.Dispose();
        first?.Dispose();
        using var next = runtime.Delivery.TryAcquire();
        Assert.NotNull(next);
    }

    [Fact]
    public void SplitBudgetsAddUpAndExplicitConnectionCapWins()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Smtp:MaximumConnections"] = "3" }).Build();
        long total = 0;
        foreach (var role in new[] { "smtp", "imap", "worker", "api" })
        {
            using var cache = Cache();
            var runtime = new ResourceRuntime(role, config);
            runtime.Apply("16gb", cache);
            total += runtime.Snapshot().CacheBudgetBytes;
            Assert.Equal(3, runtime.Smtp.Limit);
        }
        Assert.Equal(128 * 1024L * 1024, total);
    }

    [Fact]
    public void ActiveSmtpMessageLimitDoesNotConsumeConnectionCapacity()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:MaximumConnections"] = "4096",
            ["Smtp:MaximumActiveMessages"] = "1"
        }).Build();
        using var cache = Cache();
        var runtime = new ResourceRuntime("smtp", config);
        runtime.Apply("32gb", cache);
        Assert.Equal(Math.Min(4096, (int)Math.Clamp(
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (512L * 1024), 8, int.MaxValue)), runtime.Smtp.Limit);
        Assert.Equal(1, runtime.SmtpMessages.Limit);
        using var activeMessage = runtime.SmtpMessages.TryAcquire();
        Assert.NotNull(activeMessage);
        Assert.Null(runtime.SmtpMessages.TryAcquire());
        using var idleConnection = runtime.Smtp.TryAcquire();
        Assert.NotNull(idleConnection);
        Assert.Equal(1, runtime.Snapshot().ActiveSmtpMessages);
        Assert.Equal(1, runtime.Snapshot().SmtpMessageLimit);
    }

    [Fact]
    public async Task CancelledWaitersDoNotCancelSharedLoadAndAuxiliaryTasksAreRemoved()
    {
        using var cache = Cache();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<(string, long)> Load(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(token);
            return ("cached", 128);
        }
        using var cancel = new CancellationTokenSource();
        var cancelled = cache.GetAsync("key", "rules", Load, cancel.Token);
        var waiting = Enumerable.Range(0, 40).Select(_ => Task.Run(() => cache.GetAsync("key", "rules", Load, CancellationToken.None))).ToArray();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult();
        Assert.All(await Task.WhenAll(waiting), value => Assert.Equal("cached", value));
        Assert.Equal(1, calls);
        Assert.Equal(0, cache.ActiveLoads);
    }

    [Fact]
    public async Task ChangingLoadConcurrencyAdmitsNewWorkWithoutCancellingExistingLoad()
    {
        using var cache = new ConfigurationSnapshotCache(Options.Create(new ConfigurationCacheOptions
            { MaximumConcurrentLoads = 1 }), NullLogger<ConfigurationSnapshotCache>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = cache.GetAsync("first", "rules", async _ =>
        { started.SetResult(); await release.Task; return ("first", 100L); }, CancellationToken.None);
        await started.Task;
        var second = cache.GetAsync("second", "rules", _ =>
        { secondStarted.SetResult(); return Task.FromResult(("second", 100L)); }, CancellationToken.None);
        Assert.False(secondStarted.Task.IsCompleted);
        cache.Reconfigure(4 * 1024 * 1024, 1024 * 1024, 5, 90, 2, 32);
        Assert.Equal("second", await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(first.IsCompleted);
        Assert.Equal(5, cache.Options.VersionPollSeconds);
        Assert.Equal(90, cache.Options.AbsoluteRefreshSeconds);
        Assert.Equal(32, cache.Options.RuleFallbackBatchSize);
        release.SetResult();
        Assert.Equal("first", await first);
    }

    [Fact]
    public async Task ShrinkingCacheEvictsImmediatelyAndKeepsExistingReferencesValid()
    {
        using var cache = Cache();
        var first = await cache.GetAsync("first", "rules", _ => Task.FromResult(("first", 1024L)), CancellationToken.None);
        await cache.GetAsync("second", "rules", _ => Task.FromResult(("second", 1024L)), CancellationToken.None);
        cache.Resize(1024, 1024);
        Assert.Equal(1024, cache.EstimatedBytes);
        Assert.Equal(1, cache.EntryCount);
        Assert.Equal("first", first);
        cache.Resize(0, 0);
        Assert.Equal(0, cache.EstimatedBytes);
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public async Task FailedColdLoadBacksOffAndNeverReturnsAnAllowSnapshot()
    {
        using var cache = Cache();
        var calls = 0;
        Task<(string, long)> Fail(CancellationToken _)
        { calls++; throw new IOException("offline"); }
        await Assert.ThrowsAsync<IOException>(() => cache.GetAsync("firewall", "firewall", Fail, CancellationToken.None));
        for (var i = 0; i < 10; i++)
            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync("firewall", "firewall", Fail, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Equal(0, cache.ActiveLoads);
    }

    [Fact]
    public async Task AdminReadsBypassWarmCacheWhileRuntimeReadsStayCached()
    {
        await using var fixture = new SqliteFixture();
        await fixture.InitializeAsync();
        using var cache = Cache();
        var settings = new SystemSettingsService(fixture.Factory, Options.Create(new DeliveryOptions()),
            Options.Create(new MailboxDefaultsOptions()), Options.Create(new CertificatesOptions()), cache);
        await settings.SaveSmtpSettingsAsync(new(100), CancellationToken.None);
        Assert.Equal(100, (await settings.GetSmtpSettingsAsync(CancellationToken.None, true)).CommandTimeoutSeconds);
        var count = fixture.Counter.Reads;
        for (var i = 0; i < 10; i++) await settings.GetSmtpSettingsAsync(CancellationToken.None, true);
        Assert.Equal(count, fixture.Counter.Reads);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var row = await db.SystemSettings.SingleAsync(x => x.Key == "smtp:command-timeout-seconds");
            row.Value = "150";
            await db.SaveChangesAsync();
        }
        Assert.Equal(150, (await settings.GetSmtpSettingsAsync(CancellationToken.None)).CommandTimeoutSeconds);
        Assert.Equal(100, (await settings.GetSmtpSettingsAsync(CancellationToken.None, true)).CommandTimeoutSeconds);
        await settings.SaveSmtpSettingsAsync(new(200), CancellationToken.None);
        Assert.Equal(200, (await settings.GetSmtpSettingsAsync(CancellationToken.None, true)).CommandTimeoutSeconds);
    }

    [Fact]
    public async Task SelectionIsPersistedAuditedAndStatusReflectsApplicationNotObservedVersion()
    {
        await using var fixture = new SqliteFixture();
        await fixture.InitializeAsync();
        using var cache = Cache();
        var service = new ResourceSettingsService(fixture.Factory, cache);
        Assert.Null((await service.ReadAsync(CancellationToken.None)).ProfileId);
        await service.SaveAsync("4gb", "test", CancellationToken.None);
        using var restartedCache = Cache();
        var restarted = new ResourceSettingsService(fixture.Factory, restartedCache);
        Assert.Equal("4gb", (await restarted.ReadAsync(CancellationToken.None)).ProfileId);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "settings.resources.update"));
            var runtime = new ResourceRuntime("worker", new ConfigurationBuilder().Build());
            runtime.Apply("1gb", cache);
            db.ConfigurationProcessStatuses.Add(new ConfigurationProcessStatus {
                ProcessId = "worker", InstanceName = "test", Roles = "worker", LastSeenAt = DateTimeOffset.UtcNow,
                ResourceStatusJson = JsonSerializer.Serialize(runtime.Snapshot()) });
            await db.SaveChangesAsync();
        }
        Assert.Equal("applying", (await restarted.StatusAsync(CancellationToken.None)).Processes.Single().State);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var row = await db.ConfigurationProcessStatuses.SingleAsync();
            row.LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            await db.SaveChangesAsync();
        }
        var stale = await restarted.StatusAsync(CancellationToken.None);
        Assert.Equal("unreported", stale.Processes.Single().State);
        Assert.Equal(0, stale.TotalCacheBudgetBytes);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync("bad", "test", CancellationToken.None));
        Assert.Equal("4gb", (await restarted.ReadAsync(CancellationToken.None)).ProfileId);
    }

    [Fact]
    public async Task CustomValuesStoredAsJsonSurviveRestartAndDistinguishSameProfileChanges()
    {
        await using var fixture = new SqliteFixture();
        await fixture.InitializeAsync();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.SystemSettings.Add(new SystemSetting { Key = ResourceSettingsService.Key,
                Value = "{\"profileId\":\"1gb\",\"values\":{\"cacheBytes\":12582912,\"entryBytes\":1048576,\"deliveryConcurrency\":3,\"smtpConnections\":16,\"imapConnections\":32,\"versionPollSeconds\":4,\"absoluteRefreshSeconds\":80,\"maximumConcurrentLoads\":3,\"ruleFallbackBatchSize\":32}}" });
            await db.SaveChangesAsync();
        }
        using var cache = Cache();
        var service = new ResourceSettingsService(fixture.Factory, cache);
        var selection = await service.ReadAsync(CancellationToken.None);
        Assert.Equal("1gb", selection.ProfileId);
        Assert.Equal(12582912, selection.Values?.CacheBytes);
        Assert.Equal(4, selection.Values?.VersionPollSeconds);
        var runtime = new ResourceRuntime("all", new ConfigurationBuilder().Build());
        runtime.Apply(selection, cache);
        Assert.Equal(12582912, runtime.Snapshot().CacheBudgetBytes);
        Assert.Equal(3, runtime.Delivery.Limit);
        Assert.Equal(4, cache.Options.VersionPollSeconds);
    }

    [Fact]
    public async Task SameProfileCustomChangeRequiresEachProcessToApplyCompleteConfiguration()
    {
        await using var fixture = new SqliteFixture();
        await fixture.InitializeAsync();
        using var cache = Cache();
        var service = new ResourceSettingsService(fixture.Factory, cache);
        var original = await service.SaveAsync("1gb", "test", CancellationToken.None);
        var runtime = new ResourceRuntime("worker", new ConfigurationBuilder().Build());
        runtime.Apply(original, cache);
        runtime.SetServiceState("worker", "working");
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.ConfigurationProcessStatuses.Add(new ConfigurationProcessStatus {
                ProcessId = "worker-custom", InstanceName = "test", Roles = "worker", LastSeenAt = DateTimeOffset.UtcNow,
                ResourceStatusJson = JsonSerializer.Serialize(runtime.Snapshot()) });
            await db.SaveChangesAsync();
        }
        var initialStatus = await service.StatusAsync(CancellationToken.None);
        Assert.Equal("applied", initialStatus.Processes.Single().State);
        Assert.Equal("1gb", initialStatus.ProfileId);
        Assert.Equal(2, initialStatus.Values?.DeliveryConcurrency);
        var custom = original.Values! with { DeliveryConcurrency = 3, VersionPollSeconds = 4 };
        await service.SaveAsync(new ResourceSelection("1gb", custom), "test", CancellationToken.None);
        Assert.Equal("applying", (await service.StatusAsync(CancellationToken.None)).Processes.Single().State);
        runtime.Apply(await service.ReadAsync(CancellationToken.None), cache);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var process = await db.ConfigurationProcessStatuses.SingleAsync();
            process.ResourceStatusJson = JsonSerializer.Serialize(runtime.Snapshot());
            await db.SaveChangesAsync();
        }
        Assert.Equal("applied", (await service.StatusAsync(CancellationToken.None)).Processes.Single().State);
        Assert.Equal(3, runtime.Delivery.Limit);
        Assert.Equal(4, cache.Options.VersionPollSeconds);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new ResourceSelection("1gb",
            custom with { EntryBytes = custom.CacheBytes + 1 }), "test", CancellationToken.None));
    }

    [Theory]
    [InlineData("api", false)]
    [InlineData("cli", false)]
    [InlineData("smtp", true)]
    [InlineData("imap", true)]
    [InlineData("worker", true)]
    public void OnlyMailHostsRegisterCacheMonitor(string role, bool enabled)
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddMailServerInfrastructure(config, role);
        Assert.Equal(enabled, services.Any(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(ConfigurationCacheMonitor)));
    }

    [Fact]
    public async Task AdministrativeBypassDoesNotReplaceRuntimeSnapshot()
    {
        using var cache = Cache();
        var reads = 0;
        Task<(string Value, long Size)> Load(CancellationToken _) => Task.FromResult(($"value-{++reads}", 100L));
        Assert.Equal("value-1", await cache.GetAsync("shared", ConfigurationScopes.Settings, Load, CancellationToken.None));
        using (new ConfigurationCacheBypass())
        {
            Assert.Equal("value-2", await cache.GetAsync("shared", ConfigurationScopes.Settings, Load, CancellationToken.None));
            Assert.Equal("value-3", await cache.GetAsync("shared", ConfigurationScopes.Settings, Load, CancellationToken.None));
        }
        Assert.Equal("value-1", await cache.GetAsync("shared", ConfigurationScopes.Settings, Load, CancellationToken.None));
        Assert.Equal(3, reads);
    }

    [Fact]
    public void OlderPollCannotUndoLocalCommittedInvalidation()
    {
        using var cache = Cache();
        cache.ObserveCommittedVersion(ConfigurationScopes.Settings, 7);
        cache.ObserveVersions(new ConfigurationVersionsSnapshot(1, 2, 6));
        Assert.Equal(7, cache.Versions.Settings);
    }

    private static ConfigurationSnapshotCache Cache() => new(Options.Create(new ConfigurationCacheOptions()), NullLogger<ConfigurationSnapshotCache>.Instance);

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Reads); return ValueTask.FromResult(result); }
    }
    private sealed class SqliteFixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"mailserver-resources-{Guid.NewGuid():N}.db");
        public readonly ReadCounter Counter = new();
        public IDbContextFactory<MailDbContext> Factory { get; }
        public SqliteFixture()
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            Factory = new PooledDbContextFactory<MailDbContext>(new DbContextOptionsBuilder<MailDbContext>()
                .UseSqlite($"Data Source={_path};Pooling=False").AddInterceptors(Counter).Options);
        }
        public async Task InitializeAsync()
        { await using var db = await Factory.CreateDbContextAsync(); await db.Database.EnsureCreatedAsync(); }
        public ValueTask DisposeAsync() { File.Delete(_path); return ValueTask.CompletedTask; }
    }
}
