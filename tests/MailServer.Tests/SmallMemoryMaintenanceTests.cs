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
    public async Task MailboxSelectionSnapshotReplaysAndRejectsOverBudgetItems()
    {
        await using var snapshot = new MailboxSelectionSnapshot(512);
        var item = new MailboxItem(Guid.NewGuid(), 1, 10, string.Empty, 1,
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
    public void MailboxLockStripesRemainBoundedAndShareCollidingPermit()
    {
        var field = typeof(MailboxService).GetField("MailboxLocks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var stripes = Assert.IsType<SemaphoreSlim[]>(field.GetValue(null));
        Assert.Equal(256, stripes.Length);
        var first = Guid.NewGuid();
        Guid second;
        do second = Guid.NewGuid(); while (first == second || (first.GetHashCode() & 255) != (second.GetHashCode() & 255));
        Assert.Same(stripes[first.GetHashCode() & 255], stripes[second.GetHashCode() & 255]);
    }

    [Fact]
    public async Task IntegrityScanResumesAndRestartsAfterCompletingCycle()
    {
        SqliteNative.Initialize();
        var root = Path.Combine(Path.GetTempPath(), $"mail-integrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "mail.db");
        try
        {
            var options = new DbContextOptionsBuilder<MailDbContext>()
                .UseSqlite($"Data Source={database}").Options;
            var factory = new PooledDbContextFactory<MailDbContext>(options);
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
            Assert.Equal(1, (await handler.RunAsync(task, CancellationToken.None)).ProcessedCount);
            await using (var db = await factory.CreateDbContextAsync())
            {
                using var state = JsonDocument.Parse((await db.ScheduledTasks.SingleAsync()).SettingsJson);
                Assert.Equal(JsonValueKind.Null, state.RootElement.GetProperty("LastKey").ValueKind);
                Assert.Equal(1, state.RootElement.GetProperty("CompletedCycles").GetInt64());
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
