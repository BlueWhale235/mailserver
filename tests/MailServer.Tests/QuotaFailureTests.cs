using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.Smtp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace MailServer.Tests;

public sealed class QuotaFailureTests
{
    private const string Message = "From: sender@origin.test\r\nTo: alice@example.test\r\nSubject: quota\r\nMessage-ID: <quota@origin.test>\r\n\r\nhello\r\n";
    private static MemoryStream Content() => new(Encoding.UTF8.GetBytes(Message));

    [Fact]
    public async Task TunneledSmtpUsesNormalRecipientPolicyAndAcknowledgesAfterStorageCommit()
    {
        await using var env = await Environment.CreateAsync();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var senderSocket = new TcpClient();
        var connecting = senderSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var receiverSocket = await listener.AcceptTcpClientAsync();
        await connecting;
        await using var senderTunnel = new RelayTunnel(senderSocket.GetStream(), true);
        await using var receiverTunnel = new RelayTunnel(receiverSocket.GetStream(), false);
        var service = new SmtpServerService(env.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SmtpServerOptions { CommandTimeoutSeconds = 10 }),
            env.Services.GetRequiredService<DomainCertificateProvider>(), env.SmtpLog);
        var endpoint = new SmtpEndpointOptions { Port = 25, Security = ConnectionSecurity.StartTls };
        receiverTunnel.OnOpen = async (_, stream, token) =>
        {
            var method = typeof(SmtpServerService).GetMethod("HandleStreamClientAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)method.Invoke(service, [null, endpoint, token, stream, "203.0.113.7:2525"])!;
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var senderLoop = senderTunnel.RunAsync(timeout.Token);
        var receiverLoop = receiverTunnel.RunAsync(timeout.Token);
        await using var session = await senderTunnel.OpenAsync(new RelayOpen("inbound", RemoteAddress: "203.0.113.7", RemotePort: 2525), timeout.Token);
        using var reader = new StreamReader(session, Encoding.UTF8, false, 1024, true);
        using var writer = new StreamWriter(session, new UTF8Encoding(false), 1024, true) { NewLine = "\r\n", AutoFlush = true };
        Assert.StartsWith("220", await reader.ReadLineAsync(timeout.Token));
        await writer.WriteLineAsync("HELO client.test");
        Assert.StartsWith("250", await reader.ReadLineAsync(timeout.Token));
        await writer.WriteLineAsync("MAIL FROM:<sender@origin.test>");
        Assert.StartsWith("250", await reader.ReadLineAsync(timeout.Token));
        await writer.WriteLineAsync("RCPT TO:<external@outside.test>");
        Assert.StartsWith("550", await reader.ReadLineAsync(timeout.Token));
        await writer.WriteLineAsync("RCPT TO:<alice@example.test>");
        Assert.StartsWith("250", await reader.ReadLineAsync(timeout.Token));
        await writer.WriteLineAsync("DATA");
        Assert.StartsWith("354", await reader.ReadLineAsync(timeout.Token));
        await writer.WriteAsync(Message);
        await writer.WriteLineAsync(".");
        Assert.StartsWith("250", await reader.ReadLineAsync(timeout.Token));
        await using (var db = await env.Factory.CreateDbContextAsync())
            Assert.Single(await db.Messages.ToListAsync());
        await writer.WriteLineAsync("QUIT");
        Assert.StartsWith("221", await reader.ReadLineAsync(timeout.Token));
        timeout.Cancel();
        await Task.WhenAll(senderLoop, receiverLoop);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task MailboxQuotaBoundaryAndRollback(int remainingAfterMessage)
    {
        await using var env = await Environment.CreateAsync();
        var size = Encoding.UTF8.GetByteCount(Message);
        await env.SetQuotaAsync(size + remainingAfterMessage, false);
        await using var content = Content();
        if (remainingAfterMessage >= 0)
        {
            var accepted = await env.Inbound.AcceptAsync(Envelope(), content, CancellationToken.None);
            Assert.Equal(1, accepted.LocalDeliveries);
            await using var db = await env.Factory.CreateDbContextAsync();
            Assert.Equal(size, (await db.Accounts.SingleAsync(x => x.LocalPart == "alice")).UsedBytes);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<MailboxQuotaExceededException>(() => env.Inbound.AcceptAsync(Envelope(), content, CancellationToken.None));
            Assert.Equal("alice@example.test", failure.Address);
            await env.AssertOriginalRolledBackAsync();
            Assert.Empty(Directory.GetFiles(Path.Combine(env.Root, "messages"), "*.eml", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task MultiRecipientDomainFailureRollsBackEarlierLocalAndQueuedDeliveries()
    {
        await using var env = await Environment.CreateAsync();
        await env.SetQuotaAsync(Encoding.UTF8.GetByteCount(Message), true);
        await using var content = Content();
        var failure = await Assert.ThrowsAsync<DomainQuotaExceededException>(() => env.Inbound.AcceptAsync(
            Envelope(["external@outside.test", "alice@example.test", "bob@example.test"]) with { AuthenticatedUser = "sender@origin.test" },
            content, CancellationToken.None));
        Assert.Equal("example.test", failure.Domain);
        Assert.Equal("bob@example.test", failure.Address);
        await env.AssertOriginalRolledBackAsync();
    }

    [Theory]
    [InlineData(MailRuleDirection.Incoming, false)]
    [InlineData(MailRuleDirection.Incoming, true)]
    [InlineData(MailRuleDirection.Outgoing, false)]
    [InlineData(MailRuleDirection.Outgoing, true)]
    public async Task ForwardingQuotaFailureRetainsOriginalDelivery(MailRuleDirection direction, bool domainQuota)
    {
        await using var env = await Environment.CreateAsync();
        // Original recipient is bob; the failed forwarding target is alice.
        await env.SetQuotaAsync(1, false);
        Guid ruleDomainId;
        Guid senderId;
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            ruleDomainId = (await db.Domains.SingleAsync(x => x.Name == (direction == MailRuleDirection.Incoming ? "example.test" : "origin.test"))).Id;
            senderId = (await db.Accounts.SingleAsync(x => x.LocalPart == "sender")).Id;
            if (domainQuota)
            {
                // bob fits exactly; its forwarding copy does not fit the shared domain.
                (await db.Accounts.SingleAsync(x => x.LocalPart == "alice")).QuotaBytes = 100_000;
                (await db.Domains.SingleAsync(x => x.Name == "example.test")).QuotaBytes = Encoding.UTF8.GetByteCount(Message);
                await db.SaveChangesAsync();
            }
        }
        await env.Services.GetRequiredService<MailRuleService>().CreateAsync(ruleDomainId,
            new MailRuleInput("Quota forward", direction, MailRuleMatchMode.All,
                direction == MailRuleDirection.Incoming ? "INBOX" : "Sent", true,
                [new MailRuleConditionInput(MailRuleField.Subject, MailRuleOperator.EqualsAny, ["quota"])],
                [new MailRuleActionInput(MailRuleActionKind.ForwardTo, ["alice@example.test"])]), "test", CancellationToken.None);
        var envelope = Envelope(["bob@example.test"]);
        if (direction == MailRuleDirection.Outgoing)
            envelope = envelope with { AuthenticatedUser = "sender@origin.test", AuthenticatedAccountId = senderId };
        await using var content = Content();
        var accepted = await env.Inbound.AcceptAsync(envelope, content, CancellationToken.None);
        Assert.Equal(1, accepted.LocalDeliveries);
        await using var verification = await env.Factory.CreateDbContextAsync();
        Assert.Equal("bob", (await verification.MailboxMessages.Include(x => x.Mailbox).ThenInclude(x => x.Account).SingleAsync()).Mailbox.Account.LocalPart);
        Assert.Equal(0, (await verification.Accounts.SingleAsync(x => x.LocalPart == "alice")).UsedBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SmtpQuotaFailureKeepsConnectionAndAuthenticatedState(bool authenticated, bool senderFull)
    {
        await using var env = await Environment.CreateAsync();
        await env.SetQuotaAsync(1, false);
        if (senderFull)
        {
            await using var db = await env.Factory.CreateDbContextAsync();
            (await db.Accounts.SingleAsync(x => x.LocalPart == "sender")).QuotaBytes = 1;
            await db.SaveChangesAsync();
        }
        await using var session = await SmtpSession.ConnectAsync(env, authenticated);
        Assert.StartsWith("552 5.2.2", await session.SendMessageAsync("sender@origin.test", ["bob@example.test", "alice@example.test"]));
        Assert.StartsWith("250", await session.CommandAsync("NOOP")); // also waits for notification submission
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            Assert.False(await db.MailboxMessages.AnyAsync(x => x.Mailbox.Account.LocalPart != "sender"));
            Assert.False(await db.DeliveryJobs.AnyAsync());
            Assert.False(await db.SentCopyJobs.AnyAsync());
            var messages = await db.Messages.ToListAsync();
            if (authenticated && !senderFull)
            {
                var notification = Assert.Single(messages);
                Assert.Equal(string.Empty, notification.EnvelopeFrom);
                await using var stored = await env.Services.GetRequiredService<IMessageContentStore>().OpenReadAsync(notification.ContentKey, CancellationToken.None);
                var mime = await MimeMessage.LoadAsync(stored);
                Assert.Equal("auto-replied", mime.Headers["Auto-Submitted"]);
                Assert.Contains("手动联系收件人", mime.TextBody);
                Assert.Contains("所有收件人均未投递成功", mime.TextBody);
                Assert.Contains("未入队", mime.TextBody);
                Assert.Contains("bob@example.test", mime.TextBody);
                Assert.Equal("邮件投递失败：收件方容量超限", mime.Subject);
            }
            else Assert.Empty(messages);
            Assert.All(await db.Accounts.Where(x => x.Domain.Name == "example.test").ToListAsync(), x => Assert.Equal(0, x.UsedBytes));
        }
        // No EHLO or AUTH again: a fresh transaction must succeed on the same connection.
        Assert.StartsWith("250 2.0.0", await session.SendMessageAsync("sender@origin.test", ["bob@example.test"]));
        Assert.DoesNotContain(env.SmtpLog.Entries, x => x.Level >= LogLevel.Error);
        Assert.Contains(env.SmtpLog.Entries, x => x.Level == LogLevel.Information && x.Text.Contains("mailbox quota exceeded"));
    }

    [Fact]
    public async Task AnonymousNullSenderQuotaFailureDoesNotGenerateNotification()
    {
        await using var env = await Environment.CreateAsync();
        await env.SetQuotaAsync(1, false);
        await using var session = await SmtpSession.ConnectAsync(env, false);
        Assert.StartsWith("552 5.2.2", await session.SendMessageAsync("", ["alice@example.test"]));
        Assert.StartsWith("250", await session.CommandAsync("NOOP"));
        await env.AssertOriginalRolledBackAsync();
    }

    [Fact]
    public async Task DatabaseConflictReturnsTemporaryFailureAndKeepsSession()
    {
        await using var env = await Environment.CreateAsync(concurrencyFailure: true);
        await using var session = await SmtpSession.ConnectAsync(env, false);
        Assert.StartsWith("451 4.3.0", await session.SendMessageAsync("sender@origin.test", ["alice@example.test"]));
        Assert.StartsWith("250", await session.CommandAsync("MAIL FROM:<sender@origin.test>"));
        Assert.DoesNotContain(env.SmtpLog.Entries, x => x.Level >= LogLevel.Error);
        await env.AssertOriginalRolledBackAsync();
    }

    [Fact]
    public async Task SmtpDomainFailureAndMessageSizeLimitKeepDistinctResponses()
    {
        await using var env = await Environment.CreateAsync();
        await env.SetQuotaAsync(1, true);
        await using (var session = await SmtpSession.ConnectAsync(env, false))
        {
            Assert.StartsWith("552 5.2.2 Mailbox storage quota exceeded", await session.SendMessageAsync("sender@origin.test", ["bob@example.test"]));
            Assert.StartsWith("250", await session.CommandAsync("NOOP"));
        }
        await env.AssertOriginalRolledBackAsync();
        await using (var session = await SmtpSession.ConnectAsync(env, false, maximumMessageBytes: 32))
        {
            Assert.StartsWith("552 5.3.4", await session.SendMessageAsync("sender@origin.test", ["bob@example.test"]));
        }
    }

    [Theory]
    [InlineData(true, "5.2.2", DeliveryNotificationKind.QuotaExceeded)]
    [InlineData(false, "4.2.2", DeliveryNotificationKind.QuotaExceeded)]
    [InlineData(true, "5.3.4", DeliveryNotificationKind.PermanentFailure)]
    [InlineData(true, null, DeliveryNotificationKind.PermanentFailure)]
    [InlineData(false, "4.3.0", DeliveryNotificationKind.RetryExhausted)]
    [InlineData(false, "5.2.2", DeliveryNotificationKind.RetryExhausted)]
    public async Task FinalFailureSelectsAndRendersExpectedTemplate(bool permanent, string? status, DeliveryNotificationKind expected)
    {
        await using var env = await Environment.CreateAsync();
        var kind = DeliveryNotificationService.SelectKind(permanent, status);
        Assert.Equal(expected, kind);
        await env.Services.GetRequiredService<DeliveryNotificationService>().CreateAsync("sender@origin.test", "target@outside.test",
            "<remote error>", "queue123", 4, kind, CancellationToken.None);
        await using var db = await env.Factory.CreateDbContextAsync();
        var message = await db.Messages.SingleAsync();
        await using var stream = await env.Services.GetRequiredService<IMessageContentStore>().OpenReadAsync(message.ContentKey, CancellationToken.None);
        var mime = await MimeMessage.LoadAsync(stream);
        var templates = MessageTemplateDefaults.Create();
        Assert.Equal(expected switch
        {
            DeliveryNotificationKind.QuotaExceeded => templates.QuotaExceededSubject,
            DeliveryNotificationKind.PermanentFailure => templates.PermanentFailureSubject,
            _ => templates.RetryExhaustedSubject
        }, mime.Subject);
        Assert.Contains("&lt;remote error&gt;", mime.HtmlBody);
        Assert.Contains("target@outside.test", mime.TextBody);
    }

    [Theory]
    [InlineData("552 5.2.2 Mailbox full", "5.2.2")]
    [InlineData("4.2.2 Mailbox full", "4.2.2")]
    [InlineData("552 Message too large", null)]
    [InlineData("quota exceeded", null)]
    [InlineData("5.2.22 Another error", "5.2.22")]
    public void EnhancedStatusIsExtractedAsExactCode(string response, string? expected) =>
        Assert.Equal(expected, DirectMxTransport.ExtractEnhancedStatusCode(response));

    [Fact]
    public async Task LegacyTemplateUpdatePreservesQuotaTemplateAndRejectsUnsafeContent()
    {
        await using var env = await Environment.CreateAsync();
        var settings = env.Services.GetRequiredService<SystemSettingsService>();
        var defaults = await settings.GetMessageTemplateSettingsAsync(CancellationToken.None);
        Assert.Equal(MessageTemplateDefaults.Create().QuotaExceededBody, defaults.QuotaExceededBody);
        await settings.SaveMessageTemplateSettingsAsync(defaults with { QuotaExceededSubject = "Custom quota", QuotaExceededBody = "<p>Contact {recipient}</p>" }, CancellationToken.None);
        var legacy = new MessageTemplateSettingsView("Old client", "Permanent", "<p>P</p>", "Retry", "<p>R</p>");
        var saved = await settings.SaveMessageTemplateSettingsAsync(legacy, CancellationToken.None);
        Assert.Equal("Custom quota", saved.QuotaExceededSubject);
        Assert.Equal("<p>Contact {recipient}</p>", saved.QuotaExceededBody);
        Assert.Equal(saved, await settings.GetMessageTemplateSettingsAsync(CancellationToken.None));
        await Assert.ThrowsAsync<FormatException>(() => settings.SaveMessageTemplateSettingsAsync(saved with { QuotaExceededBody = "<script>bad</script>" }, CancellationToken.None));
        await Assert.ThrowsAsync<FormatException>(() => settings.SaveMessageTemplateSettingsAsync(saved with { QuotaExceededSubject = "bad\r\nSubject" }, CancellationToken.None));
    }

    [Fact]
    public async Task IndependentSqliteConnectionsCannotBothExceedQuota()
    {
        await using var env = await Environment.CreateAsync();
        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            var account = await db.Accounts.Include(x => x.Domain).SingleAsync(x => x.LocalPart == "alice");
            account.UsedBytes = 90;
            account.QuotaBytes = 100;
            account.Domain.UsedBytes = 90;
            await db.SaveChangesAsync();
        }
        using var start = new ManualResetEventSlim();
        async Task<Exception?> Attempt(string body)
        {
            start.Wait();
            await using var content = new MemoryStream(Encoding.ASCII.GetBytes(body));
            try { await env.Inbound.AcceptAsync(Envelope(), content, CancellationToken.None); return null; }
            catch (Exception exception) { return exception; }
        }
        var first = Task.Run(() => Attempt("12345678"));
        var second = Task.Run(() => Attempt("abcdefgh"));
        start.Set();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, x => x is null);
        var failure = Assert.Single(results, x => x is not null)!;
        Assert.True(failure is MailboxQuotaExceededException || DatabaseFailurePolicy.IsConcurrencyConflict(failure), failure.ToString());
        await using var verification = await env.Factory.CreateDbContextAsync();
        Assert.Equal(98, (await verification.Accounts.SingleAsync(x => x.LocalPart == "alice")).UsedBytes);
        Assert.Equal(98, (await verification.Domains.SingleAsync(x => x.Name == "example.test")).UsedBytes);
        Assert.Single(await verification.MailboxMessages.ToListAsync());
        Assert.Single(await verification.QuotaLedger.ToListAsync());
    }

    [Fact]
    public void DatabaseConflictClassificationDoesNotTreatQuotaOrOtherErrorsAsTransient()
    {
        Assert.True(DatabaseFailurePolicy.IsConcurrencyConflict(new DbUpdateException("conflict", new SqliteException("busy", 5))));
        Assert.False(DatabaseFailurePolicy.IsConcurrencyConflict(new SqliteException("constraint", 19)));
        Assert.False(DatabaseFailurePolicy.IsConcurrencyConflict(new MailboxQuotaExceededException("a@example.test")));
    }

    private static InboundEnvelope Envelope(string[]? recipients = null) =>
        new("sender@origin.test", recipients ?? ["alice@example.test"], null, "127.0.0.1", "test");

    private sealed class Environment(string root, ServiceProvider services) : IAsyncDisposable
    {
        public string Root => root;
        public ServiceProvider Services => services;
        public IDbContextFactory<MailDbContext> Factory => services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        public IInboundMessageService Inbound => services.GetRequiredService<IInboundMessageService>();
        public CaptureLogger<SmtpServerService> SmtpLog { get; } = new();

        public static async Task<Environment> CreateAsync(bool concurrencyFailure = false)
        {
            var root = Path.Combine(Path.GetTempPath(), $"mailserver-quota-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")};Default Timeout=5",
                ["Storage:RootPath"] = Path.Combine(root, "messages"),
                ["SecurityAssets:RootPath"] = Path.Combine(root, "security"),
                ["Forwarding:SrsSecret"] = "0123456789abcdef0123456789abcdef"
            }).Build();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton<IConfiguration>(configuration);
            collection.AddMailServerInfrastructure(configuration);
            if (concurrencyFailure) collection.AddSingleton<IInboundMessageService>(new ConflictInboundService());
            var services = collection.BuildServiceProvider();
            var env = new Environment(root, services);
            await using var db = await env.Factory.CreateDbContextAsync();
            await db.Database.MigrateAsync();
            var domain = new MailDomain { Name = "example.test", QuotaBytes = 1_000_000, MailHostName = "mail.example.test" };
            var senderDomain = new MailDomain { Name = "origin.test", QuotaBytes = 1_000_000 };
            var credentials = services.GetRequiredService<ICredentialService>();
            var alice = new MailAccount { Domain = domain, LocalPart = "alice", QuotaBytes = 100_000, PasswordHash = "unused" };
            var bob = new MailAccount { Domain = domain, LocalPart = "bob", QuotaBytes = 100_000, PasswordHash = "unused" };
            var sender = new MailAccount { Domain = senderDomain, LocalPart = "sender", QuotaBytes = 100_000, PasswordHash = credentials.HashPassword("test-secret") };
            db.AddRange(domain, senderDomain, alice, bob, sender,
                new Mailbox { Account = alice, Name = "INBOX" }, new Mailbox { Account = bob, Name = "INBOX" }, new Mailbox { Account = sender, Name = "INBOX" });
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=mail.example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("mail.example.test");
            request.CertificateExtensions.Add(san.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certPath = Path.Combine(root, "certificate.pem");
            var keyPath = Path.Combine(root, "key.pem");
            await File.WriteAllTextAsync(certPath, certificate.ExportCertificatePem());
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
            db.DomainCertificates.Add(new DomainCertificate { Domain = domain, CertificatePath = certPath, PrivateKeyPath = keyPath });
            await db.SaveChangesAsync();
            return env;
        }

        public async Task SetQuotaAsync(long quota, bool domain)
        {
            await using var db = await Factory.CreateDbContextAsync();
            if (domain) (await db.Domains.SingleAsync(x => x.Name == "example.test")).QuotaBytes = quota;
            else (await db.Accounts.SingleAsync(x => x.LocalPart == "alice")).QuotaBytes = quota;
            await db.SaveChangesAsync();
        }

        public async Task AssertOriginalRolledBackAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            Assert.Empty(await db.Messages.ToListAsync());
            Assert.Empty(await db.MailboxMessages.ToListAsync());
            Assert.Empty(await db.QuotaLedger.ToListAsync());
            Assert.Empty(await db.DeliveryJobs.ToListAsync());
            Assert.All(await db.Accounts.ToListAsync(), x => Assert.Equal(0, x.UsedBytes));
            Assert.All(await db.Domains.ToListAsync(), x => Assert.Equal(0, x.UsedBytes));
            Assert.All(await db.Mailboxes.ToListAsync(), x => Assert.Equal(1, x.UidNext));
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private sealed class SmtpSession(TcpClient client, Stream stream, Task serverTask) : IAsyncDisposable
    {
        private readonly StreamReader _reader = new(stream, Encoding.UTF8, false, 1024, true);
        private readonly StreamWriter _writer = new(stream, new UTF8Encoding(false), 1024, true) { NewLine = "\r\n", AutoFlush = true };

        public static async Task<SmtpSession> ConnectAsync(Environment env, bool authenticated, long maximumMessageBytes = 1_000_000)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var peer = await listener.AcceptTcpClientAsync();
            var service = new SmtpServerService(env.Services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new SmtpServerOptions { MaximumMessageBytes = maximumMessageBytes, CommandTimeoutSeconds = 10 }),
                env.Services.GetRequiredService<DomainCertificateProvider>(), env.SmtpLog);
            var endpoint = new SmtpEndpointOptions
            {
                RequireAuthentication = authenticated,
                Security = authenticated ? ConnectionSecurity.ImplicitTls : ConnectionSecurity.StartTls
            };
            var serverTask = (Task)typeof(SmtpServerService).GetMethod("HandleClientAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(service, [peer, endpoint, CancellationToken.None])!;
            Stream stream = client.GetStream();
            if (authenticated)
            {
                var tls = new SslStream(stream, false, (_, _, _, _) => true);
                await tls.AuthenticateAsClientAsync("mail.example.test");
                stream = tls;
            }
            var session = new SmtpSession(client, stream, serverTask);
            Assert.StartsWith("220", await session.ReadReplyAsync());
            Assert.StartsWith("250", await session.CommandAsync("EHLO client.test"));
            if (authenticated)
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0sender@origin.test\0test-secret"));
                Assert.StartsWith("235", await session.CommandAsync("AUTH PLAIN " + credentials));
            }
            return session;
        }

        public async Task<string> CommandAsync(string command)
        {
            await _writer.WriteLineAsync(command);
            return await ReadReplyAsync();
        }

        private async Task<string> ReadReplyAsync()
        {
            string line;
            do { line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) ?? throw new IOException("SMTP connection closed"); }
            while (line.Length > 3 && line[3] == '-');
            return line;
        }

        public async Task<string> SendMessageAsync(string sender, string[] recipients)
        {
            Assert.StartsWith("250", await CommandAsync($"MAIL FROM:<{sender}>"));
            foreach (var recipient in recipients) Assert.StartsWith("250", await CommandAsync($"RCPT TO:<{recipient}>"));
            Assert.StartsWith("354", await CommandAsync("DATA"));
            await _writer.WriteAsync(Message + ".\r\n");
            await _writer.FlushAsync();
            return await ReadReplyAsync();
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await stream.DisposeAsync();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(15));
            _reader.Dispose();
            _writer.Dispose();
        }
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public ConcurrentBag<(LogLevel Level, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ConflictInboundService : IInboundMessageService
    {
        public Task<bool> IsLocalRecipientAsync(string address, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<InboundAcceptance> AcceptAsync(InboundEnvelope envelope, Stream content, CancellationToken cancellationToken) =>
            throw new DbUpdateException("Concurrent transaction", new SqliteException("Database is busy", 5));
    }
}
