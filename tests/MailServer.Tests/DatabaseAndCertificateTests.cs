using System.Security.Cryptography;
using System.Security.Authentication;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;
using Xunit;

namespace MailServer.Tests;

public sealed class DatabaseAndCertificateTests
{
    [Fact]
    public async Task SqliteMigrationsAndQueueLeaseWorkEndToEnd()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-sqlite-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                    ["Storage:RootPath"] = Path.Combine(root, "messages")
                })
                .Build();
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddMailServerInfrastructure(configuration);
            await using var services = collection.BuildServiceProvider();
            var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                var message = new StoredMessage
                {
                    ContentKey = new string('a', 64),
                    EnvelopeFrom = "sender@example.com",
                    Size = 42
                };
                db.Messages.Add(message);
                db.DeliveryJobs.Add(new DeliveryJob
                {
                    Message = message,
                    Sender = "sender@example.com",
                    Recipient = "recipient@example.net",
                    RouteMode = OutboundMode.Relay
                });
                await db.SaveChangesAsync();
            }

            var queue = services.GetRequiredService<IDeliveryQueue>();
            var leased = await queue.LeaseAsync("test", 1, TimeSpan.FromMinutes(1), CancellationToken.None);

            Assert.Single(leased);
            Assert.Equal(DeliveryStatus.Processing, leased[0].Status);
            await services.VerifyMailDatabaseAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PemLoaderAcceptsCertificateChainFileAndPkcs8Key()
    {
        var root = CreateCertificateFiles(encrypted: false, mismatched: false, expired: false);
        try
        {
            using var loaded = PemCertificateLoader.Load(
                new PemCertificateOptions
                {
                    CertificatePath = Path.Combine(root, "chain.pem"),
                    PrivateKeyPath = Path.Combine(root, "key.pem")
                },
                "Test");

            Assert.True(loaded.Certificate.HasPrivateKey);
            Assert.Contains("mail.example.test", loaded.Certificate.Subject);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PemLoaderAcceptsEncryptedPrivateKey()
    {
        var root = CreateCertificateFiles(encrypted: true, mismatched: false, expired: false);
        try
        {
            using var loaded = PemCertificateLoader.Load(
                new PemCertificateOptions
                {
                    CertificatePath = Path.Combine(root, "chain.pem"),
                    PrivateKeyPath = Path.Combine(root, "key.pem"),
                    PrivateKeyPassword = "test-password"
                },
                "Test");

            Assert.True(loaded.Certificate.HasPrivateKey);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PemLoaderRejectsMismatchedPrivateKey()
    {
        var root = CreateCertificateFiles(encrypted: false, mismatched: true, expired: false);
        try
        {
            Assert.ThrowsAny<CryptographicException>(() =>
                PemCertificateLoader.Load(
                    new PemCertificateOptions
                    {
                        CertificatePath = Path.Combine(root, "chain.pem"),
                        PrivateKeyPath = Path.Combine(root, "key.pem")
                    },
                    "Test"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PemLoaderRejectsExpiredCertificate()
    {
        var root = CreateCertificateFiles(encrypted: false, mismatched: false, expired: true);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                PemCertificateLoader.Load(
                    new PemCertificateOptions
                    {
                        CertificatePath = Path.Combine(root, "chain.pem"),
                        PrivateKeyPath = Path.Combine(root, "key.pem")
                    },
                    "Test"));
            Assert.Contains("validity period", exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DomainCertificateUploadPersistsMetadataAndSupportsSniSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-domain-cert-tests-{Guid.NewGuid():N}");
        var certificateRoot = CreateCertificateFiles(encrypted: true, mismatched: false, expired: false);
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                    ["Storage:RootPath"] = Path.Combine(root, "messages"),
                    ["SecurityAssets:RootPath"] = Path.Combine(root, "security-assets"),
                    ["Certificates:DefaultMailHostName"] = "mail.example.test"
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMailServerInfrastructure(configuration);
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
            Guid domainId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                var domain = new MailDomain { Name = "example.test", MailHostName = "mail.example.test" };
                db.Domains.Add(domain);
                await db.SaveChangesAsync();
                domainId = domain.Id;
            }

            var service = provider.GetRequiredService<DomainCertificateService>();
            var record = await service.SaveUploadAsync(
                domainId,
                await File.ReadAllBytesAsync(Path.Combine(certificateRoot, "chain.pem")),
                await File.ReadAllBytesAsync(Path.Combine(certificateRoot, "key.pem")),
                "test-password",
                "test",
                CancellationToken.None);

            Assert.Equal(CertificateSource.ManagedUpload, record.Source);
            Assert.True(File.Exists(record.CertificatePath));
            Assert.True(File.Exists(record.PrivateKeyPath));
            Assert.Contains("mail.example.test", record.SubjectAlternativeNames);

            var selector = provider.GetRequiredService<DomainCertificateProvider>();
            var options = await selector.SelectOptionsAsync(
                new SslClientHelloInfo("mail.example.test", SslProtocols.Tls13),
                CancellationToken.None);
            Assert.NotNull(options.ServerCertificateContext);
            var fallbackOptions = await selector.SelectOptionsAsync(
                new SslClientHelloInfo(string.Empty, SslProtocols.Tls13),
                CancellationToken.None);
            Assert.NotNull(fallbackOptions.ServerCertificateContext);
            var settings = provider.GetRequiredService<SystemSettingsService>();
            await settings.SaveCertificateSelectionSettingsAsync(
                new CertificateSelectionSettingsView(null, false, [], null),
                CancellationToken.None);
            selector.Invalidate();
            await Assert.ThrowsAsync<AuthenticationException>(async () =>
                await selector.SelectOptionsAsync(
                    new SslClientHelloInfo(string.Empty, SslProtocols.Tls13),
                    CancellationToken.None));
            var saved = await settings.SaveCertificateSelectionSettingsAsync(
                new CertificateSelectionSettingsView("mail.example.test", false, [], null),
                CancellationToken.None);
            selector.Invalidate();
            Assert.Equal("mail.example.test", saved.EffectiveDefaultMailHostName);
            var databaseDefaultOptions = await selector.SelectOptionsAsync(
                new SslClientHelloInfo(string.Empty, SslProtocols.Tls13),
                CancellationToken.None);
            Assert.NotNull(databaseDefaultOptions.ServerCertificateContext);
            await Assert.ThrowsAsync<AuthenticationException>(async () =>
                await selector.SelectOptionsAsync(
                    new SslClientHelloInfo("unknown.example.test", SslProtocols.Tls13),
                    CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
            Directory.Delete(certificateRoot, recursive: true);
        }
    }

    [Fact]
    public void DnsGeneratorBuildsSpfDmarcAndDkimRecords()
    {
        var domain = new MailDomain
        {
            Name = "example.test",
            MailHostName = "mail.example.test",
            DkimSelector = "s202606",
            DkimPublicKey = "AQID"
        };
        var settings = new DomainDnsSettings
        {
            SpfIncludeMx = true,
            SpfIncludes = "spf.relay.test",
            SpfAllPolicy = "-all",
            DmarcPolicy = "reject",
            DmarcAggregateReportUris = "mailto:dmarc@example.test"
        };

        var records = DomainDnsService.GenerateRecords(domain, settings);

        Assert.Contains(records, x => x.Name == "example.test" && x.Value == "v=spf1 mx include:spf.relay.test -all");
        Assert.Contains(records, x => x.Name == "_dmarc.example.test" && x.Value.Contains("p=reject", StringComparison.Ordinal));
        Assert.Contains(records, x => x.Name == "s202606._domainkey.example.test" && x.Value.Contains("p=AQID", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GeneratedDkimKeySignsOutboundMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-dkim-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "mailserver.db")}",
                    ["Storage:RootPath"] = Path.Combine(root, "messages"),
                    ["SecurityAssets:RootPath"] = Path.Combine(root, "security-assets")
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMailServerInfrastructure(configuration);
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<MailDbContext>>();
            Guid domainId;
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                var domain = new MailDomain { Name = "example.test", MailHostName = "mail.example.test" };
                db.Domains.Add(domain);
                await db.SaveChangesAsync();
                domainId = domain.Id;
            }

            var dns = provider.GetRequiredService<DomainDnsService>();
            await dns.GenerateDkimAsync(domainId, "s202606", "test", CancellationToken.None);
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse("sender@example.test"));
            message.To.Add(MailboxAddress.Parse("recipient@example.net"));
            message.Subject = "DKIM test";
            message.Body = new TextPart("plain") { Text = "hello" };
            await using var input = new MemoryStream();
            await message.WriteToAsync(input);
            input.Position = 0;

            var signed = await OutboundMessage.LoadAndSignAsync(
                input,
                "sender@example.test",
                factory,
                CancellationToken.None);

            Assert.NotNull(signed.Headers[HeaderId.DkimSignature]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateCertificateFiles(bool encrypted, bool mismatched, bool expired)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mailserver-pem-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var issuerKey = RSA.Create(2048);
        var issuerRequest = new CertificateRequest(
            "CN=MailServer Test CA",
            issuerKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        issuerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        issuerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            critical: true));
        issuerRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(issuerRequest.PublicKey, false));
        using var issuer = issuerRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-30),
            DateTimeOffset.UtcNow.AddDays(30));
        using var certificateKey = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=mail.example.test",
            certificateKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            critical: false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("mail.example.test");
        request.CertificateExtensions.Add(san.Build());
        var notBefore = expired ? DateTimeOffset.UtcNow.AddDays(-10) : DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(7);
        using var publicCertificate = request.Create(
            issuer,
            notBefore,
            notAfter,
            RandomNumberGenerator.GetBytes(16));
        using var certificate = publicCertificate.CopyWithPrivateKey(certificateKey);
        File.WriteAllText(
            Path.Combine(root, "chain.pem"),
            certificate.ExportCertificatePem() + Environment.NewLine + issuer.ExportCertificatePem());
        using var exportedKey = mismatched ? RSA.Create(2048) : null;
        var key = exportedKey ?? certificateKey;
        File.WriteAllText(
            Path.Combine(root, "key.pem"),
            encrypted
                ? key.ExportEncryptedPkcs8PrivateKeyPem(
                    "test-password",
                    new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000))
                : key.ExportPkcs8PrivateKeyPem());
        return root;
    }
}
