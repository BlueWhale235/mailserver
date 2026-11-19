using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailServer.Tests;

public sealed class ResourceIsolationTests
{
    [Theory]
    [InlineData("api", false, false)]
    [InlineData("smtp", false, true)]
    [InlineData("imap", false, true)]
    [InlineData("worker", true, false)]
    [InlineData("all", true, true)]
    public async Task HostsResolveOnlyTheirBackgroundResponsibilities(string role, bool maintenance, bool certificates)
    {
        using var directory = new TemporaryDirectory();
        var configuration = Configuration(directory.Path, role);
        await using var provider = Services(configuration, role).BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(maintenance, hosted.OfType<SentCopyBackgroundService>().Any());
        Assert.Equal(maintenance, hosted.OfType<MessageSearchBackfillService>().Any());
        Assert.Equal(maintenance, hosted.OfType<TlsEventWriter>().Any());
        Assert.Equal(maintenance, hosted.OfType<TlsReportBackgroundService>().Any());
        Assert.Equal(maintenance, hosted.OfType<MaintenanceBackgroundService>().Any());
        Assert.Equal(certificates, hosted.OfType<DomainCertificateProvider>().Any());
        Assert.Single(hosted.OfType<LocalLogWriter>());
        Assert.Single(hosted.OfType<PerformanceStorage>());
        Assert.Equal(hosted.Length, hosted.Select(x => x.GetType()).Distinct().Count());
    }

    [Fact]
    public async Task StandaloneProtocolHostCanOwnCopiesAndDisableCrossProcessWake()
    {
        using var directory = new TemporaryDirectory();
        var configuration = Configuration(directory.Path, "smtp");
        configuration["BackgroundServices:SentCopiesEnabled"] = "true";
        configuration["BackgroundServices:ProcessWakeEnabled"] = "false";
        await using var provider = Services(configuration, "smtp").BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        Assert.Single(hosted.OfType<SentCopyBackgroundService>());
        Assert.Empty(hosted.OfType<ProcessWakeService>());
    }

    [Fact]
    public void LocalLogMaintenanceHasOneOwnerAndAnotherCanTakeOver()
    {
        using var directory = new TemporaryDirectory();
        var configuration = Configuration(directory.Path, "smtp");
        var database = new LocalLogDatabase(configuration, NullLogger<LocalLogDatabase>.Instance);
        using var first = new LocalLogMaintenanceLease(database);
        using var second = new LocalLogMaintenanceLease(new LocalLogDatabase(configuration, NullLogger<LocalLogDatabase>.Instance));
        Assert.True(first.TryAcquire());
        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
        first.Dispose();
        Assert.True(second.TryAcquire());
        Assert.False(first.TryAcquire());

        configuration["LocalLogs:Path"] = System.IO.Path.Combine(directory.Path, "other-logs.db");
        using var independent = new LocalLogMaintenanceLease(new LocalLogDatabase(configuration, NullLogger<LocalLogDatabase>.Instance));
        Assert.True(independent.TryAcquire());
    }

    [Fact]
    public async Task ContextPoolRetainsOnlyConfiguredCountAndStillAllowsMoreActiveContexts()
    {
        using var directory = new TemporaryDirectory();
        var configuration = Configuration(directory.Path, "api");
        configuration["Database:ContextPoolSize"] = "2";
        await using var provider = Services(configuration, "api").BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var contexts = Enumerable.Range(0, 3).Select(_ => factory.CreateDbContext()).ToArray();
        var ids = contexts.Select(x => x.ContextId.InstanceId).ToArray();
        Assert.Equal(3, ids.Distinct().Count());
        foreach (var context in contexts) await context.DisposeAsync();
        await using var one = await factory.CreateDbContextAsync();
        await using var two = await factory.CreateDbContextAsync();
        await using var three = await factory.CreateDbContextAsync();
        Assert.Equal(ids[0], one.ContextId.InstanceId);
        Assert.Equal(ids[1], two.ContextId.InstanceId);
        Assert.DoesNotContain(three.ContextId.InstanceId, ids);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4097")]
    public void InvalidPoolSizeFailsAtRegistration(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Database:ContextPoolSize"] = value }).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddMailServerInfrastructure(configuration));
    }

    [Fact]
    public async Task FetchCacheShrinksImmediatelyAndKeepsPersistentMetadataUsable()
    {
        var configuration = new ConfigurationBuilder().Build();
        var runtime = new ResourceRuntime("all", configuration);
        using var snapshots = new ConfigurationSnapshotCache(Options.Create(new ConfigurationCacheOptions()),
            NullLogger<ConfigurationSnapshotCache>.Instance);
        var factory = new MemoryFactory();
        var message = new StoredMessage { ContentKey = new string('a', 64), EnvelopeFrom = "sender@example.test" };
        await using (var db = factory.CreateDbContext()) { db.Messages.Add(message); await db.SaveChangesAsync(); }
        using var cache = new MessageFetchMetadataCache(factory, runtime);
        var builds = 0;
        Task<MessageFetchMetadata> Build(CancellationToken _)
        {
            builds++;
            return Task.FromResult(new MessageFetchMetadata { Envelope = new string('x', 1024) });
        }
        var initial = await cache.GetAsync(message.Id, message.ContentKey, Build, default);
        Assert.True(cache.EstimatedBytes > 0);
        Assert.Same(initial, await cache.GetAsync(message.Id, message.ContentKey, Build, default));

        var values = ResourceProfiles.Get("512mb").Values with { CacheBytes = 4096, EntryBytes = 1024 };
        runtime.Apply(new ResourceSettingsView(null, ResourceProfiles.All, values), snapshots);
        Assert.Equal(2048, snapshots.Options.SizeLimitBytes);
        Assert.Equal(1024, cache.SizeLimitBytes);
        Assert.Equal(0, cache.EstimatedBytes);
        Assert.Equal(4096, runtime.Snapshot().CacheBudgetBytes);
        Assert.Equal(initial.Envelope, (await cache.GetAsync(message.Id, message.ContentKey, Build, default)).Envelope);
        Assert.Equal(1, builds); // Eviction must not cause expensive MIME parsing when persisted metadata exists.
        Assert.Equal(0, cache.EstimatedBytes);
    }

    [Fact]
    public void DiagnosticAdmissionShrinksWithoutAbortingExistingOperations()
    {
        var runtime = new ResourceRuntime("all", new ConfigurationBuilder().Build());
        using var collector = new PerformanceCollector(runtime);
        using var snapshots = new ConfigurationSnapshotCache(Options.Create(new ConfigurationCacheOptions()),
            NullLogger<ConfigurationSnapshotCache>.Instance);
        var operation = collector.Start("smtp", "inflight");
        Assert.NotNull(operation);
        var values = ResourceProfiles.Get("512mb").Values with { CacheBytes = 1024, EntryBytes = 256 };
        runtime.Apply(new ResourceSettingsView(null, ResourceProfiles.All, values), snapshots);
        Assert.Equal(256, collector.BudgetBytes);
        Assert.Null(collector.Start("smtp", "new-work"));
        operation.Dispose();
        var records = collector.DrainOperations();
        collector.ReleaseOperations(records.Count, false);
        Assert.Equal(0, collector.ProcessStatus().BufferBytes);
    }

    private sealed class MemoryFactory : IDbContextFactory<MailDbContext>
    {
        private readonly DbContextOptions<MailDbContext> _options = new DbContextOptionsBuilder<MailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public MailDbContext CreateDbContext() => new(_options);
    }

    private static IConfigurationRoot Configuration(string directory, string role) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MailDatabase"] = $"Data Source={System.IO.Path.Combine(directory, "mail.db")};Pooling=False",
            ["LocalLogs:Path"] = System.IO.Path.Combine(directory, "logs.db"),
            ["SecurityAssets:RootPath"] = System.IO.Path.Combine(directory, "security"),
            ["RunAll:Enabled"] = (role == "all").ToString()
        }).Build();

    private static IServiceCollection Services(IConfiguration configuration, string role) => new ServiceCollection()
        .AddLogging().AddSingleton(configuration).AddMailServerInfrastructure(configuration, role == "all" ? "api" : role);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mailserver-isolation-" + Guid.NewGuid());
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
