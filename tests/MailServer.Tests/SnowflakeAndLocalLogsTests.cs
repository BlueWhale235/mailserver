using System.Collections.Concurrent;
using System.Text.Json;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MailServer.Tests;

public sealed class SnowflakeAndLocalLogsTests
{
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider, IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    [Fact]
    public void ConcurrentWorkersAndSequenceOverflowRemainUnique()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        using var first = new SnowflakeGenerator(10, clock);
        using var second = new SnowflakeGenerator(11, clock);
        var ids = new ConcurrentBag<long>();
        Parallel.For(0, 20_000, i => ids.Add((i % 2 == 0 ? first : second).NextId()));
        Assert.Equal(20_000, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id > 0));
        Assert.All(ids, id => Assert.Contains((id >> 12) & 1023, new long[] { 10, 11 }));
    }

    [Fact]
    public void RestartFenceAndExclusiveLeasePreventIdReuse()
    {
        var root = Path.Combine(Path.GetTempPath(), "snowflake-test-" + System.Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableClock(DateTimeOffset.UtcNow);
            var path = Path.Combine(root, "worker.state");
            long last;
            using (var first = new SnowflakeGenerator(42, clock, path))
            {
                last = first.NextId();
                Assert.Throws<IOException>(() => new SnowflakeGenerator(42, clock, path));
            }
            using var restarted = new SnowflakeGenerator(42, clock, path);
            Assert.True(restarted.NextId() > last);
            clock.UtcNow = clock.UtcNow.AddSeconds(-10);
            Assert.Throws<InvalidOperationException>(() => restarted.NextId());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void JsonKeepsIdsExactAndLeavesCountersNumeric()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        SnowflakeJson.Configure(options);
        const long id = 9223372036854775000;
        var json = JsonSerializer.Serialize(new IdPayload(id, id, [id], 123), options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(id.ToString(), document.RootElement.GetProperty("id").GetString());
        Assert.Equal(id.ToString(), document.RootElement.GetProperty("messageIds")[0].GetString());
        Assert.Equal(123, document.RootElement.GetProperty("size").GetInt64());
        Assert.Equal(id, JsonSerializer.Deserialize<IdPayload>(json, options)!.Id);
    }

    private sealed record IdPayload(long Id, long? DomainId, long[] MessageIds, long Size);

    [Fact]
    public async Task PooledBusinessContextReusesInstanceWithoutLeakingPendingLogs()
    {
        await using var environment = new Environment();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var first = await factory.CreateDbContextAsync();
        await first.Database.EnsureCreatedAsync();
        first.AuditRecords.Add(new AuditRecord { Actor = "test", Action = "discard", Resource = "test" });
        await first.DisposeAsync();
        await using var second = await factory.CreateDbContextAsync();
        Assert.Same(first, second);
        Assert.Null(second.PendingLogs);
        second.AuditRecords.Add(new AuditRecord { Actor = "test", Action = "keep", Resource = "test" });
        await second.SaveChangesAsync();
        Assert.Equal("keep", (await second.AuditRecords.SingleAsync()).Action);
    }

    [Fact]
    public async Task LogsAreSeparateAndOnlySuccessfulBusinessCommitsAreLogged()
    {
        await using var environment = new Environment();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            Assert.Null(db.Model.FindEntityType(typeof(AuditRecord)));
            Assert.Null(db.Model.FindEntityType(typeof(DeliveryAttempt)));
            Assert.Null(db.Model.FindEntityType(typeof(TaskExecution)));
            Assert.Null(db.Model.FindEntityType(typeof(PerformanceOperation)));
            Assert.Equal(typeof(long), db.Model.FindEntityType(typeof(StoredMessage))!.FindProperty("Id")!.ClrType);
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.SystemSettings.Add(new SystemSetting { Key = "rollback", Value = "test" });
                db.AuditRecords.Add(new AuditRecord { Actor = "test", Action = "rollback", Resource = "test" });
                await db.SaveChangesAsync();
                await using (var logs = environment.Logs.CreateDbContext()) Assert.Empty(await logs.AuditRecords.ToListAsync());
                await transaction.RollbackAsync();
            }
            db.ChangeTracker.Clear();
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.SystemSettings.Add(new SystemSetting { Key = "commit", Value = "test" });
                db.AuditRecords.Add(new AuditRecord { Actor = "test", Action = "commit", Resource = "test" });
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }
        }
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.False(await db.SystemSettings.AnyAsync(x => x.Key == "rollback"));
            Assert.True(await db.SystemSettings.AnyAsync(x => x.Key == "commit"));
        }
        await using var local = environment.Logs.CreateDbContext();
        Assert.Equal("commit", (await local.AuditRecords.SingleAsync()).Action);
        Assert.Empty(await local.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE name = 'stored_messages'").ToListAsync());
    }

    [Fact]
    public async Task RetentionDeletesEveryLogKindAndKeepsBoundaryAndBusinessRows()
    {
        await using var environment = new Environment();
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var cutoff = clock.UtcNow.AddDays(-30);
        await using (var db = environment.Logs.CreateDbContext())
        {
            foreach (var date in new[] { cutoff.AddTicks(-1), cutoff })
            {
                db.AuditRecords.Add(new AuditRecord { Actor = "test", Action = "test", Resource = "test", CreatedAt = date });
                db.DeliveryAttempts.Add(new DeliveryAttempt { DeliveryJobId = 1, CreatedAt = date });
                db.TaskExecutions.Add(new TaskExecution { ScheduledTaskId = 1, StartedAt = date, CompletedAt = date });
                db.PerformanceOperations.Add(new PerformanceOperation { StartedAt = date });
                db.PerformanceMinutes.Add(new PerformanceMinute { Minute = date });
                db.PerformanceSql.Add(new PerformanceSql { Fingerprint = date.Ticks.ToString(), LastSeenAt = date });
                db.PerformanceProcesses.Add(new PerformanceProcess { ProcessId = date.Ticks.ToString(), LastSeenAt = date });
            }
            await db.SaveChangesAsync();
        }
        var retention = new LocalLogRetentionService(environment.Logs, clock, NullLogger<LocalLogRetentionService>.Instance);
        Assert.Equal(7, await retention.CleanupAsync(CancellationToken.None));
        await using var logs = environment.Logs.CreateDbContext();
        Assert.Single(await logs.AuditRecords.ToListAsync());
        Assert.Single(await logs.DeliveryAttempts.ToListAsync());
        Assert.Single(await logs.TaskExecutions.ToListAsync());
        Assert.Single(await logs.PerformanceOperations.ToListAsync());
        Assert.Single(await logs.PerformanceMinutes.ToListAsync());
        Assert.Single(await logs.PerformanceSql.ToListAsync());
        Assert.Single(await logs.PerformanceProcesses.ToListAsync());
        await environment.Logs.SetRetentionAsync(7, "test", CancellationToken.None);
        Assert.Equal(7, (await environment.Logs.SettingsAsync(CancellationToken.None)).RetentionDays);
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalLogDatabase.ValidateRetention(0));
    }

    [Fact]
    public async Task LogStorageFailureDoesNotUndoCommittedBusinessState()
    {
        await using var environment = new Environment();
        await using (var local = environment.Logs.CreateDbContext())
            await local.Database.ExecuteSqlRawAsync("DROP TABLE audit_records");
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.SystemSettings.Add(new SystemSetting { Key = "survives-log-error", Value = "yes" });
            db.AuditRecords.Add(new AuditRecord { Actor = "test", Action = "test", Resource = "test" });
            await db.SaveChangesAsync();
        }
        await using var verify = await factory.CreateDbContextAsync();
        Assert.True(await verify.SystemSettings.AnyAsync(x => x.Key == "survives-log-error"));
        Assert.Equal(1, (await environment.Logs.SettingsAsync(CancellationToken.None)).FailedWrites);
    }

    [Fact]
    public async Task BatchWriterDrainsEveryAcceptedLogOnShutdown()
    {
        await using var environment = new Environment();
        using var writer = new LocalLogWriter(environment.Logs);
        await writer.StartAsync(default);
        var completions = new List<Task>();
        for (var index = 0; index < 600; index++)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await writer.EnqueueAsync(new LocalLogWrite(new AuditRecord
                { Actor = "batch", Action = index.ToString(), Resource = "drain" }, completion), default);
            completions.Add(completion.Task);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await writer.StopAsync(deadline.Token);
        Assert.All(completions, task => Assert.True(task.IsCompletedSuccessfully));
        await using var logs = environment.Logs.CreateDbContext();
        Assert.Equal(600, await logs.AuditRecords.CountAsync());
        Assert.False(writer.IsRunning);
    }

    private sealed class Environment : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "local-logs-test-" + System.Guid.NewGuid());
        public ServiceProvider Services { get; }
        public LocalLogDatabase Logs => Services.GetRequiredService<LocalLogDatabase>();
        public Environment()
        {
            Directory.CreateDirectory(_root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = "Data Source=" + Path.Combine(_root, "business.db"),
                ["LocalLogs:Path"] = Path.Combine(_root, "logs.db")
            }).Build();
            Services = new ServiceCollection().AddLogging().AddMailServerInfrastructure(configuration).BuildServiceProvider();
        }
        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
        }
    }
}
