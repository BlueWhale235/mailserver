using System.Text.Json;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MailServer.Tests;

public sealed class DatabaseSaveOptimizationTests
{
    [Fact]
    public async Task InboundGraphScalarChangesAuditAndRoutingVersionRemainCorrect()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var collector = new PerformanceCollector { Settings = new() { SampleRate = 1 } };
        var options = new DbContextOptionsBuilder<MailDbContext>().UseSqlite(connection).Options;
        await using var db = new MailDbContext(options, TestLocalLogs.Create());
        await db.Database.EnsureCreatedAsync();
        var domain = new MailDomain { Name = "example.test" };
        var account = new MailAccount { Domain = domain, LocalPart = "alice", PasswordHash = "unused" };
        var mailbox = new Mailbox { Account = account, Name = "INBOX" };
        db.AddRange(domain, account, mailbox);
        await db.SaveChangesAsync();

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        account.UsedBytes = 123;
        mailbox.UidNext++;
        var message = new StoredMessage { ContentKey = "content", EnvelopeFrom = "sender@outside.test", Size = 123 };
        db.Messages.Add(message);
        db.MessageRecipients.Add(new StoredMessageRecipient { Message = message, Address = "alice@example.test" });
        db.MailboxMessages.Add(new MailboxMessage { Mailbox = mailbox, Message = message, Uid = 1 });
        db.AuditRecords.Add(new AuditRecord { Actor = "test", Action = "accept", Resource = "message" });
        using (collector.Start("smtp", "save-graph"))
            await db.SaveChangesAsync();
        Assert.False(db.ChangeTracker.AutoDetectChangesEnabled);
        var traces = collector.DrainOperations();
        var trace = Assert.Single(traces);
        var stages = JsonSerializer.Deserialize<PerformanceStage[]>(trace.DetailsJson)!;
        Assert.Contains(stages, x => x.Name == "database.detect-changes");
        Assert.Contains(stages, x => x.Name == "database.prepare-entries");
        Assert.Contains(stages, x => x.Name == "database.persist");
        Assert.Contains(stages, x => x.Name == "database.save-changes");
        collector.ReleaseOperations(traces.Count, false);

        db.ChangeTracker.Clear();
        Assert.Equal(123, await db.Accounts.Where(x => x.Id == account.Id).Select(x => x.UsedBytes).SingleAsync());
        Assert.Equal(1, await db.MailboxMessages.CountAsync());
        Assert.Equal(1, await db.MessageRecipients.CountAsync());
        Assert.Equal(1, await db.AuditRecords.CountAsync());

        db.ChangeTracker.AutoDetectChangesEnabled = true;
        var routed = await db.Domains.SingleAsync(x => x.Id == domain.Id);
        routed.Name = "renamed.test";
        await db.SaveChangesAsync();
        Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
        Assert.Equal("renamed.test", (await db.Domains.SingleAsync()).Name);
        Assert.True(await db.ConfigurationVersions.AnyAsync(x => x.Scope == ConfigurationScopes.Routing && x.Version >= 2));
    }

    [Fact]
    public async Task FailedAndCancelledSavesRestoreAutoDetection()
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MailDbContext>().UseSqlite(connection).Options;
        await using var db = new MailDbContext(options, TestLocalLogs.Create());
        await db.Database.EnsureCreatedAsync();
        db.Messages.Add(new StoredMessage { ContentKey = null!, EnvelopeFrom = "sender@outside.test" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
        db.ChangeTracker.Clear();

        db.Messages.Add(new StoredMessage { ContentKey = "content", EnvelopeFrom = "sender@outside.test" });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.SaveChangesAsync(cancelled.Token));
        Assert.True(db.ChangeTracker.AutoDetectChangesEnabled);
        await db.SaveChangesAsync();
        Assert.Equal(1, await db.Messages.CountAsync());
    }
}
