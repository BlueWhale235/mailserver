using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace MailServer.Tests;

public sealed class EfficiencyBenchmarks(ITestOutputHelper output)
{
    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    [Trait("Category", "Performance")]
    public async Task QueueAndMailboxSummary(int mailboxSize)
    {
        if (Environment.GetEnvironmentVariable("MAILSERVER_EFFICIENCY_BENCHMARK") != "1") return;
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        var path = Path.Combine(Path.GetTempPath(), $"mailserver-efficiency-bench-{Guid.NewGuid():N}.db");
        var counter = new Counter();
        var factory = new PooledDbContextFactory<MailDbContext>(new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(counter).Options);
        try
        {
            Guid accountId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                var mailbox = new Mailbox { Name = "INBOX", Account = new MailAccount
                { LocalPart = "bench", PasswordHash = "unused", Domain = new MailDomain { Name = "bench.example" } } };
                var stored = new StoredMessage { ContentKey = "bench", EnvelopeFrom = "a@bench.example", Size = 1 };
                db.AddRange(mailbox, stored);
                db.DeliveryJobs.Add(new DeliveryJob { Message = stored, Sender = "a@bench.example", Recipient = "b@bench.example" });
                await db.SaveChangesAsync();
                accountId = mailbox.AccountId;
                var ticks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {mailboxSize})
                    INSERT INTO mailbox_messages (Id, MailboxId, MessageId, Uid, Flags, ModSeq, InternalDate)
                    SELECT lower(hex(randomblob(16))), {mailbox.Id}, {stored.Id}, n,
                    CASE WHEN n % 2 = 0 THEN '\Seen' ELSE '' END, 1, {ticks} FROM seq
                    """);
            }
            var admin = new AdminApplicationService(factory, null!, null!, null!, null!, null!, null!, null!, null!);
            var settings = new SystemSettingsService(factory, Options.Create(new DeliveryOptions()),
                Options.Create(new MailboxDefaultsOptions()), Options.Create(new CertificatesOptions()));
            var mailboxService = new MailboxService(factory, null!, settings);
            await Measure("queue-summary", () => admin.GetDeliveryQueueSummaryAsync(CancellationToken.None));
            await Measure($"mailbox-summary-{mailboxSize}", async () =>
            {
                var result = await mailboxService.SelectAsync(accountId, "INBOX", CancellationToken.None);
                Assert.Equal(mailboxSize, result!.Exists);
                Assert.Equal(mailboxSize / 2, result.Unseen);
            });

            async Task Measure(string name, Func<Task> action)
            {
                await action();
                var elapsed = new List<double>();
                var allocations = new List<long>();
                var reads = new List<int>();
                var collections = new int[3];
                for (var iteration = 0; iteration < 20; iteration++)
                {
                    counter.Reads = 0;
                    var allocated = GC.GetTotalAllocatedBytes(true);
                    var before = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                    var timer = Stopwatch.StartNew();
                    await action();
                    timer.Stop();
                    elapsed.Add(timer.Elapsed.TotalMilliseconds);
                    allocations.Add(GC.GetTotalAllocatedBytes(true) - allocated);
                    reads.Add(counter.Reads);
                    for (var generation = 0; generation < 3; generation++)
                        collections[generation] += GC.CollectionCount(generation) - before[generation];
                }
                elapsed.Sort(); allocations.Sort(); reads.Sort();
                output.WriteLine("EFFICIENCY_BENCHMARK:" + JsonSerializer.Serialize(new
                {
                    name, samples = elapsed.Count, medianMs = elapsed[elapsed.Count / 2],
                    p95Ms = elapsed[(int)Math.Ceiling(elapsed.Count * .95) - 1],
                    allocatedBytes = allocations[allocations.Count / 2], sqlReads = reads[reads.Count / 2],
                    gc = collections, processWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64
                }));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class Counter : DbCommandInterceptor
    {
        internal int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Reads++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
