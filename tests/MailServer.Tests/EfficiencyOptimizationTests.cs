using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit.Security;
using MailServer.Domain;
using MailServer.Imap;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace MailServer.Tests;

public sealed class EfficiencyOptimizationTests
{
    [Fact]
    public async Task QueueSummaryUsesTwoReadsIncludingEmptyQueue()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new AdminApplicationService(fixture.Factory, null!, null!, null!, null!, null!, null!, null!, null!);
        fixture.Counter.Reads = 0;
        var empty = await service.GetDeliveryQueueSummaryAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Counter.Reads);
        Assert.Equal(0, empty.Pending);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            foreach (var status in Enum.GetValues<DeliveryStatus>())
                db.DeliveryJobs.Add(new DeliveryJob { Message = new StoredMessage
                { ContentKey = status.ToString(), EnvelopeFrom = "a@test.example" },
                    Sender = "a@test.example", Recipient = "b@test.example", Status = status });
            await db.SaveChangesAsync();
        }
        fixture.Counter.Reads = 0;
        var summary = await service.GetDeliveryQueueSummaryAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Counter.Reads);
        Assert.Equal(1, summary.Pending);
        Assert.Equal(1, summary.Processing);
        Assert.Equal(1, summary.Deferred);
        Assert.Equal(1, summary.Failed);
    }

    [Fact]
    public async Task UnreadAggregationPreservesFlagBoundariesCaseAndSoftDeletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mailbox = await fixture.SeedMailboxAsync();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            long uid = 0;
            foreach (var flags in new[] { "", "\\Seenish", "  \\sEeN  ", "keyword \\Seen", "\\Seen other" })
                db.MailboxMessages.Add(new MailboxMessage { MailboxId = mailbox.Id, Uid = ++uid, Flags = flags,
                    Message = new StoredMessage { ContentKey = uid.ToString(), EnvelopeFrom = "a@test.example" } });
            db.MailboxMessages.Add(new MailboxMessage { MailboxId = mailbox.Id, Uid = ++uid,
                DeletedAt = DateTimeOffset.UtcNow,
                Message = new StoredMessage { ContentKey = "deleted", EnvelopeFrom = "a@test.example" } });
            await db.SaveChangesAsync();
        }
        var summary = await fixture.Service.SelectAsync(mailbox.AccountId, "INBOX", CancellationToken.None);
        Assert.NotNull(summary);
        Assert.Equal(5, summary.Exists);
        Assert.Equal(2, summary.Unseen);
    }

    [Fact]
    public async Task IndexedSearchMatchesOriginalAndFallsBackForMissingAndTruncatedRecords()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mailbox = await fixture.SeedMailboxAsync();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            for (var uid = 1; uid <= 3; uid++)
            {
                var mime = new MimeMessage { Subject = "中文 Résumé", Body = new TextPart("plain")
                    { Text = uid == 3 ? new string('x', MessageSearchBackfillService.MaximumTextBytes + 100) + "needle" : "needle" } };
                using var raw = new MemoryStream();
                mime.WriteTo(raw);
                fixture.Store.Messages[uid.ToString()] = raw.ToArray();
                var stored = new StoredMessage { ContentKey = uid.ToString(), EnvelopeFrom = "a@test.example" };
                db.MailboxMessages.Add(new MailboxMessage { MailboxId = mailbox.Id, Uid = uid, Message = stored });
                if (uid != 2) db.MessageSearchRecords.Add(MessageSearchBackfillService.BuildRecord(stored.Id, mime));
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal(new long[] { 1, 2, 3 }, await fixture.Service.SearchAsync(mailbox.Id, "BODY needle", true, CancellationToken.None));
        Assert.Equal(2, fixture.Store.Reads);
        fixture.Store.Reads = 0;
        Assert.Equal(new long[] { 1, 2, 3 }, await fixture.Service.SearchAsync(mailbox.Id, "SUBJECT resume", true, CancellationToken.None));
        Assert.Equal(1, fixture.Store.Reads);
        Assert.Equal(new long[] { 1, 2, 3 }, await fixture.Service.SearchAsync(mailbox.Id, "SUBJECT 中文", true, CancellationToken.None));
        fixture.Store.Reads = 0;
        Assert.Empty(await fixture.Service.SearchAsync(mailbox.Id, "BODY absent", true, CancellationToken.None));
        Assert.Equal(2, fixture.Store.Reads);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var first = await db.MailboxMessages.SingleAsync(x => x.MailboxId == mailbox.Id && x.Uid == 1);
            var record = await db.MessageSearchRecords.SingleAsync(x => x.MessageId == first.MessageId);
            record.ParserVersion = 0;
            await db.SaveChangesAsync();
        }
        fixture.Store.Reads = 0;
        Assert.Equal(new long[] { 1, 2, 3 }, await fixture.Service.SearchAsync(mailbox.Id, "BODY needle", true, CancellationToken.None));
        Assert.Equal(3, fixture.Store.Reads);
        fixture.SearchSettings.Enabled = false;
        fixture.Store.Reads = 0;
        Assert.Equal(new long[] { 1, 2, 3 }, await fixture.Service.SearchAsync(mailbox.Id, "BODY needle", true, CancellationToken.None));
        Assert.Equal(3, fixture.Store.Reads);
    }

    [Fact]
    public void OversizedBodyCanRetainHeadersWithoutMaterializingSearchText()
    {
        using var message = new MimeMessage { Subject = "中文 Résumé", Body = new TextPart("plain")
            { Text = new string('x', 1024 * 1024) } };
        var record = MessageSearchBackfillService.BuildRecord(Guid.NewGuid(), message, omitBody: true);
        Assert.Equal("中文 RESUME", record.Subject);
        Assert.Empty(record.Body);
        Assert.False(record.Complete);
        Assert.Equal("HeadersReady", record.Status);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task CompleteHeadersAvoidSourceReadsWhileIncompleteUnicodeBodyStillFallsBack(
        bool completeHeaders, bool persistentStreams)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.SearchSettings.UseCompleteHeaders = completeHeaders;
        fixture.SearchSettings.UsePersistentSearchStreams = persistentStreams;
        var mailbox = await fixture.SeedMailboxAsync();
        using var mime = new MimeMessage { Subject = "中文 Résumé", Body = new TextPart("plain")
            { Text = new string('x', MessageSearchBackfillService.MaximumTextBytes + 100) + " 中文 ＡＢＣ e\u0301 needle" } };
        mime.Headers.Add("X-Test", "中文 Résumé");
        using var raw = new MemoryStream();
        mime.WriteTo(raw);
        fixture.Store.Messages["large"] = raw.ToArray();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var stored = new StoredMessage { ContentKey = "large", EnvelopeFrom = "a@test.example" };
            db.MailboxMessages.Add(new MailboxMessage { MailboxId = mailbox.Id, Uid = 1, Message = stored });
            db.MessageSearchRecords.Add(MessageSearchBackfillService.BuildRecord(stored.Id, mime, omitBody: true));
            await db.SaveChangesAsync();
        }
        foreach (var criterion in new[] { "SUBJECT absent", "HEADER X-Test absent", "HEADER X-Missing absent" })
        {
            fixture.Store.Reads = 0;
            Assert.Empty(await fixture.Service.SearchAsync(mailbox.Id, criterion, true, CancellationToken.None));
            Assert.Equal(completeHeaders ? 0 : 1, fixture.Store.Reads);
        }
        foreach (var criterion in new[] { "BODY 中文", "BODY abc", "BODY e", "BODY needle" })
        {
            fixture.Store.Reads = 0;
            Assert.Equal(new long[] { 1 }, await fixture.Service.SearchAsync(mailbox.Id, criterion, true, CancellationToken.None));
            Assert.Equal(1, fixture.Store.Reads);
        }
        fixture.Store.Reads = 0;
        Assert.Empty(await fixture.Service.SearchAsync(mailbox.Id, "BODY absent", true, CancellationToken.None));
        Assert.Equal(1, fixture.Store.Reads);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var record = await db.MessageSearchRecords.SingleAsync();
            record.ParserVersion = 1;
            await db.SaveChangesAsync();
        }
        fixture.Store.Reads = 0;
        Assert.Empty(await fixture.Service.SearchAsync(mailbox.Id, "SUBJECT absent", true, CancellationToken.None));
        Assert.Equal(1, fixture.Store.Reads);
    }

    [Fact]
    public void SearchTextBudgetCountsUtf8BytesAndPreservesCharacterBoundaries()
    {
        using var message = new MimeMessage { Subject = "中文", Body = new TextPart("plain")
            { Text = string.Concat(Enumerable.Repeat("中文😀", 100_000)) } };
        var record = MessageSearchBackfillService.BuildRecord(Guid.NewGuid(), message);
        Assert.False(record.Complete);
        Assert.True(Encoding.UTF8.GetByteCount(record.Subject) + Encoding.UTF8.GetByteCount(record.HeadersJson)
            + Encoding.UTF8.GetByteCount(record.Body) <= MessageSearchBackfillService.MaximumTextBytes);
        Assert.False(char.IsHighSurrogate(record.Body[^1]));
    }

    [Fact]
    public async Task OversizedHeadersStillSearchTheOriginalSubject()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mailbox = await fixture.SeedMailboxAsync();
        using var mime = new MimeMessage { Subject = "中文 Résumé", Body = new TextPart("plain") { Text = "body" } };
        mime.Headers.Add("X-Large", new string('x', MessageSearchBackfillService.MaximumTextBytes + 100));
        using var raw = new MemoryStream();
        mime.WriteTo(raw);
        fixture.Store.Messages["headers"] = raw.ToArray();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var stored = new StoredMessage { ContentKey = "headers", EnvelopeFrom = "a@test.example" };
            db.MailboxMessages.Add(new MailboxMessage { MailboxId = mailbox.Id, Uid = 1, Message = stored });
            var record = MessageSearchBackfillService.BuildRecord(stored.Id, mime);
            Assert.Equal("Incomplete", record.Status);
            db.MessageSearchRecords.Add(record);
            await db.SaveChangesAsync();
        }
        Assert.Equal(new long[] { 1 }, await fixture.Service.SearchAsync(mailbox.Id, "SUBJECT resume", true, CancellationToken.None));
        Assert.Equal(1, fixture.Store.Reads);
        Assert.Empty(await fixture.Service.SearchAsync(mailbox.Id, "SUBJECT absent", true, CancellationToken.None));
        Assert.Equal(2, fixture.Store.Reads);
    }

    [Fact]
    public void AsciiNormalizationMatchesUnicodePathIncludingControlsAndMixedText()
    {
        var samples = new[]
        {
            string.Empty, new string(Enumerable.Range(0, 128).Select(x => (char)x).ToArray()),
            "Subject: plain ASCII\r\nRésumé 中文 ＡＢＣ e\u0301", "中文😀\r\n", "\t\r\n ABC xyz 123"
        };
        foreach (var value in samples)
            Assert.Equal(MailboxService.NormalizeSearchText(value, false), MailboxService.NormalizeSearchText(value, true));
    }

    [Theory]
    [InlineData("plain", "中文 Résumé ＡＢＣ e\u0301\r\nbody")]
    [InlineData("html", "<p>中文 <b>Résumé</b> ＡＢＣ e\u0301</p>")]
    [InlineData("plain", "\t\r\n ")]
    public void CachedBodyDecodingPreservesPlainHtmlAndWhitespaceSearchText(string subtype, string text)
    {
        using var mime = new MimeMessage { Body = new TextPart(subtype) { Text = text } };
        Assert.Equal(MailboxService.ExtractSearchableBody(mime, false), MailboxService.ExtractSearchableBody(mime, true));
    }

    [Fact]
    public async Task FinalMessageDeletionCascadesSearchRecord()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var message = new StoredMessage { ContentKey = "delete", EnvelopeFrom = "a@test.example" };
        db.Messages.Add(message);
        db.MessageSearchRecords.Add(new MessageSearchRecord { MessageId = message.Id, Complete = true, ParserVersion = 1 });
        await db.SaveChangesAsync();
        await db.Messages.Where(x => x.Id == message.Id).ExecuteDeleteAsync();
        Assert.False(await db.MessageSearchRecords.AnyAsync());
    }

    [Fact]
    public async Task OneHundredIdleSubscribersShareRefreshAndCommitWake()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mailbox = await fixture.SeedMailboxAsync();
        using var provider = new ServiceCollection().AddSingleton<IMailboxService>(fixture.Service).BuildServiceProvider();
        var changes = new MailboxChangeSignal();
        using var coordinator = new IdleMailboxCoordinator(provider.GetRequiredService<IServiceScopeFactory>(), changes, NullLogger.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        fixture.Counter.Reads = 0;
        var subscriptions = Enumerable.Range(0, 100)
            .Select(_ => coordinator.Subscribe(mailbox.AccountId, "INBOX", TimeSpan.FromHours(1))).ToArray();
        try
        {
            var first = await Task.WhenAll(subscriptions.Select(x => x.WaitAsync(0, timeout.Token)));
            Assert.Equal(3, fixture.Counter.Reads);
            fixture.Counter.Reads = 0;
            var waiting = subscriptions.Select((x, index) => x.WaitAsync(first[index].Version, timeout.Token)).ToArray();
            changes.Pulse(mailbox.Id);
            await Task.WhenAll(waiting);
            Assert.Equal(3, fixture.Counter.Reads);
        }
        finally { foreach (var subscription in subscriptions) subscription.Dispose(); }
    }

    [Fact]
    public async Task DeliveryWakeRetainsCommitBetweenPollAndWait()
    {
        var signal = new DeliveryWakeSignal();
        var observed = signal.Version;
        signal.Pulse();
        await signal.WaitForChangeAsync(observed, TimeSpan.FromHours(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var waiting = signal.WaitForChangeAsync(signal.Version, TimeSpan.FromHours(1), timeout.Token);
        signal.Pulse();
        await waiting;
    }

    [Fact]
    public async Task TwentyMessagesReuseOneExclusiveConnectionAndPolicyChangeSeparatesIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var deliveries = 0;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 mx.test ready");
            while (await reader.ReadLineAsync(timeout.Token) is { } command)
            {
                if (command == "DATA")
                {
                    await writer.WriteLineAsync("354 send");
                    while (await reader.ReadLineAsync(timeout.Token) is { } line && line != ".") { }
                    Interlocked.Increment(ref deliveries);
                }
                await writer.WriteLineAsync("250 OK");
            }
        }, timeout.Token);
        using (var pool = new SmtpConnectionPool(Options.Create(new SmtpConnectionReuseOptions { Enabled = true })))
        {
            for (var index = 0; index < 20; index++)
            {
                using var lease = pool.Rent("same-policy", true);
                Assert.Equal(index != 0, lease.Reused);
                if (!lease.Reused) await lease.Client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, timeout.Token);
                var message = new MimeMessage { Subject = "test", Body = new TextPart("plain") { Text = "body" } };
                await lease.Client.SendAsync(message, new MailboxAddress("", "a@test.example"),
                    [new MailboxAddress("", "b@test.example")], timeout.Token);
                lease.MarkSuccessful("127.0.0.1");
            }
            using var different = pool.Rent("changed-policy", true);
            Assert.False(different.Reused);
        }
        await server;
        Assert.Equal(20, deliveries);
    }

    private sealed class CountingStore : IMessageContentStore
    {
        internal readonly Dictionary<string, byte[]> Messages = [];
        internal int Reads;
        internal readonly List<string> Sql = [];
        public async Task<ContentDescriptor> SaveAsync(Stream content, CancellationToken token)
        {
            using var buffer = new MemoryStream(); await content.CopyToAsync(buffer, token);
            var key = Guid.NewGuid().ToString("N"); Messages[key] = buffer.ToArray();
            return new ContentDescriptor(key, buffer.Length);
        }
        public Task<Stream> OpenReadAsync(string key, CancellationToken token)
        {
            Reads++;
            return Task.FromResult<Stream>(new MemoryStream(Messages[key], false));
        }
        public Task DeleteIfUnreferencedAsync(string key, CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public async Task ConnectionPoolDiscardsMessageLimitIdleExpiryAndUncertainAttempt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            for (var index = 0; index < 3; index++)
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var stream = peer.GetStream();
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
                await writer.WriteLineAsync("220 mx.test ready");
                while (await reader.ReadLineAsync(timeout.Token) is not null) await writer.WriteLineAsync("250 OK");
            }
        }, timeout.Token);
        var clock = new Clock();
        using var pool = new SmtpConnectionPool(Options.Create(new SmtpConnectionReuseOptions
            { Enabled = true, MaximumMessages = 2 }), clock);
        using (var first = pool.Rent("target", true))
        {
            await first.Client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, timeout.Token);
            first.MarkSuccessful("127.0.0.1");
        }
        using (var second = pool.Rent("target", true))
        {
            Assert.True(second.Reused);
            second.MarkSuccessful("127.0.0.1");
        }
        using (var third = pool.Rent("target", true))
        {
            Assert.False(third.Reused);
            await third.Client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, timeout.Token);
            third.MarkSuccessful("127.0.0.1");
        }
        clock.Now += TimeSpan.FromSeconds(31);
        using (var expired = pool.Rent("target", true))
        {
            Assert.False(expired.Reused);
            await expired.Client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, timeout.Token);
            expired.MarkSuccessful("127.0.0.1");
        }
        // An attempt that does not reach MarkSuccessful cannot enter the idle pool.
        using (var uncertain = pool.Rent("target", true)) Assert.True(uncertain.Reused);
        using (var afterFailure = pool.Rent("target", true)) Assert.False(afterFailure.Reused);
        await server;
    }

    [Fact]
    public async Task UidCopyAndSequenceMoveUseBoundedSelectionAndExactAccounting()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.SeedMailboxAsync();
        Guid targetId;
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var target = new Mailbox { AccountId = source.AccountId, Name = "Target" };
            targetId = target.Id; db.Mailboxes.Add(target);
            var message = new StoredMessage { ContentKey = "shared", EnvelopeFrom = "a@test.example", Size = 10 };
            for (var uid = 1; uid <= 1001; uid++)
                db.MailboxMessages.Add(new MailboxMessage { MailboxId = source.Id, Uid = uid * 2, Message = message });
            var account = await db.Accounts.Include(x => x.Domain).SingleAsync(x => x.Id == source.AccountId);
            account.UsedBytes = account.Domain.UsedBytes = 10010;
            await db.SaveChangesAsync();
        }
        var one = await fixture.Service.CopyAsync(source.Id, "2000", true, "Target", false, CancellationToken.None);
        Assert.Equal([2000], one.SourceUids);
        Assert.Single(one.TargetUids);
        var moved = await fixture.Service.CopyAsync(source.Id, "1:600", false, "Target", true, CancellationToken.None);
        Assert.Equal(600, moved.SourceUids.Count);
        Assert.Equal(Enumerable.Range(1, 600).Select(x => (long)x), moved.ExpungedItems.Select(x => x.Sequence));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(401, await verify.MailboxMessages.CountAsync(x => x.MailboxId == source.Id));
        Assert.Equal(601, await verify.MailboxMessages.CountAsync(x => x.MailboxId == targetId));
        Assert.Equal(10020, await verify.Accounts.Where(x => x.Id == source.AccountId).Select(x => x.UsedBytes).SingleAsync());
        Assert.Single(await verify.QuotaLedger.ToListAsync());
    }

    [Fact]
    public async Task HeaderIndexProjectionAvoidsBodyAndRedundantMessageMapping()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mailbox = await fixture.SeedMailboxAsync();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var mime = new MimeMessage { Subject = "Résumé 中文", Body = new TextPart("plain") { Text = "body needle" } };
            var message = new StoredMessage { ContentKey = "shared", EnvelopeFrom = "a@test.example" };
            db.MessageSearchRecords.Add(MessageSearchBackfillService.BuildRecord(message.Id, mime));
            for (var uid = 1; uid <= 64; uid++) db.MailboxMessages.Add(new MailboxMessage { MailboxId = mailbox.Id, Uid = uid, Message = message });
            await db.SaveChangesAsync();
        }
        fixture.Counter.Sql.Clear();
        Assert.Empty(await fixture.Service.SearchAsync(mailbox.Id, "SUBJECT absent", true, CancellationToken.None));
        var indexQueries = fixture.Counter.Sql.Where(x => x.Contains("message_search_records", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, indexQueries.Length);
        Assert.All(indexQueries, sql => Assert.DoesNotContain("\"Body\"", sql));
        Assert.Equal(0, fixture.Store.Reads);
    }

    [Theory]
    [InlineData("utf-8", "us-ascii", "tailNEEDLE", true)]
    [InlineData("quoted-printable", "utf-8", "résumé 中文", true)]
    [InlineData("base64", "utf-8", "absent", false)]
    public async Task BodyFallbackPreservesEncodingAndCrossChunkMatches(string encoding, string charset, string needle, bool matches)
    {
        await using var fixture = await Fixture.CreateAsync();
        var mailbox = await fixture.SeedMailboxAsync();
        var text = new string('x', 65533) + "tailneedle résumé 中文";
        var mime = new MimeMessage { Body = new TextPart("plain") { Text = text } };
        mime.Body.ContentType.Charset = charset;
        mime.Body.Headers["Content-Transfer-Encoding"] = encoding == "utf-8" ? "8bit" : encoding;
        using var raw = new MemoryStream(); mime.WriteTo(raw); fixture.Store.Messages["fallback"] = raw.ToArray();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.MailboxMessages.Add(new MailboxMessage { MailboxId = mailbox.Id, Uid = 1,
                Message = new StoredMessage { ContentKey = "fallback", EnvelopeFrom = "a@test.example" } });
            await db.SaveChangesAsync();
        }
        var result = await fixture.Service.SearchAsync(mailbox.Id, "BODY \"" + needle + "\"", true, CancellationToken.None);
        Assert.Equal(matches, result.Count == 1);
    }

    [Fact]
    public async Task PreparedContentTransfersExactlyAndDuplicateOwnershipIsReleased()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "prepared-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var raw = Encoding.UTF8.GetBytes("Subject: raw\r\n\r\n中文\r\n");
            var descriptor = new ContentDescriptor(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw)), raw.Length);
            var store = new FileMessageContentStore(Options.Create(new StorageOptions { RootPath = root, PreparedInboundEnabled = true }), fixture.Factory);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var path = Path.Combine(root, "source-" + attempt);
                await File.WriteAllBytesAsync(path, raw);
                await using (var owned = new PreparedMessageContent(path, descriptor))
                {
                    Assert.Equal(descriptor, await store.SaveAsync(owned, CancellationToken.None));
                    if (OperatingSystem.IsLinux())
                        Assert.Equal(attempt != 0, owned.CanRead); // First save transfers; a duplicate remains owned until disposal.
                }
                Assert.False(File.Exists(path));
            }
            await using var content = await store.OpenReadAsync(descriptor.Key, CancellationToken.None);
            using var copy = new MemoryStream(); await content.CopyToAsync(copy);
            Assert.Equal(raw, copy.ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PreparedCrossDeviceContentKeepsOwnershipAndUsesVerifiedCopy()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var fixture = await Fixture.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "prepared-cross-device-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine("/dev/shm", "mailserver-prepared-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var raw = Encoding.UTF8.GetBytes("Subject: cross device\r\n\r\n中文\r\n");
            var descriptor = new ContentDescriptor(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(raw)), raw.Length);
            await File.WriteAllBytesAsync(source, raw);
            var store = new FileMessageContentStore(Options.Create(new StorageOptions
                { RootPath = root, PreparedInboundEnabled = true }), fixture.Factory);
            await using (var owned = new PreparedMessageContent(source, descriptor))
            {
                Assert.False(owned.TryTransferTo(Path.Combine(root, "must-not-be-copied")));
                Assert.True(File.Exists(source));
                Assert.False(File.Exists(Path.Combine(root, "must-not-be-copied")));
                Assert.Equal(0, owned.Position);
                Assert.Equal(descriptor, await store.SaveAsync(owned, CancellationToken.None));
                Assert.True(File.Exists(source));
                await using var content = await store.OpenReadAsync(descriptor.Key, CancellationToken.None);
                using var copy = new MemoryStream(); await content.CopyToAsync(copy);
                Assert.Equal(raw, copy.ToArray());
            }
            Assert.False(File.Exists(source));
        }
        finally { File.Delete(source); Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task BackfillResumesPersistedCursorAndRetriesFailedRecords()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mailbox = await fixture.SeedMailboxAsync();
        var at = DateTimeOffset.UtcNow;
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            for (var uid = 1; uid <= 48; uid++)
            {
                var mime = new MimeMessage { Subject = "index " + uid, Body = new TextPart("plain") { Text = "needle 中文" } };
                using var raw = new MemoryStream(); mime.WriteTo(raw); fixture.Store.Messages[uid.ToString()] = raw.ToArray();
                var message = new StoredMessage { ContentKey = uid.ToString(), EnvelopeFrom = "a@test.example", ReceivedAt = at };
                db.MailboxMessages.Add(new MailboxMessage { MailboxId = mailbox.Id, Uid = uid, Message = message });
                if (uid == 1) db.MessageSearchRecords.Add(new MessageSearchRecord { MessageId = message.Id,
                    ParserVersion = MessageSearchBackfillService.ParserVersion, Status = "Failed", RetryAfter = at.AddMinutes(-1) });
            }
            await db.SaveChangesAsync();
        }
        var wake = new SearchBackfillWakeSignal();
        async Task<MessageSearchBackfillService> StartAsync()
        {
            var worker = new MessageSearchBackfillService(fixture.Factory, fixture.Store,
                Options.Create(fixture.SearchSettings), NullLogger<MessageSearchBackfillService>.Instance, wake: wake);
            await worker.StartAsync(CancellationToken.None); return worker;
        }
        using (var first = await StartAsync())
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await using var db = await fixture.Factory.CreateDbContextAsync();
                if (await db.MessageSearchRecords.CountAsync(x => x.Status == "Ready") >= 16) break;
                await Task.Delay(50);
            }
            await first.StopAsync(CancellationToken.None);
        }
        await using (var db = await fixture.Factory.CreateDbContextAsync())
            Assert.NotEmpty((await db.SystemSettings.SingleAsync(x => x.Key == "internal.search.backfill.cursor.v2")).Value);
        using (var second = await StartAsync())
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                await using var db = await fixture.Factory.CreateDbContextAsync();
                if (await db.MessageSearchRecords.CountAsync(x => x.Status == "Ready") == 48) break;
                await Task.Delay(50);
            }
            await second.StopAsync(CancellationToken.None);
        }
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(48, await verify.MessageSearchRecords.CountAsync(x => x.Status == "Ready"));
    }

    [Fact]
    public async Task AppendWaitsForSharedHeavyCapacityBeforeSavingOrParsing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var mailbox = await fixture.SeedMailboxAsync();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var resources = new ResourceRuntime("worker", configuration);
        using var held = resources.LargeMime.TryAcquire();
        Assert.NotNull(held);
        var service = new SentCopyService(fixture.Factory, fixture.Store, Options.Create(new MailRulesOptions()),
            new SystemClock(), NullLogger<SentCopyService>.Instance, resources: resources);
        using var raw = new MemoryStream(Encoding.UTF8.GetBytes("Subject: budget\r\n\r\nbody"));
        var pending = service.AppendAsync(mailbox.AccountId, "INBOX", raw, [], null, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        Assert.Empty(fixture.Store.Messages);
        held.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, resources.LargeMime.Active);
    }

    [Fact]
    public async Task HeavyCapacityWakesOnReleaseAndCancellationDoesNotLeakPermits()
    {
        var limit = new AdjustableConcurrencyLimit(1);
        using var first = limit.TryAcquire(); Assert.NotNull(first);
        using var cancel = new CancellationTokenSource();
        var cancelled = limit.AcquireAsync(cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var waiting = limit.AcquireAsync(CancellationToken.None); Assert.False(waiting.IsCompleted);
        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, limit.Active);
        limit.Resize(2);
        using var third = await limit.AcquireAsync(CancellationToken.None);
        Assert.Equal(2, limit.Active);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Counter : DbCommandInterceptor
    {
        internal int Reads;
        internal readonly List<string> Sql = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            Sql.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"mailserver-efficiency-{Guid.NewGuid():N}.db");
        internal readonly Counter Counter = new();
        internal readonly CountingStore Store = new();
        internal readonly MessageSearchOptions SearchSettings = new();
        internal PooledDbContextFactory<MailDbContext> Factory = null!;
        internal MailboxService Service = null!;
        internal static async Task<Fixture> CreateAsync()
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            var fixture = new Fixture();
            fixture.Factory = new(new DbContextOptionsBuilder<MailDbContext>()
                .UseSqlite($"Data Source={fixture._path};Pooling=False").AddInterceptors(fixture.Counter).Options);
            await using var db = await fixture.Factory.CreateDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            var settings = new SystemSettingsService(fixture.Factory, Options.Create(new DeliveryOptions()),
                Options.Create(new MailboxDefaultsOptions()), Options.Create(new CertificatesOptions()));
            fixture.Service = new MailboxService(fixture.Factory, fixture.Store, settings, null!,
                searchOptions: Options.Create(fixture.SearchSettings));
            return fixture;
        }
        internal async Task<Mailbox> SeedMailboxAsync()
        {
            await using var db = await Factory.CreateDbContextAsync();
            var mailbox = new Mailbox { Name = "INBOX", Account = new MailAccount
            { LocalPart = "test", PasswordHash = "unused", Domain = new MailDomain { Name = "test.example" } } };
            db.Mailboxes.Add(mailbox);
            await db.SaveChangesAsync();
            return mailbox;
        }
        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_path)) File.Delete(_path);
            return ValueTask.CompletedTask;
        }
    }
}
