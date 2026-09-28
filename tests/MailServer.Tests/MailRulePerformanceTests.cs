using System.Data.Common;
using System.Diagnostics;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace MailServer.Tests;

public sealed class MailRulePerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [Trait("Category", "Performance")]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public async Task BoundedRuleSnapshotWorkload(int ruleCount)
    {
        SqliteNative.Initialize();
        var path = Path.Combine(Path.GetTempPath(), $"mail-rule-bench-{Guid.NewGuid():N}.db");
        try
        {
            var counter = new QueryCounter();
            var options = new DbContextOptionsBuilder<MailDbContext>()
                .UseSqlite($"Data Source={path}").AddInterceptors(counter).Options;
            var factory = new PooledDbContextFactory<MailDbContext>(options);
            Guid domainId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                var domain = new MailDomain { Name = "bench.test" };
                db.Domains.Add(domain);
                await db.SaveChangesAsync();
                domainId = domain.Id;
                for (var i = 0; i < ruleCount; i++)
                {
                    db.MailRules.Add(new MailRule
                    {
                        DomainId = domainId,
                        Name = $"rule-{i}",
                        Direction = MailRuleDirection.Incoming,
                        MatchMode = MailRuleMatchMode.All,
                        TargetMailboxName = "INBOX",
                        Priority = i,
                        Conditions =
                        [
                            new MailRuleCondition
                            {
                                Field = i == ruleCount / 2 ? MailRuleField.Subject : MailRuleField.SenderDomain,
                                Operator = MailRuleOperator.EqualsAny,
                                ValuesJson = i == ruleCount / 2 ? "[\"special\"]" : $"[\"sender-{i}.test\"]",
                                Position = 0
                            }
                        ]
                    });
                }
                await db.SaveChangesAsync();
            }
            using var cache = new ConfigurationSnapshotCache(Options.Create(new ConfigurationCacheOptions
                { MaximumEntryBytes = 16 * 1024 * 1024, SizeLimitBytes = 32 * 1024 * 1024 }),
                NullLogger<ConfigurationSnapshotCache>.Instance);
            var rules = new MailRuleService(factory, NullLogger<MailRuleService>.Instance, cache);
            var warm = new MailRuleFacts([], ["sender-0.test"], [], [], "ordinary", "", [], 1,
                DateTimeOffset.UtcNow, false);
            Assert.NotNull(await rules.MatchAsync(domainId, MailRuleDirection.Incoming, warm, CancellationToken.None));
            counter.Reset();
            foreach (var (name, domain) in new[]
                     { ("early", "sender-0.test"), ("late", $"sender-{ruleCount - 1}.test"),
                         ("miss", "no-match.test") })
            {
                var durations = new double[50];
                var allocatedBefore = GC.GetTotalAllocatedBytes(false);
                for (var i = 0; i < durations.Length; i++)
                {
                    var facts = new MailRuleFacts([], [domain], [], [], "ordinary", "", [], 1,
                        DateTimeOffset.UtcNow, false);
                    var start = Stopwatch.GetTimestamp();
                    using (MailRuleService.BeginEvaluationScope())
                    {
                        var match = await rules.MatchAsync(domainId, MailRuleDirection.Incoming, facts,
                            CancellationToken.None);
                        Assert.Equal(name != "miss", match is not null);
                    }
                    durations[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                Array.Sort(durations);
                var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
                output.WriteLine($"rules={ruleCount} case={name} iterations=50 p95_ms={durations[47]:F3} "
                    + $"allocated_bytes={allocated} sql={counter.Count} cache_bytes={cache.EstimatedBytes}");
            }
            Assert.Equal(0, counter.Count);
            Assert.InRange(cache.EstimatedBytes, 1, cache.Options.MaximumEntryBytes);
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
