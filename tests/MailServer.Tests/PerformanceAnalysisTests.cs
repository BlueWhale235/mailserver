using System.Diagnostics;
using System.Text.Json;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace MailServer.Tests;

public sealed class PerformanceAnalysisTests(ITestOutputHelper output)
{
    [Fact]
    public void PendingStoredRefreshCannotOverwriteNewerLocalDiagnosticSettings()
    {
        using var collector = new PerformanceCollector();
        var observed = collector.Settings;
        var local = observed with { DiagnosticUntil = DateTimeOffset.UtcNow.AddMinutes(15) };
        collector.Settings = local;
        collector.ApplyStoredSettings(observed, observed with { SampleRate = .1 });
        Assert.Same(local, collector.Settings);
        var refreshed = local with { Enabled = false };
        collector.ApplyStoredSettings(local, refreshed);
        Assert.Same(refreshed, collector.Settings);
    }

    [Fact]
    public async Task NestedWaitIsNotProcessingAndChildrenAreNotDoubleCounted()
    {
        using var collector = new PerformanceCollector { Settings = new() { SampleRate = 1 } };
        using (collector.Start("smtp", "DATA"))
        {
            using (PerformanceTelemetry.Stage("accept"))
            {
                using (PerformanceTelemetry.Stage("upload", "wait")) await Task.Delay(30);
                using (PerformanceTelemetry.Stage("parse")) await Task.Delay(10);
            }
        }
        var row = Assert.Single(collector.DrainOperations());
        var stages = JsonSerializer.Deserialize<PerformanceStage[]>(row.DetailsJson)!;
        Assert.Equal(3, stages.Length);
        var wait = stages.Single(x => x.Name == "upload");
        Assert.Equal(row.DurationMilliseconds - wait.DurationMilliseconds, row.ProcessingMilliseconds, 3);
        var parent = stages.Single(x => x.Name == "accept");
        Assert.All(stages.Where(x => x.Id != parent.Id), child => Assert.Equal(parent.Id, child.ParentId));
        Assert.True(parent.SelfMilliseconds < parent.DurationMilliseconds);
        Assert.Null(PerformanceTelemetry.CurrentScope.Value);
        Assert.Equal(15, PerformanceScope.Union([(0, 10), (5, 15), (7, 8)]));
        collector.ReleaseOperations(1, false);
        Assert.Equal(0, collector.ProcessStatus().BufferBytes);
    }

    [Fact]
    public void SamplingErrorsDiagnosticExpiryAndCapacityAreBounded()
    {
        using var collector = new PerformanceCollector { Settings = new() { SampleRate = 0 }, BudgetBytes = 4 * PerformanceCollector.Reservation };
        using (collector.Start("api", "ok")) { }
        Assert.Empty(collector.DrainOperations());
        using (var root = collector.Start("api", "failed")) root!.Fail(new InvalidOperationException("secret-error-text"));
        collector.Settings = collector.Settings with { DiagnosticUntil = DateTimeOffset.UtcNow.AddMinutes(15) };
        using (collector.Start("api", "diagnostic"))
            for (var i = 0; i < 300; i++) using (PerformanceTelemetry.Stage("same-stage")) { }
        collector.Settings = collector.Settings with { DiagnosticUntil = DateTimeOffset.UtcNow.AddMinutes(-1) };
        using (collector.Start("api", "expired")) { }
        var rows = collector.DrainOperations();
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain("secret-error-text", JsonSerializer.Serialize(rows));
        Assert.True(rows.Single(x => x.Name == "diagnostic").Truncated);
        using (var held = collector.Start("api", "held")) Assert.Null(collector.Start("api", "overflow"));
        Assert.Equal(1, collector.ProcessStatus().DroppedOperations);
        collector.ReleaseOperations(rows.Count, false);
        Assert.Equal(0, collector.ProcessStatus().BufferBytes);
    }

    [Theory]
    [InlineData("sqlite", "SELECT * FROM messages WHERE Subject = 'secret subject' AND Id = @password -- secret-comment")]
    [InlineData("mysql", "SELECT * FROM `messages` WHERE Subject = \"secret subject\" AND Id = 123 /* secret-comment */")]
    [InlineData("sqlite", "SELECT 'unterminated-secret")]
    [InlineData("sqlite", "SELECT \"secret\"")]
    [InlineData("sqlite", "SELECT secret_token FROM private_table")]
    public void SqlTemplatesExcludeLiteralsParametersAndComments(string provider, string command)
    {
        var result = PerformanceSqlInterceptor.Normalize(provider, command);
        Assert.DoesNotContain("secret", result.Template);
        Assert.DoesNotContain("password", result.Template);
        Assert.DoesNotContain("123", result.Template);
        Assert.Equal(64, result.Fingerprint.Length);
        Assert.Equal(PerformanceSqlInterceptor.Normalize(provider, "SELECT 123").Fingerprint,
            PerformanceSqlInterceptor.Normalize(provider, "SELECT 456").Fingerprint);
    }

    [Fact]
    public async Task SqliteSqlCountsRemainExactAfterTimelineLimitAndStorageDoesNotTraceItself()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var collector = new PerformanceCollector { Settings = new() { SampleRate = 0 } };
        await using var logConnection = new SqliteConnection("Data Source=:memory:");
        await logConnection.OpenAsync();
        var logDatabase = new LocalLogDatabase(new DbContextOptionsBuilder<LocalLogDbContext>().UseSqlite(logConnection).Options);
        var factory = new TestMailDbContextFactory(new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite(connection).AddInterceptors(new PerformanceSqlInterceptor(collector), new PerformanceConnectionInterceptor(), new PerformanceTransactionInterceptor()).Options, logDatabase);
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        using (collector.Start("imap", "FETCH"))
        {
            for (var i = 0; i < 270; i++) Assert.Equal(0, await db.Messages.CountAsync());
        }
        var rows = collector.DrainOperations();
        var row = Assert.Single(rows);
        Assert.Equal(270, row.SqlCount);
        Assert.True(row.Slow);
        Assert.True(row.Truncated);
        Assert.Equal(270, collector.DrainAggregates().Minutes.Where(x => x.Kind == "sql").Sum(x => x.Count));
        collector.ReleaseOperations(rows.Count, false);
        collector.Settings = collector.Settings with { SampleRate = 1 };
        using (collector.Start("api", "stored"))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            Assert.Equal(0, await db.Messages.CountAsync());
            await transaction.CommitAsync();
        }
        var storage = new PerformanceStorage(factory, collector, new ResourceRuntime("test", new ConfigurationBuilder().Build()),
            NullLogger<PerformanceStorage>.Instance);
        using (collector.Start("api", "outer")) await storage.FlushAsync(CancellationToken.None);
        Assert.False(collector.StorageUnavailable);
        Assert.Equal(1, await db.PerformanceOperations.CountAsync());
        var stored = await db.PerformanceOperations.SingleAsync();
        Assert.Contains("database.begin-transaction", stored.DetailsJson);
        Assert.Contains("database.commit", stored.DetailsJson);
        var outer = Assert.Single(collector.DrainOperations());
        Assert.Equal(0, outer.SqlCount);
        collector.ReleaseOperations(1, false);
        Assert.Equal(0, collector.ProcessStatus().BufferBytes);
        // Missing diagnostic tables must only drop diagnostic records.
        await db.Logs.Database.ExecuteSqlRawAsync("DROP TABLE performance_operations");
        using (collector.Start("api", "lost")) { }
        await storage.FlushAsync(CancellationToken.None);
        Assert.True(collector.StorageUnavailable);
        Assert.Equal(1, collector.ProcessStatus().DroppedOperations);
        Assert.Equal(0, collector.ProcessStatus().BufferBytes);
    }

    [Fact]
    public async Task FailedSyncSqlRetainsOnlySafeErrorTypeAndDoesNotLeakPendingCommand()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        using var collector = new PerformanceCollector { Settings = new() { SampleRate = 0 } };
        await using var db = new MailDbContext(new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite("Data Source=:memory:").AddInterceptors(new PerformanceSqlInterceptor(collector)).Options);
        using (collector.Start("api", "sql-failure"))
            Assert.Throws<SqliteException>(() => db.Database.ExecuteSqlRaw("SELECT 'private-subject' FROM missing_table"));
        var row = Assert.Single(collector.DrainOperations());
        Assert.Equal("error", row.Status);
        Assert.Equal(1, row.SqlCount);
        Assert.Contains("SqliteException", row.DetailsJson);
        Assert.DoesNotContain("private-subject", row.DetailsJson);
        collector.ReleaseOperations(1, false);
    }

    [Fact]
    public void DisabledAndSuppressedHaveNoDetailAndQueueClockAnomalyIsExplicit()
    {
        using var collector = new PerformanceCollector { Settings = new() { Enabled = false } };
        Assert.Null(collector.Start("smtp", "DATA"));
        collector.Settings = new() { SampleRate = 1 };
        using (var scope = collector.Start("worker", "delivery.attempt", new string('a', 32)))
        {
            scope!.SetQueueTimes(-1, 5000);
            using (PerformanceTelemetry.Suppress()) Assert.Null(PerformanceTelemetry.Stage("excluded"));
            using (var stage = PerformanceTelemetry.Stage("cancelled")) stage!.Fail(new OperationCanceledException());
        }
        var row = Assert.Single(collector.DrainOperations());
        Assert.True(row.ClockAnomaly);
        Assert.Equal(0, row.QueueWaitMilliseconds);
        Assert.Equal(5000, row.RetryBackoffMilliseconds);
        Assert.Equal("cancelled", row.Status);
        Assert.Equal(new string('a', 32), row.SourceTraceId);
        Assert.Single(JsonSerializer.Deserialize<PerformanceStage[]>(row.DetailsJson)!);
        collector.ReleaseOperations(1, false);
    }

    // Opt-in, run separately; timing is reported, never asserted.
    [Fact]
    public void CollectorBenchmark()
    {
        if (Environment.GetEnvironmentVariable("MAILSERVER_PERFORMANCE_BENCHMARK") != "1") return;
        foreach (var enabled in new[] { false, true })
        {
            using var collector = new PerformanceCollector { Settings = new() { Enabled = enabled, SampleRate = .01 } };
            var times = new List<double>(); var allocations = new List<long>();
            for (var round = 0; round < 6; round++)
            {
                var before = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
                for (var i = 0; i < 10000; i++)
                {
                    using (collector.Start("api", "benchmark"))
                        for (var j = 0; j < 5; j++) using (PerformanceTelemetry.Stage("stage")) { }
                    if (i % 50 == 0) { var rows = collector.DrainOperations(); collector.ReleaseOperations(rows.Count, false); collector.DrainAggregates(); }
                }
                timer.Stop();
                if (round > 0) { times.Add(timer.Elapsed.TotalMilliseconds); allocations.Add(GC.GetTotalAllocatedBytes(true) - before); }
            }
            times.Sort(); allocations.Sort();
            output.WriteLine($"enabled={enabled}: 10000 operations x5 stages, median={times[2]:F2}ms p95={times[4]:F2}ms allocations={allocations[2]}B peak={Process.GetCurrentProcess().PeakWorkingSet64}B");
        }
    }

    [Fact]
    public async Task ProtocolTelemetryBenchmark()
    {
        if (Environment.GetEnvironmentVariable("MAILSERVER_PERFORMANCE_BENCHMARK") != "1") return;
        using var wire = new MemoryStream();
        wire.Write("Subject: benchmark\r\n\r\n"u8);
        var line = System.Text.Encoding.ASCII.GetBytes(new string('a', 76) + "\r\n");
        while (wire.Length < 10 * 1024 * 1024) wire.Write(line);
        wire.Write(".\r\n"u8);
        var bytes = wire.ToArray();
        foreach (var enabled in new[] { false, true })
        {
            using var collector = new PerformanceCollector { Settings = new() { Enabled = enabled } };
            var times = new List<double>(); var allocations = new List<long>();
            for (var round = 0; round < 6; round++)
            {
                var before = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
                await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
                {
                    using var operation = collector.Start("smtp", "DATA");
                    await using var connection = new MailServer.Smtp.SmtpConnection(new MemoryStream(bytes), TimeSpan.FromSeconds(60), bytes.Length + 1024);
                    await using var content = await connection.ReadDataAsync(CancellationToken.None);
                    Assert.True(content.Length > 10 * 1024 * 1024);
                }));
                timer.Stop();
                if (round > 0) { times.Add(timer.Elapsed.TotalMilliseconds); allocations.Add(GC.GetTotalAllocatedBytes(true) - before); }
                var rows = collector.DrainOperations(); collector.ReleaseOperations(rows.Count, false); collector.DrainAggregates();
            }
            times.Sort(); allocations.Sort();
            output.WriteLine($"SMTP enabled={enabled}: 10MiB x8, median={times[2]:F2}ms p95={times[4]:F2}ms allocations={allocations[2]}B peak={Process.GetCurrentProcess().PeakWorkingSet64}B");
        }
    }
}
