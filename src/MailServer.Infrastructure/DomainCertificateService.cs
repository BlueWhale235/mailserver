using System.Formats.Asn1;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed record DomainCertificateInspection(
    string Subject,
    string Issuer,
    IReadOnlyList<string> SubjectAlternativeNames,
    string Thumbprint,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool HasMatchingPrivateKey,
    bool SupportsServerAuthentication,
    bool CoversMailHostName);

public sealed class DomainCertificateService(
    IDbContextFactory<MailDbContext> dbFactory,
    IOptions<SecurityAssetsOptions> securityAssets,
    DomainCertificateProvider certificateProvider)
{
    public const int MaximumCertificateBytes = 1024 * 1024;
    public const int MaximumPrivateKeyBytes = 256 * 1024;
    private readonly string _securityRoot = Path.GetFullPath(securityAssets.Value.RootPath);

    public async Task<DomainCertificateInspection> InspectUploadAsync(
        Guid domainId,
        byte[] certificatePem,
        byte[] privateKeyPem,
        string? password,
        CancellationToken cancellationToken)
    {
        ValidateUploadSizes(certificatePem, privateKeyPem);
        var domain = await GetDomainAsync(domainId, cancellationToken);
        return await WithTemporaryFilesAsync(
            certificatePem,
            privateKeyPem,
            password,
            (certificatePath, privateKeyPath) =>
                Task.FromResult(Inspect(certificatePath, privateKeyPath, password, domain.MailHostName!)));
    }

    public async Task<DomainCertificateInspection> InspectExternalAsync(
        Guid domainId,
        string certificatePath,
        string privateKeyPath,
        CancellationToken cancellationToken)
    {
        var domain = await GetDomainAsync(domainId, cancellationToken);
        return Inspect(
            RequireAbsolutePath(certificatePath, nameof(certificatePath)),
            RequireAbsolutePath(privateKeyPath, nameof(privateKeyPath)),
            password: null,
            domain.MailHostName!);
    }

    public async Task<DomainCertificate> SaveUploadAsync(
        Guid domainId,
        byte[] certificatePem,
        byte[] privateKeyPem,
        string? password,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateUploadSizes(certificatePem, privateKeyPem);
        var domain = await GetDomainAsync(domainId, cancellationToken);
        var directory = SecurityAssetFiles.GetDomainDirectory(_securityRoot, domainId);
        Directory.CreateDirectory(directory);
        SecurityAssetFiles.RestrictDirectory(directory);

        return await WithTemporaryFilesAsync(
            certificatePem,
            privateKeyPem,
            password,
            async (incomingCertificatePath, incomingPrivateKeyPath) =>
            {
                using var loaded = PemCertificateLoader.Load(
                    new PemCertificateOptions
                    {
                        CertificatePath = incomingCertificatePath,
                        PrivateKeyPath = incomingPrivateKeyPath,
                        PrivateKeyPassword = password
                    },
                    $"Domain {domain.Name}");
                var inspection = InspectLoaded(loaded, domain.MailHostName!);
                EnsureUsable(inspection, domain.MailHostName!);

                var certificatePath = Path.Combine(directory, "tls-chain.pem");
                var privateKeyPath = Path.Combine(directory, "tls-key.pem");
                var certificateTemporary = certificatePath + $".{Guid.NewGuid():N}.tmp";
                var keyTemporary = privateKeyPath + $".{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(
                    certificateTemporary,
                    ExportChain(loaded),
                    cancellationToken);
                await File.WriteAllTextAsync(
                    keyTemporary,
                    ExportPrivateKey(loaded.Certificate),
                    cancellationToken);
                SecurityAssetFiles.RestrictFile(certificateTemporary);
                SecurityAssetFiles.RestrictFile(keyTemporary);
                File.Move(certificateTemporary, certificatePath, overwrite: true);
                File.Move(keyTemporary, privateKeyPath, overwrite: true);
                SecurityAssetFiles.RestrictFile(certificatePath);
                SecurityAssetFiles.RestrictFile(privateKeyPath);

                return await SaveRecordAsync(
                    domainId,
                    CertificateSource.ManagedUpload,
                    certificatePath,
                    privateKeyPath,
                    inspection,
                    actor,
                    cancellationToken);
            });
    }

    public async Task<DomainCertificate> BindExternalAsync(
        Guid domainId,
        string certificatePath,
        string privateKeyPath,
        string actor,
        CancellationToken cancellationToken)
    {
        var domain = await GetDomainAsync(domainId, cancellationToken);
        var normalizedCertificatePath = RequireAbsolutePath(certificatePath, nameof(certificatePath));
        var normalizedPrivateKeyPath = RequireAbsolutePath(privateKeyPath, nameof(privateKeyPath));
        var inspection = Inspect(
            normalizedCertificatePath,
            normalizedPrivateKeyPath,
            password: null,
            domain.MailHostName!);
        EnsureUsable(inspection, domain.MailHostName!);
        return await SaveRecordAsync(
            domainId,
            CertificateSource.ExternalPath,
            normalizedCertificatePath,
            normalizedPrivateKeyPath,
            inspection,
            actor,
            cancellationToken);
    }

    public async Task DeleteAsync(Guid domainId, string actor, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var certificate = await db.DomainCertificates.SingleOrDefaultAsync(x => x.DomainId == domainId, cancellationToken);
        if (certificate is null) return;
        db.DomainCertificates.Remove(certificate);
        AddAudit(db, actor, "domain.certificate.delete", $"domain:{domainId}");
        await db.SaveChangesAsync(cancellationToken);
        if (certificate.Source == CertificateSource.ManagedUpload)
        {
            SecurityAssetFiles.DeleteManagedFile(_securityRoot, certificate.CertificatePath);
            SecurityAssetFiles.DeleteManagedFile(_securityRoot, certificate.PrivateKeyPath);
        }
        certificateProvider.Invalidate();
    }

    private async Task<DomainCertificate> SaveRecordAsync(
        Guid domainId,
        CertificateSource source,
        string certificatePath,
        string privateKeyPath,
        DomainCertificateInspection inspection,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.DomainCertificates.SingleOrDefaultAsync(x => x.DomainId == domainId, cancellationToken);
        var oldCertificatePath = existing?.CertificatePath;
        var oldPrivateKeyPath = existing?.PrivateKeyPath;
        var oldSource = existing?.Source;
        var record = existing ?? new DomainCertificate { DomainId = domainId };
        record.Source = source;
        record.CertificatePath = certificatePath;
        record.PrivateKeyPath = privateKeyPath;
        record.Subject = inspection.Subject;
        record.Issuer = inspection.Issuer;
        record.SubjectAlternativeNames = string.Join('\n', inspection.SubjectAlternativeNames);
        record.Thumbprint = inspection.Thumbprint;
        record.NotBefore = inspection.NotBefore;
        record.NotAfter = inspection.NotAfter;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        if (existing is null) db.DomainCertificates.Add(record);
        AddAudit(db, actor, "domain.certificate.update", $"domain:{domainId}");
        await db.SaveChangesAsync(cancellationToken);
        if (oldSource == CertificateSource.ManagedUpload)
        {
            SecurityAssetFiles.DeleteManagedFile(_securityRoot, oldCertificatePath, certificatePath);
            SecurityAssetFiles.DeleteManagedFile(_securityRoot, oldPrivateKeyPath, privateKeyPath);
        }
        certificateProvider.Invalidate();
        return record;
    }

    private async Task<MailDomain> GetDomainAsync(Guid domainId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == domainId, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        if (string.IsNullOrWhiteSpace(domain.MailHostName))
            throw new InvalidOperationException("Configure MailHostName before adding a TLS certificate.");
        return domain;
    }

    private static DomainCertificateInspection Inspect(
        string certificatePath,
        string privateKeyPath,
        string? password,
        string mailHostName)
    {
        using var loaded = PemCertificateLoader.Load(
            new PemCertificateOptions
            {
                CertificatePath = certificatePath,
                PrivateKeyPath = privateKeyPath,
                PrivateKeyPassword = password
            },
            $"Domain {mailHostName}");
        return InspectLoaded(loaded, mailHostName);
    }

    private static DomainCertificateInspection InspectLoaded(LoadedPemCertificate loaded, string mailHostName)
    {
        var names = CertificateNames.ReadDnsNames(loaded.Certificate);
        var supportsServerAuthentication = SupportsServerAuthentication(loaded.Certificate);
        return new DomainCertificateInspection(
            loaded.Certificate.Subject,
            loaded.Certificate.Issuer,
            names,
            loaded.Certificate.Thumbprint,
            loaded.Certificate.NotBefore,
            loaded.Certificate.NotAfter,
            loaded.Certificate.HasPrivateKey,
            supportsServerAuthentication,
            names.Any(name => CertificateNames.Matches(name, mailHostName)));
    }

    private static bool SupportsServerAuthentication(X509Certificate2 certificate)
    {
        var extensions = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        return extensions.Length == 0
               || extensions.SelectMany(x => x.EnhancedKeyUsages.Cast<Oid>())
                   .Any(x => x.Value == "1.3.6.1.5.5.7.3.1");
    }

    private static void EnsureUsable(DomainCertificateInspection inspection, string mailHostName)
    {
        if (!inspection.HasMatchingPrivateKey)
            throw new InvalidOperationException("The certificate does not contain a matching private key.");
        if (!inspection.SupportsServerAuthentication)
            throw new InvalidOperationException("The certificate is not valid for TLS server authentication.");
        if (!inspection.CoversMailHostName)
            throw new InvalidOperationException($"The certificate SAN does not cover {mailHostName}.");
    }

    private static string ExportChain(LoadedPemCertificate loaded)
    {
        var values = new List<string> { loaded.Certificate.ExportCertificatePem() };
        values.AddRange(loaded.Intermediates.Cast<X509Certificate2>().Select(x => x.ExportCertificatePem()));
        return string.Join(Environment.NewLine, values);
    }

    private static string ExportPrivateKey(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is not null) return rsa.ExportPkcs8PrivateKeyPem();
        using var ecdsa = certificate.GetECDsaPrivateKey();
        if (ecdsa is not null) return ecdsa.ExportPkcs8PrivateKeyPem();
        throw new InvalidOperationException("Only RSA and ECDSA TLS private keys are supported.");
    }

    private static void ValidateUploadSizes(byte[] certificatePem, byte[] privateKeyPem)
    {
        if (certificatePem.Length is 0 or > MaximumCertificateBytes)
            throw new InvalidDataException($"Certificate chain must be between 1 byte and {MaximumCertificateBytes} bytes.");
        if (privateKeyPem.Length is 0 or > MaximumPrivateKeyBytes)
            throw new InvalidDataException($"Private key must be between 1 byte and {MaximumPrivateKeyBytes} bytes.");
    }

    private static string RequireAbsolutePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute server path is required.", parameterName);
        return Path.GetFullPath(path);
    }

    private static async Task<T> WithTemporaryFilesAsync<T>(
        byte[] certificatePem,
        byte[] privateKeyPem,
        string? password,
        Func<string, string, Task<T>> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mailserver-certificate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var certificatePath = Path.Combine(directory, "chain.pem");
        var privateKeyPath = Path.Combine(directory, "key.pem");
        try
        {
            await File.WriteAllBytesAsync(certificatePath, certificatePem);
            await File.WriteAllBytesAsync(privateKeyPath, privateKeyPem);
            return await action(certificatePath, privateKeyPath);
        }
        finally
        {
            _ = password;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AddAudit(MailDbContext db, string actor, string action, string resource) =>
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = action, Resource = resource });
}

public sealed class DomainCertificateProvider(
    IDbContextFactory<MailDbContext> dbFactory,
    SystemSettingsService systemSettings,
    ILogger<DomainCertificateProvider> logger) : IDisposable
{
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly List<LoadedPemCertificate> _retired = [];
    private IReadOnlyDictionary<string, LoadedPemCertificate> _certificates =
        new Dictionary<string, LoadedPemCertificate>(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private volatile bool _invalidated = true;

    public void Invalidate() => _invalidated = true;

    public async Task<bool> HasAnyCertificateAsync(CancellationToken cancellationToken)
    {
        await ReloadIfNeededAsync(cancellationToken);
        return _certificates.Count > 0;
    }

    public async ValueTask<SslServerAuthenticationOptions> SelectOptionsAsync(
        SslClientHelloInfo hello,
        CancellationToken cancellationToken)
    {
        await ReloadIfNeededAsync(cancellationToken);
        var settings = await systemSettings.GetCertificateSelectionSettingsAsync(cancellationToken);
        var certificate = SelectCertificate(
            hello.ServerName,
            settings.DefaultMailHostName,
            settings.AllowSingleCertificateFallback);
        return new SslServerAuthenticationOptions
        {
            ServerCertificateContext = certificate.SslContext,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false
        };
    }

    private LoadedPemCertificate SelectCertificate(
        string? serverName,
        string? defaultMailHostName,
        bool allowSingleCertificateFallback)
    {
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            var host = NormalizeHostName(serverName);
            if (_certificates.TryGetValue(host, out var certificate))
            {
                return certificate;
            }

            throw new AuthenticationException($"No TLS certificate is configured for SNI host '{host}'.");
        }

        var defaultHost = NormalizeHostName(defaultMailHostName);
        if (!string.IsNullOrWhiteSpace(defaultHost)
            && _certificates.TryGetValue(defaultHost, out var defaultCertificate))
        {
            logger.LogDebug(
                "TLS client did not provide SNI; using default mail certificate for {HostName}.",
                defaultHost);
            return defaultCertificate;
        }

        if (allowSingleCertificateFallback && _certificates.Count == 1)
        {
            var only = _certificates.Single();
            logger.LogDebug(
                "TLS client did not provide SNI; using the only configured mail certificate for {HostName}.",
                only.Key);
            return only.Value;
        }

        throw new AuthenticationException(
            _certificates.Count == 0
                ? "No mail TLS certificate is configured."
                : "TLS SNI is required because no matching default mail host certificate is configured.");
    }

    private static string NormalizeHostName(string? hostName) =>
        string.IsNullOrWhiteSpace(hostName)
            ? string.Empty
            : hostName.Trim().TrimEnd('.').ToLowerInvariant();

    private async Task ReloadIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!_invalidated && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromSeconds(5)) return;
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            if (!_invalidated && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromSeconds(5)) return;
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var records = await db.DomainCertificates.AsNoTracking()
                .Where(x => x.Domain.IsActive && x.Domain.MailHostName != null)
                .Select(x => new
                {
                    HostName = x.Domain.MailHostName!,
                    x.CertificatePath,
                    x.PrivateKeyPath
                })
                .ToListAsync(cancellationToken);
            var loaded = new Dictionary<string, LoadedPemCertificate>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                try
                {
                    loaded[NormalizeHostName(record.HostName)] = PemCertificateLoader.Load(
                        new PemCertificateOptions
                        {
                            CertificatePath = record.CertificatePath,
                            PrivateKeyPath = record.PrivateKeyPath
                        },
                        $"Domain {record.HostName}");
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Unable to load TLS certificate for {HostName}.", record.HostName);
                }
            }
            foreach (var certificate in _retired) certificate.Dispose();
            _retired.Clear();
            _retired.AddRange(_certificates.Values);
            _certificates = loaded;
            _loadedAt = DateTimeOffset.UtcNow;
            _invalidated = false;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    public void Dispose()
    {
        foreach (var certificate in _certificates.Values) certificate.Dispose();
        foreach (var certificate in _retired) certificate.Dispose();
        _reloadLock.Dispose();
    }
}

internal static class CertificateNames
{
    public static IReadOnlyList<string> ReadDnsNames(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions["2.5.29.17"];
        if (extension is null)
        {
            var commonName = certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false);
            return string.IsNullOrWhiteSpace(commonName) ? [] : [commonName.ToLowerInvariant()];
        }

        var names = new List<string>();
        var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var sequence = reader.ReadSequence();
        while (sequence.HasData)
        {
            var tag = sequence.PeekTag();
            if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 2)
            {
                names.Add(sequence.ReadCharacterString(
                    UniversalTagNumber.IA5String,
                    new Asn1Tag(TagClass.ContextSpecific, 2)).ToLowerInvariant());
            }
            else
            {
                sequence.ReadEncodedValue();
            }
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool Matches(string pattern, string hostName)
    {
        var normalizedPattern = pattern.Trim().TrimEnd('.').ToLowerInvariant();
        var normalizedHost = hostName.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalizedPattern == normalizedHost) return true;
        if (!normalizedPattern.StartsWith("*.", StringComparison.Ordinal)) return false;
        var suffix = normalizedPattern[1..];
        return normalizedHost.EndsWith(suffix, StringComparison.Ordinal)
               && normalizedHost.Count(x => x == '.') == suffix.Count(x => x == '.');
    }
}
