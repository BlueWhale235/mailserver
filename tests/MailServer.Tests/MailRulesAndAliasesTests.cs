using System.Text;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Xunit;

namespace MailServer.Tests;

public sealed class MailRulesAndAliasesTests
{
    [Fact]
    public async Task DisablingRuleDoesNotTriggerMultipleCollectionQueryWarning()
    {
        SqliteNative.Initialize();
        var path = Path.Combine(Path.GetTempPath(), $"mailserver-rule-warning-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<MailDbContext>()
                .UseSqlite($"Data Source={path}")
                .ConfigureWarnings(warnings => warnings.Throw(RelationalEventId.MultipleCollectionIncludeWarning))
                .Options;
            var factory = new PooledDbContextFactory<MailDbContext>(options);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.Domains.Add(new MailDomain { Name = "example.com" });
                await db.SaveChangesAsync();
            }

            var service = new MailRuleService(
                factory,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MailRuleService>.Instance);
            Guid domainId;
            await using (var db = await factory.CreateDbContextAsync())
                domainId = await db.Domains.Select(x => x.Id).SingleAsync();
            var input = new MailRuleInput(
                "Toggle rule",
                MailRuleDirection.Incoming,
                MailRuleMatchMode.All,
                "INBOX",
                true,
                [new MailRuleConditionInput(MailRuleField.Sender, MailRuleOperator.EqualsAny, ["sender@example.test"])],
                [new MailRuleActionInput(MailRuleActionKind.ForwardTo, ["target@example.test"])]);
            var created = await service.CreateAsync(domainId, input, "test", CancellationToken.None);

            var disabled = await service.UpdateAsync(
                domainId,
                created.Id,
                input with { IsEnabled = false },
                "test",
                CancellationToken.None);

            Assert.False(disabled.IsEnabled);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AliasUsesSameDomainAccountForInboundAndOutbound()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var administration = environment.Services.GetRequiredService<AdminApplicationService>();
        Guid domainId;
        Guid otherDomainId;
        Guid aliceId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            var other = new MailDomain { Name = "other.test" };
            var alice = Account(domain, "alice");
            var outsider = Account(other, "outside");
            db.AddRange(
                domain,
                other,
                alice,
                outsider,
                new Mailbox { Account = alice, Name = "INBOX" },
                new Mailbox { Account = alice, Name = "Sent" });
            await db.SaveChangesAsync();
            domainId = domain.Id;
            otherDomainId = other.Id;
            aliceId = alice.Id;
        }

        var alias = await administration.CreateDomainAliasAsync(
            domainId,
            "sales",
            aliceId,
            "test",
            CancellationToken.None);
        Assert.Equal("alice@example.com", alias.TargetAddress);
        Assert.True(alias.IsEnabled);

        var inbound = environment.Services.GetRequiredService<IInboundMessageService>();
        Assert.True(await inbound.IsLocalRecipientAsync("sales@example.com", CancellationToken.None));
        await using var message = MessageStream("alias-1", "sales@example.com", "Alias delivery");
        var accepted = await inbound.AcceptAsync(
            new InboundEnvelope("sender@external.test", ["sales@example.com"], null, "127.0.0.1", "test"),
            message,
            CancellationToken.None);
        Assert.Equal(1, accepted.LocalDeliveries);

        var authorizer = environment.Services.GetRequiredService<ISmtpSenderAuthorizer>();
        Assert.True(await authorizer.IsAuthorizedSenderAsync(aliceId, "sales@example.com", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => administration.CreateDomainAliasAsync(
            otherDomainId,
            "sales",
            aliceId,
            "test",
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => administration.CreateDomainAccountAsync(
            domainId,
            "sales",
            "a strong test password",
            null,
            "test",
            CancellationToken.None));

        await using var verification = await factory.CreateDbContextAsync();
        var delivered = await verification.MailboxMessages.Include(x => x.Mailbox).SingleAsync();
        Assert.Equal(aliceId, delivered.Mailbox.AccountId);
        Assert.Equal("INBOX", delivered.Mailbox.Name);
    }

    [Fact]
    public async Task AliasModesAndEditsEnforceAccountAddressOwnership()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var administration = environment.Services.GetRequiredService<AdminApplicationService>();
        Guid domainId;
        Guid accountId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            var account = Account(domain, "alice");
            db.AddRange(domain, account, new Mailbox { Account = account, Name = "INBOX" });
            await db.SaveChangesAsync();
            domainId = domain.Id;
            accountId = account.Id;
        }

        var alias = await administration.CreateDomainAliasAsync(
            domainId,
            "sales",
            accountId,
            MailAliasMode.ReceiveOnly,
            "test",
            CancellationToken.None);
        Assert.True(alias.CanReceive);
        Assert.False(alias.CanSend);
        var inbound = environment.Services.GetRequiredService<IInboundMessageService>();
        var authorizer = environment.Services.GetRequiredService<ISmtpSenderAuthorizer>();
        Assert.True(await inbound.IsLocalRecipientAsync("sales@example.com", CancellationToken.None));
        Assert.False(await authorizer.IsAuthorizedSenderAsync(accountId, "sales@example.com", CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => administration.UpdateAliasAsync(
            alias.Id,
            "ALICE",
            accountId,
            MailAliasMode.SendReceive,
            "test",
            CancellationToken.None));
        var disabled = await administration.UpdateAliasAsync(
            alias.Id,
            "support",
            accountId,
            MailAliasMode.Disabled,
            "test",
            CancellationToken.None);
        Assert.False(disabled.CanReceive);
        Assert.False(disabled.CanSend);
        Assert.False(await inbound.IsLocalRecipientAsync("support@example.com", CancellationToken.None));
    }

    [Fact]
    public async Task RuleEvaluatorSupportsCompleteConditionSetAndFirstMatchWins()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        Guid domainId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            db.Domains.Add(domain);
            await db.SaveChangesAsync();
            domainId = domain.Id;
        }

        var ruleService = environment.Services.GetRequiredService<MailRuleService>();
        var now = DateTimeOffset.UtcNow;
        var conditions = new MailRuleConditionInput[]
        {
            new(MailRuleField.Sender, MailRuleOperator.ContainsAny, ["alice@sender.test"]),
            new(MailRuleField.SenderDomain, MailRuleOperator.EqualsAny, ["sender.test"]),
            new(MailRuleField.Recipient, MailRuleOperator.EqualsAny, ["sales@example.com"]),
            new(MailRuleField.Cc, MailRuleOperator.ContainsAll, ["manager@example.com"]),
            new(MailRuleField.RecipientOrCc, MailRuleOperator.NotContainsAny, ["blocked@example.com"]),
            new(MailRuleField.Subject, MailRuleOperator.ContainsAny, ["invoice"]),
            new(MailRuleField.Body, MailRuleOperator.ContainsAll, ["balance", "due"]),
            new(MailRuleField.AttachmentName, MailRuleOperator.ContainsAny, ["invoice.pdf"]),
            new(MailRuleField.MessageSize, MailRuleOperator.GreaterThan, ["100"]),
            new(MailRuleField.MessageTime, MailRuleOperator.Between, [now.AddMinutes(-1).ToString("O"), now.AddMinutes(1).ToString("O")]),
            new(MailRuleField.HasAttachment, MailRuleOperator.Is, ["true"])
        };
        var first = await ruleService.CreateAsync(
            domainId,
            new MailRuleInput("Full match", MailRuleDirection.Incoming, MailRuleMatchMode.All, "Archive", true, conditions),
            "test",
            CancellationToken.None);
        await ruleService.CreateAsync(
            domainId,
            new MailRuleInput(
                "Later match",
                MailRuleDirection.Incoming,
                MailRuleMatchMode.Any,
                "Other",
                true,
                [new MailRuleConditionInput(MailRuleField.Subject, MailRuleOperator.ContainsAny, ["invoice"])]),
            "test",
            CancellationToken.None);

        var mime = new MimeMessage
        {
            MessageId = "rules@example.test",
            Subject = "June invoice",
            Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = "Your balance is due." },
                new MimePart("application", "pdf")
                {
                    Content = new MimeContent(new MemoryStream([1, 2, 3])),
                    ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "invoice.pdf" }
                }
            }
        };
        mime.From.Add(MailboxAddress.Parse("alice@sender.test"));
        mime.To.Add(MailboxAddress.Parse("sales@example.com"));
        mime.Cc.Add(MailboxAddress.Parse("manager@example.com"));
        var facts = MailRuleService.BuildFacts(mime, "alice@sender.test", ["sales@example.com"], 2048, now);
        var match = await ruleService.MatchAsync(domainId, MailRuleDirection.Incoming, facts, CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal(first.Id, match.RuleId);
        Assert.Equal("Archive", match.TargetMailboxName);
    }

    [Fact]
    public async Task IncomingRuleSeesOriginalAliasAndFallsBackWhenFolderIsMissing()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var administration = environment.Services.GetRequiredService<AdminApplicationService>();
        var ruleService = environment.Services.GetRequiredService<MailRuleService>();
        Guid domainId;
        Guid aliceId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            var alice = Account(domain, "alice");
            db.AddRange(
                domain,
                alice,
                new Mailbox { Account = alice, Name = "INBOX" },
                new Mailbox { Account = alice, Name = "Archive" });
            await db.SaveChangesAsync();
            domainId = domain.Id;
            aliceId = alice.Id;
        }
        await administration.CreateDomainAliasAsync(domainId, "sales", aliceId, "test", CancellationToken.None);
        await ruleService.CreateAsync(
            domainId,
            new MailRuleInput(
                "Alias archive",
                MailRuleDirection.Incoming,
                MailRuleMatchMode.All,
                "Archive",
                true,
                [new MailRuleConditionInput(MailRuleField.Recipient, MailRuleOperator.EqualsAny, ["sales@example.com"])]),
            "test",
            CancellationToken.None);
        await ruleService.CreateAsync(
            domainId,
            new MailRuleInput(
                "Missing folder",
                MailRuleDirection.Incoming,
                MailRuleMatchMode.All,
                "Missing",
                true,
                [new MailRuleConditionInput(MailRuleField.Recipient, MailRuleOperator.EqualsAny, ["alice@example.com"])]),
            "test",
            CancellationToken.None);

        var inbound = environment.Services.GetRequiredService<IInboundMessageService>();
        await using (var aliasMessage = MessageStream("alias-rule", "sales@example.com", "Alias"))
            await inbound.AcceptAsync(new InboundEnvelope("sender@test.net", ["sales@example.com"], null, "127.0.0.1", "test"), aliasMessage, CancellationToken.None);
        await using (var directMessage = MessageStream("direct-rule", "alice@example.com", "Direct"))
            await inbound.AcceptAsync(new InboundEnvelope("sender@test.net", ["alice@example.com"], null, "127.0.0.1", "test"), directMessage, CancellationToken.None);

        await using var verification = await factory.CreateDbContextAsync();
        var folders = await verification.MailboxMessages.OrderBy(x => x.Message.ReceivedAt).Select(x => x.Mailbox.Name).ToListAsync();
        Assert.Equal(["Archive", "INBOX"], folders);
    }

    [Fact]
    public async Task IncomingRuleKeepsLocalCopyAndForwardsLocallyAndExternallyWithSrs()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var rules = environment.Services.GetRequiredService<MailRuleService>();
        Guid domainId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com", OutboundMode = OutboundMode.DirectMx };
            var alice = Account(domain, "alice");
            var bob = Account(domain, "bob");
            db.AddRange(
                domain,
                alice,
                bob,
                new Mailbox { Account = alice, Name = "INBOX" },
                new Mailbox { Account = alice, Name = "Archive" },
                new Mailbox { Account = bob, Name = "INBOX" });
            await db.SaveChangesAsync();
            domainId = domain.Id;
        }
        await rules.CreateAsync(
            domainId,
            new MailRuleInput(
                "Forward invoices",
                MailRuleDirection.Incoming,
                MailRuleMatchMode.All,
                "Archive",
                true,
                [new MailRuleConditionInput(MailRuleField.Recipient, MailRuleOperator.EqualsAny, ["alice@example.com"])],
                [
                    new MailRuleActionInput(MailRuleActionKind.MoveToFolder, ["Archive"]),
                    new MailRuleActionInput(MailRuleActionKind.ForwardTo, ["bob@example.com", "outside@target.test", "alice@example.com"])
                ]),
            "test",
            CancellationToken.None);

        var inbound = environment.Services.GetRequiredService<IInboundMessageService>();
        await using var message = MessageStream("forward-1", "alice@example.com", "Forward me");
        var accepted = await inbound.AcceptAsync(
            new InboundEnvelope("sender@origin.test", ["alice@example.com"], null, "127.0.0.1", "test"),
            message,
            CancellationToken.None);
        Assert.Equal(2, accepted.LocalDeliveries);
        Assert.Equal(1, accepted.QueuedDeliveries);

        await using var verification = await factory.CreateDbContextAsync();
        var localFolders = await verification.MailboxMessages
            .OrderBy(x => x.Mailbox.Account.LocalPart)
            .Select(x => x.Mailbox.Account.LocalPart + ":" + x.Mailbox.Name)
            .ToListAsync();
        Assert.Equal(["alice:Archive", "bob:INBOX"], localFolders);
        var job = await verification.DeliveryJobs.SingleAsync();
        Assert.Equal("outside@target.test", job.Recipient);
        Assert.StartsWith("srs0=", job.Sender, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, job.ForwardDepth);
        var decoded = await environment.Services.GetRequiredService<SrsService>()
            .TryDecodeAsync(verification, job.Sender, CancellationToken.None);
        Assert.NotNull(decoded);
        Assert.Equal("sender@origin.test", decoded.OriginalSender);
        Assert.True(await inbound.IsLocalRecipientAsync(job.Sender, CancellationToken.None));
        Assert.Null(await environment.Services.GetRequiredService<SrsService>()
            .TryDecodeAsync(verification, job.Sender.Replace("SRS0=", "SRS0=x", StringComparison.OrdinalIgnoreCase), CancellationToken.None));
    }

    [Fact]
    public async Task IncomingRuleDoesNotForwardBackToOriginalRecipient()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var rules = environment.Services.GetRequiredService<MailRuleService>();
        Guid domainId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            var recipient = Account(domain, "456");
            db.AddRange(domain, recipient, new Mailbox { Account = recipient, Name = "INBOX" });
            await db.SaveChangesAsync();
            domainId = domain.Id;
        }

        await rules.CreateAsync(
            domainId,
            new MailRuleInput(
                "Do not loop",
                MailRuleDirection.Incoming,
                MailRuleMatchMode.All,
                "INBOX",
                true,
                [new MailRuleConditionInput(MailRuleField.Sender, MailRuleOperator.EqualsAny, ["123@example.com"])],
                [new MailRuleActionInput(MailRuleActionKind.ForwardTo, ["456@example.com"])]),
            "test",
            CancellationToken.None);

        await using var message = MessageStream("same-recipient", "456@example.com", "Loop check");
        var accepted = await environment.Services.GetRequiredService<IInboundMessageService>().AcceptAsync(
            new InboundEnvelope("123@example.com", ["456@example.com"], null, "127.0.0.1", "test"),
            message,
            CancellationToken.None);

        Assert.Equal(1, accepted.LocalDeliveries);
        Assert.Equal(0, accepted.QueuedDeliveries);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Empty(await verification.DeliveryJobs.ToListAsync());
    }

    [Fact]
    public async Task SignedForwardingTraceStopsRuleAfterExternalReentry()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var rules = environment.Services.GetRequiredService<MailRuleService>();
        var srs = environment.Services.GetRequiredService<SrsService>();
        Guid domainId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            var recipient = Account(domain, "alice");
            db.AddRange(domain, recipient, new Mailbox { Account = recipient, Name = "INBOX" });
            await db.SaveChangesAsync();
            domainId = domain.Id;
        }

        var rule = await rules.CreateAsync(
            domainId,
            new MailRuleInput(
                "External forward",
                MailRuleDirection.Incoming,
                MailRuleMatchMode.All,
                "INBOX",
                true,
                [new MailRuleConditionInput(MailRuleField.Sender, MailRuleOperator.EqualsAny, ["sender@origin.test"])],
                [new MailRuleActionInput(MailRuleActionKind.ForwardTo, ["outside@target.test"])]),
            "test",
            CancellationToken.None);

        string trace;
        await using (var db = await factory.CreateDbContextAsync())
        {
            trace = srs.CreateForwardingTrace(db, rule.Id, 1, "<external-loop@example.test>");
            await db.SaveChangesAsync();
        }

        var raw = RawMessage(
            "external-loop",
            "Loop check",
            "hello",
            $"To: alice@example.com\r\n{SrsService.ForwardingTraceHeader}: {trace}\r\n");
        await using var message = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var accepted = await environment.Services.GetRequiredService<IInboundMessageService>().AcceptAsync(
            new InboundEnvelope("sender@origin.test", ["alice@example.com"], null, "127.0.0.1", "test"),
            message,
            CancellationToken.None);

        Assert.Equal(1, accepted.LocalDeliveries);
        Assert.Equal(0, accepted.QueuedDeliveries);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Empty(await verification.DeliveryJobs.ToListAsync());
        Assert.Empty(await srs.ReadForwardingTracesAsync(
            verification,
            "<external-loop@example.test>",
            [trace + "tampered"],
            CancellationToken.None));
    }

    [Fact]
    public async Task DomainQuotaRejectsMessageBeforeLocalDelivery()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com", QuotaBytes = 1 };
            var recipient = Account(domain, "alice");
            db.AddRange(domain, recipient, new Mailbox { Account = recipient, Name = "INBOX" });
            await db.SaveChangesAsync();
        }

        await using var message = MessageStream("domain-quota", "alice@example.com", "Quota check");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            environment.Services.GetRequiredService<IInboundMessageService>().AcceptAsync(
                new InboundEnvelope("sender@origin.test", ["alice@example.com"], null, "127.0.0.1", "test"),
                message,
                CancellationToken.None));

        Assert.Contains("Domain quota exceeded", exception.Message);
        await using var verification = await factory.CreateDbContextAsync();
        Assert.Empty(await verification.MailboxMessages.ToListAsync());
    }

    [Fact]
    public async Task SrsSecretIsGeneratedWhenForwardingSecretIsEmpty()
    {
        await using var environment = await TestEnvironment.CreateAsync(srsSecret: null);
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var srs = environment.Services.GetRequiredService<SrsService>();
        string address;

        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            db.Domains.Add(domain);
            address = srs.CreateAddress(db, domain, "sender@origin.test", 1, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        await using var verification = await factory.CreateDbContextAsync();
        var secret = await verification.SystemSettings.SingleAsync(x => x.Key == "forwarding:srs-secret");
        Assert.Equal(64, secret.Value.Length);

        var decoded = await srs.TryDecodeAsync(verification, address, CancellationToken.None);
        Assert.NotNull(decoded);
        Assert.Equal("sender@origin.test", decoded.OriginalSender);
    }

    [Fact]
    public async Task ClientSentCopyWinsServerFallbackAndLateAppendDoesNotDuplicate()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var ruleService = environment.Services.GetRequiredService<MailRuleService>();
        Guid domainId;
        Guid accountId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            var account = Account(domain, "alice");
            db.AddRange(
                domain,
                account,
                new Mailbox { Account = account, Name = "INBOX" },
                new Mailbox { Account = account, Name = "Sent" },
                new Mailbox { Account = account, Name = "Drafts" },
                new Mailbox { Account = account, Name = "Archive" });
            await db.SaveChangesAsync();
            domainId = domain.Id;
            accountId = account.Id;
        }
        await ruleService.CreateAsync(
            domainId,
            new MailRuleInput(
                "Archive invoices",
                MailRuleDirection.Outgoing,
                MailRuleMatchMode.All,
                "Archive",
                true,
                [new MailRuleConditionInput(MailRuleField.Subject, MailRuleOperator.ContainsAny, ["invoice"])]),
            "test",
            CancellationToken.None);

        var rawFirst = RawMessage("sent-one", "Invoice one", "original");
        var inbound = environment.Services.GetRequiredService<IInboundMessageService>();
        await using (var smtpContent = new MemoryStream(Encoding.UTF8.GetBytes(rawFirst)))
            await inbound.AcceptAsync(
                new InboundEnvelope("alice@example.com", ["customer@external.test"], "alice@example.com", "127.0.0.1", "test", accountId),
                smtpContent,
                CancellationToken.None);
        var mailboxes = environment.Services.GetRequiredService<IMailboxService>();
        await using (var clientCopy = new MemoryStream(Encoding.UTF8.GetBytes(rawFirst)))
            await mailboxes.AppendAsync(accountId, "Sent", clientCopy, ["\\Seen"], null, CancellationToken.None);
        await MakeJobsDueAsync(factory);
        var sentCopies = environment.Services.GetRequiredService<SentCopyService>();
        await sentCopies.ProcessDueAsync(10, CancellationToken.None);

        var rawSecond = RawMessage("sent-two", "Invoice two", "server version");
        await using (var smtpContent = new MemoryStream(Encoding.UTF8.GetBytes(rawSecond)))
            await inbound.AcceptAsync(
                new InboundEnvelope("alice@example.com", ["customer@external.test"], "alice@example.com", "127.0.0.1", "test", accountId),
                smtpContent,
                CancellationToken.None);
        await MakeJobsDueAsync(factory);
        await sentCopies.ProcessDueAsync(10, CancellationToken.None);

        var alteredSecond = RawMessage("sent-two", "Invoice two", "client version", "X-Client-Saved: yes\r\n");
        await using (var lateClientCopy = new MemoryStream(Encoding.UTF8.GetBytes(alteredSecond)))
            await mailboxes.AppendAsync(accountId, "Sent", lateClientCopy, ["\\Seen"], null, CancellationToken.None);
        await MakeJobsDueAsync(factory);
        await sentCopies.ProcessDueAsync(10, CancellationToken.None);

        var rawThird = RawMessage("sent-three", "Invoice three", "draft copy");
        await using (var smtpContent = new MemoryStream(Encoding.UTF8.GetBytes(rawThird)))
            await inbound.AcceptAsync(
                new InboundEnvelope("alice@example.com", ["customer@external.test"], "alice@example.com", "127.0.0.1", "test", accountId),
                smtpContent,
                CancellationToken.None);
        await using (var draftCopy = new MemoryStream(Encoding.UTF8.GetBytes(rawThird)))
            await mailboxes.AppendAsync(accountId, "Drafts", draftCopy, [], null, CancellationToken.None);
        await MakeJobsDueAsync(factory);
        await sentCopies.ProcessDueAsync(10, CancellationToken.None);

        await using var verification = await factory.CreateDbContextAsync();
        Assert.Equal(3, await verification.MailboxMessages.CountAsync(x => x.Mailbox.AccountId == accountId && x.Mailbox.Name == "Archive"));
        Assert.Equal(0, await verification.MailboxMessages.CountAsync(x => x.Mailbox.AccountId == accountId && x.Mailbox.Name == "Sent"));
        Assert.Equal(1, await verification.MailboxMessages.CountAsync(x => x.Mailbox.AccountId == accountId && x.Mailbox.Name == "Drafts"));
        Assert.Equal(3, await verification.SentCopyJobs.CountAsync(x => x.State == SentCopyState.Completed));
    }

    [Fact]
    public async Task MigrationMapsSameDomainAliasesAndDisablesExternalTargets()
    {
        await using var environment = await TestEnvironment.CreateAsync(migrate: false);
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var domainId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var ruleId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20260626110000_AddSystemSettings");
            var ticks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mail_domains (Id, Name, IsActive, OutboundMode, CreatedAt) VALUES ({domainId}, {"example.com"}, {true}, {"Relay"}, {ticks})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mail_accounts (Id, DomainId, LocalPart, PasswordHash, IsEnabled, QuotaBytes, CreatedAt) VALUES ({accountId}, {domainId}, {"alice"}, {"unused"}, {true}, {1_000_000L}, {ticks})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mail_aliases (Id, DomainId, SourceLocalPart, DestinationAddress, CreatedAt) VALUES ({Guid.NewGuid()}, {domainId}, {"sales"}, {"alice@example.com"}, {ticks})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mail_aliases (Id, DomainId, SourceLocalPart, DestinationAddress, CreatedAt) VALUES ({Guid.NewGuid()}, {domainId}, {"legacy"}, {"outside@external.test"}, {ticks})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mail_aliases (Id, DomainId, SourceLocalPart, DestinationAddress, CreatedAt) VALUES ({Guid.NewGuid()}, {domainId}, {"alice"}, {"alice@example.com"}, {ticks})");
            await migrator.MigrateAsync("20260627085956_MigrateIdentityAliasData");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO mail_rules (Id, DomainId, Name, Direction, MatchMode, TargetMailboxName, IsEnabled, Priority, CreatedAt, UpdatedAt) VALUES ({ruleId}, {domainId}, {"Legacy archive"}, {"Incoming"}, {"All"}, {"Archive"}, {true}, {0}, {ticks}, {ticks})");
            await migrator.MigrateAsync();
        }

        await using var verification = await factory.CreateDbContextAsync();
        var aliases = await verification.Aliases.OrderBy(x => x.SourceLocalPart).ToListAsync();
        var external = Assert.Single(aliases, x => x.SourceLocalPart == "legacy");
        var local = Assert.Single(aliases, x => x.SourceLocalPart == "sales");
        var conflict = Assert.Single(aliases, x => x.SourceLocalPart == "alice");
        Assert.False(external.IsEnabled);
        Assert.Equal(MailAliasMode.Disabled, external.Mode);
        Assert.Null(external.TargetAccountId);
        Assert.True(local.IsEnabled);
        Assert.Equal(MailAliasMode.SendReceive, local.Mode);
        Assert.Equal(accountId, local.TargetAccountId);
        Assert.Equal(MailAliasMode.Disabled, conflict.Mode);
        Assert.False(conflict.IsEnabled);
        var migratedAction = await verification.MailRuleActions.SingleAsync(x => x.RuleId == ruleId);
        Assert.Equal(MailRuleActionKind.MoveToFolder, migratedAction.Kind);
        Assert.Equal("[\"Archive\"]", migratedAction.ValuesJson);
    }

    [Fact]
    public async Task DomainFiltersAndDeletionPreserveSharedMessageContent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var factory = environment.Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        var contentStore = environment.Services.GetRequiredService<IMessageContentStore>();
        await using var raw = new MemoryStream(Encoding.UTF8.GetBytes(RawMessage("shared", "Shared", "body")));
        var content = await contentStore.SaveAsync(raw, CancellationToken.None);
        Guid firstDomainId;
        Guid secondDomainId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var first = new MailDomain { Name = "alpha.test", OutboundMode = OutboundMode.DirectMx };
            var second = new MailDomain { Name = "beta.test", OutboundMode = OutboundMode.Relay };
            var firstAccount = Account(first, "alice");
            var secondAccount = Account(second, "bob");
            var firstMailbox = new Mailbox { Account = firstAccount, Name = "INBOX" };
            var secondMailbox = new Mailbox { Account = secondAccount, Name = "INBOX" };
            var message = new StoredMessage
            {
                ContentKey = content.Key,
                EnvelopeFrom = "sender@external.test",
                Size = content.Size
            };
            first.Certificate = new DomainCertificate
            {
                Domain = first,
                Source = CertificateSource.ManagedUpload,
                CertificatePath = Path.Combine(environment.Root, "missing-cert.pem"),
                PrivateKeyPath = Path.Combine(environment.Root, "missing-key.pem"),
                Subject = "CN=alpha.test",
                Issuer = "Test CA",
                NotBefore = DateTimeOffset.UtcNow.AddDays(-1),
                NotAfter = DateTimeOffset.UtcNow.AddDays(5)
            };
            db.AddRange(
                first,
                second,
                firstAccount,
                secondAccount,
                firstMailbox,
                secondMailbox,
                message,
                new MailboxMessage { Mailbox = firstMailbox, Message = message, Uid = firstMailbox.UidNext++ },
                new MailboxMessage { Mailbox = secondMailbox, Message = message, Uid = secondMailbox.UidNext++ },
                new DomainDnsCheck { Domain = first, Status = DomainDnsCheckStatus.Healthy, CheckedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            firstDomainId = first.Id;
            secondDomainId = second.Id;
        }

        var administration = environment.Services.GetRequiredService<AdminApplicationService>();
        var filtered = await administration.SearchDomainsAsync(
            new DomainSearchFilter(null, true, true, OutboundMode.DirectMx, DomainExpiryFilter.Within7Days),
            1,
            20,
            CancellationToken.None);
        var row = Assert.Single(filtered.Items);
        Assert.Equal(firstDomainId, row.Id);
        Assert.Equal(DomainDnsCheckStatus.Healthy, row.DnsStatus);

        await administration.UpdateDomainsOutboundModeAsync([secondDomainId], OutboundMode.DirectMx, "test", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => administration.DeleteDomainsAsync([firstDomainId], "test", CancellationToken.None));
        await administration.UpdateDomainAsync(firstDomainId, false, null, null, null, "test", CancellationToken.None);
        await administration.DeleteDomainsAsync([firstDomainId], "test", CancellationToken.None);

        await using var verification = await factory.CreateDbContextAsync();
        Assert.False(await verification.Domains.AnyAsync(x => x.Id == firstDomainId));
        Assert.True(await verification.Domains.AnyAsync(x => x.Id == secondDomainId && x.OutboundMode == OutboundMode.DirectMx));
        Assert.Single(await verification.Messages.ToListAsync());
        Assert.Single(await verification.MailboxMessages.ToListAsync());
        await using var stored = await contentStore.OpenReadAsync(content.Key, CancellationToken.None);
        Assert.True(stored.Length > 0);
    }

    private static MailAccount Account(MailDomain domain, string localPart) => new()
    {
        Domain = domain,
        LocalPart = localPart,
        PasswordHash = "unused",
        QuotaBytes = 100_000_000
    };

    private static MemoryStream MessageStream(string id, string recipient, string subject) =>
        new(Encoding.UTF8.GetBytes(RawMessage(id, subject, "hello", $"To: {recipient}\r\n")));

    private static string RawMessage(string id, string subject, string body, string extraHeaders = "") =>
        $"Message-ID: <{id}@example.test>\r\nFrom: alice@example.com\r\nTo: customer@external.test\r\nSubject: {subject}\r\n{extraHeaders}\r\n{body}";

    private static async Task MakeJobsDueAsync(IDbContextFactory<MailDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        foreach (var job in await db.SentCopyJobs.Where(x => x.State != SentCopyState.Completed).ToListAsync())
            job.ReadyAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
    }

    private sealed class TestEnvironment(string root, ServiceProvider services) : IAsyncDisposable
    {
        public IServiceProvider Services => services;
        public string Root => root;

        public static async Task<TestEnvironment> CreateAsync(
            bool migrate = true,
            string? srsSecret = "0123456789abcdef0123456789abcdef")
        {
            var root = Path.Combine(Path.GetTempPath(), $"mailserver-rules-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var settings = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                ["Storage:RootPath"] = Path.Combine(root, "messages"),
                ["SecurityAssets:RootPath"] = Path.Combine(root, "security"),
                ["MailRules:SentCopyGraceSeconds"] = "60",
                ["MailRules:SentCopyDeduplicationMinutes"] = "15",
                ["Forwarding:MaxHops"] = "5",
                ["Forwarding:MaxAgeDays"] = "21",
                ["Forwarding:MaxTargetsPerRule"] = "20"
            };
            if (srsSecret is not null)
            {
                settings["Forwarding:SrsSecret"] = srsSecret;
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddMailServerInfrastructure(configuration);
            var services = collection.BuildServiceProvider();
            if (migrate)
            {
                var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.MigrateAsync();
            }
            return new TestEnvironment(root, services);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
