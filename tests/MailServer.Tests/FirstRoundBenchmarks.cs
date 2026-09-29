using System.Diagnostics;
using System.Data.Common;
using System.Text;
using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.Smtp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace MailServer.Tests;

// Run explicitly with --filter FullyQualifiedName~FirstRoundBenchmarks.
public sealed class FirstRoundBenchmarks(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(1, 8)]
    [InlineData(10, 8)]
    public async Task SmtpDataThroughput(int mebibytes, int connections)
    {
        var line = Encoding.ASCII.GetBytes(new string('a', 76) + "\r\n");
        using var wire = new MemoryStream();
        wire.Write("Subject: benchmark\r\n\r\n"u8);
        while (wire.Length < mebibytes * 1024L * 1024) wire.Write(line);
        wire.Write(".\r\n"u8);
        var bytes = wire.ToArray();
        var times = new List<double>();
        var allocations = new List<long>();
        var peak = 0L;
        for (var round = 0; round < 6; round++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, connections).Select(async _ =>
            {
                await using var connection = new SmtpConnection(new MemoryStream(bytes, writable: false),
                    TimeSpan.FromSeconds(60), bytes.Length + 1024);
                await using var message = await connection.ReadDataAsync(CancellationToken.None);
                Assert.InRange(message.Length, mebibytes * 1024L * 1024, bytes.Length);
            }));
            timer.Stop();
            if (round == 0) continue;
            times.Add(timer.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetTotalAllocatedBytes(true) - allocatedBefore);
            peak = Math.Max(peak, Process.GetCurrentProcess().PeakWorkingSet64);
        }
        times.Sort();
        allocations.Sort();
        output.WriteLine($"SMTP {mebibytes} MiB x{connections}: median={times[2]:F1} ms, p95={times[4]:F1} ms, " +
            $"median allocated={allocations[2]:N0} bytes, peak working set={peak:N0} bytes");
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public async Task UidFetchSingleLastMessage(int mailboxSize)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mailserver-fetch-bench-{Guid.NewGuid():N}.db");
        var counter = new QueryCounter();
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        var factory = new PooledDbContextFactory<MailDbContext>(new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(counter).Options);
        try
        {
            Guid mailboxId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                var domain = new MailDomain { Name = "benchmark.test" };
                var account = new MailAccount { Domain = domain, LocalPart = "bench", PasswordHash = "unused" };
                var mailbox = new Mailbox { Account = account, Name = "INBOX" };
                var stored = new StoredMessage { ContentKey = "benchmark", EnvelopeFrom = "bench@example.test", Size = 1 };
                db.AddRange(domain, account, mailbox, stored);
                await db.SaveChangesAsync();
                mailboxId = mailbox.Id;
                var ticks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {mailboxSize})
                    INSERT INTO mailbox_messages (Id, MailboxId, MessageId, Uid, Flags, ModSeq, InternalDate)
                    SELECT lower(hex(randomblob(16))), {mailbox.Id}, {stored.Id}, n, '', 1, {ticks} FROM seq
                    """);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE mailbox_messages SET DeletedAt = {ticks} WHERE MailboxId = {mailbox.Id} AND Uid = {mailboxSize / 2}
                    """);
            }
            var settings = new SystemSettingsService(factory, Options.Create(new DeliveryOptions()),
                Options.Create(new MailboxDefaultsOptions()), Options.Create(new CertificatesOptions()));
            var store = new FileMessageContentStore(Options.Create(new StorageOptions
                { RootPath = Path.GetTempPath(), MaximumMessageBytes = 1024 }), factory);
            var service = new MailboxService(factory, store, settings);
            var times = new List<double>();
            var reads = new List<int>();
            for (var round = 0; round < 6; round++)
            {
                counter.Reset();
                var timer = Stopwatch.StartNew();
                await using var snapshot = await service.CreateFetchSnapshotAsync(mailboxId,
                    mailboxSize.ToString(), true, CancellationToken.None);
                var items = new List<MailboxItem>();
                await foreach (var item in snapshot.ReadAsync(CancellationToken.None)) items.Add(item);
                timer.Stop();
                Assert.Single(items);
                Assert.Equal(mailboxSize - 1, items[0].Sequence);
                if (round == 0) continue;
                times.Add(timer.Elapsed.TotalMilliseconds);
                reads.Add(counter.Count);
            }
            times.Sort();
            reads.Sort();
            Assert.InRange(reads[2], 1, 4);
            output.WriteLine($"UID FETCH {mailboxSize:N0} messages: median={times[2]:F1} ms, " +
                $"p95={times[4]:F1} ms, median SQL reads={reads[2]}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task OverviewTrafficQueries(int days)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mailserver-traffic-bench-{Guid.NewGuid():N}.db");
        var counter = new QueryCounter();
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        var factory = new PooledDbContextFactory<MailDbContext>(new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(counter).Options);
        try
        {
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                var message = new StoredMessage
                {
                    ContentKey = "traffic-benchmark", EnvelopeFrom = "sender@example.test", Size = 1,
                    ReceivedAt = DateTimeOffset.UtcNow.AddHours(-1)
                };
                var job = new DeliveryJob
                {
                    Message = message, Sender = "sender@example.test", Recipient = "recipient@example.test"
                };
                db.DeliveryJobs.Add(job);
                db.DeliveryAttempts.Add(new DeliveryAttempt
                {
                    DeliveryJob = job, Succeeded = true, DurationMilliseconds = 120,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
                });
                await db.SaveChangesAsync();
            }
            var service = new AdminApplicationService(factory, null!, null!, null!, null!, null!, null!, null!, null!);
            var times = new List<double>();
            var reads = new List<int>();
            for (var round = 0; round < 6; round++)
            {
                counter.Reset();
                var timer = Stopwatch.StartNew();
                var result = await service.GetOverviewTrafficAsync(days, CancellationToken.None);
                timer.Stop();
                Assert.Equal(1, result.Buckets.Sum(x => x.StoredMessages));
                if (round == 0) continue;
                times.Add(timer.Elapsed.TotalMilliseconds);
                reads.Add(counter.Count);
            }
            times.Sort();
            reads.Sort();
            output.WriteLine($"Traffic {days} day(s): median={times[2]:F1} ms, p95={times[4]:F1} ms, " +
                $"median SQL reads={reads[2]}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Reset() => Interlocked.Exchange(ref _count, 0);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
