using System.Text;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using MailServer.Domain;
using MailServer.Infrastructure;
using DnsClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailServer.PluginSdk;
using MailServer.PluginSdk.Auditing;
using MailServer.PluginSdk.Delivery;
using MailServer.PluginSdk.Imap;
using MailServer.PluginSdk.Security;
using MailServer.PluginSdk.Smtp;
using Xunit;

namespace MailServer.Tests;

public sealed class CoreTests
{
    [Fact]
    public void MtaStsPolicyParserEnforcesRequiredFieldsAndMxMatching()
    {
        var now = DateTimeOffset.UtcNow;
        var policy = TransportSecurityPolicyService.ParsePolicy(
            "example.com",
            "20260709",
            "version: STSv1\r\nmode: enforce\r\nmx: mail.example.com\r\nmx: *.backup.example.com\r\nmax_age: 604800\r\n",
            now);

        Assert.Equal("enforce", policy.Mode);
        Assert.True(policy.MatchesMx("mail.example.com"));
        Assert.True(policy.MatchesMx("mx1.backup.example.com"));
        Assert.False(policy.MatchesMx("backup.example.com"));
        Assert.False(policy.MatchesMx("evil-example.com"));
        Assert.Equal(now.AddDays(7), policy.ExpiresAt);
        Assert.Throws<FormatException>(() =>
            TransportSecurityPolicyService.ParsePolicy(
                "example.com", "1", "version: STSv1\nmode: enforce\nmax_age: 60\n", now));
    }

    [Fact]
    public void DaneCertificateValidationSupportsDaneEeSpkiSha256()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=mx.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var association = new TlsaAssociation(
            Usage: 3,
            Selector: 1,
            MatchingType: 1,
            SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        var policy = new DanePolicy("mx.example.com", [association]);

        Assert.True(TransportSecurityPolicyService.ValidateDaneCertificate(
            certificate,
            null,
            SslPolicyErrors.RemoteCertificateChainErrors,
            policy));

        var wrong = new DanePolicy("mx.example.com",
            [association with { Data = SHA256.HashData("wrong"u8.ToArray()) }]);
        Assert.False(TransportSecurityPolicyService.ValidateDaneCertificate(
            certificate,
            null,
            SslPolicyErrors.None,
            wrong));
    }

    [Fact]
    public void DomainDnsRecordsIncludeMtaStsTlsRptAndDane()
    {
        var domain = new MailDomain { Name = "example.com", MailHostName = "mx.example.com" };
        var settings = new DomainDnsSettings
        {
            MtaStsMode = "enforce",
            MtaStsPolicyId = "20260709",
            MtaStsMxPatterns = "mx.example.com",
            TlsReportUris = "mailto:tls@example.com",
            DaneEnabled = true,
            DaneTlsaRecords = "3 1 1 AABB"
        };

        var records = DomainDnsService.GenerateRecords(domain, settings);

        Assert.Contains(records, x => x.Name == "_mta-sts.example.com" && x.Value.Contains("id=20260709"));
        Assert.Contains(records, x => x.Name == "_smtp._tls.example.com" && x.Value.Contains("TLSRPTv1"));
        Assert.Contains(records, x => x.Name == "_25._tcp.mx.example.com" && x.Type == "TLSA");
    }

    [Fact]
    public void TlsReportUsesRfc8460FieldNamesAndAggregatesResults()
    {
        var service = new TlsReportBackgroundService(
            null!,
            null!,
            null!,
            Options.Create(new TransportSecurityOptions
            {
                ReportOrganizationName = "Example Sender",
                ReportContactInfo = "tls@example.com"
            }),
            NullLogger<TlsReportBackgroundService>.Instance);
        var start = new DateTimeOffset(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            new TlsDeliveryEvent
            {
                PolicyDomain = "recipient.test", MxHost = "mx.recipient.test",
                PolicyType = "sts", PolicyString = "version: STSv1",
                ResultType = "success"
            },
            new TlsDeliveryEvent
            {
                PolicyDomain = "recipient.test", MxHost = "mx.recipient.test",
                PolicyType = "sts", PolicyString = "version: STSv1",
                ResultType = "starttls-not-supported", FailureReason = "STARTTLS missing"
            }
        };

        var json = service.BuildReport("recipient.test", start, start.AddDays(1), events);

        Assert.Contains("\"organization-name\":\"Example Sender\"", json);
        Assert.Contains("\"total-successful-session-count\":1", json);
        Assert.Contains("\"total-failure-session-count\":1", json);
        Assert.Contains("\"result-type\":\"starttls-not-supported\"", json);
    }

    [Fact]
    public async Task PluginSettingsValidatePersistAndProtectSecrets()
    {
        var factory = CreateFactory();
        var catalog = new PluginCatalog();
        catalog.Add(new LoadedPlugin(
            new PluginManifest
            {
                Id = "configurable",
                Version = "1",
                ContractVersion = PluginContract.Version,
                EntryAssembly = "unused.dll",
                DisplayName = "Configurable",
                Settings =
                [
                    new PluginSettingDefinition
                    {
                        Key = "limit", Label = "Limit", Type = PluginSettingType.Integer,
                        Minimum = 1, Maximum = 10, DefaultValue = JsonSerializer.SerializeToElement(5)
                    },
                    new PluginSettingDefinition
                    {
                        Key = "password", Label = "Password", Type = PluginSettingType.Password
                    }
                ]
            },
            null,
            false));
        var service = new PluginSettingsService(factory, catalog, new EphemeralDataProtectionProvider());
        var values = new Dictionary<string, JsonElement>
        {
            ["limit"] = JsonSerializer.SerializeToElement(7),
            ["password"] = JsonSerializer.SerializeToElement("super-secret")
        };

        await service.SaveAsync("configurable", false, values, new HashSet<string>(), CancellationToken.None);
        var runtime = await service.GetAsync("configurable", CancellationToken.None);
        var view = await service.GetConfigurationAsync("configurable", CancellationToken.None);

        Assert.False(runtime.Enabled);
        Assert.Equal(7, runtime.GetInteger("limit"));
        Assert.Equal("super-secret", runtime.GetString("password"));
        Assert.True(view.Values.Single(x => x.Key == "password").HasSecret);
        Assert.Null(view.Values.Single(x => x.Key == "password").Value);
        await using var db = await factory.CreateDbContextAsync();
        Assert.DoesNotContain("super-secret", (await db.SystemSettings.SingleAsync()).Value);
        await Assert.ThrowsAsync<FormatException>(() =>
            service.SaveAsync(
                "configurable",
                true,
                new Dictionary<string, JsonElement> { ["limit"] = JsonSerializer.SerializeToElement(99) },
                new HashSet<string>(),
                CancellationToken.None));
    }

    [Fact]
    public async Task PluginSettingsPersistManagementOrder()
    {
        var factory = CreateFactory();
        var catalog = new PluginCatalog();
        catalog.Add(new LoadedPlugin(
            new PluginManifest
            {
                Id = "second",
                Version = "1",
                ContractVersion = PluginContract.Version,
                EntryAssembly = "unused.dll",
                Order = 20
            },
            null,
            false));
        catalog.Add(new LoadedPlugin(
            new PluginManifest
            {
                Id = "first",
                Version = "1",
                ContractVersion = PluginContract.Version,
                EntryAssembly = "unused.dll",
                Order = 10
            },
            null,
            false));
        var service = new PluginSettingsService(factory, catalog, new EphemeralDataProtectionProvider());

        Assert.Equal(["first", "second"], (await service.ListAsync(CancellationToken.None)).Select(x => x.Id));

        await service.SaveOrderAsync(["second", "first"], CancellationToken.None);
        var reordered = await service.ListAsync(CancellationToken.None);

        Assert.Equal(["second", "first"], reordered.Select(x => x.Id));
        Assert.Equal([10, 20], reordered.Select(x => x.EffectiveOrder));
    }

    [Fact]
    public async Task PluginSettingsExposeSystemPluginClassification()
    {
        var factory = CreateFactory();
        var catalog = new PluginCatalog();
        catalog.Add(new LoadedPlugin(
            new PluginManifest
            {
                Id = "system-policy",
                System = true,
                Version = "1",
                ContractVersion = PluginContract.Version,
                EntryAssembly = "unused.dll"
            },
            null,
            false));
        catalog.Add(new LoadedPlugin(
            new PluginManifest
            {
                Id = "third-party-policy",
                Version = "1",
                ContractVersion = PluginContract.Version,
                EntryAssembly = "unused.dll"
            },
            null,
            false));
        var service = new PluginSettingsService(factory, catalog, new EphemeralDataProtectionProvider());

        var plugins = await service.ListAsync(CancellationToken.None);

        Assert.True(plugins.Single(x => x.Id == "system-policy").System);
        Assert.False(plugins.Single(x => x.Id == "third-party-policy").System);
    }

    [Fact]
    public async Task PluginHostServicesSendMailQueryDirectoryAndEditMessages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 64 * 1024 }),
                factory);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.test" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused"
                };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "INBOX" });
                await db.SaveChangesAsync();
            }

            var sender = new PluginMailSender(new InboundMessageService(
                factory,
                store,
                NullLogger<InboundMessageService>.Instance));
            var raw = new MemoryStream(Encoding.UTF8.GetBytes(
                "From: plugin@example.test\r\nTo: alice@example.test, external@remote.test\r\nSubject: SDK\r\nMessage-ID: <sdk@example.test>\r\n\r\nhello"));

            var result = await sender.SendAsync(
                new PluginMailSendRequest(
                    "plugin@example.test",
                    ["alice@example.test", "external@remote.test"],
                    raw),
                CancellationToken.None);

            var directory = new PluginDirectory(factory);
            var domainInfo = await directory.FindDomainAsync("EXAMPLE.TEST", CancellationToken.None);
            var accountInfo = await directory.FindAccountAsync("Alice@Example.Test", CancellationToken.None);
            var localRecipient = await directory.ResolveRecipientAsync("alice@example.test", CancellationToken.None);
            var remoteRecipient = await directory.ResolveRecipientAsync("external@remote.test", CancellationToken.None);
            var messages = new PluginMessageStore(factory, store, NullLogger<PluginMessageStore>.Instance);

            Assert.Equal(1, result.LocalDeliveries);
            Assert.Equal(1, result.QueuedDeliveries);
            Assert.Equal("example.test", domainInfo?.Name);
            Assert.Equal("alice@example.test", accountInfo?.Address);
            Assert.True(localRecipient.IsLocal);
            Assert.False(remoteRecipient.IsLocal);

            var info = await messages.GetAsync(result.MessageId, CancellationToken.None);
            var headers = await messages.ReadHeadersAsync(result.MessageId, CancellationToken.None);
            Assert.Equal("sdk@example.test", info?.InternetMessageId);
            Assert.Contains(headers, x => x.Name == "Subject" && x.Value == "SDK");

            await messages.UpsertHeadersAsync(
                result.MessageId,
                [new PluginMessageHeaderUpdate("X-Plugin", "yes")],
                CancellationToken.None);
            var editedHeaders = await messages.ReadHeadersAsync(result.MessageId, CancellationToken.None);
            Assert.Contains(editedHeaders, x => x.Name == "X-Plugin" && x.Value == "yes");

            await using var verification = await factory.CreateDbContextAsync();
            Assert.Single(verification.MailboxMessages);
            Assert.Single(verification.DeliveryJobs);
            Assert.Equal(result.MessageId, await verification.Messages.Select(x => x.Id).SingleAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PluginHostServicesControlDeliveryAuditAndState()
    {
        var factory = CreateFactory();
        Guid jobId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var message = new StoredMessage
            {
                ContentKey = new string('a', 64),
                EnvelopeFrom = "sender@example.test",
                Size = 12
            };
            var job = new DeliveryJob
            {
                Message = message,
                Sender = "sender@example.test",
                Recipient = "rcpt@example.net"
            };
            db.DeliveryJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.Id;
        }

        var settings = CreateSystemSettingsService(factory);
        var queue = new PluginDeliveryQueue(
            factory,
            new DeliveryQueue(
                factory,
                settings,
                Options.Create(new DatabaseOptions { Provider = DatabaseProvider.Sqlite }),
                NullLogger<DeliveryQueue>.Instance));
        var audit = new PluginAuditWriter(factory);
        var state = new PluginStateStore(factory);

        await queue.DeferAsync(jobId, TimeSpan.FromMinutes(10), "policy wait", CancellationToken.None);
        var deferred = await queue.GetAsync(jobId, CancellationToken.None);
        Assert.Equal("Deferred", deferred?.Status);
        Assert.Equal("policy wait", deferred?.LastError);

        await queue.AddNoteAsync(jobId, "checked by plugin", CancellationToken.None);
        await queue.FailPermanentAsync(jobId, "blocked by plugin", CancellationToken.None);
        var failed = await queue.GetAsync(jobId, CancellationToken.None);
        Assert.Equal("Failed", failed?.Status);

        await audit.WriteAsync(
            "plugin:test",
            "plugin.test",
            $"delivery:{jobId}",
            "done",
            "127.0.0.1",
            CancellationToken.None);
        await state.SetAsync("test.plugin", "cursor", JsonSerializer.SerializeToElement(new { Last = 42 }), CancellationToken.None);
        var saved = await state.GetAsync("test.plugin", "cursor", CancellationToken.None);

        Assert.Equal(42, saved?.GetProperty("Last").GetInt32());
        await state.DeleteAsync("test.plugin", "cursor", CancellationToken.None);
        Assert.Null(await state.GetAsync("test.plugin", "cursor", CancellationToken.None));

        await using var verification = await factory.CreateDbContextAsync();
        Assert.Contains(await verification.AuditRecords.ToListAsync(), x => x.Action == "plugin.delivery.defer");
        Assert.Contains(await verification.AuditRecords.ToListAsync(), x => x.Action == "plugin.delivery.note");
        Assert.Contains(await verification.AuditRecords.ToListAsync(), x => x.Action == "plugin.test");
        Assert.Single(verification.DeliveryAttempts);
    }

    [Fact]
    public async Task PluginMailboxStoreManagesFoldersMessagesAndFlags()
    {
        var factory = CreateFactory();
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-plugin-mailbox-{Guid.NewGuid():N}");
        try
        {
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var settings = CreateSystemSettingsService(factory);
            Guid accountId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.test", QuotaBytes = 4096, UsedBytes = 12 };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused",
                    QuotaBytes = 4096,
                    UsedBytes = 12
                };
                var inbox = new Mailbox { Account = account, Name = "INBOX" };
                var trash = new Mailbox { Account = account, Name = "Trash" };
                var message = new StoredMessage
                {
                    ContentKey = new string('a', 64),
                    EnvelopeFrom = "sender@example.net",
                    InternetMessageId = "plugin-mailbox@example.test",
                    Size = 12
                };
                db.MailboxMessages.Add(new MailboxMessage
                {
                    Mailbox = inbox,
                    Message = message,
                    Uid = 1,
                    Flags = "\\Seen"
                });
                db.Mailboxes.Add(trash);
                await db.SaveChangesAsync();
                accountId = account.Id;
            }

            var service = new PluginMailboxStore(factory, new MailboxService(factory, store, settings));

            var initialFolders = await service.ListMailboxesAsync("alice@example.test", CancellationToken.None);
            Assert.Contains(initialFolders, x => x.Name == "INBOX");

            var archive = await service.CreateMailboxAsync(accountId, "Archive", CancellationToken.None);
            Assert.Equal("Archive", archive.Name);
            var inboxMessages = await service.ListMessagesAsync(accountId, "INBOX", CancellationToken.None);
            var selector = new PluginMailboxMessageSelector(Uids: [inboxMessages.Single().Uid]);

            await service.AddFlagsAsync(accountId, "INBOX", selector, ["$Junk"], CancellationToken.None);
            var junked = (await service.ListMessagesAsync(accountId, "INBOX", CancellationToken.None)).Single();
            Assert.Contains("\\Seen", junked.Flags);
            Assert.Contains("$Junk", junked.Flags);

            await service.MarkUnreadAsync(accountId, "INBOX", selector, CancellationToken.None);
            var unread = (await service.ListMessagesAsync(accountId, "INBOX", CancellationToken.None)).Single();
            Assert.DoesNotContain("\\Seen", unread.Flags);
            Assert.Contains("$Junk", unread.Flags);

            await service.SetFlagsAsync(accountId, "INBOX", selector, ["\\Flagged"], CancellationToken.None);
            var flagged = (await service.ListMessagesAsync(accountId, "INBOX", CancellationToken.None)).Single();
            Assert.Equal(["\\Flagged"], flagged.Flags);

            var copy = await service.CopyAsync(accountId, "INBOX", selector, "Archive", CancellationToken.None);
            Assert.Equal([1], copy.SourceUids);
            Assert.Single(copy.TargetUids);
            Assert.Single(await service.ListMessagesAsync(accountId, "INBOX", CancellationToken.None));

            var archiveCopy = (await service.ListMessagesAsync(accountId, "Archive", CancellationToken.None)).Single();
            var move = await service.MoveAsync(
                accountId,
                "Archive",
                new PluginMailboxMessageSelector(MailboxMessageIds: [archiveCopy.MailboxMessageId]),
                "Trash",
                CancellationToken.None);
            Assert.Equal([archiveCopy.Uid], move.SourceUids);
            Assert.Empty(await service.ListMessagesAsync(accountId, "Archive", CancellationToken.None));
            Assert.Single(await service.ListMessagesAsync(accountId, "Trash", CancellationToken.None));

            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(24, (await verification.Accounts.SingleAsync(x => x.Id == accountId)).UsedBytes);
            Assert.Single(await verification.CleanupQueue.ToListAsync());
            var actions = await verification.AuditRecords.Select(x => x.Action).ToListAsync();
            Assert.Contains("plugin.mailbox.folder.create", actions);
            Assert.Contains("plugin.mailbox.flags", actions);
            Assert.Contains("plugin.mailbox.copy", actions);
            Assert.Contains("plugin.mailbox.move", actions);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PasswordHashesAreVersionedAndVerifiable()
    {
        var service = new CredentialService();
        var hash = service.HashPassword("a strong test password");

        Assert.True(service.VerifyPassword(hash, "a strong test password", out _));
        Assert.False(service.VerifyPassword(hash, "wrong password", out _));
    }

    [Theory]
    [InlineData("20.78.129.0/24", "20.78.129.119", true)]
    [InlineData("20.78.129.0/24", "20.78.130.1", false)]
    [InlineData("2001:db8::/32", "2001:db8::10", true)]
    [InlineData("2001:db8::/32", "2001:db9::10", false)]
    public void FirewallRulesMatchIpAndCidr(string cidr, string address, bool expected)
    {
        Assert.Equal(expected, IpFirewallRules.Matches(cidr, IPAddress.Parse(address)));
    }

    [Fact]
    public async Task DomainDetailsPersistSmtpNameAndOutboundIpAddresses()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var administration = CreateAdministration(factory, settings, new CredentialService(), store);
            Guid domainId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com", MailHostName = "mail.example.com" };
                db.Domains.Add(domain);
                await db.SaveChangesAsync();
                domainId = domain.Id;
            }

            await administration.UpdateDomainDetailsAsync(
                domainId,
                "mail.example.com",
                true,
                "mx.example.com",
                "203.0.113.10, 203.0.113.10 2001:db8::10",
                false,
                "test",
                CancellationToken.None);

            await using var verification = await factory.CreateDbContextAsync();
            var saved = await verification.Domains.SingleAsync(x => x.Id == domainId);
            Assert.Equal("mx.example.com", saved.SmtpHostName);
            Assert.Equal($"203.0.113.10{Environment.NewLine}2001:db8::10", saved.OutboundIpAddresses);
            Assert.False(saved.DirectDeliverSameDomain);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PermissionServiceFiltersDomainScope()
    {
        var factory = CreateFactory();
        Guid adminId;
        Guid allowedDomainId;
        Guid blockedDomainId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var allowed = new MailDomain { Name = "allowed.test", MailHostName = "mail.allowed.test" };
            var blocked = new MailDomain { Name = "blocked.test", MailHostName = "mail.blocked.test" };
            var root = new AdminUser
            {
                UserName = "root",
                PasswordHash = "unused",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            var admin = new AdminUser { UserName = "limited", PasswordHash = "unused" };
            db.AddRange(allowed, blocked, root, admin);
            await db.SaveChangesAsync();
            allowedDomainId = allowed.Id;
            blockedDomainId = blocked.Id;
            adminId = admin.Id;
        }

        var permissions = new PermissionService(factory);
        await permissions.ReplaceGrantsAsync(
            PermissionSubjectType.AdminUser,
            adminId,
            [
                new PermissionGrantInput(
                    PermissionCodes.DomainRead,
                    PermissionService.DomainResourceType,
                    allowedDomainId.ToString())
            ],
            "test",
            CancellationToken.None);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, adminId.ToString())],
            "test"));
        var scope = await permissions.GetPermittedDomainScopeAsync(
            principal,
            PermissionCodes.DomainRead,
            CancellationToken.None);

        Assert.False(scope.AllDomains);
        Assert.Contains(allowedDomainId, scope.DomainIds);
        Assert.DoesNotContain(blockedDomainId, scope.DomainIds);
    }

    [Fact]
    public async Task PermissionServiceManageGrantIncludesReadScope()
    {
        var factory = CreateFactory();
        Guid adminId;
        Guid domainId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "managed.test", MailHostName = "mail.managed.test" };
            var root = new AdminUser
            {
                UserName = "root",
                PasswordHash = "unused",
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            var admin = new AdminUser { UserName = "operator", PasswordHash = "unused" };
            db.AddRange(domain, root, admin);
            await db.SaveChangesAsync();
            domainId = domain.Id;
            adminId = admin.Id;
        }

        var permissions = new PermissionService(factory);
        await permissions.ReplaceGrantsAsync(
            PermissionSubjectType.AdminUser,
            adminId,
            [
                new PermissionGrantInput(
                    PermissionCodes.DomainManage,
                    PermissionService.DomainResourceType,
                    domainId.ToString())
            ],
            "test",
            CancellationToken.None);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, adminId.ToString())],
            "test"));
        Assert.True(await permissions.HasPermissionAsync(
            principal,
            PermissionCodes.DomainRead,
            domainId,
            CancellationToken.None));
    }

    [Fact]
    public void PermissionServiceRejectsDeprecatedFineGrainedPermissionCodes()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            PermissionService.NormalizeGrantInputs(
            [
                new PermissionGrantInput(
                    "domain.detail",
                    PermissionService.DomainResourceType,
                    Guid.NewGuid().ToString())
            ]));

        Assert.Contains("Unknown permission", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApiKeyCreationRequiresGrants()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var administration = CreateAdministration(factory, settings, new CredentialService(), store);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                administration.CreateApiKeyAsync("no-grants", [], null, "test", CancellationToken.None));

            var created = await administration.CreateApiKeyAsync(
                "full",
                [PermissionService.FullAccessGrant()],
                null,
                "test",
                CancellationToken.None);

            await using var db = await factory.CreateDbContextAsync();
            Assert.True(await db.PermissionGrants.AnyAsync(
                x => x.SubjectType == PermissionSubjectType.ApiKey
                     && x.SubjectId == created.Id
                     && x.PermissionCode == PermissionCodes.FullAccess));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitialAdministratorAlwaysHasFullAccessAndCannotBeReauthorized()
    {
        var factory = CreateFactory();
        Guid adminId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var admin = new AdminUser { UserName = "owner", PasswordHash = "unused", IsEnabled = true };
            db.AdminUsers.Add(admin);
            await db.SaveChangesAsync();
            adminId = admin.Id;
        }

        var permissions = new PermissionService(factory);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, adminId.ToString())],
            "test"));
        var snapshot = await permissions.GetSnapshotAsync(principal, CancellationToken.None);
        Assert.True(snapshot.FullAccess);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            permissions.ReplaceGrantsAsync(
                PermissionSubjectType.AdminUser,
                adminId,
                [new PermissionGrantInput(PermissionCodes.DashboardRead)],
                "test",
                CancellationToken.None));
        Assert.Contains("initial administrator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InitialAdministratorIdentityAndStatusAreProtected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var administration = CreateAdministration(factory, settings, new CredentialService(), store);

            var founder = await administration.CreateAdminUserAsync(
                "founder",
                "a strong founder password",
                false,
                [new PermissionGrantInput(PermissionCodes.DashboardRead)],
                "test",
                CancellationToken.None);

            Assert.True(founder.IsPrimary);
            Assert.True(founder.IsEnabled);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.True(await db.PermissionGrants.AnyAsync(x =>
                    x.SubjectType == PermissionSubjectType.AdminUser
                    && x.SubjectId == founder.Id
                    && x.PermissionCode == PermissionCodes.FullAccess));
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                administration.UpdateAdminUserAsync(
                    founder.Id,
                    "renamed",
                    true,
                    null,
                    "test",
                    CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                administration.UpdateAdminUserAsync(
                    founder.Id,
                    founder.UserName,
                    false,
                    null,
                    "test",
                    CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                administration.DeleteAdminUserAsync(founder.Id, "test", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeliveryQueueRawMessageReturnsStoredMime()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var administration = CreateAdministration(factory, settings, new CredentialService(), store);
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes(
                "From: alice@example.com\r\nTo: bob@example.net\r\nSubject: raw\r\n\r\nhello raw"));
            var descriptor = await store.SaveAsync(content, CancellationToken.None);
            Guid jobId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var message = new StoredMessage
                {
                    ContentKey = descriptor.Key,
                    Size = descriptor.Size,
                    EnvelopeFrom = "alice@example.com"
                };
                var job = new DeliveryJob
                {
                    Message = message,
                    Sender = "alice@example.com",
                    Recipient = "bob@example.net"
                };
                db.AddRange(message, job);
                await db.SaveChangesAsync();
                jobId = job.Id;
            }

            var raw = await administration.GetDeliveryRawMessageAsync(jobId, CancellationToken.None);

            Assert.Equal(jobId, raw.DeliveryJobId);
            Assert.EndsWith(".eml", raw.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Subject: raw", raw.Content);
            Assert.Contains("hello raw", raw.Content);

            var headers = await administration.GetDeliveryMessageHeadersAsync(jobId, CancellationToken.None);
            Assert.Equal(jobId, headers.DeliveryJobId);
            Assert.EndsWith("-headers.txt", headers.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Subject: raw", headers.Content);
            Assert.DoesNotContain("hello raw", headers.Content);

            var download = await administration.OpenDeliveryMessageDownloadAsync(jobId, CancellationToken.None);
            await using (download.Content)
            using (var reader = new StreamReader(download.Content, Encoding.UTF8))
            {
                var downloadedContent = await reader.ReadToEndAsync();
                Assert.Contains("Subject: raw", downloadedContent);
                Assert.Contains("hello raw", downloadedContent);
            }

            await administration.DeleteDeliveryAsync(jobId, "test", CancellationToken.None);
            await using (var verification = await factory.CreateDbContextAsync())
            {
                Assert.False(await verification.DeliveryJobs.AnyAsync(x => x.Id == jobId));
                Assert.False(await verification.Messages.AnyAsync(x => x.ContentKey == descriptor.Key));
            }

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                store.OpenReadAsync(descriptor.Key, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DomainDeliveryQueueIsFilteredAndRejectsCrossDomainOperations()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var administration = CreateAdministration(factory, settings, new CredentialService(), store);
            await using var firstContent = new MemoryStream(Encoding.UTF8.GetBytes(
                "From: alice@first.test\r\nTo: user@example.net\r\nSubject: first\r\n\r\nfirst"));
            await using var secondContent = new MemoryStream(Encoding.UTF8.GetBytes(
                "From: bob@second.test\r\nTo: user@example.org\r\nSubject: second\r\n\r\nsecond"));
            var firstDescriptor = await store.SaveAsync(firstContent, CancellationToken.None);
            var secondDescriptor = await store.SaveAsync(secondContent, CancellationToken.None);
            Guid firstDomainId;
            Guid secondDomainId;
            Guid firstJobId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var firstDomain = new MailDomain { Name = "first.test" };
                var secondDomain = new MailDomain { Name = "second.test" };
                var firstMessage = new StoredMessage
                {
                    ContentKey = firstDescriptor.Key,
                    Size = firstDescriptor.Size,
                    EnvelopeFrom = "alice@first.test"
                };
                var secondMessage = new StoredMessage
                {
                    ContentKey = secondDescriptor.Key,
                    Size = secondDescriptor.Size,
                    EnvelopeFrom = "bob@second.test"
                };
                var firstJob = new DeliveryJob
                {
                    Message = firstMessage,
                    Sender = "alice@first.test",
                    Recipient = "user@example.net",
                    RouteDomain = firstDomain,
                    Status = DeliveryStatus.Deferred
                };
                var secondJob = new DeliveryJob
                {
                    Message = secondMessage,
                    Sender = "bob@second.test",
                    Recipient = "user@example.org",
                    RouteDomain = secondDomain
                };
                db.AddRange(firstDomain, secondDomain, firstMessage, secondMessage, firstJob, secondJob);
                await db.SaveChangesAsync();
                firstDomainId = firstDomain.Id;
                secondDomainId = secondDomain.Id;
                firstJobId = firstJob.Id;
            }

            var firstDomainQueue = await administration.SearchDeliveryJobsAsync(
                null,
                null,
                DeliveryJobScope.Any,
                1,
                10,
                CancellationToken.None,
                firstDomainId);
            Assert.Single(firstDomainQueue.Items);
            Assert.Equal(firstJobId, firstDomainQueue.Items[0].Id);

            var raw = await administration.GetDeliveryRawMessageAsync(
                firstJobId,
                CancellationToken.None,
                firstDomainId);
            Assert.Contains("Subject: first", raw.Content);

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                administration.GetDeliveryRawMessageAsync(firstJobId, CancellationToken.None, secondDomainId));
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                administration.RetryDeliveryAsync(firstJobId, "test", CancellationToken.None, secondDomainId));
            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                administration.DeleteDeliveryAsync(firstJobId, "test", CancellationToken.None, secondDomainId));

            await administration.RetryDeliveryAsync(firstJobId, "test", CancellationToken.None, firstDomainId);
            await using (var verification = await factory.CreateDbContextAsync())
            {
                Assert.Equal(
                    DeliveryStatus.Pending,
                    (await verification.DeliveryJobs.SingleAsync(x => x.Id == firstJobId)).Status);
            }

            await administration.DeleteDeliveryAsync(firstJobId, "test", CancellationToken.None, firstDomainId);
            await using (var verification = await factory.CreateDbContextAsync())
            {
                Assert.False(await verification.DeliveryJobs.AnyAsync(x => x.Id == firstJobId));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AuthenticatedOutboundDeliveryKeepsRouteDomain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            Guid domainId;
            Guid accountId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain
                {
                    Name = "example.com",
                    MailHostName = "mail.example.com",
                    SmtpHostName = "mx.example.com"
                };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused",
                    QuotaBytes = 4096
                };
                db.AddRange(domain, account);
                await db.SaveChangesAsync();
                domainId = domain.Id;
                accountId = account.Id;
            }

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new InboundMessageService(factory, store, NullLogger<InboundMessageService>.Instance);
            await using var message = new MemoryStream(Encoding.UTF8.GetBytes(
                "From: alice@example.com\r\nTo: bob@external.test\r\nSubject: outbound\r\n\r\nhello"));

            var result = await service.AcceptAsync(
                new InboundEnvelope(
                    "alice@example.com",
                    ["bob@external.test"],
                    "alice@example.com",
                    "127.0.0.1",
                    "client.example.com",
                    accountId),
                message,
                CancellationToken.None);

            Assert.Equal(1, result.QueuedDeliveries);
            await using var verification = await factory.CreateDbContextAsync();
            var job = await verification.DeliveryJobs.SingleAsync();
            Assert.Equal(domainId, job.RouteDomainId);
            Assert.Equal(domainId, job.RouteDomainId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("1", 1, 10, true)]
    [InlineData("2:4", 3, 10, true)]
    [InlineData("4:2", 3, 10, true)]
    [InlineData("1,3,5", 4, 10, false)]
    [InlineData("*", 10, 10, true)]
    [InlineData("8:*", 9, 10, true)]
    public void SequenceSetsFollowImapRanges(string expression, long value, long maximum, bool expected)
    {
        Assert.Equal(expected, SequenceSet.Contains(expression, value, maximum));
    }

    [Fact]
    public async Task ContentStoreUsesSha256AndDeduplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 1024 }),
                factory);
            await using var first = new MemoryStream(Encoding.UTF8.GetBytes("Subject: test\r\n\r\nhello"));
            await using var second = new MemoryStream(Encoding.UTF8.GetBytes("Subject: test\r\n\r\nhello"));

            var firstResult = await store.SaveAsync(first, CancellationToken.None);
            var secondResult = await store.SaveAsync(second, CancellationToken.None);

            Assert.Equal(firstResult.Key, secondResult.Key);
            Assert.Equal(64, firstResult.Key.Length);
            await using var stored = await store.OpenReadAsync(firstResult.Key, CancellationToken.None);
            using var reader = new StreamReader(stored);
            Assert.Contains("hello", await reader.ReadToEndAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ContentStoreFailureRemovesTemporaryPartialFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 16 }),
                factory);
            await using var oversized = new MemoryStream(new byte[128]);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(oversized, CancellationToken.None));

            var temporaryDirectory = Path.Combine(root, ".tmp");
            Assert.True(Directory.Exists(temporaryDirectory));
            Assert.Empty(Directory.EnumerateFiles(temporaryDirectory));
            Assert.Empty(Directory.EnumerateFiles(root, "*.eml", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnauthenticatedInboundDeliveryCannotRelay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new InboundMessageService(factory, store, NullLogger<InboundMessageService>.Instance);
            await using var message = new MemoryStream(Encoding.UTF8.GetBytes(
                "From: sender@example.net\r\nTo: victim@example.org\r\nSubject: relay\r\n\r\nblocked"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.AcceptAsync(
                    new InboundEnvelope(
                        "sender@example.net",
                        ["victim@example.org"],
                        null,
                        "127.0.0.1",
                        "client.example.net"),
                    message,
                    CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalInboundDeliveryCreatesInboxMessageWithUid()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused",
                    QuotaBytes = 4096
                };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "INBOX" });
                await db.SaveChangesAsync();
            }

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new InboundMessageService(factory, store, NullLogger<InboundMessageService>.Instance);
            await using var message = new MemoryStream(Encoding.UTF8.GetBytes(
                "Authentication-Results: mx.example.com; spf=pass; dkim=pass; dmarc=pass\r\n"
                + "DKIM-Signature: v=1; d=example.net; s=test\r\n"
                + "From: sender@example.net\r\nTo: alice@example.com\r\nSubject: local\r\n\r\nhello"));

            var result = await service.AcceptAsync(
                new InboundEnvelope(
                    "sender@example.net",
                    ["alice@example.com"],
                    null,
                    "127.0.0.1",
                    "client.example.net"),
                message,
                CancellationToken.None);

            Assert.Equal(1, result.LocalDeliveries);
            Assert.Equal(0, result.QueuedDeliveries);
            await using var verification = await factory.CreateDbContextAsync();
            var delivered = await verification.MailboxMessages.Include(x => x.Message).SingleAsync();
            Assert.Equal(1, delivered.Uid);
            Assert.Equal("sender@example.net", delivered.Message.EnvelopeFrom);
            var inspectionAudit = await verification.AuditRecords.SingleAsync(x => x.Action == "smtp.inbound.mime-inspection");
            Assert.Contains("authenticationResults=1", inspectionAudit.Detail);
            Assert.Contains("dkim=1", inspectionAudit.Detail);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InboundDeliveryAcceptsBodyOnlyAndAuditsDuplicateHeadersSpoofingAndRetries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-edge-31-40-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused",
                    QuotaBytes = 64 * 1024
                };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "INBOX" });
                await db.SaveChangesAsync();
            }

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new InboundMessageService(factory, store, NullLogger<InboundMessageService>.Instance);

            await using (var bodyOnly = new MemoryStream(Encoding.UTF8.GetBytes("hello body only\r\n")))
            {
                var accepted = await service.AcceptAsync(
                    new InboundEnvelope("sender@example.net", ["alice@example.com"], null, "203.0.113.10", "mx.example.net"),
                    bodyOnly,
                    CancellationToken.None);
                Assert.Equal(1, accepted.LocalDeliveries);
            }

            const string duplicateHeaders =
                "From: a@example.net\r\nFrom: b@example.net\r\nSubject: one\r\nSubject: two\r\nMessage-ID: <dup@example.net>\r\n\r\nbody";
            for (var i = 0; i < 2; i++)
            {
                await using var content = new MemoryStream(Encoding.UTF8.GetBytes(duplicateHeaders));
                await service.AcceptAsync(
                    new InboundEnvelope("admin@example.com", ["alice@example.com"], null, "198.51.100.20", "mx.attacker.test"),
                    content,
                    CancellationToken.None);
            }

            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(3, await verification.Messages.CountAsync());
            Assert.Equal(3, await verification.MailboxMessages.CountAsync());
            Assert.Contains(await verification.AuditRecords.ToListAsync(), x => x.Action == "smtp.inbound.duplicate-header");
            Assert.Contains(await verification.AuditRecords.ToListAsync(), x => x.Action == "smtp.inbound.local-domain-spoof");
            Assert.Contains(await verification.AuditRecords.ToListAsync(), x => x.Action == "smtp.inbound.duplicate-suspected");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InboundDeliveryPreservesEnvelopeAndStoresDuplicateOrMissingMessageIdsSeparately()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-edge-cases-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var bob = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "bob",
                    PasswordHash = "unused",
                    QuotaBytes = 32 * 1024
                };
                var carol = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "carol",
                    PasswordHash = "unused",
                    QuotaBytes = 32 * 1024
                };
                db.AddRange(
                    domain,
                    bob,
                    carol,
                    new Mailbox { Account = bob, Name = "INBOX" },
                    new Mailbox { Account = carol, Name = "INBOX" });
                await db.SaveChangesAsync();
            }

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new InboundMessageService(factory, store, NullLogger<InboundMessageService>.Instance);

            Assert.True(await service.IsLocalRecipientAsync("bob@example.com", CancellationToken.None));
            Assert.False(await service.IsLocalRecipientAsync("notfound@example.com", CancellationToken.None));
            Assert.True(await service.IsLocalRecipientAsync("carol@example.com", CancellationToken.None));

            var messages = new[]
            {
                "From: CEO <ceo@company.test>\r\nMessage-ID: <same@example.test>\r\nSubject: first\r\n\r\none",
                "From: CEO <ceo@company.test>\r\nMessage-ID: <same@example.test>\r\nSubject: second\r\n\r\ntwo",
                "From: CEO <ceo@company.test>\r\nSubject: no id\r\n\r\nthree"
            };
            foreach (var raw in messages)
            {
                await using var content = new MemoryStream(Encoding.UTF8.GetBytes(raw));
                var accepted = await service.AcceptAsync(
                    new InboundEnvelope(
                        "bounce@mailer.test",
                        ["bob@example.com", "carol@example.com"],
                        null,
                        "203.0.113.10",
                        "mx.mailer.test"),
                    content,
                    CancellationToken.None);
                Assert.Equal(2, accepted.LocalDeliveries);
            }

            await using var verification = await factory.CreateDbContextAsync();
            var stored = await verification.Messages.OrderBy(x => x.ReceivedAt).ToListAsync();
            Assert.Equal(3, stored.Count);
            Assert.Equal(2, stored.Count(x => x.InternetMessageId == "same@example.test"));
            Assert.Single(stored, x => x.InternetMessageId == null);
            Assert.All(stored, x => Assert.Equal("bounce@mailer.test", x.EnvelopeFrom));
            Assert.Equal(6, await verification.MailboxMessages.CountAsync());
            var bobUids = await verification.MailboxMessages
                .Where(x => x.Mailbox.Account.LocalPart == "bob")
                .OrderBy(x => x.Uid)
                .Select(x => x.Uid)
                .ToArrayAsync();
            Assert.Equal(new long[] { 1, 2, 3 }, bobUids);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ApiSecretsUseConstantTimeComparableHashes()
    {
        var service = new CredentialService();
        var hash = service.HashApiSecret("secret-value");

        Assert.True(service.VerifyApiSecret(hash, "secret-value"));
        Assert.False(service.VerifyApiSecret(hash, "different-value"));
    }

    [Fact]
    public async Task SystemSettingsOverrideDeliveryDefaults()
    {
        var factory = CreateFactory();
        var service = CreateSystemSettingsService(factory);

        await service.SaveDeliverySettingsAsync(
            new DeliverySettingsView(8, 90, 6, 3),
            CancellationToken.None);

        var options = await service.GetDeliveryOptionsAsync(CancellationToken.None);

        Assert.Equal(8, options.BatchSize);
        Assert.Equal(90, options.LeaseSeconds);
        Assert.Equal(6, options.MaximumAttempts);
        Assert.Equal(3, options.PollIntervalSeconds);
    }

    [Fact]
    public async Task SystemSettingsSaveSmtpTimeout()
    {
        var factory = CreateFactory();
        var service = CreateSystemSettingsService(factory);

        var saved = await service.SaveSmtpSettingsAsync(new SmtpSettingsView(45), CancellationToken.None);
        var reloaded = await service.GetSmtpSettingsAsync(CancellationToken.None);

        Assert.Equal(45, saved.CommandTimeoutSeconds);
        Assert.Equal(45, reloaded.CommandTimeoutSeconds);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveSmtpSettingsAsync(new SmtpSettingsView(1), CancellationToken.None));
    }

    [Fact]
    public async Task SystemSettingsSaveAndNormalizeMailboxDefaults()
    {
        var factory = CreateFactory();
        var service = CreateSystemSettingsService(factory);

        var saved = await service.SaveMailboxSettingsAsync(
            new MailboxSettingsView(["Sent", "INBOX", "sent", "Drafts"]),
            CancellationToken.None);
        var reloaded = await service.GetMailboxSettingsAsync(CancellationToken.None);

        Assert.Equal(["INBOX", "Sent", "Drafts", "Trash", "Junk", "Archives"], saved.DefaultFolders);
        Assert.Equal(saved.DefaultFolders, reloaded.DefaultFolders);
    }

    [Fact]
    public async Task DomainMailboxSettingsFallBackToGlobalAndThenBecomeIndependent()
    {
        var factory = CreateFactory();
        var service = CreateSystemSettingsService(factory);
        Guid firstDomainId;
        Guid secondDomainId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var first = new MailDomain { Name = "first.example" };
            var second = new MailDomain { Name = "second.example" };
            db.Domains.AddRange(first, second);
            await db.SaveChangesAsync();
            firstDomainId = first.Id;
            secondDomainId = second.Id;
        }

        await service.SaveMailboxSettingsAsync(new MailboxSettingsView(["Legacy"]), CancellationToken.None);
        var fallback = await service.GetDomainMailboxSettingsAsync(firstDomainId, CancellationToken.None);
        Assert.Contains("Legacy", fallback.DefaultFolders);

        var saved = await service.SaveDomainMailboxSettingsAsync(
            firstDomainId,
            new MailboxSettingsView(["Projects"]),
            CancellationToken.None);
        var secondSettings = await service.GetDomainMailboxSettingsAsync(secondDomainId, CancellationToken.None);
        Assert.Equal(["INBOX", "Sent", "Drafts", "Trash", "Junk", "Archives", "Projects"], saved.DefaultFolders);
        Assert.Contains("Legacy", secondSettings.DefaultFolders);
        Assert.DoesNotContain("Legacy", saved.DefaultFolders);
    }

    [Fact]
    public async Task SystemSettingsSaveAndRenderMessageTemplates()
    {
        var factory = CreateFactory();
        var service = CreateSystemSettingsService(factory);
        var saved = await service.SaveMessageTemplateSettingsAsync(
            new MessageTemplateSettingsView(
                "Delivery Robot",
                "Failed: {recipient}",
                "<p>Permanent {reason} / {queueId}</p>",
                "Retry exhausted: {recipient}",
                "Retried {attempts} times for {recipient}"),
            CancellationToken.None);
        var reloaded = await service.GetMessageTemplateSettingsAsync(CancellationToken.None);

        Assert.Equal(saved, reloaded);
        Assert.Equal(
            "Failed: user@example.com",
            SystemSettingsService.RenderTemplate(
                reloaded.PermanentFailureSubject,
                new Dictionary<string, string> { ["recipient"] = "user@example.com" }));
        Assert.Equal(
            "<p>Permanent &lt;bad&gt; / abc</p>",
            SystemSettingsService.RenderHtmlTemplate(
                reloaded.PermanentFailureBody,
                new Dictionary<string, string> { ["reason"] = "<bad>", ["queueId"] = "abc" }));
    }

    [Fact]
    public async Task SystemSettingsSaveCertificateSelectionSettings()
    {
        var factory = CreateFactory();
        var service = CreateSystemSettingsService(factory);
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.com", MailHostName = "mail.example.com" };
            db.Domains.Add(domain);
            db.DomainCertificates.Add(new DomainCertificate
            {
                Domain = domain,
                CertificatePath = "chain.pem",
                PrivateKeyPath = "key.pem",
                Subject = "CN=mail.example.com",
                Issuer = "CN=Test",
                SubjectAlternativeNames = "mail.example.com",
                Thumbprint = "00",
                Source = CertificateSource.ExternalPath
            });
            await db.SaveChangesAsync();
        }

        var saved = await service.SaveCertificateSelectionSettingsAsync(
            new CertificateSelectionSettingsView("MAIL.EXAMPLE.COM.", false, [], null),
            CancellationToken.None);
        var reloaded = await service.GetCertificateSelectionSettingsAsync(CancellationToken.None);

        Assert.Equal("mail.example.com", saved.DefaultMailHostName);
        Assert.Equal("mail.example.com", reloaded.EffectiveDefaultMailHostName);
        Assert.False(reloaded.AllowSingleCertificateFallback);
        Assert.Equal(["mail.example.com"], reloaded.AvailableMailHostNames);
        await Assert.ThrowsAsync<FormatException>(() =>
            service.SaveCertificateSelectionSettingsAsync(
                new CertificateSelectionSettingsView("bad host", true, [], null),
                CancellationToken.None));
    }

    [Fact]
    public async Task AdminAccountCreationUsesConfiguredDefaultFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            await settings.SaveMailboxSettingsAsync(
                new MailboxSettingsView(["INBOX", "Sent", "Archive/Test"]),
                CancellationToken.None);
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Domains.Add(new MailDomain { Name = "example.com" });
                await db.SaveChangesAsync();
            }

            var credentials = new CredentialService();
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var administration = CreateAdministration(factory, settings, credentials, store);

            await administration.CreateAccountAsync(
                "alice@example.com",
                "a strong test password",
                null,
                "test",
                CancellationToken.None);

            await using var verification = await factory.CreateDbContextAsync();
            var folders = await verification.Mailboxes
                .OrderBy(x => x.Name == "INBOX" ? 0 : 1)
                .ThenBy(x => x.Name)
                .Select(x => x.Name)
                .ToListAsync();
            Assert.Equal(["INBOX", "Archive/Test", "Archives", "Drafts", "Junk", "Sent", "Trash"], folders);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DomainScopedAccountAndAliasManagementStayInsideDomain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            await settings.SaveMailboxSettingsAsync(
                new MailboxSettingsView(["INBOX", "Sent", "Drafts"]),
                CancellationToken.None);
            Guid exampleId;
            Guid otherId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var example = new MailDomain { Name = "example.com" };
                var other = new MailDomain { Name = "other.com" };
                db.Domains.AddRange(example, other);
                await db.SaveChangesAsync();
                exampleId = example.Id;
                otherId = other.Id;
            }

            var credentials = new CredentialService();
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var administration = CreateAdministration(factory, settings, credentials, store);

            var account = await administration.CreateDomainAccountAsync(
                exampleId,
                "Alice",
                "a strong test password",
                512 * 1_048_576L,
                "test",
                CancellationToken.None);
            var alias = await administration.CreateDomainAliasAsync(
                exampleId,
                "sales",
                "alice@example.com",
                "test",
                CancellationToken.None);

            var exampleAccounts = await administration.GetDomainAccountsAsync(exampleId, CancellationToken.None);
            var otherAccounts = await administration.GetDomainAccountsAsync(otherId, CancellationToken.None);
            var exampleAliases = await administration.GetDomainAliasesAsync(exampleId, CancellationToken.None);
            var otherAliases = await administration.GetDomainAliasesAsync(otherId, CancellationToken.None);
            var health = await administration.GetDomainHealthAsync(exampleId, CancellationToken.None);
            var audit = await administration.GetDomainAuditAsync(exampleId, CancellationToken.None);

            Assert.Equal("alice@example.com", account.Address);
            Assert.Equal(512 * 1_048_576L, account.QuotaBytes);
            Assert.Single(exampleAccounts);
            Assert.Empty(otherAccounts);
            Assert.Equal("sales@example.com", alias.Source);
            Assert.Single(exampleAliases);
            Assert.Empty(otherAliases);
            Assert.Equal(1, health.AccountCount);
            Assert.Equal(1, health.EnabledAccountCount);
            Assert.Equal(1, health.AliasCount);
            Assert.Contains(audit, x => x.Action == "account.create" && x.Detail == "alice@example.com");
            Assert.Contains(audit, x => x.Action == "alias.create" && x.Detail == "sales@example.com->alice@example.com");

            await using var verification = await factory.CreateDbContextAsync();
            var folders = await verification.Mailboxes
                .Where(x => x.AccountId == account.Id)
                .OrderBy(x => x.Name == "INBOX" ? 0 : 1)
                .ThenBy(x => x.Name)
                .Select(x => x.Name)
                .ToListAsync();
            Assert.Equal(["INBOX", "Archives", "Drafts", "Junk", "Sent", "Trash"], folders);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DomainAccountAndAliasBatchManagementIsAtomicAndSearchable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            await settings.SaveMailboxSettingsAsync(new MailboxSettingsView(["INBOX"]), CancellationToken.None);
            Guid domainId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                db.Domains.Add(domain);
                await db.SaveChangesAsync();
                domainId = domain.Id;
            }

            var administration = CreateAdministration(
                factory,
                settings,
                new CredentialService(),
                new FileMessageContentStore(
                    Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                    factory));
            var alice = await administration.CreateDomainAccountAsync(domainId, "alice", "a strong test password", null, "test", CancellationToken.None);
            var bob = await administration.CreateDomainAccountAsync(domainId, "bob", "a strong test password", null, "test", CancellationToken.None);
            var sales = await administration.CreateDomainAliasAsync(domainId, "sales", alice.Id, MailAliasMode.SendReceive, "test", CancellationToken.None);
            var support = await administration.CreateDomainAliasAsync(domainId, "support", alice.Id, MailAliasMode.ReceiveOnly, "test", CancellationToken.None);

            var accounts = await administration.SearchDomainAccountsAsync(domainId, "alice", null, 1, 10, CancellationToken.None);
            Assert.Single(accounts.Items);
            Assert.Equal(2, accounts.Items[0].AliasCount);
            var bySource = await administration.SearchDomainAliasesAsync(domainId, "sales", 1, 10, CancellationToken.None);
            var byTarget = await administration.SearchDomainAliasesAsync(domainId, "alice@example.com", 1, 10, CancellationToken.None);
            Assert.Single(bySource.Items);
            Assert.Equal(2, byTarget.TotalCount);

            await administration.UpdateDomainAliasesModeAsync(domainId, [sales.Id, support.Id], MailAliasMode.Disabled, "test", CancellationToken.None);
            var disabled = await administration.SearchDomainAliasesAsync(domainId, null, 1, 10, CancellationToken.None);
            Assert.All(disabled.Items, alias => { Assert.Equal(MailAliasMode.Disabled, alias.Mode); Assert.False(alias.IsEnabled); });
            await administration.UpdateDomainAliasesModeAsync(domainId, [sales.Id, support.Id], MailAliasMode.ReceiveOnly, "test", CancellationToken.None);
            var receiveOnly = await administration.SearchDomainAliasesAsync(domainId, null, 1, 10, CancellationToken.None);
            Assert.All(receiveOnly.Items, alias => { Assert.True(alias.CanReceive); Assert.False(alias.CanSend); });

            await Assert.ThrowsAsync<InvalidOperationException>(() => administration.DeleteDomainAccountsAsync(
                domainId, [alice.Id, bob.Id], "test", CancellationToken.None));
            await using (var verification = await factory.CreateDbContextAsync())
                Assert.Equal(2, await verification.Accounts.CountAsync(x => x.DeletedAt == null));

            await administration.DisableDomainAccountsAsync(domainId, [alice.Id, bob.Id], "test", CancellationToken.None);
            await administration.EnableDomainAccountsAsync(domainId, [alice.Id, bob.Id], "test", CancellationToken.None);
            var enabledAccounts = await administration.SearchDomainAccountsAsync(domainId, null, true, 1, 10, CancellationToken.None);
            Assert.Equal(2, enabledAccounts.TotalCount);
            await administration.DisableDomainAccountsAsync(domainId, [alice.Id, bob.Id], "test", CancellationToken.None);
            await administration.DeleteDomainAccountsAsync(domainId, [alice.Id, bob.Id], "test", CancellationToken.None);
            await using (var verification = await factory.CreateDbContextAsync())
                Assert.Equal(2, await verification.Accounts.IgnoreQueryFilters().CountAsync(x => x.DeletedAt != null));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AccountQuotaCannotExceedDomainQuota()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            await settings.SaveMailboxSettingsAsync(new MailboxSettingsView(["INBOX"]), CancellationToken.None);
            Guid domainId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com", QuotaBytes = 100 * 1_048_576L };
                db.Domains.Add(domain);
                await db.SaveChangesAsync();
                domainId = domain.Id;
            }

            var administration = CreateAdministration(
                factory,
                settings,
                new CredentialService(),
                new FileMessageContentStore(
                    Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                    factory));

            await Assert.ThrowsAsync<InvalidOperationException>(() => administration.CreateDomainAccountAsync(
                domainId,
                "oversized",
                "a strong test password",
                101 * 1_048_576L,
                "test",
                CancellationToken.None));

            var account = await administration.CreateDomainAccountAsync(
                domainId,
                "alice",
                "a strong test password",
                80 * 1_048_576L,
                "test",
                CancellationToken.None);

            await Assert.ThrowsAsync<InvalidOperationException>(() => administration.UpdateAccountAsync(
                account.Id,
                null,
                101 * 1_048_576L,
                "test",
                CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => administration.UpdateDomainQuotaAsync(
                domainId,
                79 * 1_048_576L,
                "test",
                CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MailboxServiceEnsuresDefaultFoldersIdempotently()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            await settings.SaveMailboxSettingsAsync(
                new MailboxSettingsView(["INBOX", "Sent", "Drafts"]),
                CancellationToken.None);
            Guid accountId;
            Guid deletedSentId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused"
                };
                var deletedSent = new Mailbox
                {
                    Account = account,
                    Name = "Sent",
                    DeletedAt = DateTimeOffset.UtcNow,
                    DeletedBy = "imap",
                    PurgeAfter = DateTimeOffset.UtcNow.AddDays(30)
                };
                db.AddRange(domain, account, new Mailbox { Account = account, Name = "INBOX" }, deletedSent);
                db.CleanupQueue.Add(new CleanupQueueItem
                {
                    ResourceType = CleanupResourceType.Mailbox,
                    ResourceId = deletedSent.Id,
                    AccountId = account.Id,
                    PurgeAfter = deletedSent.PurgeAfter!.Value
                });
                await db.SaveChangesAsync();
                accountId = account.Id;
                deletedSentId = deletedSent.Id;
            }

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new MailboxService(factory, store, settings);

            await service.EnsureDefaultFoldersAsync(accountId, CancellationToken.None);
            await service.EnsureDefaultFoldersAsync(accountId, CancellationToken.None);

            var subscribed = await service.ListSubscribedAsync(accountId, CancellationToken.None);
            Assert.Equal(["INBOX", "Sent", "Drafts", "Trash", "Junk", "Archives"], subscribed.Select(x => x.Name));
            await service.UnsubscribeAsync(accountId, "Drafts", CancellationToken.None);
            await service.EnsureDefaultFoldersAsync(accountId, CancellationToken.None);
            subscribed = await service.ListSubscribedAsync(accountId, CancellationToken.None);
            Assert.DoesNotContain(subscribed, x => x.Name == "Drafts");

            await using var verification = await factory.CreateDbContextAsync();
            var folders = await verification.Mailboxes
                .OrderBy(x => x.Name == "INBOX" ? 0 : 1)
                .ThenBy(x => x.Name)
                .Select(x => x.Name)
                .ToListAsync();
            Assert.Equal(["INBOX", "Archives", "Drafts", "Junk", "Sent", "Trash"], folders);
            Assert.Equal(
                CleanupQueueStatus.Restored,
                await verification.CleanupQueue.Where(x => x.ResourceId == deletedSentId).Select(x => x.Status).SingleAsync());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MailboxServiceChangesUidValidityWhenMailboxIsRebuilt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
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
                db.AddRange(domain, account);
                await db.SaveChangesAsync();
                accountId = account.Id;
            }

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new MailboxService(factory, store, settings);

            var first = await service.CreateAsync(accountId, "Archive", CancellationToken.None);
            await service.DeleteAsync(accountId, "Archive", CancellationToken.None);
            var rebuilt = await service.CreateAsync(accountId, "Archive", CancellationToken.None);

            Assert.NotEqual(first.Id, rebuilt.Id);
            Assert.True(rebuilt.UidValidity > first.UidValidity);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MailboxServicePlusFlagsMergeWithoutReplacingExistingKeywords()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
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
                var message = new StoredMessage
                {
                    ContentKey = "message",
                    EnvelopeFrom = "sender@example.net",
                    InternetMessageId = "flags@example.net",
                    Size = 0
                };
                db.AddRange(domain, account, mailbox, message);
                db.MailboxMessages.Add(new MailboxMessage
                {
                    Mailbox = mailbox,
                    Message = message,
                    Uid = 1,
                    Flags = "\\Seen"
                });
                await db.SaveChangesAsync();
                mailboxId = mailbox.Id;
            }

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new MailboxService(factory, store, settings);

            await Task.WhenAll(
                service.StoreFlagsAsync(mailboxId, "1", useUid: false, "+FLAGS", ["$Forwarded"], CancellationToken.None),
                service.StoreFlagsAsync(mailboxId, "1", useUid: false, "+FLAGS", ["MyCustomFlag"], CancellationToken.None));
            var item = (await service.FetchAsync(mailboxId, "1", useUid: false, CancellationToken.None)).Single();

            Assert.Contains("\\Seen", item.Flags);
            Assert.Contains("$Forwarded", item.Flags);
            Assert.Contains("MyCustomFlag", item.Flags);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MimeInspectionExtractsSafeTextAndBackendRiskMetadata()
    {
        var service = new MimeInspectionService();
        var raw = """
            Authentication-Results: mx.example.com; dkim=pass
            DKIM-Signature: v=1; d=example.com; s=s1
            ARC-Seal: i=1; a=rsa-sha256
            Auto-Submitted: auto-replied
            Precedence: bulk
            List-Unsubscribe: <mailto:unsubscribe@example.com>, <https://example.com/unsub>
            List-Unsubscribe-Post: List-Unsubscribe=One-Click
            From: Sender <sender@example.net>
            Reply-To: list@example.com
            MIME-Version: 1.0
            Subject: inspection
            Content-Type: multipart/mixed; boundary="b"

            --b
            Content-Type: text/html; charset=utf-8
            Content-Transfer-Encoding: quoted-printable

            hello=
            world <script>alert(1)</script><style>body{display:none}</style><img src="https://tracker.example/open.png">&amp;#20320;
            --b
            Content-Type: application/zip; name="../../../../etc/passwd.zip"
            Content-Disposition: attachment; filename="../../../../etc/passwd.zip"

            PK
            --b
            Content-Type: image/svg+xml; name="x.svg"
            Content-Disposition: attachment; filename="x.svg"

            <svg></svg>
            --b
            Content-Type: application/pdf; name="x.pdf"
            Content-Disposition: attachment; filename="x.pdf"

            %PDF
            --b--
            """.ReplaceLineEndings("\r\n");

        var result = service.Inspect(Encoding.UTF8.GetBytes(raw));

        Assert.Contains("helloworld", result.Text.Replace("\r", string.Empty).Replace("\n", string.Empty));
        Assert.DoesNotContain("alert(1)", result.Text);
        Assert.DoesNotContain("display:none", result.Text);
        Assert.Contains("&#20320;", result.Text);
        Assert.DoesNotContain("你", result.Text);
        Assert.Contains(MimeRiskFlag.HtmlScript, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.HtmlStyle, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.RemoteResource, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.AttachmentPathTraversal, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.ArchiveAttachment, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.SvgAttachment, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.PdfAttachment, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.AuthenticationResults, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.ListUnsubscribe, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.AutoSubmitted, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.BulkPrecedence, result.RiskFlags);
        Assert.Contains(MimeRiskFlag.ReplyToDifferentFromFrom, result.RiskFlags);
        Assert.Contains(result.Attachments, x => x.SafeFileName == "passwd.zip");
        Assert.Single(result.Authentication.AuthenticationResults);
        Assert.Single(result.Authentication.DkimSignatures);
        Assert.Single(result.Authentication.ArcResults);
        Assert.Equal("List-Unsubscribe=One-Click", result.ListUnsubscribePost);
    }

    [Fact]
    public void MimeInspectionSanitizesHeaderPhrasesAndAttachmentFileNames()
    {
        var service = new MimeInspectionService();

        Assert.Equal("Alice  Bcc: victim@example.com", service.SanitizeHeaderPhrase("Alice\r\nBcc: victim@example.com"));
        Assert.Equal("hosts", service.SanitizeFileName(@"C:\Windows\system32\drivers\etc\hosts"));
        Assert.Equal("passwd", service.SanitizeFileName("../../../../etc/passwd"));
        Assert.Equal("_CON.txt", service.SanitizeFileName("CON.txt"));
    }

    [Fact]
    public void MimeInspectionLimitsDeepTreesAndLargePartCounts()
    {
        var service = new MimeInspectionService();
        var deep = new StringBuilder("MIME-Version: 1.0\r\n");
        for (var depth = 0; depth < 70; depth++)
        {
            deep.Append($"Content-Type: multipart/mixed; boundary=\"b{depth}\"\r\n\r\n--b{depth}\r\n");
        }
        deep.Append("Content-Type: text/plain\r\n\r\nbottom\r\n");
        for (var depth = 69; depth >= 0; depth--) deep.Append($"--b{depth}--\r\n");

        var many = new StringBuilder("MIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=\"many\"\r\n\r\n");
        for (var index = 0; index < 2100; index++)
        {
            many.Append("--many\r\nContent-Type: text/plain\r\n\r\nx\r\n");
        }
        many.Append("--many--\r\n");

        var deepResult = service.Inspect(Encoding.UTF8.GetBytes(deep.ToString()));
        var manyResult = service.Inspect(Encoding.UTF8.GetBytes(many.ToString()));

        Assert.Contains(MimeRiskFlag.DeepMimeTree, deepResult.RiskFlags);
        Assert.Contains(MimeRiskFlag.TooManyMimeParts, manyResult.RiskFlags);
        Assert.True(manyResult.Text.Length <= 512 * 1024);
    }

    [Fact]
    public void SentCopySchedulingUsesInjectedClock()
    {
        var factory = CreateFactory();
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero));
        var service = new SentCopyService(
            factory,
            null!,
            Options.Create(new MailRulesOptions { SentCopyGraceSeconds = 30, SentCopyDeduplicationMinutes = 10 }),
            clock,
            NullLogger<SentCopyService>.Instance);
        using var db = factory.CreateDbContext();
        var account = new MailAccount { LocalPart = "alice", PasswordHash = "unused", Domain = new MailDomain { Name = "example.com" } };
        var message = new StoredMessage { ContentKey = new string('a', 64), EnvelopeFrom = "alice@example.com", Size = 1 };
        var mime = new MimeKit.MimeMessage { MessageId = "clock@example.com", Subject = "clock" };
        mime.From.Add(MimeKit.MailboxAddress.Parse("alice@example.com"));

        var job = service.Queue(db, account, message, mime, "Sent", null);

        Assert.Equal(clock.UtcNow.AddSeconds(30), job.ReadyAt);
        Assert.Equal(clock.UtcNow.AddMinutes(10), job.DeduplicateUntil);
    }

    [Fact]
    public async Task MailboxAppendRepairsRolledBackUidNextBeforeAssigningUid()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-tests-{Guid.NewGuid():N}");
        try
        {
            var factory = CreateFactory();
            var settings = CreateSystemSettingsService(factory);
            Guid accountId;
            Guid mailboxId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.com" };
                var account = new MailAccount { Domain = domain, LocalPart = "alice", PasswordHash = "unused" };
                var mailbox = new Mailbox { Account = account, Name = "INBOX", UidNext = 1 };
                var message = new StoredMessage
                {
                    ContentKey = new string('a', 64),
                    EnvelopeFrom = "sender@example.net",
                    Size = 0
                };
                db.AddRange(domain, account, mailbox, message);
                db.MailboxMessages.Add(new MailboxMessage { Mailbox = mailbox, Message = message, Uid = 5 });
                await db.SaveChangesAsync();
                accountId = account.Id;
                mailboxId = mailbox.Id;
            }

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new MailboxService(factory, store, settings);
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Subject: uid\r\n\r\nbody\r\n"));

            var result = await service.AppendAsync(accountId, "INBOX", content, [], null, CancellationToken.None);

            Assert.Equal(6, result.Item.Uid);
            Assert.Equal(7, result.UidNext);
            await using var verification = await factory.CreateDbContextAsync();
            var verifiedMailbox = await verification.Mailboxes.SingleAsync(x => x.Id == mailboxId);
            Assert.Equal(7, verifiedMailbox.UidNext);
            var uids = await verification.MailboxMessages
                .Where(x => x.MailboxId == mailboxId)
                .OrderBy(x => x.Uid)
                .Select(x => x.Uid)
                .ToArrayAsync();
            Assert.Equal([5, 6], uids);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MailServerLogFormatterUsesConfiguredTimestampAndColor()
    {
        var line = MailServerLogFormatter.Format(
            "MailServer.Smtp.SmtpServerService",
            LogLevel.Warning,
            "authentication failed",
            null,
            new DateTimeOffset(
                2026,
                6,
                26,
                7,
                8,
                9,
                TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 6, 26, 7, 8, 9))),
            "yyyy/MM/dd HH:mm:ss",
            color: true);

        Assert.StartsWith("2026/06/26 07:08:09", line);
        Assert.Contains("\u001b[", line);
        Assert.Contains("SMTP", line);
    }

    [Fact]
    public void MailServerLoggingWritesDailyFileAndKeepsFilePlainText()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-logs-{Guid.NewGuid():N}");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = "Warning",
                    ["MailServerLogging:Console:Enabled"] = "false",
                    ["MailServerLogging:File:Enabled"] = "true",
                    ["MailServerLogging:File:Directory"] = root,
                    ["MailServerLogging:TimestampFormat"] = "yyyy/MM/dd HH:mm:ss"
                })
                .Build();
            var path = Path.Combine(root, $"{DateTime.Now:yyyy-MM-dd}.log");
            using (var loggerFactory = LoggerFactory.Create(builder => builder.AddMailServerLogging(configuration)))
            {
                var logger = loggerFactory.CreateLogger("MailServer.Imap.Tests");

                logger.LogInformation("hidden");
                logger.LogWarning("visible");
            }

            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Contains($"{DateTime.Now:yyyy/MM/dd} ", content);
            Assert.Contains("visible", content);
            Assert.DoesNotContain("hidden", content);
            Assert.DoesNotContain("\u001b[", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DnsDiagnosticsWarnsWhenPublishedIpv6MxIsUnreachable()
    {
        var warnings = DnsDiagnosticService.BuildConnectivityWarnings(
            [new MxRecordDiagnostic(10, "mail.example.test")],
            [
                new MxConnectivityDiagnostic("mail.example.test", "192.0.2.10", "IPv4", 25, true, null),
                new MxConnectivityDiagnostic("mail.example.test", "2001:db8::10", "IPv6", 25, false, "timeout")
            ]);

        Assert.Contains(warnings, warning => warning.Contains("IPv6", StringComparison.Ordinal));
        Assert.DoesNotContain(
            warnings,
            warning => warning.StartsWith("No MX host accepted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecycleBinListUsesHumanReadableNamesForDeletedResources()
    {
        var factory = CreateFactory();
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-recycle-{Guid.NewGuid():N}");
        try
        {
            var deletedAt = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
            var purgeAfter = deletedAt.AddDays(30);
            Guid accountId;
            Guid mailboxId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.test" };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused",
                    DeletedAt = deletedAt,
                    DeletedBy = "test",
                    PurgeAfter = purgeAfter
                };
                var mailbox = new Mailbox
                {
                    Account = account,
                    Name = "Archive",
                    DeletedAt = deletedAt,
                    DeletedBy = "test",
                    PurgeAfter = purgeAfter
                };
                db.AddRange(domain, account, mailbox);
                await db.SaveChangesAsync();
                accountId = account.Id;
                mailboxId = mailbox.Id;
                db.CleanupQueue.AddRange(
                    new CleanupQueueItem
                    {
                        ResourceType = CleanupResourceType.Account,
                        ResourceId = account.Id,
                        DomainId = domain.Id,
                        EstimatedBytes = 1024,
                        CreatedAt = deletedAt,
                        PurgeAfter = purgeAfter
                    },
                    new CleanupQueueItem
                    {
                        ResourceType = CleanupResourceType.Mailbox,
                        ResourceId = mailbox.Id,
                        AccountId = account.Id,
                        DomainId = domain.Id,
                        EstimatedBytes = 512,
                        CreatedAt = deletedAt,
                        PurgeAfter = purgeAfter
                    });
                await db.SaveChangesAsync();
            }

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new CleanupQueueService(factory, store, new FixedClock(deletedAt));

            var rows = await service.ListAsync(CancellationToken.None);

            Assert.Contains(rows, item => item.ResourceId == accountId && item.DisplayName == "alice@example.test");
            Assert.Contains(rows, item => item.ResourceId == mailboxId && item.DisplayName == "alice@example.test / Archive");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DnsHealthIgnoresConnectivityWarningsButRejectsMissingOrConflictingRecords()
    {
        var matching = new DomainDnsValidation(
            "example.test",
            [new DomainDnsValidationItem("MX", "example.test", "mail.example.test", ["10 mail.example.test"], "match")],
            ["SMTP port 25 is not reachable from this diagnostic environment."]);
        var missing = matching with
        {
            Items = [new DomainDnsValidationItem("TXT", "example.test", "v=spf1 -all", [], "missing")]
        };
        var conflicting = matching with
        {
            Items = [new DomainDnsValidationItem("TXT", "example.test", "v=spf1 -all", ["v=spf1 ~all"], "conflict")]
        };

        Assert.Equal(DomainDnsCheckStatus.Healthy, DomainDnsService.DetermineCheckStatus(matching));
        Assert.Equal(DomainDnsCheckStatus.Unhealthy, DomainDnsService.DetermineCheckStatus(missing));
        Assert.Equal(DomainDnsCheckStatus.Unhealthy, DomainDnsService.DetermineCheckStatus(conflicting));
    }

    [Fact]
    public async Task MovingMessageToTrashSchedulesDailyPurgeAndPurgeReleasesQuota()
    {
        var factory = CreateFactory();
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-trash-{Guid.NewGuid():N}");
        try
        {
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            Guid inboxId;
            Guid accountId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.test", UsedBytes = 12 };
                var account = new MailAccount
                {
                    Domain = domain, LocalPart = "alice", PasswordHash = "unused",
                    UsedBytes = 12, QuotaBytes = 4096
                };
                var inbox = new Mailbox { Account = account, Name = "INBOX" };
                var trash = new Mailbox { Account = account, Name = "Trash" };
                var stored = new StoredMessage { ContentKey = new string('a', 64), EnvelopeFrom = "", Size = 12 };
                db.MailboxMessages.Add(new MailboxMessage { Mailbox = inbox, Message = stored, Uid = 1 });
                db.Mailboxes.Add(trash);
                await db.SaveChangesAsync();
                inboxId = inbox.Id;
                accountId = account.Id;
            }
            var settings = CreateSystemSettingsService(factory);
            var sent = new SentCopyService(factory, store, Options.Create(new MailRulesOptions()), NullLogger<SentCopyService>.Instance);
            var mailboxService = new MailboxService(factory, store, settings, sent,
                Options.Create(new MaintenanceOptions { SoftDeleteRetentionDays = 30 }));

            await mailboxService.CopyAsync(inboxId, "1", false, "Trash", true, CancellationToken.None);

            Guid queueId;
            DateTimeOffset due;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var trashed = await db.MailboxMessages.SingleAsync();
                Assert.NotNull(trashed.ScheduledDeleteAt);
                var queued = await db.CleanupQueue.SingleAsync();
                Assert.Equal(trashed.Id, queued.ResourceId);
                queueId = queued.Id;
                due = queued.PurgeAfter;
            }
            var cleanup = new CleanupQueueService(factory, store, new FixedClock(due.AddDays(1)));
            var result = await cleanup.PurgeDueAsync(100, CancellationToken.None);
            Assert.Equal(1, result.ProcessedCount);
            Assert.Equal(12, result.ReleasedBytes);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Empty(await db.MailboxMessages.ToListAsync());
                Assert.Equal(0, (await db.Accounts.SingleAsync(x => x.Id == accountId)).UsedBytes);
                Assert.Equal(CleanupQueueStatus.Completed, (await db.CleanupQueue.SingleAsync(x => x.Id == queueId)).Status);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task QuotaReconciliationRepairsPersistedAccountAndDomainCounters()
    {
        var factory = CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var domain = new MailDomain { Name = "example.test", UsedBytes = 999 };
            var account = new MailAccount { Domain = domain, LocalPart = "alice", PasswordHash = "unused", UsedBytes = 999 };
            var mailbox = new Mailbox { Account = account, Name = "INBOX" };
            db.MailboxMessages.Add(new MailboxMessage
            {
                Mailbox = mailbox,
                Message = new StoredMessage { ContentKey = new string('b', 64), EnvelopeFrom = "", Size = 42 },
                Uid = 1
            });
            await db.SaveChangesAsync();
        }
        var service = new QuotaReconciliationService(factory, new FixedClock(DateTimeOffset.UtcNow));
        await service.ReconcileAsync(CancellationToken.None);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(42, (await verify.Accounts.SingleAsync()).UsedBytes);
        Assert.Equal(42, (await verify.Domains.SingleAsync()).UsedBytes);
    }

    [Fact]
    public async Task QuarantineReleaseDeliversToOriginalInboxAndUpdatesQuota()
    {
        var factory = CreateFactory();
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-quarantine-{Guid.NewGuid():N}");
        try
        {
            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            Guid domainId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                var domain = new MailDomain { Name = "example.test", QuotaBytes = 4096 };
                var account = new MailAccount
                {
                    Domain = domain,
                    LocalPart = "alice",
                    PasswordHash = "unused",
                    QuotaBytes = 4096
                };
                db.Accounts.Add(account);
                await db.SaveChangesAsync();
                domainId = domain.Id;
            }

            var service = new QuarantineService(
                factory,
                store,
                new FixedClock(new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero)),
                NullLogger<QuarantineService>.Instance);
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("Subject: suspicious\r\nMessage-ID: <q@example.test>\r\n\r\nbody"));
            var records = await service.CreateAsync(
                new QuarantineCreateRequest(
                    "sender@example.net",
                    ["alice@example.test"],
                    "192.0.2.55",
                    content,
                    "test-plugin",
                    "测试插件",
                    "命中测试策略"),
                CancellationToken.None);

            Assert.Single(records);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Empty(await db.MailboxMessages.ToListAsync());
                Assert.Equal(0, (await db.Accounts.SingleAsync()).UsedBytes);
            }

            await service.ReleaseAsync(domainId, records[0].Id, "admin", "确认安全", CancellationToken.None);

            await using (var db = await factory.CreateDbContextAsync())
            {
                var mailboxMessage = await db.MailboxMessages.Include(x => x.Mailbox).Include(x => x.Message).SingleAsync();
                Assert.Equal("INBOX", mailboxMessage.Mailbox.Name);
                Assert.Equal("q@example.test", mailboxMessage.Message.InternetMessageId);
                Assert.True((await db.Accounts.SingleAsync()).UsedBytes > 0);
                Assert.Equal(QuarantineStatus.Released, (await db.QuarantineRecords.SingleAsync()).Status);
                Assert.Contains(await db.AuditRecords.Select(x => x.Action).ToListAsync(), x => x == "quarantine.release");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task WitnessLeaseAllowsOnlyOneWriterUntilLeaseExpires()
    {
        var factory = CreateFactory();
        var now = DateTimeOffset.UtcNow;
        var witness = new WitnessLeaseService(factory, new FixedClock(now));
        var first = await witness.AcquireAsync(new WitnessLeaseRequest("node-a", 15), CancellationToken.None);
        var second = await witness.AcquireAsync(new WitnessLeaseRequest("node-b", 15), CancellationToken.None);
        Assert.True(first.Granted);
        Assert.False(second.Granted);
        Assert.Equal(first.Term, second.Term);
    }

    [Fact]
    public async Task AuditLogSearchInfersLegacyRecordsAndRedactsSensitiveRawJson()
    {
        var factory = CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.AuditRecords.AddRange(
                new AuditRecord
                {
                    Actor = "admin",
                    Action = "domain.create",
                    Resource = "domain:example",
                    Detail = "example.com"
                },
                new AuditRecord
                {
                    Actor = "plugin:policy",
                    Action = "plugin.runtime.reject",
                    Resource = "plugin:policy",
                    Detail = "blocked",
                    Category = AuditCategory.Plugin,
                    Status = AuditStatus.Warning,
                    PluginId = "policy",
                    PluginName = "Policy",
                    RemoteAddress = "2001:db8::1",
                    RawJson = "{\"pluginId\":\"policy\",\"secret\":\"plain\",\"decision\":\"Reject\",\"message\":\"被拒绝\"}"
                });
            await db.SaveChangesAsync();
        }

        var service = new AuditLogService(factory, Options.Create(new ClusterOptions { NodeId = "node-a" }));
        var plugins = await service.SearchAsync(
            new AuditSearchRequest(null, AuditCategory.Plugin, null, null, null, "policy", null, null),
            1,
            20,
            CancellationToken.None);
        var creates = await service.SearchAsync(
            new AuditSearchRequest(null, AuditCategory.Create, null, null, null, null, null, null),
            1,
            20,
            CancellationToken.None);
        var detail = await service.GetAsync(plugins.Items.Single().Id, CancellationToken.None);

        Assert.Single(plugins.Items);
        Assert.Equal(AuditCategory.Plugin, plugins.Items.Single().Category);
        Assert.Single(creates.Items);
        Assert.Equal(AuditCategory.Create, creates.Items.Single().Category);
        Assert.Contains("\"secret\": \"***\"", detail.RawJson);
        Assert.Contains("\"operationIp\": \"2001:db8::1\"", detail.RawJson);
        Assert.Contains("message", detail.RawJson, StringComparison.Ordinal);
        Assert.DoesNotContain("plain", detail.RawJson);
    }

    [Fact]
    public async Task AuditStampInterceptorRecordsRequestIpRequestIdAndNode()
    {
        var http = new DefaultHttpContext();
        http.TraceIdentifier = "req-42";
        http.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.44");
        var accessor = new HttpContextAccessor { HttpContext = http };
        var interceptor = new AuditStampInterceptor(accessor, Options.Create(new ClusterOptions { NodeId = "node-a" }));
        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseInMemoryDatabase($"mailserver-audit-stamp-{Guid.NewGuid():N}")
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new MailDbContext(options);
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = "admin",
            Action = "domain.create",
            Resource = "domain:example"
        });
        await db.SaveChangesAsync();

        var record = await db.AuditRecords.SingleAsync();
        Assert.Equal("192.0.2.44", record.RemoteAddress);
        Assert.Equal("req-42", record.RequestId);
        Assert.Equal("node-a", record.NodeId);
        Assert.Equal("2001:db8::8", AuditStampInterceptor.Normalize(IPAddress.Parse("2001:db8::8")));
    }

    [Fact]
    public async Task AuditReadPermissionIsSeparateFromDashboardRead()
    {
        var factory = CreateFactory();
        var adminId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.AdminUsers.Add(new AdminUser
            {
                UserName = "primary",
                PasswordHash = "hash"
            });
            db.AdminUsers.Add(new AdminUser
            {
                Id = adminId,
                UserName = "auditor",
                PasswordHash = "hash"
            });
            db.PermissionGrants.Add(new PermissionGrant
            {
                SubjectType = PermissionSubjectType.AdminUser,
                SubjectId = adminId,
                PermissionCode = PermissionCodes.DashboardRead,
                ResourceType = PermissionService.AllResources,
                ResourceKey = PermissionService.AllResources
            });
            await db.SaveChangesAsync();
        }

        var permissions = new PermissionService(factory);
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, adminId.ToString())],
            "test"));
        var catalog = permissions.GetCatalog().SelectMany(x => x.Permissions).Select(x => x.Code).ToArray();

        Assert.Contains(PermissionCodes.AuditRead, catalog);
        Assert.True(await permissions.HasPermissionAsync(user, PermissionCodes.DashboardRead, null, CancellationToken.None));
        Assert.False(await permissions.HasPermissionAsync(user, PermissionCodes.AuditRead, null, CancellationToken.None));
    }

    [Fact]
    public async Task PluginAuditWriterSkipsContinueAndRecordsSignificantDecision()
    {
        var factory = CreateFactory();
        var service = new AuditLogService(factory, Options.Create(new ClusterOptions { NodeId = "node-a" }));

        await service.RecordPluginDecisionAsync(
            new PluginAuditEvent("smtp", PluginDecisionKindName.Continue, null, "policy", "Policy", null, null, null, null, 1),
            CancellationToken.None);
        await service.RecordPluginDecisionAsync(
            new PluginAuditEvent("smtp", PluginDecisionKindName.Reject, "blocked", "policy", "Policy", null, "req-1", null, null, 7),
            CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var record = await db.AuditRecords.SingleAsync();
        Assert.Equal(AuditCategory.Plugin, record.Category);
        Assert.Equal(AuditStatus.Succeeded, record.Status);
        Assert.Equal("policy", record.PluginId);
        Assert.Equal("req-1", record.RequestId);
        Assert.DoesNotContain("Content", record.RawJson ?? string.Empty);
    }

    [Fact]
    public async Task ClusterInvitationIsSingleUseAndLimitedToFixedRoles()
    {
        var factory = CreateFactory();
        var now = new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero);
        var protectionRoot = Path.Combine(Path.GetTempPath(), $"mailserver-cluster-protection-{Guid.NewGuid():N}");
        var previousConfig = Environment.GetEnvironmentVariable(PortableConfiguration.EnvironmentVariable);
        try
        {
            Directory.CreateDirectory(protectionRoot);
            var configPath = Path.Combine(protectionRoot, "mailserver.json");
            await File.WriteAllTextAsync(configPath, "{}");
            Environment.SetEnvironmentVariable(PortableConfiguration.EnvironmentVariable, configPath);
            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(protectionRoot));
            var certificates = new ClusterCertificateService(factory, dataProtection, new FixedClock(now));
            var schemaFingerprint = new ReplicationSchemaFingerprintProvider(factory);
            var plugins = new ClusterPluginPackageService(Options.Create(new PluginOptions { Path = Path.Combine(protectionRoot, "plugins") }));
            var service = new ClusterManagementService(
                factory,
                Options.Create(new ClusterOptions { NodeId = "primary-a", NodeName = "Primary A" }),
                new FixedClock(now),
                new ClusterRuntimeState(false),
                new ClusterTransportRuntimeState(),
                new FixedOptionsMonitor<ClusterTransportOptions>(new ClusterTransportOptions()),
                certificates,
                schemaFingerprint,
                plugins,
                new FixedClusterHealth());

            var created = await service.CreateAsync(new CreateClusterRequest("prod", "tls://primary.example.test:2526"), "admin", CancellationToken.None);
            var primaryCertificatePath = Path.Combine(protectionRoot, "security-assets", "cluster", "node.crt");
            using (var primaryCertificate = X509Certificate2.CreateFromPem(
                await File.ReadAllTextAsync(primaryCertificatePath)))
            {
                Assert.Equal(
                    ["primary.example.test"],
                    primaryCertificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                        .Single().EnumerateDnsNames().ToArray());
            }

            string originalPrimaryThumbprint;
            using (var primaryCertificate = X509Certificate2.CreateFromPem(
                await File.ReadAllTextAsync(primaryCertificatePath)))
                originalPrimaryThumbprint = primaryCertificate.Thumbprint;
            await service.RepairLocalPrimaryTransportAsync("admin", CancellationToken.None);
            using (var repairedCertificate = X509Certificate2.CreateFromPem(
                await File.ReadAllTextAsync(primaryCertificatePath)))
            {
                Assert.NotEqual(originalPrimaryThumbprint, repairedCertificate.Thumbprint);
                Assert.Equal(
                    ["primary.example.test"],
                    repairedCertificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                        .Single().EnumerateDnsNames().ToArray());
            }

            var invitation = await service.CreateInvitationAsync(ClusterNodeRole.Standby, "admin", CancellationToken.None);
            using var standbyKey = RSA.Create(2048);
            var standbyRequest = new CertificateRequest("CN=primary-a", standbyKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var untrustedSan = new SubjectAlternativeNameBuilder();
            untrustedSan.AddDnsName("attacker.example.test");
            standbyRequest.CertificateExtensions.Add(untrustedSan.Build());
            var csr = standbyRequest.CreateSigningRequestPem();
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.JoinWithCertificateAsync(
                new ClusterJoinWireRequest(
                    invitation.Token!,
                    "standby-a",
                    "Standby A",
                    "tls://standby.example.test:2526",
                    csr,
                    new string('0', 64)),
                CancellationToken.None));
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Null((await db.ClusterJoinInvitations.SingleAsync(x => x.Id == invitation.Id)).UsedAt);
                Assert.False(await db.ClusterNodes.AnyAsync(x => x.Id == "standby-a"));
            }
            var joined = await service.JoinWithCertificateAsync(
                new ClusterJoinWireRequest(
                    invitation.Token!,
                    "standby-a",
                    "Standby A",
                    "tls://standby.example.test:2526",
                    csr,
                    await schemaFingerprint.GetAsync(CancellationToken.None)),
                CancellationToken.None);

            Assert.Equal("primary-a", created.PrimaryNodeId);
            Assert.Equal(ClusterNodeRole.Standby, joined.Result.Role);
            Assert.NotEmpty(joined.CaCertificatePem);
            Assert.NotEmpty(joined.NodeCertificatePem);
            using (var joinedCertificate = X509Certificate2.CreateFromPem(joined.NodeCertificatePem))
            {
                Assert.Equal("standby-a", joinedCertificate.GetNameInfo(X509NameType.SimpleName, false));
                Assert.Equal(
                    ["standby.example.test"],
                    joinedCertificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                        .Single().EnumerateDnsNames().ToArray());
            }
            await using (var db = await factory.CreateDbContextAsync())
                Assert.True(await db.ClusterNodeCertificates.AnyAsync(x => x.NodeId == "standby-a"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                service.JoinAsync(new ClusterJoinRequest(invitation.Token!, "standby-b", "Standby B", "tls://standby-b.example.test:2526"), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.CreateInvitationAsync(ClusterNodeRole.Standby, "admin", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PortableConfiguration.EnvironmentVariable, previousConfig);
            if (Directory.Exists(protectionRoot)) Directory.Delete(protectionRoot, true);
        }
    }

    [Theory]
    [InlineData("tls://192.0.2.10:2526", "192.0.2.10")]
    [InlineData("tls://[2001:db8::10]:2526", "2001:db8::10")]
    public async Task ClusterNodeCertificateUsesEndpointIpAddressSan(string endpoint, string expectedAddress)
    {
        var factory = CreateFactory();
        var protectionRoot = Path.Combine(Path.GetTempPath(), $"mailserver-cluster-cert-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(protectionRoot);
            var certificates = new ClusterCertificateService(
                factory,
                DataProtectionProvider.Create(new DirectoryInfo(protectionRoot)),
                new FixedClock(new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero)));
            using var key = RSA.Create(2048);
            var csr = new CertificateRequest("CN=node-a", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSigningRequestPem();

            var issued = await certificates.SignNodeCertificateAsync(
                "node-a", "Node A", endpoint, csr, "test", CancellationToken.None);

            using var certificate = X509Certificate2.CreateFromPem(issued.CertificatePem);
            var addresses = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                .Single().EnumerateIPAddresses().ToArray();
            Assert.Equal([IPAddress.Parse(expectedAddress)], addresses);
        }
        finally
        {
            if (Directory.Exists(protectionRoot)) Directory.Delete(protectionRoot, true);
        }
    }

    [Theory]
    [InlineData(SslPolicyErrors.None, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors, true)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable, false)]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch, false)]
    public void ClusterJoinBootstrapAcceptsOnlyCleanOrPrivateCaChainErrors(SslPolicyErrors errors, bool expected)
    {
        Assert.Equal(expected, ClusterTransportClient.IsBootstrapServerCertificateAccepted(errors));
    }

    [Fact]
    public void ClusterTrustedCaLoaderAcceptsCertificateOnlyPem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mailserver-cluster-ca-{Guid.NewGuid():N}.crt");
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Test Cluster CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var source = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllText(path, PemEncoding.WriteString("CERTIFICATE", source.RawData));

            using var loaded = ClusterTransportClient.LoadCertificateOnly(path);

            Assert.Equal(source.Thumbprint, loaded.Thumbprint);
            Assert.False(loaded.HasPrivateKey);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ReplicationSchemaFingerprintIsProviderIndependentAndDetectsChanges()
    {
        SqliteNative.Initialize();
        using var sqlite = new MailDbContext(new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        using var postgres = new MailDbContext(new DbContextOptionsBuilder<MailDbContext>()
            .UseNpgsql("Host=localhost;Database=mailserver;Username=test;Password=test").Options);
        using var mysql = new MailDbContext(new DbContextOptionsBuilder<MailDbContext>()
            .UseMySQL("Server=localhost;Database=mailserver;User=test;Password=test").Options);

        var sqliteFingerprint = ReplicationModelDescriptor.ComputeFingerprint(sqlite.Model);
        Assert.Equal(64, sqliteFingerprint.Length);
        Assert.Equal(sqliteFingerprint, ReplicationModelDescriptor.ComputeFingerprint(postgres.Model));
        Assert.Equal(sqliteFingerprint, ReplicationModelDescriptor.ComputeFingerprint(mysql.Model));
        Assert.NotEqual(
            ReplicationModelDescriptor.ComputeFingerprint(["entity|pk=Id|properties=Id:System.Int32:False"]),
            ReplicationModelDescriptor.ComputeFingerprint(["entity|pk=Id|properties=Id:System.Int64:False"]));
        Assert.Equal(2, ClusterBinaryProtocol.ProtocolVersion);
    }

    [Fact]
    public async Task ClusterBinaryProtocolUsesItsResolverForWitnessResponses()
    {
        var expected = new WitnessLeaseResponse(true, 9, new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero), "primary-a", 42);
        var requestId = Guid.NewGuid();
        await using var stream = new MemoryStream();
        await ClusterBinaryProtocol.WriteAsync(stream, ClusterMessageType.WitnessLeaseResponse, requestId, expected, CancellationToken.None);
        stream.Position = 0;

        var actual = await ClusterBinaryProtocol.ReadAsync<WitnessLeaseResponse>(stream, CancellationToken.None);

        Assert.Equal(ClusterMessageType.WitnessLeaseResponse, actual.Type);
        Assert.Equal(requestId, actual.RequestId);
        Assert.Equal(expected, actual.Payload);
    }

    [Fact]
    public async Task StandbyReplicationReplacesLocalScheduledTaskIdentityAtomically()
    {
        var factory = CreateFactory();
        var localId = Guid.NewGuid();
        var primaryId = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ScheduledTasks.Add(new ScheduledTask { Id = localId, Type = "cache.cleanup", Name = "local default", IsClusterTask = false });
            await db.SaveChangesAsync();
        }
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 15, 1, 0, 0, TimeSpan.Zero));
        var service = new ReplicationBackgroundService(
            factory,
            null!,
            Options.Create(new ClusterOptions { Enabled = true, Role = ClusterNodeRole.Standby, NodeId = "standby-a" }),
            new ClusterRuntimeState(true),
            null!,
            null!,
            null!,
            null!,
            clock,
            NullLogger<ReplicationBackgroundService>.Instance);
        var authoritative = new ScheduledTask
        {
            Id = primaryId,
            Type = "cache.cleanup",
            Name = "primary task",
            IsClusterTask = false,
            IntervalMinutes = 30
        };

        await service.ApplyEntryAsync(new ReplicationLog
        {
            Sequence = 17,
            BatchId = Guid.NewGuid(),
            EntityType = typeof(ScheduledTask).FullName!,
            EntityKey = primaryId.ToString(),
            Operation = ReplicationOperation.Upsert,
            PayloadJson = JsonSerializer.Serialize(authoritative)
        }, CancellationToken.None);

        await using var resultDb = await factory.CreateDbContextAsync();
        var task = await resultDb.ScheduledTasks.SingleAsync(x => x.Type == "cache.cleanup");
        Assert.Equal(primaryId, task.Id);
        Assert.Equal("primary task", task.Name);
        Assert.Equal(17, (await resultDb.ReplicationCursors.SingleAsync(x => x.NodeId == "standby-a")).LastAppliedSequence);
        Assert.True(await resultDb.ReplicationLogs.AnyAsync(x => x.Sequence == 17));
    }

    [Fact]
    public async Task WitnessTrustsOnlyConfiguredPrimaryUnderLocalClusterCa()
    {
        var factory = CreateFactory();
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-witness-trust-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var clock = new FixedClock(new DateTimeOffset(2026, 7, 15, 2, 0, 0, TimeSpan.Zero));
            var service = new ClusterCertificateService(
                factory,
                DataProtectionProvider.Create(new DirectoryInfo(root)),
                clock,
                new FixedOptionsMonitor<ClusterOptions>(new ClusterOptions { Role = ClusterNodeRole.Witness }));
            using var key = RSA.Create(2048);
            var issued = await service.SignNodeCertificateAsync(
                "primary-a",
                "Primary A",
                "tls://primary.example.test:2526",
                new CertificateRequest("CN=forged", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem(),
                "test",
                CancellationToken.None);
            var ca = await service.EnsureAuthorityAsync("test", CancellationToken.None);
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.ClusterNodeCertificates.RemoveRange(db.ClusterNodeCertificates);
                db.ClusterConfigurations.Add(new ClusterConfiguration
                {
                    Name = "production",
                    PrimaryNodeId = "primary-a",
                    CaCertificatePem = ca.CertificatePem,
                    ClusterCaThumbprint = ca.Thumbprint,
                    CreatedBy = "test"
                });
                await db.SaveChangesAsync();
            }
            using var certificate = X509Certificate2.CreateFromPem(issued.CertificatePem);

            Assert.True(await service.ValidatePeerAsync(certificate, "primary-a", CancellationToken.None));
            Assert.False(await service.ValidatePeerAsync(certificate, "another-primary", CancellationToken.None));

            await using (var db = await factory.CreateDbContextAsync())
            {
                db.ClusterCertificateRevocations.Add(new ClusterCertificateRevocation
                {
                    NodeId = "primary-a",
                    Thumbprint = certificate.Thumbprint,
                    Reason = "test"
                });
                await db.SaveChangesAsync();
            }
            Assert.False(await service.ValidatePeerAsync(certificate, "primary-a", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PrimaryKeepsLocalLeaseUntilWitnessFirstActivation()
    {
        var factory = CreateFactory();
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 15, 3, 0, 0, TimeSpan.Zero));
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ClusterConfigurations.Add(new ClusterConfiguration
            {
                Name = "production", PrimaryNodeId = "primary-a", CreatedBy = "test"
            });
            db.ClusterNodes.AddRange(
                new ClusterNode { Id = "primary-a", Name = "Primary A", Role = ClusterNodeRole.Primary, Status = ClusterNodeStatus.Healthy },
                new ClusterNode
                {
                    Id = "witness-a", Name = "Witness A", Role = ClusterNodeRole.Witness,
                    Status = ClusterNodeStatus.Synchronizing, Endpoint = "tls://127.0.0.1:1"
                });
            await db.SaveChangesAsync();
        }
        var options = new FixedOptionsMonitor<ClusterOptions>(new ClusterOptions
        {
            Enabled = true,
            NodeId = "primary-a",
            Role = ClusterNodeRole.Primary,
            Endpoint = "tls://primary.example.test:2526",
            LeaseSeconds = 15
        });
        var lease = new ClusterLeaseService(
            factory,
            options,
            clock,
            new ClusterTransportClient(Options.Create(new ClusterTransportOptions { ConnectTimeoutSeconds = 5 })),
            new ReplicationSchemaFingerprintProvider(factory),
            new ClusterRuntimeState(true),
            NullLogger<ClusterLeaseService>.Instance);

        Assert.True(await lease.TryAcquirePrimaryAsync(CancellationToken.None));
        await using var resultDb = await factory.CreateDbContextAsync();
        Assert.True(await resultDb.ClusterLeases.AnyAsync(x =>
            x.Name == "primary" && x.OwnerNodeId == "primary-a" && x.ExpiresAt > clock.UtcNow));
        Assert.Equal(ClusterNodeStatus.Synchronizing,
            (await resultDb.ClusterNodes.SingleAsync(x => x.Id == "witness-a")).Status);
    }

    [Fact]
    public async Task ClusterNodeRemovalAndClusterDeletionPreserveBusinessData()
    {
        var factory = CreateFactory();
        var now = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-cluster-delete-{Guid.NewGuid():N}");
        var previousConfig = Environment.GetEnvironmentVariable(PortableConfiguration.EnvironmentVariable);
        try
        {
            Directory.CreateDirectory(root);
            var configPath = Path.Combine(root, "mailserver.json");
            await File.WriteAllTextAsync(configPath, "{}");
            Environment.SetEnvironmentVariable(PortableConfiguration.EnvironmentVariable, configPath);
            var runtime = new ClusterRuntimeState(false);
            var transportRuntime = new ClusterTransportRuntimeState();
            var fingerprint = new ReplicationSchemaFingerprintProvider(factory);
            var certificates = new ClusterCertificateService(
                factory,
                DataProtectionProvider.Create(new DirectoryInfo(root)),
                new FixedClock(now));
            var service = new ClusterManagementService(
                factory,
                Options.Create(new ClusterOptions { NodeId = "primary-a", NodeName = "Primary A" }),
                new FixedClock(now),
                runtime,
                transportRuntime,
                new FixedOptionsMonitor<ClusterTransportOptions>(new ClusterTransportOptions()),
                certificates,
                fingerprint,
                new ClusterPluginPackageService(Options.Create(new PluginOptions { Path = Path.Combine(root, "plugins") })),
                new FixedClusterHealth());

            await service.CreateAsync(new CreateClusterRequest("production", "tls://primary.example.test:2526"), "admin", CancellationToken.None);
            string originalCaThumbprint;
            string certificatePath;
            await using (var db = await factory.CreateDbContextAsync())
                originalCaThumbprint = (await db.ClusterCertificateAuthorities.SingleAsync()).Thumbprint;
            var config = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
            certificatePath = config[ClusterTransportOptions.SectionName]!["CertificatePath"]!.GetValue<string>();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.RemoveNodeAsync("primary-a", "admin", CancellationToken.None));

            async Task JoinStandbyAsync()
            {
                var invitation = await service.CreateInvitationAsync(ClusterNodeRole.Standby, "admin", CancellationToken.None);
                using var key = RSA.Create(2048);
                var csr = new CertificateRequest("CN=standby-a", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                    .CreateSigningRequestPem();
                await service.JoinWithCertificateAsync(
                    new ClusterJoinWireRequest(
                        invitation.Token!, "standby-a", "Standby A", "tls://standby.example.test:2526", csr,
                        await fingerprint.GetAsync(CancellationToken.None)),
                    CancellationToken.None);
            }

            await JoinStandbyAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.ReplicationCursors.Add(new ReplicationCursor { NodeId = "standby-a", LastAppliedSequence = 12 });
                await db.SaveChangesAsync();
            }

            await service.RemoveNodeAsync("standby-a", "admin", CancellationToken.None);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.False(await db.ClusterNodes.AnyAsync(x => x.Id == "standby-a"));
                Assert.False(await db.ReplicationCursors.AnyAsync(x => x.NodeId == "standby-a"));
                Assert.False(await db.ClusterSnapshots.AnyAsync(x => x.NodeId == "standby-a"));
                Assert.True(await db.ClusterNodeCertificates.AnyAsync(x => x.NodeId == "standby-a" && x.Status == ClusterCertificateStatus.Revoked));
                Assert.True(await db.ClusterCertificateRevocations.AnyAsync(x => x.NodeId == "standby-a"));
                Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "cluster.node.remove"));
            }

            await JoinStandbyAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Domains.Add(new MailDomain { Name = "example.test" });
                await db.SaveChangesAsync();
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DeleteClusterAsync("wrong-name", "admin", CancellationToken.None));
            var childLocalService = new ClusterManagementService(
                factory,
                Options.Create(new ClusterOptions { NodeId = "standby-a", NodeName = "Standby A", Role = ClusterNodeRole.Standby }),
                new FixedClock(now),
                runtime,
                transportRuntime,
                new FixedOptionsMonitor<ClusterTransportOptions>(new ClusterTransportOptions()),
                certificates,
                fingerprint,
                new ClusterPluginPackageService(Options.Create(new PluginOptions { Path = Path.Combine(root, "plugins") })),
                new FixedClusterHealth(writable: false));
            await childLocalService.ForceLeaveAsync("production", "cli", CancellationToken.None);

            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.True(await db.Domains.AnyAsync(x => x.Name == "example.test"));
                Assert.Empty(await db.ClusterConfigurations.ToListAsync());
                Assert.Empty(await db.ClusterNodes.ToListAsync());
                Assert.Empty(await db.ClusterCertificateAuthorities.ToListAsync());
                Assert.Empty(await db.ClusterNodeCertificates.ToListAsync());
                Assert.Empty(await db.ReplicationLogs.ToListAsync());
                Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "cluster.force-leave"));
            }
            var disabledConfig = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
            Assert.False(disabledConfig[ClusterOptions.SectionName]!["Enabled"]!.GetValue<bool>());
            Assert.Equal(string.Empty, disabledConfig[ClusterOptions.SectionName]!["NodeId"]!.GetValue<string>());
            Assert.False(disabledConfig[ClusterTransportOptions.SectionName]!["Enabled"]!.GetValue<bool>());
            Assert.False(File.Exists(certificatePath));
            Assert.False(runtime.Enabled);
            Assert.True(transportRuntime.StopRequested);

            await service.CreateAsync(new CreateClusterRequest("replacement", "tls://primary.example.test:2526"), "admin", CancellationToken.None);
            Assert.True(runtime.Enabled);
            Assert.False(transportRuntime.StopRequested);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.NotEqual(originalCaThumbprint, (await db.ClusterCertificateAuthorities.SingleAsync()).Thumbprint);
                Assert.True(await db.Domains.AnyAsync(x => x.Name == "example.test"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(PortableConfiguration.EnvironmentVariable, previousConfig);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ForceRemovalLocksUntilFinalChildThenAutomaticallyRenewsLease()
    {
        var factory = CreateFactory();
        var now = new DateTimeOffset(2026, 7, 14, 11, 0, 0, TimeSpan.Zero);
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-cluster-recovery-{Guid.NewGuid():N}");
        var previousConfig = Environment.GetEnvironmentVariable(PortableConfiguration.EnvironmentVariable);
        try
        {
            Directory.CreateDirectory(root);
            var configPath = Path.Combine(root, "mailserver.json");
            await File.WriteAllTextAsync(configPath, "{}");
            Environment.SetEnvironmentVariable(PortableConfiguration.EnvironmentVariable, configPath);
            var runtime = new ClusterRuntimeState(false);
            var fingerprint = new ReplicationSchemaFingerprintProvider(factory);
            var service = new ClusterManagementService(
                factory,
                Options.Create(new ClusterOptions { NodeId = "primary-a", NodeName = "Primary A", LeaseSeconds = 15 }),
                new FixedClock(now),
                runtime,
                new ClusterTransportRuntimeState(),
                new FixedOptionsMonitor<ClusterTransportOptions>(new ClusterTransportOptions()),
                new ClusterCertificateService(factory, DataProtectionProvider.Create(new DirectoryInfo(root)), new FixedClock(now)),
                fingerprint,
                new ClusterPluginPackageService(Options.Create(new PluginOptions { Path = Path.Combine(root, "plugins") })),
                new FixedClusterHealth(writable: false));

            await service.CreateAsync(new CreateClusterRequest("production", "tls://primary.example.test:2526"), "admin", CancellationToken.None);
            async Task JoinAsync(ClusterNodeRole role, string id, string name, string endpoint)
            {
                var invitation = await service.CreateInvitationAsync(role, "admin", CancellationToken.None);
                using var key = RSA.Create(2048);
                var csr = new CertificateRequest($"CN={id}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                    .CreateSigningRequestPem();
                await service.JoinWithCertificateAsync(new ClusterJoinWireRequest(
                    invitation.Token!, id, name, endpoint, csr,
                    await fingerprint.GetAsync(CancellationToken.None)), CancellationToken.None);
            }

            await JoinAsync(ClusterNodeRole.Witness, "witness-a", "Witness A", "tls://witness.example.test:2526");
            await JoinAsync(ClusterNodeRole.Standby, "standby-a", "Standby A", "tls://standby.example.test:2526");

            await service.RemoveNodeAsync("witness-a", "admin", CancellationToken.None);

            Assert.True(runtime.RecoveryRequired);
            var lockedConfig = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
            Assert.True(lockedConfig[ClusterOptions.SectionName]!["RecoveryRequired"]!.GetValue<bool>());
            Assert.Equal("standby-a", await service.ResolveNodeIdAsync("Standby A", CancellationToken.None));
            await service.RemoveNodeAsync("standby-a", "admin", CancellationToken.None);

            Assert.False(runtime.RecoveryRequired);
            var recoveredConfig = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!.AsObject();
            Assert.False(recoveredConfig[ClusterOptions.SectionName]!["RecoveryRequired"]!.GetValue<bool>());
            Assert.Equal(string.Empty, recoveredConfig[ClusterOptions.SectionName]!["WitnessEndpoint"]!.GetValue<string>());
            await using var db = await factory.CreateDbContextAsync();
            var lease = await db.ClusterLeases.SingleAsync(x => x.Name == "primary");
            Assert.Equal("primary-a", lease.OwnerNodeId);
            Assert.Equal(2, lease.Term);
            Assert.Equal(now.AddSeconds(15), lease.ExpiresAt);
            Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "cluster.node.force-remove"));
            Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "cluster.primary.auto-recover"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(PortableConfiguration.EnvironmentVariable, previousConfig);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ClusterDisconnectClassifierHandlesCrossPlatformResetShapes()
    {
        Assert.True(ClusterConnectionErrors.IsExpectedDisconnect(new EndOfStreamException()));
        Assert.True(ClusterConnectionErrors.IsExpectedDisconnect(
            new IOException("reset", new SocketException((int)SocketError.ConnectionReset))));
        Assert.True(ClusterConnectionErrors.IsExpectedDisconnect(
            new SocketException((int)SocketError.ConnectionAborted)));
        Assert.False(ClusterConnectionErrors.IsExpectedDisconnect(new InvalidDataException("bad frame")));
        Assert.False(ClusterConnectionErrors.IsExpectedDisconnect(new AuthenticationException("bad certificate")));
    }

    [Fact]
    public async Task ClusterRecoveryLockOverridesAnOtherwiseValidPrimaryLease()
    {
        var factory = CreateFactory();
        var now = new DateTimeOffset(2026, 7, 14, 11, 30, 0, TimeSpan.Zero);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ClusterLeases.Add(new ClusterLease
            {
                Name = "primary",
                OwnerNodeId = "primary-a",
                Term = 7,
                ExpiresAt = now.AddMinutes(1)
            });
            await db.SaveChangesAsync();
        }
        var runtime = new ClusterRuntimeState(enabled: true, recoveryRequired: true);
        var lease = new ClusterLeaseService(
            factory,
            new FixedOptionsMonitor<ClusterOptions>(new ClusterOptions { NodeId = "primary-a", Role = ClusterNodeRole.Primary }),
            new FixedClock(now),
            new ClusterTransportClient(Options.Create(new ClusterTransportOptions())),
            new ReplicationSchemaFingerprintProvider(factory),
            runtime,
            NullLogger<ClusterLeaseService>.Instance);

        Assert.False(await lease.IsWritableAsync(CancellationToken.None));
        Assert.False(await lease.TryAcquirePrimaryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClusterMaintenanceSkipsWriteTasksWithoutPrimaryLease()
    {
        var scheduler = new CountingTaskScheduler();
        var service = new MaintenanceBackgroundService(
            scheduler,
            new FixedLeaseService(acquired: false),
            Options.Create(new MaintenanceOptions()),
            NullLogger<MaintenanceBackgroundService>.Instance);

        await service.RunIterationAsync(CancellationToken.None);

        Assert.Equal(0, scheduler.RunDueCount);
    }

    [Fact]
    public async Task ClusterRuntimeLeavesClusterModeAfterExternalConfigurationDisable()
    {
        var factory = CreateFactory();
        var runtime = new ClusterRuntimeState(enabled: true, recoveryRequired: true);
        var options = new MutableOptionsMonitor<ClusterOptions>(new ClusterOptions { Enabled = true, RecoveryRequired = true });
        var bootstrap = new ClusterRuntimeBootstrap(
            runtime,
            factory,
            options,
            new FixedClock(DateTimeOffset.UtcNow),
            NullLogger<ClusterRuntimeBootstrap>.Instance);
        await bootstrap.StartAsync(CancellationToken.None);

        options.Set(new ClusterOptions { Enabled = false, RecoveryRequired = false });
        for (var attempt = 0; attempt < 20 && runtime.Enabled; attempt++)
            await Task.Delay(25);

        Assert.False(runtime.Enabled);
        Assert.False(runtime.RecoveryRequired);
        await bootstrap.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ClusterRuntimeAcceptsExternallyCompletedFinalNodeRecovery()
    {
        var factory = CreateFactory();
        var now = new DateTimeOffset(2026, 7, 14, 12, 30, 0, TimeSpan.Zero);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ClusterConfigurations.Add(new ClusterConfiguration
            {
                Name = "production",
                PrimaryNodeId = "primary-a",
                CreatedBy = "admin"
            });
            db.ClusterNodes.Add(new ClusterNode
            {
                Id = "primary-a",
                Name = "Primary A",
                Role = ClusterNodeRole.Primary
            });
            db.ClusterLeases.Add(new ClusterLease
            {
                Name = "primary",
                OwnerNodeId = "primary-a",
                Term = 2,
                ExpiresAt = now.AddMinutes(1)
            });
            db.AuditRecords.Add(new AuditRecord
            {
                Actor = "cli",
                Action = "cluster.primary.auto-recover",
                Resource = "cluster:production",
                Category = AuditCategory.Node,
                Status = AuditStatus.Warning
            });
            await db.SaveChangesAsync();
        }
        var runtime = new ClusterRuntimeState(enabled: true, recoveryRequired: true);
        var options = new MutableOptionsMonitor<ClusterOptions>(new ClusterOptions
        {
            Enabled = true,
            RecoveryRequired = true,
            NodeId = "primary-a"
        });
        var bootstrap = new ClusterRuntimeBootstrap(
            runtime,
            factory,
            options,
            new FixedClock(now),
            NullLogger<ClusterRuntimeBootstrap>.Instance);
        await bootstrap.StartAsync(CancellationToken.None);

        options.Set(new ClusterOptions { Enabled = true, RecoveryRequired = false, NodeId = "primary-a" });
        for (var attempt = 0; attempt < 20 && runtime.RecoveryRequired; attempt++)
            await Task.Delay(25);

        Assert.False(runtime.RecoveryRequired);
        Assert.True(runtime.Enabled);
        await bootstrap.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WitnessLeaseRejectsStandbyBehindSafeSequence()
    {
        var factory = CreateFactory();
        var now = new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero);
        var witness = new WitnessLeaseService(factory, new FixedClock(now));

        var primary = await witness.AcquireAsync(new WitnessLeaseRequest("primary-a", 15, AppliedSequence: 12, SafeSequence: 12, Role: ClusterNodeRole.Primary), CancellationToken.None);
        var lagging = await witness.AcquireAsync(new WitnessLeaseRequest("standby-a", 15, AppliedSequence: 7, SafeSequence: 7, Role: ClusterNodeRole.Standby), CancellationToken.None);

        Assert.True(primary.Granted);
        Assert.False(lagging.Granted);
        Assert.Equal(12, lagging.RequiredSafeSequence);
    }

    [Fact]
    public async Task ClusterSyncThrottlePausesLargeTransfersWhenQueuesAreBusy()
    {
        var factory = CreateFactory();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var message = new StoredMessage { ContentKey = new string('c', 64), EnvelopeFrom = "sender@example.test", Size = 10 };
            db.Messages.Add(message);
            for (var i = 1; i <= 600; i++)
            {
                db.ReplicationLogs.Add(new ReplicationLog
                {
                    Sequence = i,
                    BatchId = Guid.NewGuid(),
                    EntityType = typeof(StoredMessage).FullName!,
                    EntityKey = i.ToString(),
                    Operation = ReplicationOperation.Upsert
                });
            }
            for (var i = 0; i < 250; i++)
            {
                db.DeliveryJobs.Add(new DeliveryJob
                {
                    Message = message,
                    Sender = "sender@example.test",
                    Recipient = $"user{i}@remote.test",
                    Status = DeliveryStatus.Pending
                });
            }
            db.ScheduledTasks.Add(new ScheduledTask
            {
                Type = "mailbox.folder-delete",
                Name = "文件夹邮件迁移",
                Status = ScheduledTaskStatus.Running
            });
            await db.SaveChangesAsync();
        }

        var throttle = new ClusterSyncThrottle(
            factory,
            Options.Create(new ClusterSyncThrottleOptions
            {
                BusyMetadataBatchSize = 20,
                NormalBatchSize = 200,
                BusyHoldSeconds = 60
            }),
            new FixedClock(DateTimeOffset.UtcNow));

        var decision = await throttle.EvaluateAsync(0, CancellationToken.None);

        Assert.True(decision.IsBusy);
        Assert.False(decision.AllowContentTransfer);
        Assert.Equal(20, decision.BatchSize);
        Assert.Contains("delivery backlog", decision.Reason);
        Assert.Contains("running tasks", decision.Reason);
    }

    [Fact]
    public async Task ReplicationCursorStoresLastSyncDetails()
    {
        var factory = CreateFactory();
        var now = new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ReplicationCursors.Add(new ReplicationCursor
            {
                NodeId = "standby-a",
                LastAppliedSequence = 42,
                LastAttemptAt = now.AddSeconds(-3),
                LastSuccessAt = now,
                LastSuccessSequence = 42,
                LastBatchCount = 7,
                LastContentDownloads = 2,
                LastDurationMilliseconds = 1234,
                SyncStatus = ClusterSyncStatus.Throttled,
                PauseReason = "delivery backlog 60",
                LastDetailJson = "{\"applied\":7}"
            });
            await db.SaveChangesAsync();
        }

        await using var verify = await factory.CreateDbContextAsync();
        var cursor = await verify.ReplicationCursors.SingleAsync();
        Assert.Equal(ClusterSyncStatus.Throttled, cursor.SyncStatus);
        Assert.Equal(now, cursor.LastSuccessAt);
        Assert.Equal(7, cursor.LastBatchCount);
        Assert.Equal(2, cursor.LastContentDownloads);
        Assert.Contains("delivery backlog", cursor.PauseReason);
    }

    [Fact]
    public void ClusterHostValidatorRequiresWitnessRoleAndMtlsFiles()
    {
        Assert.True(ClusterHostValidator.IsNotJoinedYet(
            new ClusterOptions(),
            new ClusterTransportOptions()));
        Assert.False(ClusterHostValidator.IsNotJoinedYet(
            new ClusterOptions { Enabled = true, Role = ClusterNodeRole.Witness, NodeId = "witness-a" },
            new ClusterTransportOptions
            {
                Enabled = true,
                AdvertiseEndpoint = "tls://witness.example.test:2526",
                CertificatePath = "missing-node.crt",
                PrivateKeyPath = "missing-node.key",
                TrustedCaPath = "missing-ca.crt"
            }));

        var invalid = ClusterHostValidator.ValidateWitnessHost(
            new ClusterOptions { Enabled = true, Role = ClusterNodeRole.Primary, NodeId = "primary-a" },
            new ClusterTransportOptions
            {
                Enabled = true,
                AdvertiseEndpoint = "https://witness.example.test:2526",
                Port = 2526,
                CertificatePath = "node.crt",
                PrivateKeyPath = "node.key",
                TrustedCaPath = "ca.crt"
            },
            _ => false);

        Assert.Contains(invalid, x => x.Contains("Cluster:Role", StringComparison.Ordinal));
        Assert.Contains(invalid, x => x.Contains("tls://", StringComparison.Ordinal));
        Assert.Contains(invalid, x => x.Contains("CertificatePath", StringComparison.Ordinal));

        var valid = ClusterHostValidator.ValidateWitnessHost(
            new ClusterOptions { Enabled = true, Role = ClusterNodeRole.Witness, NodeId = "witness-a" },
            new ClusterTransportOptions
            {
                Enabled = true,
                AdvertiseEndpoint = "tls://witness.example.test:2526",
                Port = 2526,
                CertificatePath = "node.crt",
                PrivateKeyPath = "node.key",
                TrustedCaPath = "ca.crt"
            },
            _ => true);

        Assert.Empty(valid);
    }

    private static IDbContextFactory<MailDbContext> CreateFactory()
    {
        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseInMemoryDatabase($"mailserver-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new PooledDbContextFactory<MailDbContext>(options);
    }

    private static SystemSettingsService CreateSystemSettingsService(IDbContextFactory<MailDbContext> factory) =>
        new(
            factory,
            Options.Create(new DeliveryOptions
            {
                BatchSize = 20,
                LeaseSeconds = 120,
                MaximumAttempts = 12,
                PollIntervalSeconds = 5
            }),
            Options.Create(new MailboxDefaultsOptions
            {
                Folders = ["INBOX", "Sent", "Drafts", "Trash", "Junk", "Archives"]
            }),
            Options.Create(new CertificatesOptions()));

    private static AdminApplicationService CreateAdministration(
        IDbContextFactory<MailDbContext> factory,
        SystemSettingsService settings,
        ICredentialService credentials,
        IMessageContentStore store)
    {
        var configuration = new ConfigurationBuilder().Build();
        var queue = new DeliveryQueue(
            factory,
            settings,
            Options.Create(new DatabaseOptions { Provider = DatabaseProvider.Sqlite }),
            NullLogger<DeliveryQueue>.Instance);
        var certificates = new DomainCertificateProvider(
            factory,
            settings,
            NullLogger<DomainCertificateProvider>.Instance);
        return new AdminApplicationService(
            factory,
            credentials,
            queue,
            new DnsDiagnosticService(new LookupClient()),
            configuration,
            settings,
            store,
            certificates,
            new PermissionService(factory));
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class FixedClusterHealth(bool writable = true) : IClusterHealthService
    {
        public Task<ClusterHealth> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClusterHealth(true, writable, "primary-a", ClusterNodeRole.Primary, writable, 0, 0));
    }

    private sealed class FixedOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public FixedOptionsMonitor(T value) => _value = value;

        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class MutableOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        private Action<T, string?>? _listener;
        public T CurrentValue { get; private set; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener)
        {
            _listener += listener;
            return new CallbackDisposable(() => _listener -= listener);
        }
        public void Set(T updated)
        {
            CurrentValue = updated;
            _listener?.Invoke(updated, Options.DefaultName);
        }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    private sealed class FixedLeaseService(bool acquired) : IClusterLeaseService
    {
        public Task<bool> IsWritableAsync(CancellationToken cancellationToken) => Task.FromResult(acquired);
        public Task<bool> TryAcquirePrimaryAsync(CancellationToken cancellationToken) => Task.FromResult(acquired);
    }

    private sealed class CountingTaskScheduler : ITaskScheduler
    {
        public int RunDueCount { get; private set; }
        public Task EnsureDefaultsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunDueAsync(CancellationToken cancellationToken)
        {
            RunDueCount++;
            return Task.CompletedTask;
        }
        public Task<Guid> RunNowAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
    }
}
