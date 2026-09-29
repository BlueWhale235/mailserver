using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;
using Xunit;
using Xunit.Abstractions;

namespace MailServer.Tests;

// Run explicitly with MAILSERVER_DATABASE_BENCHMARK=1 and a Release test build.
public sealed class DatabaseStageBenchmarks(ITestOutputHelper output)
{
    [Fact]
    public async Task SmtpLocalDeliveryAndSqliteConnectionPool()
    {
        if (Environment.GetEnvironmentVariable("MAILSERVER_DATABASE_BENCHMARK") != "1") return;
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-db-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "mail.db");
            await MeasureSqlitePoolAsync(database);
            using var collector = new PerformanceCollector { Settings = new() { SampleRate = 1 } };
            var factory = new PooledDbContextFactory<MailDbContext>(new DbContextOptionsBuilder<MailDbContext>()
                .UseSqlite($"Data Source={database};Pooling=True")
                .AddInterceptors(new PerformanceSqlInterceptor(collector), new PerformanceConnectionInterceptor(),
                    new PerformanceTransactionInterceptor()).Options);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                var domain = new MailDomain { Name = "example.test" };
                var account = new MailAccount { Domain = domain, LocalPart = "alice", PasswordHash = "unused",
                    QuotaBytes = long.MaxValue / 4 };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "INBOX" });
                await db.SaveChangesAsync();
            }
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = Path.Combine(root, "messages"), MaximumMessageBytes = 2 * 1024 * 1024 }),
                factory);
            var service = new InboundMessageService(factory, store,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<InboundMessageService>.Instance);
            var payload = Encoding.ASCII.GetBytes("From: sender@outside.test\r\nTo: alice@example.test\r\nSubject: benchmark\r\n\r\n" +
                new string('x', 1024 * 1024));
            var durations = new List<double>();
            var allocations = new List<long>();
            var saves = new List<double>();
            var detects = new List<double>();
            var prepares = new List<double>();
            var persists = new List<double>();
            var sqlCounts = new List<double>();
            var sqlDurations = new List<double>();
            var openCounts = new List<double>();
            var openDurations = new List<double>();
            var peak = 0L;
            for (var round = 0; round < 6; round++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var before = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                await using var message = new MemoryStream(payload, writable: false);
                InboundAcceptance result;
                using (collector.Start("benchmark", "smtp-local"))
                    result = await service.AcceptAsync(new InboundEnvelope("sender@outside.test", ["alice@example.test"],
                        null, "127.0.0.1", "client.outside.test"), message, CancellationToken.None);
                timer.Stop();
                Assert.Equal(1, result.LocalDeliveries);
                var operations = collector.DrainOperations();
                var trace = Assert.Single(operations);
                var stages = JsonSerializer.Deserialize<PerformanceStage[]>(trace.DetailsJson)!;
                collector.ReleaseOperations(operations.Count, false);
                if (round == 0) continue;
                durations.Add(timer.Elapsed.TotalMilliseconds);
                allocations.Add(GC.GetTotalAllocatedBytes(true) - before);
                saves.Add(stages.Where(x => x.Name == "database.save-changes").Sum(x => x.DurationMilliseconds));
                detects.Add(stages.Where(x => x.Name == "database.detect-changes").Sum(x => x.DurationMilliseconds));
                prepares.Add(stages.Where(x => x.Name == "database.prepare-entries").Sum(x => x.DurationMilliseconds));
                persists.Add(stages.Where(x => x.Name == "database.persist").Sum(x => x.DurationMilliseconds));
                sqlCounts.Add(trace.SqlCount);
                sqlDurations.Add(trace.SqlMilliseconds);
                openCounts.Add(stages.Count(x => x.Name == "database.open"));
                openDurations.Add(stages.Where(x => x.Name == "database.open").Sum(x => x.DurationMilliseconds));
                peak = Math.Max(peak, Process.GetCurrentProcess().PeakWorkingSet64);
            }
            output.WriteLine($"SMTP_LOCAL_1MIB median_ms={Median(durations):F2} p95_ms={P95(durations):F2} " +
                $"median_alloc_bytes={Median(allocations):F0} peak_working_set_bytes={peak} " +
                $"save_median_ms={Median(saves):F2} save_p95_ms={P95(saves):F2} " +
                $"detect_median_ms={Median(detects):F2} prepare_median_ms={Median(prepares):F2} " +
                $"persist_median_ms={Median(persists):F2} sql_count_median={Median(sqlCounts):F0} " +
                $"sql_median_ms={Median(sqlDurations):F2} open_count_median={Median(openCounts):F0} " +
                $"open_median_ms={Median(openDurations):F2}");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private async Task MeasureSqlitePoolAsync(string database)
    {
        foreach (var pooling in new[] { false, true })
        {
            var durations = new List<double>();
            var allocations = new List<long>();
            var connectionString = $"Data Source={database};Pooling={pooling}";
            for (var round = 0; round < 6; round++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var before = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                for (var i = 0; i < 100; i++)
                {
                    await using var connection = new SqliteConnection(connectionString);
                    await connection.OpenAsync();
                    await connection.CloseAsync();
                }
                timer.Stop();
                if (round == 0) continue;
                durations.Add(timer.Elapsed.TotalMilliseconds);
                allocations.Add(GC.GetTotalAllocatedBytes(true) - before);
            }
            output.WriteLine($"SQLITE_POOL pooling={pooling} opens=100 median_ms={Median(durations):F2} " +
                $"p95_ms={P95(durations):F2} median_alloc_bytes={Median(allocations):F0}");
        }
    }

    [Fact]
    public async Task MySqlConnectionPool()
    {
        if (Environment.GetEnvironmentVariable("MAILSERVER_DATABASE_BENCHMARK") != "1") return;
        var connectionString = Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            output.WriteLine("MYSQL_POOL not verified: MAILSERVER_TEST_MYSQL_CONNECTION is missing");
            return;
        }
        if (!connectionString.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MySQL benchmarks require an isolated mailserver_acceptance database.");
        foreach (var pooling in new[] { false, true })
        {
            var builder = new MySqlConnectionStringBuilder(connectionString) { Pooling = pooling };
            var durations = new List<double>();
            var allocations = new List<long>();
            for (var round = 0; round < 6; round++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var before = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                for (var i = 0; i < 20; i++)
                {
                    await using var connection = new MySqlConnection(builder.ConnectionString);
                    await connection.OpenAsync();
                    await connection.CloseAsync();
                }
                timer.Stop();
                if (round == 0) continue;
                durations.Add(timer.Elapsed.TotalMilliseconds);
                allocations.Add(GC.GetTotalAllocatedBytes(true) - before);
            }
            output.WriteLine($"MYSQL_POOL pooling={pooling} opens=20 median_ms={Median(durations):F2} " +
                $"p95_ms={P95(durations):F2} median_alloc_bytes={Median(allocations):F0}");
        }
        await MeasureMySqlSmtpAsync(connectionString);
    }

    private async Task MeasureMySqlSmtpAsync(string connectionString)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-mysql-db-bench-{Guid.NewGuid():N}");
        var domainName = $"bench-{Guid.NewGuid():N}.example.test";
        try
        {
            using var collector = new PerformanceCollector { Settings = new() { SampleRate = 1 } };
            var factory = new PooledDbContextFactory<MailDbContext>(new DbContextOptionsBuilder<MailDbContext>()
                .UseMySQL(connectionString, options => options.MigrationsAssembly("MailServer.Migrations.MySql"))
                .AddInterceptors(new PerformanceSqlInterceptor(collector), new PerformanceConnectionInterceptor(),
                    new PerformanceTransactionInterceptor()).Options);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                var domain = new MailDomain { Name = domainName };
                var account = new MailAccount { Domain = domain, LocalPart = "alice", PasswordHash = "unused",
                    QuotaBytes = long.MaxValue / 4 };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "INBOX" });
                await db.SaveChangesAsync();
            }
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = Path.Combine(root, "messages"), MaximumMessageBytes = 2 * 1024 * 1024 }),
                factory);
            var service = new InboundMessageService(factory, store,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<InboundMessageService>.Instance);
            var recipient = $"alice@{domainName}";
            var payload = Encoding.ASCII.GetBytes($"From: sender@outside.test\r\nTo: {recipient}\r\nSubject: benchmark\r\n\r\n" +
                new string('x', 1024 * 1024));
            var durations = new List<double>();
            var allocations = new List<long>();
            var saves = new List<double>();
            var detects = new List<double>();
            var prepares = new List<double>();
            var persists = new List<double>();
            var sqlCounts = new List<double>();
            var openCounts = new List<double>();
            var openDurations = new List<double>();
            var peak = 0L;
            for (var round = 0; round < 6; round++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var before = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                await using var message = new MemoryStream(payload, writable: false);
                InboundAcceptance result;
                using (collector.Start("benchmark", "mysql-smtp-local"))
                    result = await service.AcceptAsync(new InboundEnvelope("sender@outside.test", [recipient],
                        null, "127.0.0.1", "client.outside.test"), message, CancellationToken.None);
                timer.Stop();
                Assert.Equal(1, result.LocalDeliveries);
                var operations = collector.DrainOperations();
                var trace = Assert.Single(operations);
                var stages = JsonSerializer.Deserialize<PerformanceStage[]>(trace.DetailsJson)!;
                collector.ReleaseOperations(operations.Count, false);
                if (round == 0) continue;
                durations.Add(timer.Elapsed.TotalMilliseconds);
                allocations.Add(GC.GetTotalAllocatedBytes(true) - before);
                saves.Add(stages.Where(x => x.Name == "database.save-changes").Sum(x => x.DurationMilliseconds));
                detects.Add(stages.Where(x => x.Name == "database.detect-changes").Sum(x => x.DurationMilliseconds));
                prepares.Add(stages.Where(x => x.Name == "database.prepare-entries").Sum(x => x.DurationMilliseconds));
                persists.Add(stages.Where(x => x.Name == "database.persist").Sum(x => x.DurationMilliseconds));
                sqlCounts.Add(trace.SqlCount);
                openCounts.Add(stages.Count(x => x.Name == "database.open"));
                openDurations.Add(stages.Where(x => x.Name == "database.open").Sum(x => x.DurationMilliseconds));
                peak = Math.Max(peak, Process.GetCurrentProcess().PeakWorkingSet64);
            }
            output.WriteLine($"MYSQL_SMTP_LOCAL_1MIB median_ms={Median(durations):F2} p95_ms={P95(durations):F2} " +
                $"median_alloc_bytes={Median(allocations):F0} peak_working_set_bytes={peak} " +
                $"save_median_ms={Median(saves):F2} detect_median_ms={Median(detects):F2} " +
                $"prepare_median_ms={Median(prepares):F2} persist_median_ms={Median(persists):F2} " +
                $"sql_count_median={Median(sqlCounts):F0} open_count_median={Median(openCounts):F0} " +
                $"open_median_ms={Median(openDurations):F2}");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static double Median<T>(IEnumerable<T> values) where T : struct, IConvertible =>
        values.Select(x => Convert.ToDouble(x)).Order().ElementAt(2);
    private static double P95(List<double> values) => values.Max();
}
