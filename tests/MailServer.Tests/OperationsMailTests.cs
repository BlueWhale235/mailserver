using System.Text;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailServer.Tests;

public sealed class OperationsMailTests
{
    [Fact]
    public async Task SqliteMigrationAndSearchReuseExistingMessagesCopiesAndRawContent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-operations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mail.db")}",
                ["Storage:RootPath"] = Path.Combine(root, "content"),
                ["SecurityAssets:RootPath"] = Path.Combine(root, "security")
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddMailServerInfrastructure(configuration);
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
            var contentStore = provider.GetRequiredService<IMessageContentStore>();
            var mail = provider.GetRequiredService<OperationsMailService>();
            await using (var db = await factory.CreateDbContextAsync())
                await db.Database.MigrateAsync();
            var ids = new List<Guid>();
            for (var index = 0; index < 2; index++)
            {
                await using var raw = new MemoryStream(Encoding.UTF8.GetBytes(
                    $"Message-ID: <complaint@example.test>\r\nFrom: Header <claimed@example.test>\r\nSubject: report {index}\r\n\r\nbody {index}"));
                var descriptor = await contentStore.SaveAsync(raw, CancellationToken.None);
                await using var db = await factory.CreateDbContextAsync();
                var domain = index == 0 ? new MailDomain { Name = "example.test" } : null;
                var account = index == 0 ? new MailAccount { Domain = domain!, LocalPart = "alice", PasswordHash = "unused" } : null;
                var mailbox = index == 0 ? new Mailbox { Account = account!, Name = "INBOX" } : null;
                var message = new StoredMessage
                {
                    ContentKey = descriptor.Key, EnvelopeFrom = "actual@example.test",
                    InternetMessageId = index == 0 ? "complaint@example.test" : "<complaint@example.test>",
                    InternetMessageIdHash = index == 0 ? MessageIdLookup.Hash("complaint@example.test") : null,
                    SourceType = index == 0 ? "smtp" : null, Size = descriptor.Size
                };
                db.Messages.Add(message);
                if (mailbox is not null)
                {
                    db.MailboxMessages.Add(new MailboxMessage { Mailbox = mailbox, Message = message, Uid = 1, DeletedAt = DateTimeOffset.UtcNow });
                    db.MailboxMessages.Add(new MailboxMessage { Mailbox = new Mailbox { Account = account!, Name = "Archive" }, Message = message, Uid = 1 });
                    db.MessageRecipients.Add(new StoredMessageRecipient { Message = message, Address = "alice@example.test" });
                    for (var recipientIndex = 0; recipientIndex < 60; recipientIndex++)
                        db.MessageRecipients.Add(new StoredMessageRecipient { Message = message,
                            Address = $"recipient-{recipientIndex:000}@example.test" });
                    var job = new DeliveryJob { Message = message, Sender = "actual@example.test",
                        Recipient = "forward@remote.test", Status = DeliveryStatus.Delivered };
                    db.DeliveryJobs.Add(job);
                    for (var attempt = 1; attempt <= 60; attempt++)
                        db.DeliveryAttempts.Add(new DeliveryAttempt { DeliveryJob = job, AttemptNumber = attempt });
                    db.QuarantineRecords.Add(new QuarantineRecord { Domain = domain!, Message = message,
                        Sender = "actual@example.test", RecipientsJson = "[\"alice@example.test\"]" });
                }
                await db.SaveChangesAsync();
                ids.Add(message.Id);
                if (account is not null)
                {
                    account.DeletedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync();
                }
            }
            var result = await mail.SearchAsync(new("Message-ID: <complaint@example.test>", null, null,
                null, null, null, null, null), CancellationToken.None);
            Assert.Equal(2, result.TotalCount);
            Assert.Contains(result.Items, x => x.RecycledCopies == 1);
            Assert.Single((await mail.SearchAsync(new(null, null, "alice@example.test", null,
                null, null, null, true), CancellationToken.None)).Items);
            Assert.Single((await mail.SearchAsync(new(null, null, "forward@remote.test", null,
                null, null, DeliveryStatus.Delivered, false), CancellationToken.None)).Items);
            var detail = await mail.GetAsync(ids[0], CancellationToken.None);
            Assert.Contains("alice@example.test", detail!.OriginalRecipients);
            Assert.Equal(61, detail.OriginalRecipientCount);
            Assert.Equal(50, detail.OriginalRecipients.Count);
            Assert.Equal(11, (await mail.GetRecipientsAsync(ids[0], "original", 2, 50, CancellationToken.None))!.Items.Count);
            Assert.Equal(2, detail.MailboxCopies.Count);
            Assert.All(detail.MailboxCopies, copy => Assert.Equal("alice@example.test", copy.AccountAddress));
            Assert.Contains("alice@example.test", detail.QuarantineRecipients);
            var deliveryPage = await mail.GetDeliveriesAsync(ids[0], 1, 50, CancellationToken.None);
            Assert.Single(deliveryPage!.Items);
            var attemptPage = await mail.GetAttemptsAsync(ids[0], deliveryPage.Items[0].Id, 2, 50, CancellationToken.None);
            Assert.Equal(60, attemptPage!.TotalCount);
            Assert.Equal(10, attemptPage.Items.Count);
            var preview = await mail.GetContentAsync(ids[0], "operator", "operations.mail.preview", CancellationToken.None);
            Assert.Equal("report 0", preview!.Subject);
            Assert.Contains("body 0", preview.Text);
            await using (var db = await factory.CreateDbContextAsync())
                Assert.Single(await db.AuditRecords.Where(x => x.Action == "operations.mail.preview").ToListAsync());
            await using var downloaded = await mail.OpenAuditedAsync(ids[0], "operator", "operations.mail.download", CancellationToken.None);
            Assert.NotNull(downloaded);
            await using var check = await factory.CreateDbContextAsync();
            Assert.Equal(2, await check.AuditRecords.CountAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
