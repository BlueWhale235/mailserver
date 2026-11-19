using System.Text.Json;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailServer.Tests;

public sealed class SmallMemoryMaintenanceTests
{
    [Fact]
    public async Task SqliteFlagSearchFiltersInDatabaseWithoutRenumberingSequences()
    {
        SqliteNative.Initialize();
        var root = Path.Combine(Path.GetTempPath(), $"mail-flag-search-{MailServer.Domain.SnowflakeIds.NewId()}");
        Directory.CreateDirectory(root);
        try
        {
            var options = new DbContextOptionsBuilder<MailDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "mail.db")}").Options;
            var factory = new TestMailDbContextFactory(options);
            long mailboxId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                var domain = new MailDomain { Name = "example.test" };
                var account = new MailAccount { Domain = domain, LocalPart = "alice", PasswordHash = "unused" };
                var mailbox = new Mailbox { Account = account, Name = "INBOX" };
                var day = new DateTime(2026, 7, 1, 12, 0, 0);
                db.AddRange(domain, account, mailbox);
                for (var uid = 1; uid <= 4; uid++)
                {
                    var localDate = day.AddDays(uid switch { 1 => -1, 3 => 1, _ => 0 });
                    db.MailboxMessages.Add(new MailboxMessage
                    {
                        Mailbox = mailbox, Message = new StoredMessage
                        {
                            ContentKey = MailServer.Domain.SnowflakeIds.NewId().ToString(), EnvelopeFrom = "sender@example.test"
                        },
                        Uid = uid,
                        Flags = uid switch { 1 => "\\SeenOther", 2 => "\\sEeN \\Flagged", 3 => "\\Deleted", _ => "" },
                        InternalDate = new DateTimeOffset(localDate, TimeZoneInfo.Local.GetUtcOffset(localDate))
                    });
                }
                await db.SaveChangesAsync();
                mailboxId = mailbox.Id;
            }
            var service = new MailboxService(factory, null!, null!, null!);
            Assert.Equal([2], await service.SearchAsync(mailboxId, "SEEN", false, CancellationToken.None));
            Assert.Equal([1, 3, 4], await service.SearchAsync(mailboxId, "UNSEEN", false, CancellationToken.None));
            Assert.Equal([2], await service.SearchAsync(mailboxId, "FLAGGED", true, CancellationToken.None));
            Assert.Equal([1, 2, 4], await service.SearchAsync(mailboxId, "UNDELETED", false, CancellationToken.None));
            Assert.Equal([3], await service.SearchAsync(mailboxId, "DELETED", true, CancellationToken.None));
            Assert.Equal([2, 4], await service.SearchAsync(mailboxId, "ON 1-Jul-2026", false, CancellationToken.None));
            Assert.Equal([2, 3, 4], await service.SearchAsync(mailboxId, "SINCE 1-Jul-2026", true, CancellationToken.None));
            Assert.Equal([1], await service.SearchAsync(mailboxId, "BEFORE 1-Jul-2026", false, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MailboxSelectionSnapshotReplaysAndRejectsOverBudgetItems()
    {
        await using var snapshot = new MailboxSelectionSnapshot(512);
        var item = new MailboxItem(MailServer.Domain.SnowflakeIds.NewId(), 1, 10, string.Empty, 1,
            DateTimeOffset.UtcNow, 20, "content-key");
        await snapshot.AddAsync(item, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshot.AddAsync(
            item with { ContentKey = new string('x', 1024) }, CancellationToken.None));
        await snapshot.SealAsync(CancellationToken.None);
        var first = new List<MailboxItem>();
        await foreach (var value in snapshot.ReadAsync(CancellationToken.None)) first.Add(value);
        var second = new List<MailboxItem>();
        await foreach (var value in snapshot.ReadAsync(CancellationToken.None)) second.Add(value);
        Assert.Equal([item], first);
        Assert.Equal(first, second);
        Assert.Equal(1, snapshot.Count);
        await snapshot.DisposeAsync();
        Assert.False(File.Exists(snapshot.FilePath));
    }

    [Fact]
    public async Task MailboxLocksIsolateCollisionsAndReleaseUnusedEntries()
    {
        var locks = new AsyncKeyedLock<long>();
        using (var first = await locks.AcquireAsync(1, default))
        {
            using var second = await locks.AcquireAsync(257, default).AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            using var cancelled = new CancellationTokenSource();
            var blocked = locks.AcquireAsync(1, cancelled.Token).AsTask();
            Assert.False(blocked.IsCompleted);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            Assert.Equal(2, locks.Count);
        }
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task IntegrityScanResumesAndRestartsAfterCompletingCycle()
    {
        SqliteNative.Initialize();
        var root = Path.Combine(Path.GetTempPath(), $"mail-integrity-{MailServer.Domain.SnowflakeIds.NewId()}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "mail.db");
        try
        {
            var options = new DbContextOptionsBuilder<MailDbContext>()
                .UseSqlite($"Data Source={database}").Options;
            var factory = new TestMailDbContextFactory(options);
            var keys = new[] { new string('a', 64), new string('b', 64), new string('c', 64) };
            var task = new ScheduledTask { Type = "storage.integrity-check", Name = "integrity", BatchSize = 2 };
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.ScheduledTasks.Add(task);
                db.Messages.AddRange(keys.Select(key => new StoredMessage { ContentKey = key, EnvelopeFrom = "" }));
                await db.SaveChangesAsync();
            }
            var handler = new StorageIntegrityTaskHandler(factory, Options.Create(new StorageOptions { RootPath = root }));
            Assert.Equal(2, (await handler.RunAsync(task, CancellationToken.None)).ProcessedCount);
            await using (var db = await factory.CreateDbContextAsync())
            {
                using var state = JsonDocument.Parse((await db.ScheduledTasks.SingleAsync()).SettingsJson);
                Assert.Equal(keys[1], state.RootElement.GetProperty("LastKey").GetString());
                Assert.Equal(2, state.RootElement.GetProperty("Scanned").GetInt64());
            }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.RunAsync(task, canceled.Token));
            }
            await using (var db = await factory.CreateDbContextAsync())
            {
                using var state = JsonDocument.Parse((await db.ScheduledTasks.SingleAsync()).SettingsJson);
                Assert.Equal(keys[1], state.RootElement.GetProperty("LastKey").GetString());
                Assert.Single(await db.AuditRecords.ToListAsync());
            }
            // A new factory and handler simulate a restarted service reading only persisted progress.
            var restartedFactory = new TestMailDbContextFactory(options);
            var restartedHandler = new StorageIntegrityTaskHandler(restartedFactory,
                Options.Create(new StorageOptions { RootPath = root }));
            Assert.Equal(1, (await restartedHandler.RunAsync(task, CancellationToken.None)).ProcessedCount);
            await using (var db = await factory.CreateDbContextAsync())
            {
                using var state = JsonDocument.Parse((await db.ScheduledTasks.SingleAsync()).SettingsJson);
                Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("LastKey").ValueKind);
                Assert.Equal(1, state.RootElement.GetProperty("CompletedCycles").GetInt64());
                Assert.Equal(2, await db.AuditRecords.CountAsync());
            }
            Assert.Equal(2, (await handler.RunAsync(task, CancellationToken.None)).ProcessedCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
