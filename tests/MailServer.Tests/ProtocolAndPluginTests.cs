using System.Text;
using System.Text.RegularExpressions;
using MailKit.Security;
using MailServer.Domain;
using MailServer.Imap;
using MailServer.Infrastructure;
using MailServer.PluginSdk;
using MailServer.PluginSdk.Auditing;
using MailServer.PluginSdk.Delivery;
using MailServer.PluginSdk.Imap;
using MailServer.PluginSdk.Security;
using MailServer.PluginSdk.Smtp;
using System.Text.Json;
using MailServer.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailServer.Tests;

public sealed class ProtocolAndPluginTests
{
    [Fact]
    public async Task ProtocolListenersStopCleanlyWhenAcceptIsCanceled()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var smtp = new SmtpServerService(
            scopeFactory,
            Options.Create(new SmtpServerOptions
            {
                Endpoints = [new SmtpEndpointOptions { Address = "127.0.0.1", Port = 0 }]
            }),
            null!,
            NullLogger<SmtpServerService>.Instance);
        var imap = new ImapServerService(
            scopeFactory,
            Options.Create(new ImapServerOptions
            {
                Endpoints = [new ImapEndpointOptions { Address = "127.0.0.1", Port = 0 }]
            }),
            null!,
            NullLogger<ImapServerService>.Instance);

        await smtp.StartAsync(CancellationToken.None);
        await imap.StartAsync(CancellationToken.None);
        await smtp.StopAsync(CancellationToken.None);
        await imap.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SmtpDataReaderRemovesDotStuffingWithoutBufferingMessageInMemory()
    {
        await using var input = new MemoryStream(Encoding.ASCII.GetBytes("Subject: test\r\n\r\n..leading-dot\r\n.\r\n"));
        await using var connection = new SmtpConnection(input, TimeSpan.FromSeconds(1), 4096);

        await using var message = await connection.ReadDataAsync(CancellationToken.None);
        using var reader = new StreamReader(message);

        Assert.Equal("Subject: test\r\n\r\n.leading-dot\r\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task SmtpDataReaderRejectsOversizedMessagesDuringIngestion()
    {
        await using var input = new MemoryStream(Encoding.ASCII.GetBytes("1234567890\r\n.\r\n"));
        await using var connection = new SmtpConnection(input, TimeSpan.FromSeconds(1), 5);

        await Assert.ThrowsAsync<InvalidDataException>(() => connection.ReadDataAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SmtpDataReaderPreservesEightBitBodyOctets()
    {
        byte[] input = [.. "Subject: binary\r\n\r\n"u8.ToArray(), 0xff, 0x00, 0x80, .. "\r\n.\r\n"u8.ToArray()];
        await using var connection = new SmtpConnection(
            new MemoryStream(input),
            TimeSpan.FromSeconds(1),
            4096);

        await using var message = await connection.ReadDataAsync(CancellationToken.None);
        using var output = new MemoryStream();
        await message.CopyToAsync(output);

        Assert.Equal(
            [.. "Subject: binary\r\n\r\n"u8.ToArray(), 0xff, 0x00, 0x80, .. "\r\n"u8.ToArray()],
            output.ToArray());
    }

    [Fact]
    public async Task SmtpDataReaderDeletesTemporaryMessageWhenClientDisconnects()
    {
        var before = Directory.GetFiles(Path.GetTempPath(), "mailserver-*.eml").ToHashSet();
        await using var connection = new SmtpConnection(
            new MemoryStream("Subject: incomplete\r\n\r\nbody"u8.ToArray()),
            TimeSpan.FromSeconds(1),
            4096);

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => connection.ReadDataAsync(CancellationToken.None));

        Assert.DoesNotContain(
            Directory.GetFiles(Path.GetTempPath(), "mailserver-*.eml"),
            path => !before.Contains(path));
    }

    [Fact]
    public async Task SmtpDataReaderHandlesAllDotStuffingBoundaries()
    {
        await using var connection = new SmtpConnection(
            new MemoryStream("Subject: dots\r\n\r\n..\r\n...\r\n..test\r\n.\r\n"u8.ToArray()),
            TimeSpan.FromSeconds(1),
            4096);

        await using var message = await connection.ReadDataAsync(CancellationToken.None);
        using var reader = new StreamReader(message, Encoding.ASCII);

        Assert.Equal("Subject: dots\r\n\r\n.\r\n..\r\n.test\r\n", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    public async Task SmtpDataReaderAppliesExactDecodedMessageSizeBoundary(long limit, bool accepted)
    {
        await using var connection = new SmtpConnection(
            new MemoryStream("X:a\r\n\r\nx\r\n.\r\n"u8.ToArray()),
            TimeSpan.FromSeconds(1),
            limit);

        if (accepted)
        {
            await using var message = await connection.ReadDataAsync(CancellationToken.None);
            Assert.Equal(10, message.Length);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => connection.ReadDataAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task SmtpSizeLimitCountsBase64TransferEncodingBytes()
    {
        const string data =
            "Content-Type: application/octet-stream\r\n"
            + "Content-Transfer-Encoding: base64\r\n"
            + "\r\n"
            + "AAECAwQFBgcICQ==\r\n";
        var storedLength = Encoding.ASCII.GetByteCount(data);
        var wire = Encoding.ASCII.GetBytes(data + ".\r\n");

        await using (var exactConnection = new SmtpConnection(
                         new MemoryStream(wire),
                         TimeSpan.FromSeconds(1),
                         storedLength))
        await using (var message = await exactConnection.ReadDataAsync(CancellationToken.None))
        {
            Assert.Equal(storedLength, message.Length);
        }

        await using var oversizedConnection = new SmtpConnection(
            new MemoryStream(wire),
            TimeSpan.FromSeconds(1),
            storedLength - 1);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => oversizedConnection.ReadDataAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SmtpDataReaderAcceptsFoldedHeadersAndRejectsHeaderInjection()
    {
        await using (var acceptedConnection = new SmtpConnection(
                         new MemoryStream("Subject: hello\r\n world\r\n\r\nbody\r\n.\r\n"u8.ToArray()),
                         TimeSpan.FromSeconds(1),
                         4096))
        {
            await using var message = await acceptedConnection.ReadDataAsync(CancellationToken.None);
            Assert.True(message.Length > 0);
        }

        await using var rejectedConnection = new SmtpConnection(
            new MemoryStream(" injected\r\nSubject: hello\r\n\r\nbody\r\n.\r\n"u8.ToArray()),
            TimeSpan.FromSeconds(1),
            4096);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => rejectedConnection.ReadDataAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SmtpDataReaderRejectsOversizedHeaderLineAndHeaderBlock()
    {
        var longLine = $"Subject: {new string('a', SmtpServerOptions.MaximumHeaderLineBytes)}\r\n\r\n.\r\n";
        await using var lineConnection = new SmtpConnection(
            new MemoryStream(Encoding.ASCII.GetBytes(longLine)),
            TimeSpan.FromSeconds(1),
            1024 * 1024);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => lineConnection.ReadDataAsync(CancellationToken.None));

        var headers = new StringBuilder();
        while (headers.Length <= SmtpServerOptions.MaximumHeaderBytes)
            headers.Append("X-Test: ").Append(new string('a', 900)).Append("\r\n");
        headers.Append("\r\n.\r\n");
        await using var blockConnection = new SmtpConnection(
            new MemoryStream(Encoding.ASCII.GetBytes(headers.ToString())),
            TimeSpan.FromSeconds(1),
            1024 * 1024);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => blockConnection.ReadDataAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Subject: test\n\nbody\n.\n")]
    [InlineData("Subject: test\rX-Test: bad\r\n\r\nbody\r\n.\r\n")]
    public async Task SmtpDataReaderRejectsBareLfOrCr(string wire)
    {
        await using var connection = new SmtpConnection(
            new MemoryStream(Encoding.ASCII.GetBytes(wire)),
            TimeSpan.FromSeconds(1),
            4096);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => connection.ReadDataAsync(CancellationToken.None));
        Assert.Contains("line ending", exception.Message);
    }

    [Fact]
    public async Task SmtpDataTerminatorMustBeExact()
    {
        await using var connection = new SmtpConnection(
            new MemoryStream("Subject: dots\r\n\r\n. \r\n .\r\n\t.\r\n.\r\n"u8.ToArray()),
            TimeSpan.FromSeconds(1),
            4096);
        await using var message = await connection.ReadDataAsync(CancellationToken.None);
        using var reader = new StreamReader(message, Encoding.ASCII);

        Assert.EndsWith(". \r\n .\r\n\t.\r\n", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("FROM:<\"weird name\"@example.com>", "\"weird name\"@example.com")]
    [InlineData("TO:<user+tag@example.com>", "user+tag@example.com")]
    [InlineData("TO:<user@[192.0.2.1]>", "user@[192.0.2.1]")]
    [InlineData("TO:<user@[IPv6:2001:db8::1]>", "user@[ipv6:2001:db8::1]")]
    public void SmtpPathParserSupportsQuotedPlusAndAddressLiterals(string arguments, string expected)
    {
        Assert.True(SmtpServerService.TryParsePathAndParameters(
            arguments,
            arguments.StartsWith("FROM", StringComparison.Ordinal) ? "FROM:" : "TO:",
            false,
            out var address,
            out var parameters));
        Assert.Equal(expected, address);
        Assert.Empty(parameters);
    }

    [Fact]
    public async Task SmtpDataReaderAcceptsBodyOnlyMessagesAndLimitsReceivedHeaders()
    {
        await using (var bodyOnlyInput = new MemoryStream(Encoding.ASCII.GetBytes("hello world\r\n.\r\n")))
        await using (var connection = new SmtpConnection(bodyOnlyInput, TimeSpan.FromSeconds(1), 4096))
        {
            await using var message = await connection.ReadDataAsync(CancellationToken.None);
            using var reader = new StreamReader(message, Encoding.ASCII);
            Assert.Equal("hello world\r\n", await reader.ReadToEndAsync());
        }

        var receivedHeaders = string.Concat(
            Enumerable.Range(0, SmtpServerOptions.MaximumReceivedHeaders + 1)
                .Select(index => $"Received: by mx{index}.example\r\n"));
        await using var oversizedInput = new MemoryStream(Encoding.ASCII.GetBytes(receivedHeaders + "\r\nbody\r\n.\r\n"));
        await using var oversizedConnection = new SmtpConnection(oversizedInput, TimeSpan.FromSeconds(1), 8192);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            oversizedConnection.ReadDataAsync(CancellationToken.None));
        Assert.Contains("Received header count", exception.Message);
    }

    [Fact]
    public void ImapTokenizerPreservesQuotedSpacesAndParenthesizedFlagLists()
    {
        var tokens = ImapTokenizer.Tokenize("\"Archive 2026\" (\\Seen \\Flagged)");

        Assert.Equal(["Archive 2026", "(\\Seen \\Flagged)"], tokens);
    }

    [Fact]
    public void ImapTokenizerPreservesEmptyQuotedAndEscapedValues()
    {
        var tokens = ImapTokenizer.Tokenize("\"\" \"pa \\\"ss\\\\word\" \"(*)\"");

        Assert.Equal(["", "pa \"ss\\word", "(*)"], tokens);
    }

    [Theory]
    [InlineData("\"unterminated")]
    [InlineData("(\\Seen")]
    [InlineData("\\Seen)")]
    public void ImapTokenizerRejectsUnbalancedQuotedStringsOrLists(string input)
    {
        Assert.Throws<FormatException>(() => ImapTokenizer.Tokenize(input));
    }

    [Theory]
    [InlineData("\"Sent\" {12}", "Sent", 12, false)]
    [InlineData("\"Sent Items\" (\\Seen) {12}", "Sent Items", 12, false)]
    [InlineData("\"Sent\" {12+}", "Sent", 12, true)]
    public void ImapAppendParserAcceptsCommonThunderbirdForms(
        string arguments,
        string expectedMailbox,
        long expectedLength,
        bool expectedNonSynchronizing)
    {
        var request = ImapServerService.ParseAppendArguments(arguments);

        Assert.Equal(expectedMailbox, request.MailboxName);
        Assert.Equal(expectedLength, request.LiteralLength);
        Assert.Equal(expectedNonSynchronizing, request.NonSynchronizing);
    }

    [Fact]
    public void ImapAppendParserAcceptsFlagsAndInternalDate()
    {
        var request = ImapServerService.ParseAppendArguments(
            "\"Sent\" (\\Seen $Forwarded) \"26-Jun-2026 23:02:44 +0800\" {12}");

        Assert.Equal("Sent", request.MailboxName);
        Assert.Equal(["\\Seen", "$Forwarded"], request.Flags);
        Assert.Equal(
            new DateTimeOffset(2026, 6, 26, 23, 2, 44, TimeSpan.FromHours(8)),
            request.InternalDate);
    }

    [Fact]
    public void ImapAppendParserAcceptsNilDateAndBinaryLiteralMarker()
    {
        var request = ImapServerService.ParseAppendArguments("\"Sent\" (\\Seen) NIL ~{12}");

        Assert.Equal("Sent", request.MailboxName);
        Assert.Null(request.InternalDate);
        Assert.True(request.BinaryLiteral);
        Assert.False(request.NonSynchronizing);
        Assert.Equal(12, request.LiteralLength);
    }

    [Theory]
    [InlineData("\"Sent\" UTF8 (~{12}", false)]
    [InlineData("\"Sent\" (\\Seen) \"27-Jun-2026 12:52:44 +0800\" UTF8 (~{12+}", true)]
    [InlineData("\"Sent\" UTF8 ({12}", false)]
    public void ImapAppendParserAcceptsUtf8DataExtension(
        string arguments,
        bool expectedNonSynchronizing)
    {
        var request = ImapServerService.ParseAppendArguments(arguments);

        Assert.Equal("Sent", request.MailboxName);
        Assert.True(request.Utf8Wrapped);
        Assert.Equal(expectedNonSynchronizing, request.NonSynchronizing);
        Assert.Equal(12, request.LiteralLength);
    }

    [Fact]
    public void ImapAppendParserAcceptsInlineLiteralMailbox()
    {
        const string mailboxName = "\u5df2\u53d1\u9001";
        var request = ImapServerService.ParseAppendArguments($"{{9}}\r\n{mailboxName} (\\Seen) {{12}}");

        Assert.Equal(mailboxName, request.MailboxName);
        Assert.Equal(["\\Seen"], request.Flags);
        Assert.Equal("mailbox", request.MailboxLiteralKind);
        Assert.Equal(12, request.LiteralLength);
    }

    [Fact]
    public void ImapAppendParserAcceptsModifiedUtf7MailboxAtom()
    {
        var request = ImapServerService.ParseAppendArguments("&ZeVnLIqe- (\\Seen) {12+}");

        Assert.Equal("\u65e5\u672c\u8a9e", request.MailboxName);
        Assert.True(request.NonSynchronizing);
    }

    [Fact]
    public void ImapAppendParserAcceptsEscapedQuotedMailbox()
    {
        var request = ImapServerService.ParseAppendArguments(
            "\"Sent (Archive) \\\"Quotes\\\\Backslash\" {12}");

        Assert.Equal("Sent (Archive) \"Quotes\\Backslash", request.MailboxName);
    }

    [Theory]
    [InlineData("\"Sent\" (\\Seen {12}")]
    [InlineData("\"Sent {12}")]
    [InlineData("\"Sent\"")]
    public void ImapAppendParserRejectsMalformedCommands(string arguments)
    {
        var exception = Assert.Throws<FormatException>(() => ImapServerService.ParseAppendArguments(arguments));

        Assert.Equal("Malformed APPEND command.", exception.Message);
    }

    [Fact]
    public void ImapCapabilityDoesNotAdvertiseRev2BeforeFullSupport()
    {
        Assert.Contains("IMAP4rev1", ImapServerService.BaseCapabilities);
        Assert.Contains("SPECIAL-USE", ImapServerService.BaseCapabilities);
        Assert.DoesNotContain("IMAP4rev2", ImapServerService.BaseCapabilities);
    }

    [Fact]
    public async Task ImapAppendStoresThunderbirdSentCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-append-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            Guid accountId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused"
                };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "Sent" });
                await db.SaveChangesAsync();
                accountId = account.Id;
            }

            var settings = new SystemSettingsService(
                factory,
                Options.Create(new DeliveryOptions()),
                Options.Create(new MailboxDefaultsOptions()),
                Options.Create(new CertificatesOptions()));
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var services = new ServiceCollection();
            services.AddSingleton<IMailboxService>(new MailboxService(factory, store, settings));
            using var provider = services.BuildServiceProvider();
            var service = new ImapServerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ImapServerOptions()),
                null!,
                NullLogger<ImapServerService>.Instance);
            var message = "Subject: saved\r\n\r\nbody\r\n";
            await using var stream = new DuplexMemoryStream(message + "\r\n");
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);
            var state = new ImapSessionState { AccountId = accountId };

            await service.AppendAsync(
                connection,
                state,
                "A004",
                $"\"Sent\" (\\Seen $Forwarded) \"26-Jun-2026 23:02:44 +0800\" {{{Encoding.ASCII.GetByteCount(message)}}}",
                "127.0.0.1:143",
                CancellationToken.None);

            Assert.Contains("A004 OK [APPENDUID", stream.OutputText);
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(1, await verification.Mailboxes.CountAsync(x => x.AccountId == accountId && x.Name == "Sent"));
            var stored = await verification.MailboxMessages.Include(x => x.Mailbox).SingleAsync();
            Assert.Equal("Sent", stored.Mailbox.Name);
            Assert.Equal("\\Seen $Forwarded", stored.Flags);
            Assert.Equal(
                new DateTimeOffset(2026, 6, 26, 23, 2, 44, TimeSpan.FromHours(8)),
                stored.InternalDate);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapAppendStoresUtf8WrappedMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-append-utf8-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            Guid accountId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused"
                };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "Sent" });
                await db.SaveChangesAsync();
                accountId = account.Id;
            }

            var settings = new SystemSettingsService(
                factory,
                Options.Create(new DeliveryOptions()),
                Options.Create(new MailboxDefaultsOptions()),
                Options.Create(new CertificatesOptions()));
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var services = new ServiceCollection();
            services.AddSingleton<IMailboxService>(new MailboxService(factory, store, settings));
            using var provider = services.BuildServiceProvider();
            var service = new ImapServerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ImapServerOptions()),
                null!,
                NullLogger<ImapServerService>.Instance);
            var message = "Subject: \u6d4b\u8bd5\r\n\r\nbody\r\n";
            await using var stream = new DuplexMemoryStream(message + ")\r\n");
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);
            var state = new ImapSessionState { AccountId = accountId, UserName = "alice@example.com" };

            await service.AppendAsync(
                connection,
                state,
                "A004",
                $"\"Sent\" (\\Seen) \"27-Jun-2026 12:52:44 +0800\" UTF8 (~{{{Encoding.UTF8.GetByteCount(message)}}}",
                "127.0.0.1:143",
                CancellationToken.None);

            Assert.Contains("+ Ready for literal data", stream.OutputText);
            Assert.Contains("A004 OK [APPENDUID", stream.OutputText);
            await using var verification = await factory.CreateDbContextAsync();
            var stored = await verification.MailboxMessages.Include(x => x.Mailbox).SingleAsync();
            Assert.Equal("Sent", stored.Mailbox.Name);
            Assert.Equal("\\Seen", stored.Flags);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapAppendStoresThunderbirdLiteralMailboxCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-append-literal-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            Guid accountId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused"
                };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "\u5df2\u53d1\u9001" });
                await db.SaveChangesAsync();
                accountId = account.Id;
            }

            var settings = new SystemSettingsService(
                factory,
                Options.Create(new DeliveryOptions()),
                Options.Create(new MailboxDefaultsOptions()),
                Options.Create(new CertificatesOptions()));
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var services = new ServiceCollection();
            services.AddSingleton<IMailboxService>(new MailboxService(factory, store, settings));
            using var provider = services.BuildServiceProvider();
            var service = new ImapServerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ImapServerOptions()),
                null!,
                NullLogger<ImapServerService>.Instance);
            var message = "Subject: saved\r\n\r\nbody\r\n";
            var literalMailbox = "\u5df2\u53d1\u9001";
            await using var stream = new DuplexMemoryStream(
                literalMailbox + $" (\\Seen) {{{Encoding.ASCII.GetByteCount(message)}}}\r\n" + message + "\r\n");
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);
            var state = new ImapSessionState { AccountId = accountId, UserName = "alice@example.com" };

            await service.AppendAsync(
                connection,
                state,
                "A004",
                $"{{{Encoding.UTF8.GetByteCount(literalMailbox)}}}",
                "127.0.0.1:143",
                CancellationToken.None);

            Assert.Contains("+ Ready for mailbox name literal", stream.OutputText);
            Assert.Contains("A004 OK [APPENDUID", stream.OutputText);
            await using var verification = await factory.CreateDbContextAsync();
            var stored = await verification.MailboxMessages.Include(x => x.Mailbox).SingleAsync();
            Assert.Equal(literalMailbox, stored.Mailbox.Name);
            Assert.Equal("\\Seen", stored.Flags);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapAppendMissingMailboxReturnsTryCreateWithoutReadingMessageLiteral()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-append-missing-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            Guid accountId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused"
                };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "INBOX" });
                await db.SaveChangesAsync();
                accountId = account.Id;
            }

            var settings = new SystemSettingsService(
                factory,
                Options.Create(new DeliveryOptions()),
                Options.Create(new MailboxDefaultsOptions()),
                Options.Create(new CertificatesOptions()));
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var services = new ServiceCollection();
            services.AddSingleton<IMailboxService>(new MailboxService(factory, store, settings));
            using var provider = services.BuildServiceProvider();
            var service = new ImapServerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ImapServerOptions()),
                null!,
                NullLogger<ImapServerService>.Instance);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);
            var state = new ImapSessionState { AccountId = accountId, UserName = "alice@example.com" };

            await service.AppendAsync(
                connection,
                state,
                "A004",
                "\"Missing\" {12}",
                "127.0.0.1:143",
                CancellationToken.None);

            Assert.Contains("A004 NO [TRYCREATE] Mailbox does not exist.", stream.OutputText);
            Assert.DoesNotContain("+ Ready for literal data", stream.OutputText);
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Empty(await verification.MailboxMessages.ToListAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapAppendMissingMailboxDrainsNonSynchronizingUtf8Literal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-append-missing-nonsync-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            Guid accountId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused"
                };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "INBOX" });
                await db.SaveChangesAsync();
                accountId = account.Id;
            }

            var settings = new SystemSettingsService(
                factory,
                Options.Create(new DeliveryOptions()),
                Options.Create(new MailboxDefaultsOptions()),
                Options.Create(new CertificatesOptions()));
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var services = new ServiceCollection();
            services.AddSingleton<IMailboxService>(new MailboxService(factory, store, settings));
            using var provider = services.BuildServiceProvider();
            var service = new ImapServerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new ImapServerOptions()),
                null!,
                NullLogger<ImapServerService>.Instance);
            var message = "Subject: queued\r\n\r\nbody\r\n";
            await using var stream = new DuplexMemoryStream(message + ")\r\nA005 NOOP\r\n");
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);
            var state = new ImapSessionState { AccountId = accountId, UserName = "alice@example.com" };

            await service.AppendAsync(
                connection,
                state,
                "A004",
                $"\"Missing\" UTF8 (~{{{Encoding.ASCII.GetByteCount(message)}+}}",
                "127.0.0.1:143",
                CancellationToken.None);

            Assert.Contains("A004 NO [TRYCREATE] Mailbox does not exist.", stream.OutputText);
            Assert.DoesNotContain("+ Ready for literal data", stream.OutputText);
            Assert.Equal("A005 NOOP", await connection.ReadLineAsync(CancellationToken.None));
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Empty(await verification.MailboxMessages.ToListAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapListReturnsSpecialUseAttributesForDefaultFolders()
    {
        var factory = CreateFactory();
        Guid accountId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            var account = new MailAccount
            {
                Domain = domain,
                LocalPart = "alice",
                PasswordHash = "unused"
            };
            db.AddRange(
                domain,
                account,
                new Mailbox { Account = account, Name = "INBOX" },
                new Mailbox { Account = account, Name = "Sent" },
                new Mailbox { Account = account, Name = "Drafts" },
                new Mailbox { Account = account, Name = "Trash" },
                new Mailbox { Account = account, Name = "Junk" },
                new Mailbox { Account = account, Name = "Archives" });
            await db.SaveChangesAsync();
            accountId = account.Id;
        }

        var settings = new SystemSettingsService(
            factory,
            Options.Create(new DeliveryOptions()),
            Options.Create(new MailboxDefaultsOptions()),
            Options.Create(new CertificatesOptions()));
        var store = new FileMessageContentStore(
            Options.Create(new StorageOptions { RootPath = Path.GetTempPath(), MaximumMessageBytes = 4096 }),
            factory);
        var services = new ServiceCollection();
        services.AddSingleton<IMailboxService>(new MailboxService(factory, store, settings));
        using var provider = services.BuildServiceProvider();
        var service = new ImapServerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ImapServerOptions()),
            null!,
            NullLogger<ImapServerService>.Instance);
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

        await service.ListAsync(
            connection,
            new ImapSessionState { AccountId = accountId },
            subscribedOnly: false,
            CancellationToken.None);

        Assert.Contains(@"LIST (\HasNoChildren \Sent) ""/"" ""Sent""", stream.OutputText);
        Assert.Contains(@"LIST (\HasNoChildren \Drafts) ""/"" ""Drafts""", stream.OutputText);
        Assert.Contains(@"LIST (\HasNoChildren \Trash) ""/"" ""Trash""", stream.OutputText);
        Assert.Contains(@"LIST (\HasNoChildren \Junk) ""/"" ""Junk""", stream.OutputText);
        Assert.Contains(@"LIST (\HasNoChildren \Archive) ""/"" ""Archives""", stream.OutputText);
    }

    [Fact]
    public async Task ImapFetchReturnsHeaderFieldsForClientMessageLists()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-fetch-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.FetchAsync(
                connection,
                scenario.State,
                "1:* (FLAGS UID RFC822.SIZE INTERNALDATE BODY.PEEK[HEADER.FIELDS (FROM TO SUBJECT DATE MESSAGE-ID)])",
                "127.0.0.1:143",
                useUid: true,
                CancellationToken.None);

            Assert.Contains("BODY[HEADER.FIELDS (FROM TO SUBJECT DATE MESSAGE-ID)]", stream.OutputText);
            Assert.Contains("Subject: hello", stream.OutputText);
            Assert.Contains("Message-ID: <m1@example.net>", stream.OutputText);
            Assert.Contains("UID 1", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapFetchReturnsEnvelopeBodyStructureAndBodySections()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-fetch-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.FetchAsync(
                connection,
                scenario.State,
                "1 (ENVELOPE BODYSTRUCTURE BODY.PEEK[] BODY.PEEK[HEADER] BODY.PEEK[TEXT])",
                "127.0.0.1:143",
                useUid: false,
                CancellationToken.None);

            Assert.Contains("ENVELOPE", stream.OutputText);
            Assert.Contains("BODYSTRUCTURE", stream.OutputText);
            Assert.Contains("BODY[]", stream.OutputText);
            Assert.Contains("BODY[HEADER]", stream.OutputText);
            Assert.Contains("BODY[TEXT]", stream.OutputText);
            Assert.Contains("hello body", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapRawFetchStreamsExactHeaderTextAndPartialLiterals()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-stream-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.FetchAsync(connection, scenario.State,
                "1 (RFC822 BODY.PEEK[HEADER] BODY.PEEK[TEXT] BODY.PEEK[]<5.8>)",
                "local", useUid: false, CancellationToken.None);
            var output = stream.OutputText;
            Assert.Contains("BODY[HEADER]", output);
            Assert.Contains("BODY[TEXT]", output);
            Assert.Contains("hello body", output);
            Assert.Contains("BODY[]<5> {8}\r\n", output);
            Assert.Contains("RFC822 {", output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapHeaderFetchFiltersCaseFoldedAndRepeatedFields()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-header-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                1,
                "From: Sender <sender@example.net>\r\n"
                + "To: Alice <alice@example.com>\r\n"
                + "subject: first line\r\n"
                + "\tfolded line\r\n"
                + "X-Tag: one\r\n"
                + "X-Tag: two\r\n"
                + "Bcc: hidden@example.net\r\n"
                + "\r\n"
                + "body must not be returned\r\n");
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.FetchAsync(
                connection,
                scenario.State,
                "1 (BODY.PEEK[HEADER.FIELDS (SUBJECT x-tag)])",
                "127.0.0.1:143",
                useUid: false,
                CancellationToken.None);

            Assert.Contains("BODY[HEADER.FIELDS (SUBJECT X-TAG)]", stream.OutputText);
            Assert.Contains("subject: first line", stream.OutputText);
            Assert.Contains("folded line", stream.OutputText);
            Assert.Contains("X-Tag: one", stream.OutputText);
            Assert.Contains("X-Tag: two", stream.OutputText);
            Assert.DoesNotContain("Bcc:", stream.OutputText);
            Assert.DoesNotContain("body must not be returned", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapBodyPeekDoesNotMarkSeenButBodyFetchDoes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-seen-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            await using var peekStream = new DuplexMemoryStream(string.Empty);
            await using var peekConnection = new ImapConnection(peekStream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.FetchAsync(
                peekConnection,
                scenario.State,
                "1 (BODY.PEEK[])",
                "127.0.0.1:143",
                useUid: false,
                CancellationToken.None);
            var afterPeek = (await mailbox.FetchAsync(scenario.MailboxId, "1", useUid: false, CancellationToken.None)).Single();
            await using var bodyStream = new DuplexMemoryStream(string.Empty);
            await using var bodyConnection = new ImapConnection(bodyStream, TimeSpan.FromSeconds(1), 4096);
            await Task.WhenAll(
                scenario.Service.FetchAsync(bodyConnection, scenario.State, "1 (BODY[])", "127.0.0.1:143", useUid: false, CancellationToken.None),
                scenario.Service.FetchAsync(bodyConnection, scenario.State, "1 (BODY[TEXT])", "127.0.0.1:143", useUid: false, CancellationToken.None));
            var afterBody = (await mailbox.FetchAsync(scenario.MailboxId, "1", useUid: false, CancellationToken.None)).Single();

            Assert.DoesNotContain("\\Seen", afterPeek.Flags);
            Assert.Contains("\\Seen", afterBody.Flags);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapSearchDateCriteriaUseInternalDateBoundaries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-search-date-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                1,
                "Date: Wed, 01 Jul 2026 23:59:59 +0000\r\nSubject: header lies\r\n\r\nbody\r\n",
                internalDate: new DateTimeOffset(2026, 6, 30, 23, 59, 59, TimeSpan.FromHours(8)));
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                2,
                "Date: Tue, 30 Jun 2026 00:00:00 +0000\r\nSubject: internal wins\r\n\r\nbody\r\n",
                internalDate: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.FromHours(8)));
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                3,
                "Subject: later\r\n\r\nbody\r\n",
                internalDate: new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.FromHours(8)));
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();

            Assert.Equal([2], await mailbox.SearchAsync(scenario.MailboxId, "ON 1-Jul-2026", useUid: false, CancellationToken.None));
            Assert.Equal([2, 3], await mailbox.SearchAsync(scenario.MailboxId, "SINCE 1-Jul-2026", useUid: false, CancellationToken.None));
            Assert.Equal([1], await mailbox.SearchAsync(scenario.MailboxId, "BEFORE 1-Jul-2026", useUid: false, CancellationToken.None));
            Assert.Equal([2, 3], await mailbox.SearchAsync(scenario.MailboxId, "SINCE 1-Jul-2026", useUid: true, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapUnsupportedSortThreadAndInvalidStatesReturnTaggedErrors()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-state-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);
            var anonymous = new ImapSessionState();
            var authenticated = new ImapSessionState { AccountId = scenario.State.AccountId, UserName = "alice@example.com" };

            await scenario.Service.ExecuteCommandAsync(connection, anonymous, "A001", "SELECT", "INBOX", false, "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteCommandAsync(connection, anonymous, "A002", "FETCH", "1 BODY[]", false, "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteCommandAsync(connection, authenticated, "A003", "FETCH", "1 BODY[]", false, "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteCommandAsync(connection, authenticated, "A004", "SORT", string.Empty, false, "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteCommandAsync(connection, authenticated, "A005", "THREAD", string.Empty, false, "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteCommandAsync(connection, authenticated, "A006", "NOOP", string.Empty, false, "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            Assert.Contains("A001 NO \"Authenticate first.\"", stream.OutputText);
            Assert.Contains("A002 NO \"Authenticate first.\"", stream.OutputText);
            Assert.Contains("A003 NO \"Select a mailbox first.\"", stream.OutputText);
            Assert.Contains("A004 BAD Unsupported command", stream.OutputText);
            Assert.Contains("A005 BAD Unsupported command", stream.OutputText);
            Assert.Contains("A006 OK NOOP completed", stream.OutputText);
            Assert.DoesNotContain("SORT", ImapServerService.BaseCapabilities);
            Assert.DoesNotContain("THREAD", ImapServerService.BaseCapabilities);
            Assert.DoesNotContain("Mailbox not found", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapIdlePushesMailboxChangesAndTerminatesOnDone()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-idle-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            scenario.State.Exists = 0;
            scenario.State.UidNext = 1;
            scenario.State.HighestModSeq = 0;
            await using var stream = new DuplexMemoryStream("DONE\r\n");
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteCommandAsync(
                connection,
                scenario.State,
                "A001",
                "IDLE",
                string.Empty,
                false,
                "local",
                ConnectionSecurity.StartTls,
                true,
                CancellationToken.None);

            Assert.Contains("+ idling", stream.OutputText);
            Assert.Contains("* 1 EXISTS", stream.OutputText);
            Assert.Contains("[UIDNEXT 2]", stream.OutputText);
            Assert.Contains("[HIGHESTMODSEQ 2]", stream.OutputText);
            Assert.Contains("A001 OK IDLE terminated", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapAppendLiteralLimitReturnsTaggedError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-literal-limit-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using var stream = new DuplexMemoryStream("hello\r\n");
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), maximumLiteralBytes: 4);

            await scenario.Service.ExecuteCommandAsync(
                connection,
                scenario.State,
                "A001",
                "APPEND",
                "\"INBOX\" {5}",
                false,
                "local",
                ConnectionSecurity.StartTls,
                true,
                CancellationToken.None);

            Assert.Contains("+ Ready for literal data", stream.OutputText);
            Assert.Contains("A001 BAD \"Literal exceeds the 4 byte limit.\"", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapExamineRejectsWritesAndCloseExpungesWithoutUntaggedExpunge()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-close-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            await mailbox.StoreFlagsAsync(scenario.MailboxId, "1", useUid: false, "+FLAGS", ["\\Deleted"], CancellationToken.None);
            await using var readOnlyStream = new DuplexMemoryStream(string.Empty);
            await using var readOnlyConnection = new ImapConnection(readOnlyStream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.SelectAsync(readOnlyConnection, scenario.State, "INBOX", readOnly: true, "local", CancellationToken.None);

            await scenario.Service.ExecuteCommandAsync(readOnlyConnection, scenario.State, "A001", "STORE", "1 +FLAGS (\\Seen)", false, "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            var stillDeleted = (await mailbox.FetchAsync(scenario.MailboxId, "1", useUid: false, CancellationToken.None)).Single();
            scenario.State.ReadOnly = false;
            await using var closeStream = new DuplexMemoryStream(string.Empty);
            await using var closeConnection = new ImapConnection(closeStream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.ExecuteCommandAsync(closeConnection, scenario.State, "A002", "CLOSE", string.Empty, false, "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteCommandAsync(closeConnection, scenario.State, "A003", "FETCH", "1 BODY[]", false, "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            var remaining = await mailbox.FetchAsync(scenario.MailboxId, "1:*", useUid: false, CancellationToken.None);

            Assert.Contains("A001 NO \"Mailbox is read-only.\"", readOnlyStream.OutputText);
            Assert.DoesNotContain("\\Seen", stillDeleted.Flags);
            Assert.Contains("\\Deleted", stillDeleted.Flags);
            Assert.Contains("A002 OK CLOSE completed", closeStream.OutputText);
            Assert.DoesNotContain("EXPUNGE", closeStream.OutputText);
            Assert.Contains("A003 NO \"Select a mailbox first.\"", closeStream.OutputText);
            Assert.Empty(remaining);
            Assert.Null(scenario.State.MailboxId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapUnselectReturnsToAuthenticatedState()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-unselect-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 UNSELECT", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 NOOP", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A003 FETCH 1 FLAGS", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            Assert.Null(scenario.State.MailboxId);
            Assert.Contains("A001 OK UNSELECT completed", stream.OutputText);
            Assert.Contains("A002 OK NOOP completed", stream.OutputText);
            Assert.Contains("A003 NO \"Select a mailbox first.\"", stream.OutputText);
            Assert.DoesNotContain("UNSELECT", ImapServerService.BaseCapabilities);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapNoopPipeliningAndTagsStayOrdered()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-pipeline-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            scenario.State.Exists = 0;
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "abc123 SELECT INBOX", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, ". FETCH 1:* FLAGS", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A3 NOOP", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "\u4e2d\u6587 LOGIN a b", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            var output = stream.OutputText;
            Assert.Contains("* 1 EXISTS", output);
            Assert.Contains("abc123 OK [READ-WRITE] SELECT completed", output);
            Assert.Contains(". OK FETCH completed", output);
            Assert.Contains("A3 OK NOOP completed", output);
            Assert.Contains("* BAD Invalid command tag", output);
            Assert.True(output.IndexOf("abc123 OK", StringComparison.Ordinal) < output.IndexOf(". OK", StringComparison.Ordinal));
            Assert.True(output.IndexOf(". OK", StringComparison.Ordinal) < output.IndexOf("A3 OK", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapLargeAndMissingFetchRangesAreStable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-large-fetch-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            for (var uid = 2; uid <= 300; uid++)
            {
                await AddInboxMessageAsync(
                    scenario.Factory,
                    scenario.Store,
                    scenario.MailboxId,
                    uid,
                    $"Subject: bulk {uid}\r\n\r\nbody {uid}\r\n");
            }

            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 FETCH 1:* FLAGS", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 FETCH 999999 FLAGS", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A003 UID FETCH 999999 FLAGS", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            var output = stream.OutputText;
            Assert.Equal(300, Regex.Matches(output, @"\* \d+ FETCH").Count);
            Assert.Contains("A001 OK FETCH completed", output);
            Assert.Contains("A002 OK FETCH completed", output);
            Assert.Contains("A003 OK FETCH completed", output);
            Assert.DoesNotContain("BAD", output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapEmptyMailboxCommandsReturnEmptyResults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-empty-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                var mailbox = await db.Mailboxes.SingleAsync(x => x.Id == scenario.MailboxId);
                mailbox.UidNext = 1;
                mailbox.HighestModSeq = 1;
                await db.SaveChangesAsync();
            }

            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 SELECT INBOX", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 FETCH 1 FLAGS", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A003 SEARCH ALL", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A004 EXPUNGE", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            var output = stream.OutputText;
            Assert.Contains("* 0 EXISTS", output);
            Assert.Contains("A002 OK FETCH completed", output);
            Assert.Contains("* SEARCH", output);
            Assert.Contains("A003 OK SEARCH completed", output);
            Assert.Contains("A004 OK EXPUNGE completed", output);
            Assert.DoesNotContain("BAD", output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapSearchCharsetAndUnicodeNormalizationAreStable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-search-unicode-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                1,
                "Subject: caf\u0065\u0301 \uff25xample\r\n\r\nbody\r\n");
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                2,
                "Subject: other\r\n\r\nbody\r\n");
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 SEARCH CHARSET UTF-8 SUBJECT \"CAFE example\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 SEARCH SUBJECT \"caf\u00e9 \uff45xample\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A003 SEARCH CHARSET ISO-8859-1 SUBJECT \"caf\u00e9\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            var output = stream.OutputText;
            Assert.Contains("* SEARCH 1", output);
            Assert.Contains("A001 OK SEARCH completed", output);
            Assert.Contains("A002 OK SEARCH completed", output);
            Assert.Contains("A003 NO \"Unsupported search charset: ISO-8859-1.\"", output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapExpungeUpdatesSequencesWithoutChangingRemainingUids()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-expunge-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                2,
                "Subject: second\r\n\r\nsecond body\r\n");
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                3,
                "Subject: third\r\n\r\nthird body\r\n");
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            await mailbox.StoreFlagsAsync(scenario.MailboxId, "1", useUid: false, "+FLAGS", ["\\Deleted"], CancellationToken.None);
            await using var expungeStream = new DuplexMemoryStream(string.Empty);
            await using var expungeConnection = new ImapConnection(expungeStream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExpungeAsync(expungeConnection, scenario.State, CancellationToken.None);
            await using var fetchStream = new DuplexMemoryStream(string.Empty);
            await using var fetchConnection = new ImapConnection(fetchStream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.FetchAsync(
                fetchConnection,
                scenario.State,
                "1 (UID FLAGS)",
                "127.0.0.1:143",
                useUid: false,
                CancellationToken.None);
            await using var uidFetchStream = new DuplexMemoryStream(string.Empty);
            await using var uidFetchConnection = new ImapConnection(uidFetchStream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.FetchAsync(
                uidFetchConnection,
                scenario.State,
                "1 (UID FLAGS)",
                "127.0.0.1:143",
                useUid: true,
                CancellationToken.None);

            Assert.Contains("* 1 EXPUNGE", expungeStream.OutputText);
            Assert.Contains("* 1 FETCH (UID 2", fetchStream.OutputText);
            Assert.DoesNotContain("UID 1", fetchStream.OutputText);
            Assert.DoesNotContain("FETCH", uidFetchStream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapCopyAndMoveReturnUidMappingsAndAssignNewTargetUids()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-copy-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                2,
                "Subject: second\r\n\r\nsecond body\r\n");
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            var archive = await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Archive", CancellationToken.None);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            var copy = await scenario.Service.CopyMoveAsync(
                connection,
                scenario.State,
                "1:2 Archive",
                useUid: false,
                move: false,
                CancellationToken.None);
            var move = await scenario.Service.CopyMoveAsync(
                connection,
                scenario.State,
                "2 Archive",
                useUid: true,
                move: true,
                CancellationToken.None);
            var targetItems = await mailbox.FetchAsync(archive.Id, "1:*", useUid: false, CancellationToken.None);
            var sourceItems = await mailbox.FetchAsync(scenario.MailboxId, "1:*", useUid: false, CancellationToken.None);

            Assert.Equal([1, 2], copy.SourceUids);
            Assert.Equal([1, 2], copy.TargetUids);
            Assert.Equal(archive.UidValidity, copy.TargetUidValidity);
            Assert.Equal([2], move.SourceUids);
            Assert.Equal([3], move.TargetUids);
            Assert.Contains("* 2 EXPUNGE", stream.OutputText);
            Assert.Equal([1, 2, 3], targetItems.Select(x => x.Uid));
            Assert.Equal([1], sourceItems.Select(x => x.Uid));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapStoresCustomFlagsAndAdvertisesPermanentKeywordSupport()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-flags-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            await using var selectStream = new DuplexMemoryStream(string.Empty);
            await using var selectConnection = new ImapConnection(selectStream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.SelectAsync(
                selectConnection,
                scenario.State,
                "INBOX",
                readOnly: false,
                "127.0.0.1:143",
                CancellationToken.None);

            await mailbox.StoreFlagsAsync(
                scenario.MailboxId,
                "1",
                useUid: false,
                "+FLAGS",
                ["$Forwarded", "$Junk", "MyCustomFlag"],
                CancellationToken.None);
            var items = await mailbox.FetchAsync(scenario.MailboxId, "1", useUid: false, CancellationToken.None);

            Assert.Contains(@"[PERMANENTFLAGS (\Answered \Flagged \Deleted \Seen \Draft \*)]", selectStream.OutputText);
            Assert.Contains("$Forwarded", items.Single().Flags);
            Assert.Contains("$Junk", items.Single().Flags);
            Assert.Contains("MyCustomFlag", items.Single().Flags);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapMailboxNamesSupportSpecialCharactersAndInboxCasePolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-names-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();

            var inboxLower = await mailbox.SelectAsync(scenario.State.AccountId!.Value, "inbox", CancellationToken.None);
            var special = await mailbox.CreateAsync(scenario.State.AccountId!.Value, "项目/客户A", CancellationToken.None);
            var dotted = await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Archive.2026", CancellationToken.None);
            var quoted = await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Quotes \"Q\" 😀", CancellationToken.None);
            var duplicateCase = await mailbox.CreateAsync(scenario.State.AccountId!.Value, "archive.2026", CancellationToken.None);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.ListAsync(
                connection,
                new ImapSessionState { AccountId = scenario.State.AccountId },
                subscribedOnly: false,
                CancellationToken.None);

            Assert.Equal(scenario.MailboxId, inboxLower?.Id);
            Assert.Equal("项目/客户A", special.Name);
            Assert.Equal(dotted.Id, duplicateCase.Id);
            Assert.Contains(@"""项目/客户A""", stream.OutputText);
            Assert.Contains(@"""Archive.2026""", stream.OutputText);
            Assert.Contains(@"""Quotes \""Q\"" 😀""", stream.OutputText);
            Assert.Equal("Quotes \"Q\" 😀", quoted.Name);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapPartialFetchSlicesBodySectionsByUtf8ByteOffset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-partial-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                1,
                "Subject: utf8\r\n\r\nhello 世界 😀 body\r\n");
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.FetchAsync(
                connection,
                scenario.State,
                "1 (BODY.PEEK[TEXT]<6.6> BODY.PEEK[TEXT]<999.20>)",
                "127.0.0.1:143",
                useUid: false,
                CancellationToken.None);

            Assert.Contains("BODY[TEXT]<6> {6}\r\n世界", stream.OutputText);
            Assert.Contains("BODY[TEXT]<999> {0}", stream.OutputText);
            Assert.DoesNotContain("hello 世界", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapSearchHeaderMatchesFoldedHeaderValues()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-search-header-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                1,
                "Subject: hello\r\n world\r\nX-Trace: first\r\n second\r\n\r\nbody\r\n");
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                2,
                "Subject: other\r\nX-Trace: unrelated\r\n\r\nbody\r\n");
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 SEARCH HEADER Subject \"hello world\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 SEARCH HEADER X-Trace \"first second\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            Assert.Equal(2, Regex.Matches(stream.OutputText, @"\* SEARCH 1").Count);
            Assert.DoesNotContain("* SEARCH 2", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapBodyStructureDescribesMultipartAndAttachments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-bodystructure-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                1,
                """
                MIME-Version: 1.0
                Subject: multipart
                Content-Type: multipart/mixed; boundary="b1"

                --b1
                Content-Type: text/plain; charset=utf-8
                Content-Transfer-Encoding: quoted-printable

                hello
                --b1
                Content-Type: application/pdf; name="file.pdf"
                Content-Transfer-Encoding: base64
                Content-Disposition: attachment; filename="file.pdf"

                SGVsbG8=
                --b1--
                """.ReplaceLineEndings("\r\n"));
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.FetchAsync(
                connection,
                scenario.State,
                "1 (BODYSTRUCTURE)",
                "127.0.0.1:143",
                useUid: false,
                CancellationToken.None);

            var output = stream.OutputText;
            Assert.Contains("BODYSTRUCTURE", output);
            Assert.Contains("\"MIXED\"", output);
            Assert.Contains("\"TEXT\" \"PLAIN\"", output);
            Assert.Contains("\"CHARSET\" \"utf-8\"", output);
            Assert.Contains("\"APPLICATION\" \"PDF\"", output);
            Assert.Contains("\"NAME\" \"file.pdf\"", output);
            Assert.Contains("(\"ATTACHMENT\" (\"FILENAME\" \"file.pdf\"))", output);
            Assert.Contains("\"BASE64\"", output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapRfc822SizeUsesOriginalStoredBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-rfc822-size-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            var raw = "Subject: size\r\nX-Test: one\r\n\tfolded\r\n\r\nhello\r\n";
            await AddInboxMessageAsync(scenario.Factory, scenario.Store, scenario.MailboxId, 1, raw);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.FetchAsync(
                connection,
                scenario.State,
                "1 (RFC822.SIZE RFC822)",
                "127.0.0.1:143",
                useUid: false,
                CancellationToken.None);

            var size = Encoding.UTF8.GetByteCount(raw);
            Assert.Contains($"RFC822.SIZE {size}", stream.OutputText);
            Assert.Contains($"RFC822 {{{size}}}", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapAppendUidUsesTargetUidValidityAndPreservesUidNext()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-appenduid-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var target = await scenario.Provider.GetRequiredService<IMailboxService>()
                .CreateAsync(scenario.State.AccountId!.Value, "Uploads", CancellationToken.None);
            var message = "Subject: append uid\r\n\r\nbody\r\n";
            await using var stream = new DuplexMemoryStream(message + "\r\n");
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.AppendAsync(
                connection,
                scenario.State,
                "A001",
                $"\"Uploads\" (\\Seen \\Flagged) \"08-Jul-2026 12:34:56 +0900\" {{{Encoding.UTF8.GetByteCount(message)}}}",
                "local",
                CancellationToken.None);

            Assert.Contains($"A001 OK [APPENDUID {target.UidValidity} 1]", stream.OutputText);
            await using var verification = await scenario.Factory.CreateDbContextAsync();
            var mailbox = await verification.Mailboxes.SingleAsync(x => x.Id == target.Id);
            var stored = await verification.MailboxMessages.SingleAsync(x => x.MailboxId == target.Id);
            Assert.Equal(2, mailbox.UidNext);
            Assert.Equal("\\Seen \\Flagged", stored.Flags);
            Assert.Equal(new DateTimeOffset(2026, 7, 8, 12, 34, 56, TimeSpan.FromHours(9)), stored.InternalDate);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapCopyMoveKeepInternalDateStable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-internaldate-stable-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var expected = new DateTimeOffset(2026, 7, 8, 12, 34, 56, TimeSpan.FromHours(8));
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                var item = await db.MailboxMessages.SingleAsync(x => x.MailboxId == scenario.MailboxId);
                item.InternalDate = expected;
                await db.SaveChangesAsync();
            }

            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            var archive = await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Archive", CancellationToken.None);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.CopyMoveAsync(connection, scenario.State, "1 Archive", useUid: false, move: false, CancellationToken.None);
            await scenario.Service.CopyMoveAsync(connection, scenario.State, "1 Archive", useUid: false, move: true, CancellationToken.None);

            var copied = await mailbox.FetchAsync(archive.Id, "1:*", useUid: false, CancellationToken.None);
            Assert.Equal([expected, expected], copied.Select(x => x.InternalDate));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapLiteralPlusIsAcceptedButNotAdvertised()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-literal-plus-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var message = "Subject: literal plus\r\n\r\nbody\r\n";
            await using var stream = new DuplexMemoryStream(message + "\r\n");
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.AppendAsync(
                connection,
                scenario.State,
                "A001",
                $"\"INBOX\" {{{Encoding.UTF8.GetByteCount(message)}+}}",
                "local",
                CancellationToken.None);

            Assert.DoesNotContain("LITERAL+", ImapServerService.BaseCapabilities);
            Assert.DoesNotContain("+ Ready for literal data", stream.OutputText);
            Assert.Contains("A001 OK [APPENDUID", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapQuotedMailboxNamesRoundTripAndListWildcardFilters()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-list-wildcard-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await scenario.Service.ExecuteLineAsync(
                new ImapConnection(new DuplexMemoryStream(string.Empty), TimeSpan.FromSeconds(1), 4096),
                scenario.State,
                "A001 CREATE \"folder \\\"quoted\\\" name\"",
                "local",
                ConnectionSecurity.StartTls,
                true,
                CancellationToken.None);
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "folder \\ slash", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Projects/A", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Projects/Nested/B", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Archive", CancellationToken.None);

            await using var oneLevelStream = new DuplexMemoryStream(string.Empty);
            await using var oneLevelConnection = new ImapConnection(oneLevelStream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.ListAsync(oneLevelConnection, scenario.State, "\"\" \"%\"", subscribedOnly: false, CancellationToken.None);
            await using var allStream = new DuplexMemoryStream(string.Empty);
            await using var allConnection = new ImapConnection(allStream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.ListAsync(allConnection, scenario.State, "\"\" \"*\"", subscribedOnly: false, CancellationToken.None);
            await using var projectStream = new DuplexMemoryStream(string.Empty);
            await using var projectConnection = new ImapConnection(projectStream, TimeSpan.FromSeconds(1), 4096);
            await scenario.Service.ListAsync(projectConnection, scenario.State, "\"Projects/\" \"%\"", subscribedOnly: false, CancellationToken.None);

            Assert.Contains(@"""folder \""quoted\"" name""", oneLevelStream.OutputText);
            Assert.Contains(@"""folder \\ slash""", oneLevelStream.OutputText);
            Assert.Contains(@"""Archive""", oneLevelStream.OutputText);
            Assert.DoesNotContain(@"""Projects/A""", oneLevelStream.OutputText);
            Assert.Contains(@"""Projects/Nested/B""", allStream.OutputText);
            Assert.Contains(@"""Projects/A""", projectStream.OutputText);
            Assert.DoesNotContain(@"""Projects/Nested/B""", projectStream.OutputText);
            Assert.Contains(@"""/""", projectStream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapSubscribeLsubAndUnsubscribePersist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-lsub-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await scenario.Provider.GetRequiredService<IMailboxService>()
                .CreateAsync(scenario.State.AccountId!.Value, "Archive", CancellationToken.None);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 SUBSCRIBE \"Archive\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 LSUB \"\" \"*\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A003 UNSUBSCRIBE \"Archive\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A004 LSUB \"\" \"*\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            var output = stream.OutputText;
            Assert.Contains(@"* LSUB (\HasNoChildren \Archive) ""/"" ""Archive""", output);
            Assert.True(output.IndexOf("A003 OK", StringComparison.Ordinal) < output.LastIndexOf("A004 OK", StringComparison.Ordinal));
            Assert.Single(Regex.Matches(output, @"\* LSUB"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapSelectedMailboxDeletedByAnotherConnectionDeselectsOnNoop()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-selected-deleted-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Projects", CancellationToken.None);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 SELECT Projects", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await mailbox.DeleteAsync(scenario.State.AccountId!.Value, "Projects", CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 NOOP", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A003 FETCH 1 FLAGS", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A004 CLOSE", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            Assert.Contains("* 0 EXISTS", stream.OutputText);
            Assert.Contains("A002 OK NOOP completed", stream.OutputText);
            Assert.Contains("A003 NO \"Select a mailbox first.\"", stream.OutputText);
            Assert.Contains("A004 NO \"Select a mailbox first.\"", stream.OutputText);
            Assert.Null(scenario.State.MailboxId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapRenameParentMailboxMovesChildrenAndSystemMailboxesAreProtected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-rename-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Projects", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Projects/Alpha", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Projects/Beta", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Sent", CancellationToken.None);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 RENAME \"Projects\" \"OldProjects\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 RENAME INBOX OldInbox", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A003 DELETE Sent", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            var names = (await mailbox.ListAsync(scenario.State.AccountId!.Value, CancellationToken.None)).Select(x => x.Name).ToArray();
            Assert.Contains("OldProjects", names);
            Assert.Contains("OldProjects/Alpha", names);
            Assert.Contains("OldProjects/Beta", names);
            Assert.DoesNotContain("Projects", names);
            Assert.DoesNotContain("OldInbox", names);
            Assert.Contains("INBOX", names);
            Assert.Contains("Sent", names);
            Assert.Contains("A002 NO", stream.OutputText);
            Assert.Contains("System mailbox 'INBOX' cannot be renamed.", stream.OutputText);
            Assert.Contains("A003 NO", stream.OutputText);
            Assert.Contains("System mailbox 'Sent' cannot be deleted.", stream.OutputText);
            await using var verification = await scenario.Factory.CreateDbContextAsync();
            var inbox = await verification.Mailboxes.SingleAsync(x => x.AccountId == scenario.State.AccountId && x.Name == "INBOX");
            Assert.True(await verification.MailboxMessages.AnyAsync(x => x.MailboxId == inbox.Id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapDeleteMailboxWithChildrenIsRejectedAndStatusDoesNotSelect()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-delete-status-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Projects", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Projects/Alpha", CancellationToken.None);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 SELECT INBOX", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            var selected = scenario.State.MailboxId;
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 STATUS Projects (MESSAGES UNSEEN UIDNEXT UIDVALIDITY)", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A003 DELETE Projects", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            Assert.Equal(selected, scenario.State.MailboxId);
            Assert.Equal("INBOX", scenario.State.MailboxName);
            Assert.Contains("* STATUS \"Projects\"", stream.OutputText);
            Assert.Contains("A002 OK STATUS completed", stream.OutputText);
            Assert.Contains("A003 NO \"Mailbox has child mailboxes.\"", stream.OutputText);
            Assert.NotNull(await mailbox.SelectAsync(scenario.State.AccountId!.Value, "Projects", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapMimeBoundaryLikeTextNestedMultipartAndMalformedContentStayFetchable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-mime-87-90-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                1,
                """
                MIME-Version: 1.0
                Subject: boundary
                Content-Type: multipart/mixed; boundary="----abc"

                ------abc
                Content-Type: text/plain

                this line mentions ------abc but is not a delimiter
                ------abc--
                """.ReplaceLineEndings("\r\n"));
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                2,
                """
                MIME-Version: 1.0
                Subject: nested
                Content-Type: multipart/mixed; boundary="outer"

                --outer
                Content-Type: multipart/alternative; boundary="alt"

                --alt
                Content-Type: text/plain; charset=utf-8

                plain
                --alt
                Content-Type: text/html; charset=utf-8

                <b>html</b>
                --alt--
                --outer
                Content-Type: application/pdf; name="a.pdf"
                Content-Disposition: attachment; filename="a.pdf"
                Content-Transfer-Encoding: base64

                SGVsbG8=
                --outer--
                """.ReplaceLineEndings("\r\n"));
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                3,
                "Subject: bad content type\r\nContent-Type: text/html; charset=\"unknown-charset\"\r\n\r\n<html>safe</html>\r\n");
            var invalidBase64 = "Subject: bad base64\r\nContent-Type: application/octet-stream\r\nContent-Transfer-Encoding: base64\r\n\r\n%%%not-base64%%%\r\n";
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                4,
                invalidBase64);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 16384);

            await scenario.Service.FetchAsync(connection, scenario.State, "1:* (BODYSTRUCTURE RFC822)", "local", useUid: false, CancellationToken.None);

            var output = stream.OutputText;
            Assert.Contains("this line mentions ------abc", output);
            Assert.Contains("\"ALTERNATIVE\"", output);
            Assert.Contains("\"TEXT\" \"HTML\"", output);
            Assert.Contains("\"APPLICATION\" \"PDF\"", output);
            Assert.Contains("unknown-charset", output);
            Assert.Contains("%%%not-base64%%%", output);
            Assert.Contains($"RFC822 {{{Encoding.UTF8.GetByteCount(invalidBase64)}}}", output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapMimeAttachmentsKeepDuplicateAndEncodedFileNamesDistinct()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-mime-91-92-99-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                1,
                """
                MIME-Version: 1.0
                Subject: attachments
                Content-Type: multipart/mixed; boundary="files"

                --files
                Content-Type: text/plain; charset=utf-8

                body
                --files
                Content-Type: application/pdf; name="invoice.pdf"
                Content-Disposition: attachment; filename="invoice.pdf"
                Content-Transfer-Encoding: base64

                Zmlyc3Q=
                --files
                Content-Type: application/pdf; name="invoice.pdf"
                Content-Disposition: attachment; filename="invoice.pdf"
                Content-Transfer-Encoding: base64

                c2Vjb25k
                --files
                Content-Type: application/pdf
                Content-Disposition: attachment;
                 filename*=UTF-8''%E6%8A%A5%E5%91%8A.pdf
                Content-Transfer-Encoding: base64

                cmZjMjIzMQ==
                --files
                Content-Type: application/pdf
                Content-Disposition: attachment; filename="=?UTF-8?B?5rWL6K+VLnBkZg==?="
                Content-Transfer-Encoding: base64

                cmZjMjA0Nw==
                --files--
                """.ReplaceLineEndings("\r\n"));
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 32768);

            await scenario.Service.FetchAsync(connection, scenario.State, "1 (BODYSTRUCTURE RFC822)", "local", useUid: false, CancellationToken.None);

            var output = stream.OutputText;
            Assert.True(Regex.Matches(output, "\"FILENAME\" \"invoice.pdf\"").Count >= 2);
            Assert.Contains("\"FILENAME\" \"报告.pdf\"", output);
            Assert.Contains("\"FILENAME\" \"测试.pdf\"", output);
            Assert.Contains("filename*=UTF-8''%E6%8A%A5%E5%91%8A.pdf", output);
            Assert.Contains("=?UTF-8?B?5rWL6K+VLnBkZg==?=", output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapSearchTextFindsHtmlOnlyPlainOnlyAndEncodedSubject()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-mime-93-94-98-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                1,
                """
                MIME-Version: 1.0
                Subject: =?UTF-8?B?5rWL?= =?UTF-8?B?6K+V?=
                Content-Type: text/html; charset=utf-8

                <html><body><h1>欢迎</h1><p>Only HTML searchable summary</p><script>alert(1)</script></body></html>
                """.ReplaceLineEndings("\r\n"));
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                2,
                """
                MIME-Version: 1.0
                Subject: plain
                Content-Type: text/plain; charset=utf-8

                Only plain searchable body.
                """.ReplaceLineEndings("\r\n"));
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 SEARCH TEXT \"HTML searchable\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 SEARCH BODY \"plain searchable\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A003 SEARCH SUBJECT \"测试\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            Assert.Contains("* SEARCH 1", stream.OutputText);
            Assert.Contains("* SEARCH 2", stream.OutputText);
            Assert.Equal(2, Regex.Matches(stream.OutputText, @"\* SEARCH 1").Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapDirtyMultipartBoundariesRemainFetchable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-mime-95-97-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            var missingClose = """
                MIME-Version: 1.0
                Subject: missing close
                Content-Type: multipart/mixed; boundary=abc

                --abc
                Content-Type: text/plain

                hello without final boundary
                """.ReplaceLineEndings("\r\n");
            var trailingSpace = """
                MIME-Version: 1.0
                Subject: trailing boundary
                Content-Type: multipart/mixed; boundary=abc

                --abc
                Content-Type: text/plain

                hello with dirty boundary
                --abc--
                """.ReplaceLineEndings("\r\n")
                .Replace("--abc\r\nContent-Type", "--abc \r\nContent-Type", StringComparison.Ordinal);
            var specialBoundary = """
                MIME-Version: 1.0
                Subject: special boundary
                Content-Type: multipart/mixed; boundary="----=_NextPart_(abc)+123"

                ------=_NextPart_(abc)+123
                Content-Type: text/plain

                hello special boundary
                ------=_NextPart_(abc)+123--
                """.ReplaceLineEndings("\r\n");
            await AddInboxMessageAsync(scenario.Factory, scenario.Store, scenario.MailboxId, 1, missingClose);
            await AddInboxMessageAsync(scenario.Factory, scenario.Store, scenario.MailboxId, 2, trailingSpace);
            await AddInboxMessageAsync(scenario.Factory, scenario.Store, scenario.MailboxId, 3, specialBoundary);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 32768);

            await scenario.Service.FetchAsync(connection, scenario.State, "1:* (BODYSTRUCTURE RFC822)", "local", useUid: false, CancellationToken.None);

            var output = stream.OutputText;
            Assert.Contains("hello without final boundary", output);
            Assert.Contains("hello with dirty boundary", output);
            Assert.Contains("hello special boundary", output);
            Assert.Contains($"RFC822 {{{Encoding.UTF8.GetByteCount(missingClose)}}}", output);
            Assert.Contains($"RFC822 {{{Encoding.UTF8.GetByteCount(trailingSpace)}}}", output);
            Assert.Contains($"RFC822 {{{Encoding.UTF8.GetByteCount(specialBoundary)}}}", output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapMismatchedTransferEncodingKeepsOriginalBytesFetchable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-mime-100-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            var badBase64 = "Subject: bad base64\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\nhello world\r\n";
            var badQuotedPrintable = "Subject: bad qp\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nhello =XX world=\r\n";
            await AddInboxMessageAsync(scenario.Factory, scenario.Store, scenario.MailboxId, 1, badBase64);
            await AddInboxMessageAsync(scenario.Factory, scenario.Store, scenario.MailboxId, 2, badQuotedPrintable);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 32768);

            await scenario.Service.FetchAsync(connection, scenario.State, "1:* (BODYSTRUCTURE RFC822)", "local", useUid: false, CancellationToken.None);

            Assert.Contains("hello world", stream.OutputText);
            Assert.Contains("hello =XX world=", stream.OutputText);
            Assert.Contains($"RFC822 {{{Encoding.UTF8.GetByteCount(badBase64)}}}", stream.OutputText);
            Assert.Contains($"RFC822 {{{Encoding.UTF8.GetByteCount(badQuotedPrintable)}}}", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapMime101To111StructuresSearchAndFetchRemainStable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-mime-101-111-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            var raw = """
                MIME-Version: 1.0
                Subject: complex
                Content-Type: multipart/mixed; boundary="outer"

                --outer
                Content-Type: text/plain; charset=utf-8
                Content-Transfer-Encoding: quoted-printable

                hello=
                world
                --outer
                Content-Type: multipart/mixed; boundary="empty"

                --empty
                Content-Type: text/plain

                --empty
                Content-Type: application/pdf
                Content-Disposition: attachment; filename="empty.pdf"

                --empty--
                --outer
                Content-Type: message/rfc822
                Content-Disposition: attachment; filename="forwarded.eml"

                From: Nested <nested@example.net>
                To: Alice <alice@example.com>
                Subject: nested message

                nested searchable body
                --outer
                Content-Type: multipart/related; boundary="rel"

                --rel
                Content-Type: text/html; charset=utf-8

                <html><body><img src="cid:image001.png@abc">related body &amp;#20320;</body></html>
                --rel
                Content-Type: image/png
                Content-ID: <image001.png@abc>
                Content-Disposition: inline; filename="image001.png"

                png
                --rel--
                --outer
                Content-Type: text/calendar; method=REQUEST; charset=utf-8

                BEGIN:VCALENDAR
                METHOD:REQUEST
                BEGIN:VEVENT
                UID:event-1
                SEQUENCE:2
                END:VEVENT
                END:VCALENDAR
                --outer
                Content-Type: multipart/signed; protocol="application/pgp-signature"; boundary="signed"

                --signed
                Content-Type: text/plain

                signed body
                --signed
                Content-Type: application/pgp-signature

                signature
                --signed--
                --outer
                Content-Type: application/pkcs7-mime; name="smime.p7m"
                Content-Disposition: attachment; filename="smime.p7m"

                encrypted
                --outer--
                """.ReplaceLineEndings("\r\n");
            await AddInboxMessageAsync(scenario.Factory, scenario.Store, scenario.MailboxId, 1, raw);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 65536);

            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 SEARCH TEXT \"helloworld\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A002 SEARCH TEXT \"nested searchable\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.FetchAsync(connection, scenario.State, "1 (BODYSTRUCTURE RFC822)", "local", useUid: false, CancellationToken.None);

            var output = stream.OutputText;
            Assert.Contains("* SEARCH 1", output);
            Assert.Contains("\"MESSAGE\" \"RFC822\"", output);
            Assert.Contains("\"RELATED\"", output);
            Assert.Contains("image001.png@abc", output);
            Assert.Contains("\"TEXT\" \"CALENDAR\"", output);
            Assert.Contains("\"SIGNED\"", output);
            Assert.Contains("\"APPLICATION\" \"PKCS7-MIME\"", output);
            Assert.Contains("related body &amp;#20320;", output);
            Assert.Contains($"RFC822 {{{Encoding.UTF8.GetByteCount(raw)}}}", output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapFetchHandlesMissingBlobAndLargePartialFetchWithoutLosingMailbox()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-recovery-partial-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                db.MailboxMessages.RemoveRange(db.MailboxMessages.Where(x => x.MailboxId == scenario.MailboxId));
                await db.SaveChangesAsync();
            }

            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                1,
                "Subject: missing\r\n\r\nwill lose blob\r\n");
            var longLine = new string('A', 12_000) + "尾巴";
            var raw = "Subject: long\r\n\r\n" + longLine + "\r\n";
            await AddInboxMessageAsync(scenario.Factory, scenario.Store, scenario.MailboxId, 2, raw);
            string missingKey;
            await using (var db = await scenario.Factory.CreateDbContextAsync())
            {
                missingKey = await db.MailboxMessages
                    .Where(x => x.MailboxId == scenario.MailboxId && x.Uid == 1)
                    .Select(x => x.Message.ContentKey)
                    .SingleAsync();
            }
            var missingPath = Path.Combine(root, missingKey[..2], missingKey[2..4], $"{missingKey}.eml");
            File.Delete(missingPath);
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 65536);

            await scenario.Service.FetchAsync(connection, scenario.State, "1:* (RFC822.SIZE RFC822 BODY.PEEK[]<20.40>)", "local", useUid: false, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "A001 SEARCH ALL", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            var output = stream.OutputText;
            Assert.Contains("* 1 FETCH", output);
            Assert.Contains("RFC822 {0}", output);
            Assert.Contains("* 2 FETCH", output);
            Assert.Contains("BODY[]<20> {40}", output);
            Assert.Contains("* SEARCH 1 2", output);
            Assert.Contains("A001 OK SEARCH completed", output);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapClientCompatibilityFetchSubscriptionsDraftsAndTrashRemainStable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-client-124-131-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            var mailbox = scenario.Provider.GetRequiredService<IMailboxService>();
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Sent", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Trash", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Drafts", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Junk", CancellationToken.None);
            await mailbox.CreateAsync(scenario.State.AccountId!.Value, "Archives", CancellationToken.None);
            var draftOne = "Subject: draft one\r\n\r\n1\r\n";
            var draftTwo = "Subject: draft two\r\n\r\n2\r\n";
            await using var stream = new DuplexMemoryStream(draftOne + "\r\n" + draftTwo + "\r\n");
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 65536);

            for (var i = 0; i < 5; i++)
            {
                await scenario.Service.ExecuteLineAsync(connection, scenario.State, $"F{i} UID FETCH 1:* (UID FLAGS BODY.PEEK[HEADER.FIELDS (FROM TO SUBJECT DATE MESSAGE-ID)])", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            }
            var inboxFlags = (await mailbox.FetchAsync(scenario.MailboxId, "1:*", useUid: false, CancellationToken.None)).Single().Flags;
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "S001 SUBSCRIBE Sent", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "S002 LSUB \"\" \"*\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.AppendAsync(connection, scenario.State, "D001", $"\"Drafts\" (\\Draft) {{{Encoding.UTF8.GetByteCount(draftOne)}}}", "local", CancellationToken.None);
            await scenario.Service.AppendAsync(connection, scenario.State, "D002", $"\"Drafts\" (\\Draft) {{{Encoding.UTF8.GetByteCount(draftTwo)}}}", "local", CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "T001 MOVE 1 Trash", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);
            await scenario.Service.ExecuteLineAsync(connection, scenario.State, "L001 LIST \"\" \"*\"", "local", ConnectionSecurity.StartTls, true, CancellationToken.None);

            var output = stream.OutputText;
            Assert.DoesNotContain("\\Seen", inboxFlags);
            Assert.Contains(@"* LSUB (\HasNoChildren \Sent) ""/"" ""Sent""", output);
            Assert.Contains(@"LIST (\HasNoChildren \Trash) ""/"" ""Trash""", output);
            Assert.Contains(@"LIST (\HasNoChildren \Drafts) ""/"" ""Drafts""", output);
            Assert.Contains(@"LIST (\HasNoChildren \Junk) ""/"" ""Junk""", output);
            Assert.Contains(@"LIST (\HasNoChildren \Archive) ""/"" ""Archives""", output);
            var drafts = await mailbox.SelectAsync(scenario.State.AccountId.Value, "Drafts", CancellationToken.None);
            Assert.Equal(2, drafts!.Exists);
            Assert.True(drafts.UidNext >= 3);
            var trash = await mailbox.SelectAsync(scenario.State.AccountId.Value, "Trash", CancellationToken.None);
            Assert.Equal(1, trash!.Exists);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ImapRefreshSelectedMailboxReportsNewExists()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-imap-refresh-{Guid.NewGuid():N}");
        try
        {
            var scenario = await CreateImapInboxScenarioAsync(root);
            await AddInboxMessageAsync(
                scenario.Factory,
                scenario.Store,
                scenario.MailboxId,
                2,
                "Subject: second\r\n\r\nsecond body\r\n");
            await using var stream = new DuplexMemoryStream(string.Empty);
            await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);

            await scenario.Service.RefreshSelectedMailboxAsync(
                connection,
                scenario.State,
                "127.0.0.1:143",
                CancellationToken.None);

            Assert.Contains("* 2 EXISTS", stream.OutputText);
            Assert.Contains("[UIDNEXT 3]", stream.OutputText);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SmtpCapabilitiesAdvertisePlainAndLoginAfterTls()
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);

        await service.ReplyCapabilitiesAsync(
            connection,
            new SmtpEndpointOptions { Port = 587, Security = ConnectionSecurity.StartTls, RequireAuthentication = true },
            isTls: true,
            CancellationToken.None);

        Assert.Contains("250-AUTH PLAIN LOGIN", stream.OutputText);
    }

    [Fact]
    public async Task SmtpCapabilitiesDoNotAdvertiseUnimplementedDsnOrSmtpUtf8()
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);

        await service.ReplyCapabilitiesAsync(
            connection,
            new SmtpEndpointOptions { Port = 25, Security = ConnectionSecurity.Plain },
            isTls: false,
            CancellationToken.None);

        Assert.DoesNotContain("SMTPUTF8", stream.OutputText);
        Assert.DoesNotContain("250-DSN", stream.OutputText);
        Assert.Contains("250-SIZE", stream.OutputText);
        Assert.Contains("250-8BITMIME", stream.OutputText);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("[127.0.0.1]", true)]
    [InlineData("[IPv6:::1]", true)]
    [InlineData("mail.example.com", true)]
    [InlineData("", false)]
    [InlineData("-bad.example", false)]
    [InlineData("bad..example", false)]
    public void SmtpHeloValidationIsStrictAndNonThrowing(string value, bool expected)
    {
        Assert.Equal(expected, SmtpServerService.TryValidateHelo(value, out _));
    }

    [Fact]
    public void SmtpRecipientLimitCountsOnlyDistinctRecipients()
    {
        var state = new SmtpSessionState();
        for (var index = 0; index < 500; index++)
        {
            var result = state.TryAddRecipient($"user{index}@example.com", 100);
            Assert.Equal(index < 100 ? RecipientAddResult.Added : RecipientAddResult.LimitExceeded, result);
        }

        Assert.Equal(RecipientAddResult.Duplicate, state.TryAddRecipient("USER0@example.com", 100));
        Assert.Equal(100, state.Recipients.Count);
    }

    [Fact]
    public void SmtpRsetClearsOnlyTransactionState()
    {
        var accountId = Guid.NewGuid();
        var state = new SmtpSessionState
        {
            HeloName = "client.example.com",
            Extended = true,
            AuthenticatedAccountId = accountId,
            AuthenticatedUser = "alice@example.com",
            Sender = "alice@example.com"
        };
        state.Recipients.Add("bob@example.com");

        state.ResetTransaction();

        Assert.Null(state.Sender);
        Assert.Empty(state.Recipients);
        Assert.Equal("client.example.com", state.HeloName);
        Assert.True(state.Extended);
        Assert.Equal(accountId, state.AuthenticatedAccountId);
    }

    [Fact]
    public void SmtpAuthFailureLimiterBlocksAcrossConnectionsAndExpires()
    {
        var limiter = new SmtpAuthFailureLimiter(new SmtpAbuseOptions
        {
            MaximumAuthenticationFailures = 3,
            AuthenticationWindowMinutes = 15,
            AuthenticationBlockMinutes = 30
        });
        var now = DateTimeOffset.UtcNow;

        Assert.False(limiter.RecordFailure("192.0.2.1:1000", now));
        Assert.False(limiter.RecordFailure("192.0.2.1:2000", now.AddMinutes(1)));
        Assert.True(limiter.RecordFailure("192.0.2.1:3000", now.AddMinutes(2)));
        Assert.True(limiter.IsBlocked("192.0.2.1:4000", now.AddMinutes(3)));
        Assert.False(limiter.IsBlocked("192.0.2.1:4000", now.AddMinutes(33)));
    }

    [Fact]
    public async Task ImapOnlyCountsRejectedCredentialsAndClosesAfterThreshold()
    {
        var protection = new ConnectionProtection(new ConfigurationBuilder().Build());
        protection.Apply(ConnectionProtectionSettings.Default with
        {
            ImapLogin = new LoginProtection(15, 2, 30)
        });
        var services = new ServiceCollection();
        services.AddSingleton<IAccountAuthenticator>(new FakeAccountAuthenticator("alice@example.com", "secret"));
        using var provider = services.BuildServiceProvider();
        var imap = new ImapServerService(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ImapServerOptions()), null!, NullLogger<ImapServerService>.Instance,
            protection: protection);
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new ImapConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = new ImapSessionState();
        const string remote = "192.0.2.44:50000";
        Assert.True(await imap.ExecuteCommandAsync(connection, state, "A001", "LOGIN", "invalid",
            false, remote, ConnectionSecurity.StartTls, true, CancellationToken.None));
        Assert.True(await imap.ExecuteCommandAsync(connection, state, "A002", "LOGIN", "alice@example.com wrong",
            false, remote, ConnectionSecurity.StartTls, true, CancellationToken.None));
        Assert.False(await imap.ExecuteCommandAsync(connection, state, "A003", "LOGIN", "alice@example.com wrong",
            false, remote, ConnectionSecurity.StartTls, true, CancellationToken.None));
        Assert.Contains("A001 BAD", stream.OutputText);
        Assert.Contains("A002 NO", stream.OutputText);
        Assert.Contains("A003 NO", stream.OutputText);
        Assert.Contains("* BYE [UNAVAILABLE] Too many authentication failures", stream.OutputText);
    }

    [Theory]
    [InlineData("TO:<bob@example.com>", true, 0)]
    [InlineData("TO:<bob@example.com> NOTIFY=FAILURE", true, 1)]
    [InlineData("TO:<bob@example.com> ORCPT=rfc822;bob@example.com", true, 1)]
    public void SmtpRcptParametersNeverBecomePartOfAddress(
        string arguments,
        bool valid,
        int parameterCount)
    {
        Assert.Equal(
            valid,
            SmtpServerService.TryParsePathAndParameters(
                arguments,
                "TO:",
                allowEmpty: false,
                out var address,
                out var parameters));
        Assert.Equal("bob@example.com", address);
        Assert.Equal(parameterCount, parameters.Length);
    }

    [Fact]
    public async Task SmtpPort25CanAdvertiseStartTlsWithoutAuth()
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);

        await service.ReplyCapabilitiesAsync(
            connection,
            new SmtpEndpointOptions { Port = 25, Security = ConnectionSecurity.StartTls, RequireAuthentication = false },
            isTls: false,
            CancellationToken.None);

        Assert.Contains("250-STARTTLS", stream.OutputText);
        Assert.DoesNotContain("AUTH PLAIN LOGIN", stream.OutputText);
    }

    [Fact]
    public async Task SmtpPort25PlainDoesNotAdvertiseStartTls()
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);

        await service.ReplyCapabilitiesAsync(
            connection,
            new SmtpEndpointOptions { Port = 25, Security = ConnectionSecurity.Plain },
            isTls: false,
            CancellationToken.None);

        Assert.DoesNotContain("250-STARTTLS", stream.OutputText);
    }

    [Fact]
    public async Task SmtpAuthPlainStillAuthenticates()
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = new SmtpSessionState();

        await service.AuthenticateAsync(
            connection,
            state,
            $"PLAIN {Base64("\0alice@example.com\0secret")}",
            "127.0.0.1:587",
            CancellationToken.None);

        Assert.Equal("alice@example.com", state.AuthenticatedUser);
        Assert.NotNull(state.AuthenticatedAccountId);
        Assert.Contains("235 2.7.0 Authentication successful", stream.OutputText);
    }

    [Fact]
    public async Task SmtpAuthLoginTwoStepAuthenticates()
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream($"{Base64("alice@example.com")}\r\n{Base64("secret")}\r\n");
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = new SmtpSessionState();

        await service.AuthenticateAsync(
            connection,
            state,
            "LOGIN",
            "127.0.0.1:587",
            CancellationToken.None);

        Assert.Equal("alice@example.com", state.AuthenticatedUser);
        Assert.NotNull(state.AuthenticatedAccountId);
        Assert.Contains("334 VXNlcm5hbWU6", stream.OutputText);
        Assert.Contains("334 UGFzc3dvcmQ6", stream.OutputText);
        Assert.Contains("235 2.7.0 Authentication successful", stream.OutputText);
    }

    [Fact]
    public async Task SmtpAuthLoginInitialUserNameAuthenticates()
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream($"{Base64("secret")}\r\n");
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = new SmtpSessionState();

        await service.AuthenticateAsync(
            connection,
            state,
            $"LOGIN {Base64("alice@example.com")}",
            "127.0.0.1:587",
            CancellationToken.None);

        Assert.Equal("alice@example.com", state.AuthenticatedUser);
        Assert.NotNull(state.AuthenticatedAccountId);
        Assert.DoesNotContain("334 VXNlcm5hbWU6", stream.OutputText);
        Assert.Contains("334 UGFzc3dvcmQ6", stream.OutputText);
        Assert.Contains("235 2.7.0 Authentication successful", stream.OutputText);
    }

    [Theory]
    [InlineData("LOGIN !!!", "501 5.5.2 Invalid AUTH payload")]
    [InlineData("CRAM-MD5", "504 5.5.4 Authentication mechanism is disabled")]
    public async Task SmtpAuthRejectsInvalidPayloadsAndUnsupportedMechanisms(
        string arguments,
        string expectedResponse)
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = new SmtpSessionState();

        await service.AuthenticateAsync(
            connection,
            state,
            arguments,
            "127.0.0.1:587",
            CancellationToken.None);

        Assert.Contains(expectedResponse, stream.OutputText);
        Assert.False(state.CredentialRejected);
        Assert.DoesNotContain("secret", stream.OutputText);
    }

    [Fact]
    public async Task SmtpAuthRejectsWrongPasswordWithoutLeakingIt()
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream($"{Base64("alice@example.com")}\r\n{Base64("wrong-password")}\r\n");
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = new SmtpSessionState();

        await service.AuthenticateAsync(
            connection,
            state,
            "LOGIN",
            "127.0.0.1:587",
            CancellationToken.None);

        Assert.Null(state.AuthenticatedUser);
        Assert.Null(state.AuthenticatedAccountId);
        Assert.Contains("535 5.7.8 Authentication credentials invalid", stream.OutputText);
        Assert.True(state.CredentialRejected);
        Assert.DoesNotContain("wrong-password", stream.OutputText);
    }

    [Fact]
    public async Task SmtpMailFromAllowsAuthenticatedAccountAndOwnedAlias()
    {
        var scenario = await CreateSmtpSenderScenarioAsync();
        using var provider = scenario.Provider;
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = scenario.AuthenticatedState();

        await scenario.Service.HandleMailFromAsync(
            connection,
            state,
            "FROM:<alice@example.com>",
            "127.0.0.1:587",
            AuthenticatedEndpoint(),
            CancellationToken.None);

        Assert.Equal("alice@example.com", state.Sender);
        Assert.Contains("250 2.1.0 Sender OK", stream.OutputText);

        state.ResetTransaction();
        await scenario.Service.HandleMailFromAsync(
            connection,
            state,
            "FROM:<sales@example.com>",
            "127.0.0.1:587",
            AuthenticatedEndpoint(),
            CancellationToken.None);

        Assert.Equal("sales@example.com", state.Sender);
        Assert.Contains("250 2.1.0 Sender OK", stream.OutputText);
    }

    [Fact]
    public async Task SmtpSecondMailFromRequiresRsetAndPreservesOriginalTransaction()
    {
        var scenario = await CreateSmtpSenderScenarioAsync();
        using var provider = scenario.Provider;
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = scenario.AuthenticatedState();

        await scenario.Service.HandleMailFromAsync(
            connection, state, "FROM:<alice@example.com> BODY=8BITMIME",
            "127.0.0.1:587", AuthenticatedEndpoint(), CancellationToken.None);
        state.Recipients.Add("bob@example.net");
        await scenario.Service.HandleMailFromAsync(
            connection, state, "FROM:<sales@example.com>",
            "127.0.0.1:587", AuthenticatedEndpoint(), CancellationToken.None);

        Assert.Equal("alice@example.com", state.Sender);
        Assert.Equal("8BITMIME", state.BodyType);
        Assert.Single(state.Recipients);
        Assert.Contains("503 5.5.1 MAIL transaction already in progress", stream.OutputText);
    }

    [Fact]
    public async Task SmtpMailFromRejectsAuthenticatedImpersonationAndEmptySender()
    {
        var scenario = await CreateSmtpSenderScenarioAsync();
        using var provider = scenario.Provider;
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = scenario.AuthenticatedState();

        await scenario.Service.HandleMailFromAsync(
            connection,
            state,
            "FROM:<smtp@example.com>",
            "127.0.0.1:587",
            AuthenticatedEndpoint(),
            CancellationToken.None);

        Assert.Null(state.Sender);
        Assert.Contains("553 5.7.1 Sender address is not authorized for authenticated user", stream.OutputText);

        await scenario.Service.HandleMailFromAsync(
            connection,
            state,
            "FROM:<>",
            "127.0.0.1:587",
            AuthenticatedEndpoint(),
            CancellationToken.None);

        Assert.Null(state.Sender);
        Assert.Contains("553 5.7.1 Sender address is not authorized for authenticated user", stream.OutputText);
    }

    [Fact]
    public async Task SmtpMailFromDoesNotApplyAuthenticatedPolicyToExternalInboundMail()
    {
        var scenario = await CreateSmtpSenderScenarioAsync();
        using var provider = scenario.Provider;
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = new SmtpSessionState { HeloName = "mx.example.net" };

        await scenario.Service.HandleMailFromAsync(
            connection,
            state,
            "FROM:<spoof@example.com>",
            "203.0.113.10:25",
            new SmtpEndpointOptions { Port = 25, Security = ConnectionSecurity.StartTls, RequireAuthentication = false },
            CancellationToken.None);

        Assert.Equal("spoof@example.com", state.Sender);
        Assert.Contains("250 2.1.0 Sender OK", stream.OutputText);
    }

    [Fact]
    public async Task SmtpMailFromAcceptsNullReversePathForInboundBounce()
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = new SmtpSessionState { HeloName = "mx.example.net" };

        await service.HandleMailFromAsync(
            connection,
            state,
            "FROM:<>",
            "203.0.113.10:25",
            new SmtpEndpointOptions { Port = 25, Security = ConnectionSecurity.Plain },
            CancellationToken.None);

        Assert.Equal(string.Empty, state.Sender);
        Assert.Contains("250 2.1.0 Sender OK", stream.OutputText);
    }

    [Theory]
    [InlineData("FROM:<测试@example.com> SMTPUTF8", "555 5.5.4 SMTPUTF8 is not supported")]
    [InlineData("FROM:<测试@example.com>", "501 5.5.4 Invalid MAIL FROM")]
    [InlineData("FROM:<alice@example.com> UNKNOWN=value", "555 5.5.4 Unsupported MAIL FROM parameter")]
    [InlineData("FROM:<alice@example.com> SIZE=52428801", "552 5.3.4 Message size exceeds fixed maximum")]
    public async Task SmtpMailFromRejectsUnsupportedUtf8AndMailParameters(
        string arguments,
        string expectedResponse)
    {
        var service = CreateSmtpService();
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        var state = new SmtpSessionState { HeloName = "client.example.net" };

        await service.HandleMailFromAsync(
            connection,
            state,
            arguments,
            "203.0.113.10:25",
            new SmtpEndpointOptions { Port = 25, Security = ConnectionSecurity.Plain },
            CancellationToken.None);

        Assert.Null(state.Sender);
        Assert.Contains(expectedResponse, stream.OutputText);
    }

    [Fact]
    public async Task SmtpDataRejectsUnauthorizedFromHeaderForAuthenticatedUser()
    {
        var scenario = await CreateSmtpSenderScenarioAsync();
        using var provider = scenario.Provider;
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        await using var message = MessageStream("""
            From: Mallory <smtp@example.com>
            To: Bob <bob@example.net>
            Subject: spoof

            body
            """);

        var accepted = await scenario.Service.ValidateAuthenticatedMessageHeadersAsync(
            connection,
            message,
            scenario.AuthenticatedState(),
            "127.0.0.1:587",
            provider,
            CancellationToken.None);

        Assert.False(accepted);
        Assert.Contains("550 5.7.1 Message sender header is not authorized", stream.OutputText);
    }

    [Fact]
    public async Task SmtpDataAcceptsAuthorizedFromAndSenderHeaders()
    {
        var scenario = await CreateSmtpSenderScenarioAsync();
        using var provider = scenario.Provider;
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        await using var message = MessageStream("""
            From: Sales <sales@example.com>
            Sender: Alice <alice@example.com>
            To: Bob <bob@example.net>
            Subject: ok

            body
            """);
        message.Position = message.Length;

        var accepted = await scenario.Service.ValidateAuthenticatedMessageHeadersAsync(
            connection,
            message,
            scenario.AuthenticatedState(),
            "127.0.0.1:587",
            provider,
            CancellationToken.None);

        Assert.True(accepted);
        Assert.Equal(0, message.Position);
        Assert.DoesNotContain("550 5.7.1", stream.OutputText);
    }

    [Theory]
    [InlineData("To: Bob <bob@example.net>\r\nSubject: missing\r\n\r\nbody\r\n")]
    [InlineData("From: Friends: Alice <alice@example.com>;\r\nTo: Bob <bob@example.net>\r\nSubject: group\r\n\r\nbody\r\n")]
    [InlineData("From: Alice <alice@example.com>, Mallory <smtp@example.com>\r\nTo: Bob <bob@example.net>\r\nSubject: mixed\r\n\r\nbody\r\n")]
    public async Task SmtpDataRejectsMissingNonMailboxOrPartlyUnauthorizedFrom(string rawMessage)
    {
        var scenario = await CreateSmtpSenderScenarioAsync();
        using var provider = scenario.Provider;
        await using var stream = new DuplexMemoryStream(string.Empty);
        await using var connection = new SmtpConnection(stream, TimeSpan.FromSeconds(1), 4096);
        await using var message = MessageStream(rawMessage);

        var accepted = await scenario.Service.ValidateAuthenticatedMessageHeadersAsync(
            connection,
            message,
            scenario.AuthenticatedState(),
            "127.0.0.1:587",
            provider,
            CancellationToken.None);

        Assert.False(accepted);
        Assert.Contains("550 5.7.1 Message sender header is not authorized", stream.OutputText);
    }

    [Fact]
    public void LegacyImplicitTlsConfigurationMapsSubmissionAndImapToStartTls()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Smtp:Endpoints:0:Port"] = "25",
                ["Smtp:Endpoints:0:ImplicitTls"] = "false",
                ["Smtp:Endpoints:1:Port"] = "587",
                ["Smtp:Endpoints:1:ImplicitTls"] = "false",
                ["Imap:Endpoints:0:Port"] = "143",
                ["Imap:Endpoints:0:ImplicitTls"] = "false"
            })
            .Build();

        var smtp = configuration.GetSection("Smtp").Get<SmtpServerOptions>()!;
        var imap = configuration.GetSection("Imap").Get<ImapServerOptions>()!;

        Assert.Equal(ConnectionSecurity.StartTls, smtp.Endpoints[0].Security);
        Assert.Equal(ConnectionSecurity.StartTls, smtp.Endpoints[1].Security);
        Assert.Equal(ConnectionSecurity.StartTls, imap.Endpoints[0].Security);
    }

    [Fact]
    public void SmtpPort25RespectsStartTlsConfiguration()
    {
        var endpoint = new SmtpEndpointOptions
        {
            Port = 25,
            Security = ConnectionSecurity.StartTls
        };

        Assert.Equal(ConnectionSecurity.StartTls, endpoint.EffectiveSecurity);
    }

    [Fact]
    public void DirectMxUsesOnlyServerToServerSmtpPort()
    {
        Assert.Equal(25, DirectMxTransport.DeliveryPort);
        Assert.Equal(SecureSocketOptions.StartTlsWhenAvailable, DirectMxTransport.DeliverySecurity);
    }

    [Fact]
    public void PluginLoaderDiscoversTrustedDllAndRegistersMiddleware()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-plugin-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "plugin.json"),
                JsonSerializer.Serialize(new
                {
                    id = "sample-large-message-policy",
                    version = "1.0.0",
                    contractVersion = PluginContract.Version,
                    entryAssembly = typeof(LoaderTestPlugin).Assembly.Location,
                    hosts = new[] { "smtp" },
                    failureMode = "reject"
                }));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:Path"] = root })
                .Build();
            var services = new ServiceCollection();

            services.AddMailServerPlugins(configuration, "smtp");
            using var provider = services.BuildServiceProvider();

            Assert.Single(provider.GetServices<IMessageAcceptanceMiddleware>());
            Assert.Single(provider.GetRequiredService<PluginCatalog>().Plugins);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PluginLoaderRejectsOlderContractVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-plugin-contract-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "plugin.json"),
                JsonSerializer.Serialize(new
                {
                    id = "old-contract-plugin",
                    version = "1.0.0",
                    contractVersion = "1.0",
                    entryAssembly = typeof(LoaderTestPlugin).Assembly.Location,
                    hosts = new[] { "smtp" }
                }));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:Path"] = root })
                .Build();
            var services = new ServiceCollection();

            var exception = Assert.Throws<InvalidOperationException>(() =>
                services.AddMailServerPlugins(configuration, "smtp"));

            Assert.Contains("requires contract 1.0", exception.Message);
            Assert.Contains("host supports 2.0", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PluginLoaderDoesNotTreatPluginSourceDirectoryAsAnInstallation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-plugin-source-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "SourcePlugin.csproj"), "<Project />");
            File.WriteAllText(
                Path.Combine(root, "plugin.json"),
                JsonSerializer.Serialize(new
                {
                    id = "source-only",
                    version = "1.0.0",
                    contractVersion = "1.0",
                    entryAssembly = "missing.dll",
                    hosts = new[] { "smtp" }
                }));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:Path"] = root })
                .Build();
            var services = new ServiceCollection();

            services.AddMailServerPlugins(configuration, "smtp");
            using var provider = services.BuildServiceProvider();

            var catalog = provider.GetRequiredService<PluginCatalog>();
            Assert.Empty(catalog.Plugins);
            Assert.Equal(root, Assert.Single(catalog.SearchPaths));
            Assert.Equal(root, Assert.Single(catalog.SkippedSourceDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RelativePluginPathIsResolvedBesideTheApplication()
    {
        var resolved = PluginServiceCollectionExtensions.ResolvePluginPath("plugins");

        Assert.Equal(Path.GetFullPath("plugins", AppContext.BaseDirectory), resolved);
    }

    [Theory]
    [InlineData("XOAUTH2", "user=alice@example.com\u0001auth=Bearer token-123\u0001\u0001")]
    [InlineData("OAUTHBEARER", "n,a=alice@example.com,\u0001auth=Bearer token-123\u0001\u0001")]
    public void SmtpBearerPayloadParsesIdentityAndToken(string mechanism, string payload)
    {
        var parsed = SmtpServerService.ParseBearerPayload(mechanism, payload);

        Assert.Equal("alice@example.com", parsed.UserName);
        Assert.Equal("token-123", parsed.Token);
    }

    [Fact]
    public async Task AuthenticationPipelineStopsAfterFirstFailure()
    {
        var failure = new FirstFailureAuthenticationMiddleware();
        var success = new LaterSuccessAuthenticationMiddleware();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationMiddleware>(failure);
        services.AddSingleton<IAuthenticationMiddleware>(success);
        using var provider = services.BuildServiceProvider();
        var pipeline = new PluginPipeline(
            provider,
            new PluginCatalog(),
            new EmptyPluginSettingsProvider(),
            NullLogger<PluginPipeline>.Instance);

        Assert.Equal(["XOAUTH2"], await pipeline.GetAuthenticationMechanismsAsync(MailProtocol.Smtp, CancellationToken.None));
        Assert.Empty(await pipeline.GetAuthenticationMechanismsAsync(MailProtocol.Imap, CancellationToken.None));
        var result = await pipeline.AuthenticateAsync(
            new AuthenticationContext(
                MailProtocol.Smtp,
                "XOAUTH2",
                "alice@example.com",
                "bad-token",
                "127.0.0.1",
                true),
            CancellationToken.None);

        Assert.Equal(AuthenticationStatus.Failure, result.Status);
        Assert.Equal(1, failure.Invocations);
        Assert.Equal(0, success.Invocations);
    }

    [Fact]
    public async Task AuthenticationPipelineContinuesAfterNotHandled()
    {
        var notHandled = new EarlierNotHandledAuthenticationMiddleware();
        var success = new LaterSuccessAuthenticationMiddleware();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationMiddleware>(notHandled);
        services.AddSingleton<IAuthenticationMiddleware>(success);
        using var provider = services.BuildServiceProvider();
        var pipeline = new PluginPipeline(
            provider,
            new PluginCatalog(),
            new EmptyPluginSettingsProvider(),
            NullLogger<PluginPipeline>.Instance);

        var result = await pipeline.AuthenticateAsync(
            new AuthenticationContext(
                MailProtocol.Smtp,
                "XOAUTH2",
                "alice@example.com",
                "token-123",
                "127.0.0.1",
                true),
            CancellationToken.None);

        Assert.Equal(AuthenticationStatus.Success, result.Status);
        Assert.Equal("alice@example.com", result.Identity);
        Assert.Equal(1, notHandled.Invocations);
        Assert.Equal(1, success.Invocations);
    }

    [Fact]
    public async Task SemanticSenderMiddlewareReceivesParsedEnvelopeContext()
    {
        var middleware = new CapturingSenderMiddleware();
        var services = new ServiceCollection();
        services.AddSingleton<ISmtpSenderMiddleware>(middleware);
        using var provider = services.BuildServiceProvider();
        var pipeline = new PluginPipeline(
            provider,
            new PluginCatalog(),
            new EmptyPluginSettingsProvider(),
            NullLogger<PluginPipeline>.Instance);

        var result = await pipeline.OnSmtpSenderAsync(
            new SmtpSenderContext(
                "sender@example.com",
                "203.0.113.10:25",
                "alice@example.com",
                Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(PluginDecisionKind.Reject, result.Kind);
        Assert.Equal("sender@example.com", middleware.Context?.Sender);
        Assert.Equal("alice@example.com", middleware.Context?.AuthenticatedUser);
    }

    private static async Task<ImapInboxScenario> CreateImapInboxScenarioAsync(string root)
    {
        var factory = CreateFactory();
        Guid accountId;
        Guid mailboxId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            var account = new MailAccount
            {
                Domain = domain,
                LocalPart = "alice",
                PasswordHash = "unused"
            };
            var mailbox = new Mailbox { Account = account, Name = "INBOX" };
            db.AddRange(domain, account, mailbox);
            await db.SaveChangesAsync();
            accountId = account.Id;
            mailboxId = mailbox.Id;
        }

        var settings = new SystemSettingsService(
            factory,
            Options.Create(new DeliveryOptions()),
            Options.Create(new MailboxDefaultsOptions()),
            Options.Create(new CertificatesOptions()));
        var store = new FileMessageContentStore(
            Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 16 * 1024 * 1024 }),
            factory);
        await AddInboxMessageAsync(
            factory,
            store,
            mailboxId,
            1,
            """
            From: Sender <sender@example.net>
            To: Alice <alice@example.com>
            Subject: hello
            Date: Fri, 26 Jun 2026 23:36:15 +0800
            Message-ID: <m1@example.net>

            hello body
            """.ReplaceLineEndings("\r\n"));
        var services = new ServiceCollection();
        services.AddSingleton<IMailboxService>(new MailboxService(factory, store, settings));
        var provider = services.BuildServiceProvider();
        var service = new ImapServerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ImapServerOptions { IdlePollSeconds = 1 }),
            null!,
            NullLogger<ImapServerService>.Instance);
        var state = new ImapSessionState
        {
            AccountId = accountId,
            UserName = "alice@example.com",
            MailboxId = mailboxId,
            MailboxName = "INBOX",
            Exists = 1,
            UidNext = 2,
            HighestModSeq = 2
        };
        return new ImapInboxScenario(factory, store, provider, service, state, mailboxId);
    }

    private static async Task AddInboxMessageAsync(
        IDbContextFactory<MailDbContext> factory,
        IMessageContentStore store,
        Guid mailboxId,
        long uid,
        string rawMessage,
        string flags = "",
        DateTimeOffset? internalDate = null)
    {
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes(rawMessage));
        var descriptor = await store.SaveAsync(content, CancellationToken.None);
        await using var db = await factory.CreateDbContextAsync();
        var mailbox = await db.Mailboxes.SingleAsync(x => x.Id == mailboxId);
        var stored = new StoredMessage
        {
            ContentKey = descriptor.Key,
            EnvelopeFrom = "sender@example.net",
            InternetMessageId = $"m{uid}@example.net",
            Size = descriptor.Size
        };
        db.Messages.Add(stored);
        db.MailboxMessages.Add(new MailboxMessage
        {
            Mailbox = mailbox,
            Message = stored,
            Uid = uid,
            ModSeq = uid + 1,
            Flags = flags,
            InternalDate = internalDate ?? DateTimeOffset.UtcNow
        });
        mailbox.UidNext = Math.Max(mailbox.UidNext, uid + 1);
        mailbox.HighestModSeq = Math.Max(mailbox.HighestModSeq, uid + 1);
        await db.SaveChangesAsync();
    }

    private static SmtpServerService CreateSmtpService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccountAuthenticator>(
            new FakeAccountAuthenticator("alice@example.com", "secret"));
        var provider = services.BuildServiceProvider();
        return new SmtpServerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SmtpServerOptions()),
            null!,
            NullLogger<SmtpServerService>.Instance);
    }

    private static async Task<SmtpSenderScenario> CreateSmtpSenderScenarioAsync()
    {
        var factory = CreateFactory();
        Guid accountId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com" };
            var account = new MailAccount
            {
                Domain = domain,
                LocalPart = "alice",
                PasswordHash = "unused"
            };
            var smtpAccount = new MailAccount
            {
                Domain = domain,
                LocalPart = "smtp",
                PasswordHash = "unused"
            };
            db.AddRange(
                domain,
                account,
                smtpAccount,
                new MailAlias
                {
                    Domain = domain,
                    SourceLocalPart = "sales",
                    TargetAccount = account,
                    DestinationAddress = "alice@example.com",
                    IsEnabled = true
                },
                new MailAlias
                {
                    Domain = domain,
                    SourceLocalPart = "support",
                    TargetAccount = smtpAccount,
                    DestinationAddress = "smtp@example.com",
                    IsEnabled = true
                });
            await db.SaveChangesAsync();
            accountId = account.Id;
        }

        var services = new ServiceCollection();
        services.AddSingleton<ISmtpSenderAuthorizer>(new SmtpSenderAuthorizer(factory));
        var provider = services.BuildServiceProvider();
        var service = new SmtpServerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SmtpServerOptions()),
            null!,
            NullLogger<SmtpServerService>.Instance);
        return new SmtpSenderScenario(provider, service, accountId);
    }

    private static SmtpEndpointOptions AuthenticatedEndpoint() =>
        new() { Port = 587, Security = ConnectionSecurity.StartTls, RequireAuthentication = true };

    private static MemoryStream MessageStream(string rawMessage) =>
        new(Encoding.UTF8.GetBytes(rawMessage.ReplaceLineEndings("\r\n")));

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static IDbContextFactory<MailDbContext> CreateFactory()
    {
        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseInMemoryDatabase($"mailserver-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new PooledDbContextFactory<MailDbContext>(options);
    }

    private sealed record ImapInboxScenario(
        IDbContextFactory<MailDbContext> Factory,
        IMessageContentStore Store,
        ServiceProvider Provider,
        ImapServerService Service,
        ImapSessionState State,
        Guid MailboxId) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private sealed record SmtpSenderScenario(
        ServiceProvider Provider,
        SmtpServerService Service,
        Guid AccountId)
    {
        public SmtpSessionState AuthenticatedState() =>
            new()
            {
                HeloName = "client.example.net",
                AuthenticatedAccountId = AccountId,
                AuthenticatedUser = "alice@example.com"
            };
    }

    private sealed class FakeAccountAuthenticator(string userName, string password) : IAccountAuthenticator
    {
        public Task<Guid?> AuthenticateMailboxAsync(
            string address,
            string candidatePassword,
            CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(
                address.Equals(userName, StringComparison.OrdinalIgnoreCase)
                && candidatePassword == password
                    ? Guid.NewGuid()
                    : null);

        public Task<Guid?> ResolveMailboxIdentityAsync(
            string address,
            CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(
                address.Equals(userName, StringComparison.OrdinalIgnoreCase)
                    ? Guid.NewGuid()
                    : null);
    }

    private sealed class EmptyPluginSettingsProvider : IPluginSettingsProvider
    {
        public Task<PluginSettingsSnapshot> GetAsync(string pluginId, CancellationToken cancellationToken) =>
            Task.FromResult(new PluginSettingsSnapshot(true, new Dictionary<string, JsonElement>()));
    }

    public sealed class LoaderTestPlugin : IMailServerPlugin
    {
        public void ConfigureServices(IServiceCollection services) =>
            services.AddSingleton<IMessageAcceptanceMiddleware, LoaderTestMiddleware>();
    }

    private sealed class LoaderTestMiddleware : IMessageAcceptanceMiddleware
    {
        public Task<PluginDecision> InvokeAsync(MessageAcceptanceContext context, CancellationToken cancellationToken) =>
            Task.FromResult(PluginDecision.Continue());
    }

    private sealed class FirstFailureAuthenticationMiddleware : IAuthenticationMiddleware
    {
        public IReadOnlyCollection<string> GetMechanisms(MailProtocol protocol) =>
            protocol == MailProtocol.Smtp ? ["XOAUTH2"] : [];
        public int Invocations { get; private set; }

        public Task<AuthenticationResult> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(AuthenticationResult.Failure("invalid"));
        }
    }

    private sealed class EarlierNotHandledAuthenticationMiddleware : IAuthenticationMiddleware
    {
        public IReadOnlyCollection<string> GetMechanisms(MailProtocol protocol) =>
            protocol == MailProtocol.Smtp ? ["XOAUTH2"] : [];
        public int Invocations { get; private set; }

        public Task<AuthenticationResult> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(AuthenticationResult.NotHandled());
        }
    }

    private sealed class LaterSuccessAuthenticationMiddleware : IAuthenticationMiddleware
    {
        public IReadOnlyCollection<string> GetMechanisms(MailProtocol protocol) =>
            protocol == MailProtocol.Smtp ? ["XOAUTH2"] : [];
        public int Invocations { get; private set; }

        public Task<AuthenticationResult> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(AuthenticationResult.Success("alice@example.com"));
        }
    }

    private sealed class CapturingSenderMiddleware : ISmtpSenderMiddleware
    {
        public SmtpSenderContext? Context { get; private set; }

        public Task<PluginDecision> InvokeAsync(SmtpSenderContext context, CancellationToken cancellationToken)
        {
            Context = context;
            return Task.FromResult(new PluginDecision(PluginDecisionKind.Reject, "blocked"));
        }
    }

    private sealed class DuplexMemoryStream : Stream
    {
        private readonly MemoryStream _input;
        private readonly MemoryStream _output = new();

        public DuplexMemoryStream(string input)
        {
            _input = new MemoryStream(Encoding.UTF8.GetBytes(input));
        }

        public string OutputText => Encoding.UTF8.GetString(_output.ToArray());
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _output.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
