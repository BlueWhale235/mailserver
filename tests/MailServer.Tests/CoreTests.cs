using System.Text;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
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
using System.Text.Json;
using MailServer.PluginSdk;
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
                ContractVersion = "1.0",
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
                ContractVersion = "1.0",
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
                ContractVersion = "1.0",
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
                ContractVersion = "1.0",
                EntryAssembly = "unused.dll"
            },
            null,
            false));
        catalog.Add(new LoadedPlugin(
            new PluginManifest
            {
                Id = "third-party-policy",
                Version = "1",
                ContractVersion = "1.0",
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
                OutboundMode.DirectMx,
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
                    Recipient = "bob@example.net",
                    RouteMode = OutboundMode.DirectMx
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
                    RouteMode = OutboundMode.DirectMx,
                    Status = DeliveryStatus.Deferred
                };
                var secondJob = new DeliveryJob
                {
                    Message = secondMessage,
                    Sender = "bob@second.test",
                    Recipient = "user@example.org",
                    RouteDomain = secondDomain,
                    RouteMode = OutboundMode.Relay
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
                    SmtpHostName = "mx.example.com",
                    OutboundMode = OutboundMode.DirectMx
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
            Assert.Equal(OutboundMode.DirectMx, job.RouteMode);
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
                "From: sender@example.net\r\nTo: alice@example.com\r\nSubject: local\r\n\r\nhello"));

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

        Assert.Equal(["INBOX", "Sent", "Drafts"], saved.DefaultFolders);
        Assert.Equal(saved.DefaultFolders, reloaded.DefaultFolders);
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
            Assert.Equal(["INBOX", "Archive/Test", "Sent"], folders);
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
            Assert.Equal(["INBOX", "Drafts", "Sent"], folders);
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

            var store = new FileMessageContentStore(
                Options.Create(new StorageOptions { RootPath = root, MaximumMessageBytes = 4096 }),
                factory);
            var service = new MailboxService(factory, store, settings);

            await service.EnsureDefaultFoldersAsync(accountId, CancellationToken.None);
            await service.EnsureDefaultFoldersAsync(accountId, CancellationToken.None);

            await using var verification = await factory.CreateDbContextAsync();
            var folders = await verification.Mailboxes
                .OrderBy(x => x.Name == "INBOX" ? 0 : 1)
                .ThenBy(x => x.Name)
                .Select(x => x.Name)
                .ToListAsync();
            Assert.Equal(["INBOX", "Drafts", "Sent"], folders);
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
}
