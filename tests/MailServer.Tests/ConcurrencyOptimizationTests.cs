using System.Data.Common;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MailKit.Security;
using MailServer.Domain;
using MailServer.Imap;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace MailServer.Tests;

public sealed class ConcurrencyOptimizationTests
{
    [Fact]
    public async Task UpgradeBackfillsExistingMessagesAndNeverRegressesUidNext()
    {
        await using var env = await Database.CreateAsync(migrate: true);
        long mailboxId;
        await using (var db = env.Factory.CreateDbContext())
        {
            var mailbox = new Mailbox { Name = "INBOX", Account = new MailAccount
                { LocalPart = "old", PasswordHash = "unused", Domain = new MailDomain { Name = "upgrade.test" } } };
            db.Mailboxes.Add(mailbox);
            for (var index = 1; index <= 3; index++)
                db.MailboxMessages.Add(new MailboxMessage { Mailbox = mailbox, Uid = index * 10,
                    Flags = index == 1 ? "\\sEeN" : "", DeletedAt = index == 3 ? DateTimeOffset.UtcNow : null,
                    Message = new StoredMessage { ContentKey = index.ToString().PadLeft(64, '0'), EnvelopeFrom = "old@test" } });
            await db.SaveChangesAsync();
            mailboxId = mailbox.Id;
            await db.GetService<IMigrator>().MigrateAsync("20261012053237_InitialSnowflakeMailServer");
            await db.Database.ExecuteSqlRawAsync("UPDATE mailboxes SET UidNext = 1");
            await db.Database.MigrateAsync();
        }
        await AssertCountsAsync(env, mailboxId, 2, 1);
        await using var verify = env.Factory.CreateDbContext();
        Assert.Equal(31, (await verify.Mailboxes.SingleAsync()).UidNext);
        Assert.Equal(mailboxId, (await verify.Mailboxes.SingleAsync(x => x.Name == "inbox")).Id);
        Assert.Equal(3, await verify.MailboxMessages.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task FirewallRadixIndexMatchesReferenceAcrossIpv4AndIpv6Prefixes()
    {
        await using var env = await Database.CreateAsync();
        var random = new Random(4817);
        var rules = new List<string> { "192.0.2.1/32", "2001:db8::1/128", "10.123.4.5/8", "10.42.1.1/17", "2001:db8:1234::/47" };
        foreach (var size in new[] { 4, 16 })
            for (var index = 0; index < 70; index++)
            {
                var bytes = new byte[size]; random.NextBytes(bytes);
                rules.Add($"{new IPAddress(bytes)}/{random.Next(5, size * 8 + 1)}");
            }
        await using (var db = env.Factory.CreateDbContext())
        {
            db.FirewallRules.AddRange(rules.Select(cidr => new FirewallRule { Cidr = cidr, IsEnabled = true }));
            await db.SaveChangesAsync();
        }
        using var cache = new ConfigurationSnapshotCache(Options.Create(new ConfigurationCacheOptions()),
            NullLogger<ConfigurationSnapshotCache>.Instance);
        var firewall = new IpFirewallService(env.Factory, NullLogger<IpFirewallService>.Instance, cache);
        var addresses = rules.Select(x => IPAddress.Parse(x[..x.IndexOf('/')])).ToList();
        foreach (var size in new[] { 4, 16 })
            for (var index = 0; index < 1000; index++)
            {
                var bytes = new byte[size]; random.NextBytes(bytes); addresses.Add(new IPAddress(bytes));
            }
        foreach (var address in addresses)
            Assert.Equal(rules.Any(cidr => IpFirewallRules.Matches(cidr, address)), await firewall.IsBlockedAsync(address, default));
    }

    [Fact]
    public async Task SelectionUsesMemoryThenSpillsAndRetainsRepeatableReads()
    {
        await using var snapshot = new MailboxSelectionSnapshot();
        var item = new MailboxItem(1, 1, 1, "\\Seen", 1, DateTimeOffset.UtcNow, 10, new string('a', 64), 2);
        await snapshot.AddAsync(item, default);
        Assert.False(File.Exists(snapshot.FilePath));
        for (var index = 2; index <= 1100; index++) await snapshot.AddAsync(item with { Id = index, Uid = index }, default);
        Assert.True(File.Exists(snapshot.FilePath));
        await snapshot.SealAsync(default);
        for (var pass = 0; pass < 2; pass++)
        {
            var count = 0;
            await foreach (var value in snapshot.ReadAsync(default)) Assert.Equal(++count, value.Id);
            Assert.Equal(1100, count);
        }
        await snapshot.DisposeAsync();
        Assert.False(File.Exists(snapshot.FilePath));
    }

    [Fact]
    public async Task SmallLiteralAndBufferedResponseAvoidFilesAndPerFieldFlushes()
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("folder"));
        await using var connection = new ImapConnection(input, TimeSpan.FromSeconds(1), 65536);
        await using var literal = await connection.ReadLiteralAsync(6, default);
        Assert.IsType<PooledMemoryStream>(literal);
        Assert.Equal("folder", await new StreamReader(literal).ReadToEndAsync());
        await using var output = new CountStream();
        await using var writer = new ImapConnection(output, TimeSpan.FromSeconds(1), 65536);
        using (writer.BufferResponse())
        {
            await writer.WriteAsync("* 1 FETCH (", default);
            await writer.WriteAsync("UID 1 FLAGS (\\Seen)", default);
            await writer.WriteLineAsync(")", default);
            Assert.Equal(0, output.Flushes);
            await writer.FlushResponseAsync(default);
        }
        Assert.Equal(1, output.Flushes);
        Assert.Equal("* 1 FETCH (UID 1 FLAGS (\\Seen))\r\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task CountersFollowFlagsMoveRecycleRestoreAndRollback()
    {
        await using var env = await Database.CreateAsync();
        long firstId, secondId, itemId;
        await using (var db = env.Factory.CreateDbContext())
        {
            var account = new MailAccount { Domain = new MailDomain { Name = "counts.test" }, LocalPart = "a", PasswordHash = "unused" };
            var first = new Mailbox { Account = account, Name = "INBOX" };
            var second = new Mailbox { Account = account, Name = "Other" };
            db.Mailboxes.AddRange(first, second);
            var item = new MailboxMessage { Mailbox = first, Uid = 10, Message = new StoredMessage { ContentKey = new string('a', 64), EnvelopeFrom = "a@test" } };
            db.MailboxMessages.Add(item);
            await db.SaveChangesAsync();
            (firstId, secondId, itemId) = (first.Id, second.Id, item.Id);
        }
        await AssertCountsAsync(env, firstId, 1, 1);
        await using (var db = env.Factory.CreateDbContext())
        {
            var item = await db.MailboxMessages.SingleAsync(x => x.Id == itemId);
            item.Flags = "\\sEeN \\Flagged";
            await db.SaveChangesAsync();
            await AssertCountsAsync(env, firstId, 1, 0);
            item.MailboxId = secondId;
            await db.SaveChangesAsync();
            await AssertCountsAsync(env, firstId, 0, 0);
            await AssertCountsAsync(env, secondId, 1, 0);
            item.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            await AssertCountsAsync(env, secondId, 0, 0);
            item.DeletedAt = null;
            item.Flags = string.Empty;
            await db.SaveChangesAsync();
            await AssertCountsAsync(env, secondId, 1, 1);
            await using var tx = await db.Database.BeginTransactionAsync();
            db.MailboxMessages.Remove(item);
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        await AssertCountsAsync(env, secondId, 1, 1);
    }

    [Fact]
    public async Task AtomicDomainQuotaRejectsStaleWriterAndRollsBackItsAccountDelta()
    {
        await using var env = await Database.CreateAsync();
        await using (var seed = env.Factory.CreateDbContext())
        {
            var domain = new MailDomain { Name = "quota.test", QuotaBytes = 5 };
            seed.Accounts.AddRange(new MailAccount { Domain = domain, LocalPart = "a", PasswordHash = "unused" },
                new MailAccount { Domain = domain, LocalPart = "b", PasswordHash = "unused" });
            await seed.SaveChangesAsync();
        }
        await using var first = env.Factory.CreateDbContext();
        await using var second = env.Factory.CreateDbContext();
        var a = await first.Accounts.Include(x => x.Domain).SingleAsync(x => x.LocalPart == "a");
        var b = await second.Accounts.Include(x => x.Domain).SingleAsync(x => x.LocalPart == "b");
        first.AtomicQuotaUpdates = second.AtomicQuotaUpdates = true;
        MailboxWriteAccounting.ApplyQuotaDelta(a, 4);
        await first.SaveChangesAsync();
        MailboxWriteAccounting.ApplyQuotaDelta(b, 4);
        second.Messages.Add(new StoredMessage { ContentKey = new string('b', 64), EnvelopeFrom = "a@test" });
        await Assert.ThrowsAsync<DomainQuotaExceededException>(() => second.SaveChangesAsync());
        await using var verify = env.Factory.CreateDbContext();
        Assert.Equal(4, (await verify.Domains.SingleAsync()).UsedBytes);
        Assert.Equal(0, (await verify.Accounts.SingleAsync(x => x.LocalPart == "b")).UsedBytes);
        Assert.Empty(await verify.Messages.ToListAsync());
    }

    [Fact]
    public async Task PersistedFetchMetadataSurvivesCacheRestartAndCoalescesMisses()
    {
        await using var env = await Database.CreateAsync();
        var message = new StoredMessage { ContentKey = new string('c', 64), EnvelopeFrom = "a@test" };
        await using (var seed = env.Factory.CreateDbContext()) { seed.Messages.Add(message); await seed.SaveChangesAsync(); }
        var builds = 0;
        using (var cache = new MessageFetchMetadataCache(env.Factory))
        {
            var values = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => cache.GetAsync(message.Id, message.ContentKey, async token =>
            {
                Interlocked.Increment(ref builds);
                await Task.Delay(10, token);
                return new MessageFetchMetadata { Envelope = "cached-envelope", BodyStructure = "cached-structure", HeaderEnd = 12, BodyStart = 14 };
            }, default)));
            Assert.All(values, x => Assert.Equal("cached-envelope", x.Envelope));
            Assert.Equal(1, builds);
        }
        using var restarted = new MessageFetchMetadataCache(env.Factory);
        var hit = await restarted.GetAsync(message.Id, message.ContentKey, _ => throw new InvalidOperationException("must not parse"), default);
        Assert.Equal(14, hit.BodyStart);
    }

    [Fact]
    public async Task CertificateHotPathReusesContextWithoutDatabaseReads()
    {
        await using var env = await Database.CreateAsync();
        using var rsa = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=mail.tls.test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certPath = Path.Combine(env.Root, "cert.pem");
        var keyPath = Path.Combine(env.Root, "key.pem");
        await File.WriteAllTextAsync(certPath, certificate.ExportCertificatePem());
        await File.WriteAllTextAsync(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        await using (var db = env.Factory.CreateDbContext())
        {
            db.DomainCertificates.Add(new DomainCertificate { Domain = new MailDomain { Name = "tls.test", MailHostName = "mail.tls.test" },
                CertificatePath = certPath, PrivateKeyPath = keyPath });
            await db.SaveChangesAsync();
        }
        var settings = new SystemSettingsService(env.Factory, Options.Create(new DeliveryOptions()), Options.Create(new MailboxDefaultsOptions()), Options.Create(new CertificatesOptions()));
        using var provider = new DomainCertificateProvider(env.Factory, settings, NullLogger<DomainCertificateProvider>.Instance);
        var hello = new SslClientHelloInfo("mail.tls.test", System.Security.Authentication.SslProtocols.Tls12);
        var first = await provider.SelectOptionsAsync(hello, default);
        env.Reads.Count = 0;
        for (var index = 0; index < 100; index++) Assert.Same(first.ServerCertificateContext, (await provider.SelectOptionsAsync(hello, default)).ServerCertificateContext);
        Assert.Equal(0, env.Reads.Count);
    }

    [Fact]
    public async Task NamedPipeHintsReachOtherInstanceWithoutFeedbackLoop()
    {
        var root = Path.Combine(Path.GetTempPath(), "mail-ipc-test-" + Guid.NewGuid());
        var options = Options.Create(new StorageOptions { RootPath = root });
        var firstDelivery = new DeliveryWakeSignal(); var secondDelivery = new DeliveryWakeSignal();
        var firstMailbox = new MailboxChangeSignal(); var secondMailbox = new MailboxChangeSignal();
        using var first = new ProcessWakeService(firstDelivery, firstMailbox, options, NullLogger<ProcessWakeService>.Instance);
        using var second = new ProcessWakeService(secondDelivery, secondMailbox, options, NullLogger<ProcessWakeService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var changed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        secondMailbox.Changed += id => changed.TrySetResult(id);
        try
        {
            await first.StartAsync(timeout.Token); await second.StartAsync(timeout.Token);
            while (!Directory.Exists(Path.Combine(root, ".signals")) || Directory.GetFiles(Path.Combine(root, ".signals")).Length != 2)
                await Task.Delay(10, timeout.Token);
            // First pulse may precede receiver startup; repeated hints are deliberately harmless.
            while (!changed.Task.IsCompleted)
            {
                firstMailbox.Pulse(123); firstDelivery.Pulse();
                await Task.Delay(100, timeout.Token);
            }
            Assert.Equal(123, await changed.Task);
            Assert.True(secondDelivery.Version > 0);
            var version = secondDelivery.Version;
            await Task.Delay(250, timeout.Token);
            Assert.Equal(version, secondDelivery.Version);
        }
        finally
        {
            await first.StopAsync(default); await second.StopAsync(default);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BatchSmtpPreservesPartialRecipientRejectionsAndSendsDataOnce()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var dataCount = 0;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            using var reader = new StreamReader(peer.GetStream());
            using var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 ready");
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                if (line.StartsWith("RCPT TO:<temp", StringComparison.OrdinalIgnoreCase)) { await writer.WriteLineAsync("450 4.2.0 later"); continue; }
                if (line.StartsWith("RCPT TO:<bad", StringComparison.OrdinalIgnoreCase)) { await writer.WriteLineAsync("550 5.1.1 missing"); continue; }
                if (line == "DATA")
                {
                    dataCount++; await writer.WriteLineAsync("354 go");
                    while (await reader.ReadLineAsync(timeout.Token) is { } body && body != ".") { }
                }
                await writer.WriteLineAsync("250 OK");
            }
        }, timeout.Token);
        using var client = new RecipientSmtpClient { CollectRecipientFailures = true };
        await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, SecureSocketOptions.None, timeout.Token);
        using var message = new MimeMessage { Body = new TextPart("plain") { Text = "shared" } };
        await client.SendAsync(message, MailboxAddress.Parse("sender@test"),
            new[] { "temp@test", "good@test", "bad@test" }.Select(MailboxAddress.Parse), timeout.Token);
        Assert.Equal(450, (int)client.Rejected["temp@test"].StatusCode);
        Assert.Equal(550, (int)client.Rejected["bad@test"].StatusCode);
        await client.DisconnectAsync(true, timeout.Token); await server;
        Assert.Equal(1, dataCount);
    }

    private static async Task AssertCountsAsync(Database env, long id, long exists, long unseen)
    {
        await using var db = env.Factory.CreateDbContext();
        var mailbox = await db.Mailboxes.SingleAsync(x => x.Id == id);
        Assert.Equal(exists, mailbox.MessageCount); Assert.Equal(unseen, mailbox.UnseenCount);
    }
    private sealed class CountStream : MemoryStream
    {
        public int Flushes;
        public override Task FlushAsync(CancellationToken cancellationToken) { Flushes++; return base.FlushAsync(cancellationToken); }
    }
    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Count); return ValueTask.FromResult(result); }
    }
    private sealed class Database(string root, TestMailDbContextFactory factory, ReadCounter reads) : IAsyncDisposable
    {
        public string Root => root;
        public TestMailDbContextFactory Factory => factory;
        public ReadCounter Reads => reads;
        public static async Task<Database> CreateAsync(bool migrate = false)
        {
            SqliteNative.Initialize();
            var root = Path.Combine(Path.GetTempPath(), "mail-optimization-" + Guid.NewGuid()); Directory.CreateDirectory(root);
            var counter = new ReadCounter();
            var options = new DbContextOptionsBuilder<MailDbContext>().UseSqlite($"Data Source={Path.Combine(root, "mail.db")};Pooling=False",
                sqlite => sqlite.MigrationsAssembly("MailServer.Migrations.Sqlite"))
                .AddInterceptors(counter).Options;
            var factory = new TestMailDbContextFactory(options);
            await using var db = factory.CreateDbContext();
            if (migrate) await db.Database.MigrateAsync(); else await db.Database.EnsureCreatedAsync();
            return new(root, factory, counter);
        }
        public ValueTask DisposeAsync() { Directory.Delete(root, true); return ValueTask.CompletedTask; }
    }
}
