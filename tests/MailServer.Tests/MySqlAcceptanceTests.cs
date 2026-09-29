using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailServer.Tests;

public sealed class MySqlAcceptanceTests
{
    [Fact]
    public async Task MySqlMigrationVersionContentionAndTwoWorkerLeases()
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) return; // Opt-in: the wrapper script requires this value.
        if (!connection.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("MySQL acceptance tests require an isolated mailserver_acceptance database.");
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-mysql-acceptance-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "MySql",
            ["ConnectionStrings:MailDatabase"] = connection,
            ["Storage:RootPath"] = Path.Combine(root, "messages"),
            ["SecurityAssets:RootPath"] = Path.Combine(root, "security")
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddMailServerInfrastructure(configuration, "worker");
        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            Assert.True(await db.ConfigurationVersions.AnyAsync(x => x.Scope == ConfigurationScopes.Routing));
        }
        long before;
        await using (var db = await factory.CreateDbContextAsync())
            before = await db.ConfigurationVersions.Where(x => x.Scope == ConfigurationScopes.Rules)
                .Select(x => x.Version).SingleAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = await factory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Rules, CancellationToken.None);
            await transaction.CommitAsync();
        }));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(before + 8, await db.ConfigurationVersions.Where(x => x.Scope == ConfigurationScopes.Rules)
                .Select(x => x.Version).SingleAsync());
            var message = new StoredMessage { ContentKey = Guid.NewGuid().ToString("N"), EnvelopeFrom = "sender@example.test" };
            db.Messages.Add(message);
            for (var index = 0; index < 4; index++)
                db.DeliveryJobs.Add(new DeliveryJob
                {
                    Message = message, Sender = "sender@example.test",
                    Recipient = $"recipient{index}@outside.test",
                    NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                });
            await db.SaveChangesAsync();
        }
        var queue = provider.GetRequiredService<IDeliveryQueue>();
        var first = queue.LeaseAsync("acceptance-a", 2, TimeSpan.FromMinutes(1), CancellationToken.None);
        var second = queue.LeaseAsync("acceptance-b", 2, TimeSpan.FromMinutes(1), CancellationToken.None);
        await Task.WhenAll(first, second);
        var leased = (await first).Concat(await second).ToArray();
        Assert.Equal(4, leased.Length);
        Assert.Equal(4, leased.Select(x => x.Id).Distinct().Count());

        Guid mailboxId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = $"search-{Guid.NewGuid():N}.example.test" };
            var account = new MailAccount { Domain = domain, LocalPart = "alice", PasswordHash = "unused" };
            var mailbox = new Mailbox { Account = account, Name = "INBOX" };
            db.AddRange(domain, account, mailbox);
            for (var uid = 1; uid <= 3; uid++)
                db.MailboxMessages.Add(new MailboxMessage
                {
                    Mailbox = mailbox,
                    Message = new StoredMessage { ContentKey = Guid.NewGuid().ToString("N"), EnvelopeFrom = "sender@example.test" },
                    Uid = uid,
                    Flags = uid switch { 1 => "\\SeenOther", 2 => "\\Seen", _ => "" }
                });
            await db.SaveChangesAsync();
            mailboxId = mailbox.Id;
        }
        var mailboxService = new MailboxService(factory, null!, null!, null!);
        Assert.Equal([2], await mailboxService.SearchAsync(mailboxId, "SEEN", false, CancellationToken.None));
        Assert.Equal([1, 3], await mailboxService.SearchAsync(mailboxId, "UNSEEN", true, CancellationToken.None));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var middle = await db.MailboxMessages.SingleAsync(x => x.MailboxId == mailboxId && x.Uid == 2);
            middle.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        await using (var snapshot = await mailboxService.CreateFetchSnapshotAsync(
            mailboxId, "3,1", true, CancellationToken.None))
        {
            var items = new List<MailboxItem>();
            await foreach (var item in snapshot.ReadAsync(CancellationToken.None)) items.Add(item);
            Assert.Equal([(1L, 1L), (3L, 2L)], items.Select(x => (x.Uid, x.Sequence)).ToArray());
        }
        var administration = provider.GetRequiredService<AdminApplicationService>();
        Assert.Equal(7, (await administration.GetOverviewTrafficAsync(7, CancellationToken.None)).Buckets.Count);
        Assert.True((await administration.GetDashboardAsync(CancellationToken.None)).Domains >= 1);
    }
}
