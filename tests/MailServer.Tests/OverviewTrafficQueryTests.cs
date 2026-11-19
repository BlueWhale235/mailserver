using System.Data.Common;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace MailServer.Tests;

public sealed class OverviewTrafficQueryTests
{
    [Fact]
    public async Task TrafficUsesAtMostFourQueriesAndPreservesFailureBuckets()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mailserver-traffic-query-{MailServer.Domain.SnowflakeIds.NewId()}.db");
        var counter = new QueryCounter();
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        var factory = new TestMailDbContextFactory(new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").AddInterceptors(counter).Options);
        var service = new AdminApplicationService(factory, null!, null!, null!, null!, null!, null!, null!, null!);
        try
        {
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                counter.Reset();
                var empty = await service.GetOverviewTrafficAsync(1, CancellationToken.None);
                Assert.InRange(counter.Count, 1, 4);
                Assert.All(empty.Buckets, bucket => Assert.Equal(0, bucket.StoredMessages));
                Assert.Null(empty.P95DeliveryMilliseconds);
                var message = new StoredMessage
                {
                    ContentKey = "traffic-query-test", EnvelopeFrom = "sender@example.test", Size = 1,
                    ReceivedAt = DateTimeOffset.UtcNow.AddHours(-1)
                };
                var job = new DeliveryJob
                {
                    Message = message, Sender = "sender@example.test", Recipient = "recipient@example.test"
                };
                db.DeliveryJobs.Add(job);
                db.DeliveryAttempts.AddRange(
                    new DeliveryAttempt { DeliveryJob = job, Succeeded = true, DurationMilliseconds = 120,
                        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-45) },
                    new DeliveryAttempt { DeliveryJob = job, PermanentFailure = false,
                        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30) },
                    new DeliveryAttempt { DeliveryJob = job, PermanentFailure = true,
                        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15) });
                await db.SaveChangesAsync();
            }
            counter.Reset();
            var traffic = await service.GetOverviewTrafficAsync(7, CancellationToken.None);
            Assert.InRange(counter.Count, 1, 4);
            Assert.Equal(1, traffic.Buckets.Sum(x => x.StoredMessages));
            Assert.Equal(1, traffic.Buckets.Sum(x => x.Delivered));
            Assert.Equal(1, traffic.Buckets.Sum(x => x.TemporaryFailures));
            Assert.Equal(1, traffic.Buckets.Sum(x => x.PermanentFailures));
            Assert.Equal(120, traffic.P95DeliveryMilliseconds);
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
