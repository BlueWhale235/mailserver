using System.Net;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Xunit;

namespace MailServer.Tests;

public sealed class DeliveryCompletionRegressionTests
{
    public static IEnumerable<object[]> CompletionCases()
    {
        foreach (var provider in Providers())
        foreach (var outcome in new[] { "success", "temporary", "permanent", "exhausted" })
            yield return [provider, outcome];
    }

    public static IEnumerable<object[]> RelationalProviders() => Providers().Select(provider => new object[] { provider });

    public static IEnumerable<object[]> StaleLeaseCases()
    {
        foreach (var provider in Providers())
        foreach (var stale in new[] { "owner", "attempt", "expired" })
            yield return [provider, stale];
    }

    private static IEnumerable<string> Providers()
    {
        yield return "Sqlite";
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION")))
            yield return "MySql";
    }

    [Theory]
    [MemberData(nameof(CompletionCases))]
    public async Task CompletionResultAndRecordedAttemptMatchCommittedState(string provider, string outcome)
    {
        await using var env = await TestEnvironment.CreateAsync(provider);
        var delivery = await env.Services.GetRequiredService<SystemSettingsService>().GetDeliveryOptionsAsync(CancellationToken.None);
        var job = await env.SeedAsync(outcome == "exhausted" ? delivery.MaximumAttempts : 1);
        var lease = new DeliveryLease(job, job.LeaseOwner!, job.Attempts, job.LeaseUntil!.Value);
        var result = outcome switch
        {
            "success" => DeliveryResult.Success("250 accepted"),
            "permanent" => DeliveryResult.Permanent("550 5.1.1 rejected"),
            _ => DeliveryResult.Temporary("451 busy")
        };
        var expected = outcome switch
        {
            "success" => DeliveryStatus.Delivered,
            "temporary" => DeliveryStatus.Deferred,
            _ => DeliveryStatus.Failed
        };
        var queue = env.Services.GetRequiredService<IDeliveryQueue>();
        var completion = await queue.CompleteAsync(lease, result, CancellationToken.None);
        Assert.True(completion.Applied);
        Assert.Equal(expected, completion.Status);
        Assert.Equal(expected == DeliveryStatus.Failed, completion.FinalFailure);
        await using var verify = await env.Factory.CreateDbContextAsync();
        var current = await verify.DeliveryJobs.SingleAsync(x => x.Id == job.Id);
        Assert.Equal(expected, current.Status);
        Assert.Null(current.LeaseOwner);
        Assert.Null(current.LeaseUntil);
        Assert.Equal(result.Succeeded ? null : result.Response, current.LastError);
        var attempt = Assert.Single(await verify.DeliveryAttempts.Where(x => x.DeliveryJobId == job.Id).ToListAsync());
        Assert.Equal(job.Attempts, attempt.AttemptNumber);
        Assert.Equal(result.Succeeded, attempt.Succeeded);
        Assert.Equal(result.PermanentFailure, attempt.PermanentFailure);
        if (expected == DeliveryStatus.Deferred)
        {
            Assert.True(current.NextAttemptAt > current.UpdatedAt);
            Assert.Equal(current.NextAttemptAt, attempt.NextRetryAt);
        }
        var repeated = await queue.CompleteAsync(lease, result, CancellationToken.None);
        Assert.False(repeated.Applied);
        Assert.Null(repeated.Status);
        Assert.False(repeated.FinalFailure);
        Assert.Equal(1, await verify.DeliveryAttempts.CountAsync(x => x.DeliveryJobId == job.Id));
    }

    [Theory]
    [MemberData(nameof(StaleLeaseCases))]
    public async Task StaleLeaseCannotProduceCompletionOrAttempt(string provider, string staleKind)
    {
        await using var env = await TestEnvironment.CreateAsync(provider);
        var job = await env.SeedAsync(1);
        if (staleKind == "expired")
        {
            await using var expiredDb = await env.Factory.CreateDbContextAsync();
            await expiredDb.DeliveryJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(setters =>
                setters.SetProperty(x => x.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        var stale = new DeliveryLease(job, staleKind == "owner" ? "former-worker" : job.LeaseOwner!,
            staleKind == "attempt" ? job.Attempts + 1 : job.Attempts, job.LeaseUntil!.Value);
        var completion = await env.Services.GetRequiredService<IDeliveryQueue>().CompleteAsync(
            stale, DeliveryResult.Permanent("550 rejected"), CancellationToken.None);
        Assert.False(completion.Applied);
        Assert.Null(completion.Status);
        Assert.False(completion.FinalFailure);
        await using var verify = await env.Factory.CreateDbContextAsync();
        Assert.Empty(await verify.DeliveryAttempts.Where(x => x.DeliveryJobId == job.Id).ToListAsync());
        Assert.Equal(DeliveryStatus.Processing, (await verify.DeliveryJobs.SingleAsync(x => x.Id == job.Id)).Status);
    }

    [Theory]
    [MemberData(nameof(RelationalProviders))]
    public async Task FinalCompletionCanCreateOneNotificationAndRepeatedCompletionCannot(string provider)
    {
        await using var env = await TestEnvironment.CreateAsync(provider);
        var job = await env.SeedAsync(1);
        var lease = new DeliveryLease(job, job.LeaseOwner!, job.Attempts, job.LeaseUntil!.Value);
        var queue = env.Services.GetRequiredService<IDeliveryQueue>();
        var notifications = env.Services.GetRequiredService<DeliveryNotificationService>();
        for (var call = 0; call < 2; call++)
        {
            var completion = await queue.CompleteAsync(lease, DeliveryResult.Permanent("550 5.1.1 rejected"), CancellationToken.None);
            if (completion.Applied && completion.FinalFailure)
                await notifications.CreateAsync(job.Sender, job.Recipient, "550 5.1.1 rejected", job.Id.ToString("N"),
                    job.Attempts, DeliveryNotificationKind.PermanentFailure, CancellationToken.None);
        }
        await using var verify = await env.Factory.CreateDbContextAsync();
        var notification = Assert.Single(await verify.Messages.Where(x => x.SourceType == "bounce")
            .Where(x => verify.MailboxMessages.Any(m => m.MessageId == x.Id && m.Mailbox.AccountId == env.Account.Id)).ToListAsync());
        Assert.Equal(string.Empty, notification.EnvelopeFrom);
        await using var content = await env.Services.GetRequiredService<IMessageContentStore>().OpenReadAsync(notification.ContentKey, CancellationToken.None);
        using var mime = await MimeMessage.LoadAsync(content);
        Assert.Equal("auto-replied", mime.Headers["Auto-Submitted"]);
        Assert.Equal(job.Sender, Assert.Single(mime.To.Mailboxes).Address);
        Assert.Equal(1, await verify.DeliveryAttempts.CountAsync(x => x.DeliveryJobId == job.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("sender@example.test")]
    public async Task ExplicitEnvelopeSenderIsUsedOnSmtpWireInsteadOfMessageFrom(string sender)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? mailCommand = null;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 controlled.test ESMTP ready");
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("EHLO ")) await writer.WriteLineAsync("250 controlled.test");
                else if (line.StartsWith("MAIL FROM:")) { mailCommand = line; await writer.WriteLineAsync("250 accepted"); }
                else if (line.StartsWith("RCPT TO:")) await writer.WriteLineAsync("250 accepted");
                else if (line == "DATA")
                {
                    await writer.WriteLineAsync("354 send data");
                    while (await reader.ReadLineAsync(timeout.Token) is { } data && data != ".") { }
                    await writer.WriteLineAsync("250 committed");
                }
                else if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
            }
        }, timeout.Token);
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Notification", "postmaster@header.test"));
        message.To.Add(MailboxAddress.Parse("recipient@example.test"));
        message.Subject = "Delivery notification";
        message.Body = new TextPart("plain") { Text = "controlled notification" };
        using var client = new SmtpClient();
        await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, SecureSocketOptions.None, timeout.Token);
        await client.SendAsync(message, DirectMxTransport.ParseEnvelopeSender(sender), message.To.Mailboxes, timeout.Token);
        await client.DisconnectAsync(true, timeout.Token);
        await server;
        Assert.Equal($"MAIL FROM:<{sender}>", mailCommand);
    }

    private sealed class TestEnvironment(string root, ServiceProvider services, MailAccount account) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public MailAccount Account { get; } = account;
        public IDbContextFactory<MailDbContext> Factory => Services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        private readonly List<Guid> _messageIds = [];

        public static async Task<TestEnvironment> CreateAsync(string provider)
        {
            var root = Path.Combine(Path.GetTempPath(), "mailserver-completion-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var connection = provider == "MySql" ? Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION")!
                : "Data Source=" + Path.Combine(root, "test.db") + ";Pooling=False";
            if (provider == "MySql" && !connection.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An isolated acceptance database is required.");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider, ["ConnectionStrings:MailDatabase"] = connection,
                ["Storage:RootPath"] = Path.Combine(root, "messages"),
                ["SecurityAssets:RootPath"] = Path.Combine(root, "security"),
                ["Forwarding:SrsSecret"] = "0123456789abcdef0123456789abcdef"
            }).Build();
            var collection = new ServiceCollection();
            collection.AddLogging();collection.AddSingleton<IConfiguration>(configuration);
            collection.AddMailServerInfrastructure(configuration);
            var services = collection.BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
            var domain = new MailDomain { Name = Guid.NewGuid().ToString("N") + ".test", QuotaBytes = 1000000 };
            var account = new MailAccount { Domain = domain, LocalPart = "sender", QuotaBytes = 1000000, PasswordHash = "unused" };
            db.Accounts.Add(account);db.Mailboxes.Add(new Mailbox { Account = account, Name = "INBOX" });
            await db.SaveChangesAsync();
            return new(root, services, account);
        }

        public async Task<DeliveryJob> SeedAsync(int attempts)
        {
            var job = new DeliveryJob
            {
                Sender = Account.LocalPart + "@" + Account.Domain.Name,
                Recipient = "recipient@controlled.test", Status = DeliveryStatus.Processing,
                Attempts = attempts, LeaseOwner = "current-worker", LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1),
                Message = new StoredMessage { ContentKey = new string('a', 64), EnvelopeFrom = "sender@controlled.test", Size = 42 }
            };
            await using var db = await Factory.CreateDbContextAsync();db.DeliveryJobs.Add(job);await db.SaveChangesAsync();
            _messageIds.Add(job.MessageId);return job;
        }

        public async ValueTask DisposeAsync()
        {
            await using (var db = await Factory.CreateDbContextAsync())
            {
                var mailboxIds = await db.Mailboxes.IgnoreQueryFilters().Where(x => x.AccountId == Account.Id)
                    .Select(x => x.Id).ToListAsync();
                var references = new List<(Guid Id, Guid MessageId)>();
                foreach (var mailboxId in mailboxIds)
                {
                    var owned = await db.MailboxMessages.IgnoreQueryFilters().Where(x => x.MailboxId == mailboxId)
                        .Select(x => new { x.Id, x.MessageId }).ToListAsync();
                    references.AddRange(owned.Select(x => (x.Id, x.MessageId)));
                }
                foreach (var id in _messageIds) await db.DeliveryJobs.IgnoreQueryFilters().Where(x => x.MessageId == id).ExecuteDeleteAsync();
                // Delete exact owned IDs: navigation-based bulk deletes can produce
                // a self-referencing target subquery which MySQL rejects.
                foreach (var reference in references)
                    await db.MailboxMessages.IgnoreQueryFilters().Where(x => x.Id == reference.Id).ExecuteDeleteAsync();
                await db.QuotaLedger.IgnoreQueryFilters().Where(x => x.AccountId == Account.Id).ExecuteDeleteAsync();
                foreach (var id in mailboxIds) await db.Mailboxes.IgnoreQueryFilters().Where(x => x.Id == id).ExecuteDeleteAsync();
                await db.Accounts.IgnoreQueryFilters().Where(x => x.Id == Account.Id).ExecuteDeleteAsync();
                await db.Domains.IgnoreQueryFilters().Where(x => x.Id == Account.DomainId).ExecuteDeleteAsync();
                foreach (var id in _messageIds.Concat(references.Select(x => x.MessageId)).Distinct())
                    await db.Messages.IgnoreQueryFilters().Where(x => x.Id == id).ExecuteDeleteAsync();
            }
            await Services.DisposeAsync();SqliteConnection.ClearAllPools();Directory.Delete(root,recursive:true);
        }
    }
}
