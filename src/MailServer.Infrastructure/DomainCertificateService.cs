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
    DomainCertificateProvider certificateProvider,
    ConfigurationSnapshotCache? cache = null)
{
    public const int MaximumCertificateBytes = 1024 * 1024;
    public const int MaximumPrivateKeyBytes = 256 * 1024;
    private readonly string _securityRoot = Path.GetFullPath(securityAssets.Value.RootPath);

    public async Task<DomainCertificateInspection> InspectUploadAsync(
        long domainId,
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
        long domainId,
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
        long domainId,
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
                var certificateTemporary = certificatePath + $".{System.Guid.NewGuid()}.tmp";
                var keyTemporary = privateKeyPath + $".{System.Guid.NewGuid()}.tmp";
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
        long domainId,
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

    public async Task DeleteAsync(long domainId, string actor, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var certificate = await db.DomainCertificates.SingleOrDefaultAsync(x => x.DomainId == domainId, cancellationToken);
        if (certificate is null) return;
        db.DomainCertificates.Remove(certificate);
        AddAudit(db, actor, "domain.certificate.delete", $"domain:{domainId}");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Settings, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        cache?.ObserveCommittedVersion(ConfigurationScopes.Settings, version);
        if (certificate.Source == CertificateSource.ManagedUpload)
        {
            SecurityAssetFiles.DeleteManagedFile(_securityRoot, certificate.CertificatePath);
            SecurityAssetFiles.DeleteManagedFile(_securityRoot, certificate.PrivateKeyPath);
        }
        certificateProvider.Invalidate();
    }

    private async Task<DomainCertificate> SaveRecordAsync(
        long domainId,
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
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Settings, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        cache?.ObserveCommittedVersion(ConfigurationScopes.Settings, version);
        if (oldSource == CertificateSource.ManagedUpload)
        {
            SecurityAssetFiles.DeleteManagedFile(_securityRoot, oldCertificatePath, certificatePath);
            SecurityAssetFiles.DeleteManagedFile(_securityRoot, oldPrivateKeyPath, privateKeyPath);
        }
        certificateProvider.Invalidate();
        return record;
    }

    private async Task<MailDomain> GetDomainAsync(long domainId, CancellationToken cancellationToken)
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
        var directory = Path.Combine(Path.GetTempPath(), $"mailserver-certificate-{System.Guid.NewGuid()}");
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
    ILogger<DomainCertificateProvider> logger,
    ConfigurationSnapshotCache? cache = null) : Microsoft.Extensions.Hosting.BackgroundService
{
    private sealed record CertificateRecord(string HostName, string CertificatePath, string PrivateKeyPath);
    private sealed record CertificateRecords(IReadOnlyList<CertificateRecord> Items);
    private sealed record Loaded(string Stamp, LoadedPemCertificate Certificate);
    private sealed record Snapshot(IReadOnlyDictionary<string, Loaded> Certificates,
        CertificateSelectionSettingsView Settings, long SettingsVersion, long LocalVersion);
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly List<(LoadedPemCertificate Certificate, DateTimeOffset RetiredAt)> _retired = [];
    private Snapshot? _snapshot;
    private long _invalidation;
    private long _appliedInvalidation = -1;

    public void Invalidate() => Interlocked.Increment(ref _invalidation);

    public async Task<bool> HasAnyCertificateAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        return Volatile.Read(ref _snapshot)!.Certificates.Count > 0;
    }

    public async ValueTask<SslServerAuthenticationOptions> SelectOptionsAsync(SslClientHelloInfo hello, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        var snapshot = Volatile.Read(ref _snapshot)!;
        var host = NormalizeHostName(hello.ServerName);
        Loaded? selected = null;
        if (host.Length > 0)
        {
            if (!snapshot.Certificates.TryGetValue(host, out selected))
                throw new AuthenticationException($"No TLS certificate is configured for SNI host '{host}'.");
        }
        else
        {
            var fallback = NormalizeHostName(snapshot.Settings.DefaultMailHostName);
            if (fallback.Length > 0) snapshot.Certificates.TryGetValue(fallback, out selected);
            if (selected is null && snapshot.Settings.AllowSingleCertificateFallback && snapshot.Certificates.Count == 1)
                selected = snapshot.Certificates.Values.Single();
        }
        if (selected is null) throw new AuthenticationException("No matching mail TLS certificate is configured.");
        return new SslServerAuthenticationOptions
        {
            ServerCertificateContext = selected.Certificate.SslContext,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            ClientCertificateRequired = false
        };
    }

    private Task EnsureLoadedAsync(CancellationToken token)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot is not null && _appliedInvalidation == Interlocked.Read(ref _invalidation)
            && snapshot.SettingsVersion == (cache?.Versions.Settings ?? 0)
            && snapshot.LocalVersion == systemSettings.CertificateSettingsVersion
            ? Task.CompletedTask : RefreshAsync(token);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Unable to refresh mail TLS certificates."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        await _reloadLock.WaitAsync(token);
        try
        {
            var invalidation = Interlocked.Read(ref _invalidation);
            var version = cache?.Versions.Settings ?? 0;
            async Task<(CertificateRecords Value, long Size)> LoadAsync(CancellationToken ct)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var records = await db.DomainCertificates.AsNoTracking()
                    .Where(x => x.Domain.IsActive && x.Domain.MailHostName != null)
                    .Select(x => new CertificateRecord(x.Domain.MailHostName!, x.CertificatePath, x.PrivateKeyPath)).ToListAsync(ct);
                return (new CertificateRecords(records), 256L + records.Sum(x => 128L + 2L * (x.HostName.Length + x.CertificatePath.Length + x.PrivateKeyPath.Length)));
            }
            var records = cache is null || invalidation != _appliedInvalidation
                ? (await LoadAsync(token)).Value
                : await cache.GetAsync($"certificates:{cache.Versions.Routing}", ConfigurationScopes.Settings, LoadAsync, token);
            var settings = await systemSettings.GetCertificateSelectionSettingsAsync(token);
            var previous = Volatile.Read(ref _snapshot);
            var loaded = new Dictionary<string, Loaded>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records.Items)
            {
                var host = NormalizeHostName(record.HostName);
                try
                {
                    var certificateFile = new FileInfo(record.CertificatePath);
                    var keyFile = new FileInfo(record.PrivateKeyPath);
                    var stamp = $"{certificateFile.FullName}:{certificateFile.LastWriteTimeUtc.Ticks}:{certificateFile.Length}|{keyFile.FullName}:{keyFile.LastWriteTimeUtc.Ticks}:{keyFile.Length}";
                    if (previous is not null && previous.Certificates.TryGetValue(host, out var old) && old.Stamp == stamp
                        && old.Certificate.Certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow)
                        loaded[host] = old;
                    else loaded[host] = new Loaded(stamp, PemCertificateLoader.Load(new PemCertificateOptions
                    {
                        CertificatePath = record.CertificatePath, PrivateKeyPath = record.PrivateKeyPath
                    }, $"Domain {record.HostName}"));
                }
                catch (Exception error) { logger.LogError(error, "Unable to load TLS certificate for {HostName}.", host); }
            }
            var now = DateTimeOffset.UtcNow;
            if (previous is not null)
                foreach (var old in previous.Certificates.Values)
                    if (!loaded.Values.Contains(old)) _retired.Add((old.Certificate, now));
            Volatile.Write(ref _snapshot, new Snapshot(loaded, settings, version, systemSettings.CertificateSettingsVersion));
            _appliedInvalidation = invalidation;
            // Allow handshakes already using the previous context to finish (configured maximum: 120s).
            foreach (var old in _retired.Where(x => now - x.RetiredAt > TimeSpan.FromMinutes(10)).ToArray())
            {
                old.Certificate.Dispose();
                _retired.Remove(old);
            }
        }
        finally { _reloadLock.Release(); }
    }

    private static string NormalizeHostName(string? name) => name?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;
    public override void Dispose()
    {
        base.Dispose();
        _reloadLock.Wait();
        try
        {
            if (_snapshot is { } current) foreach (var value in current.Certificates.Values) value.Certificate.Dispose();
            foreach (var old in _retired) old.Certificate.Dispose();
        }
        finally { _reloadLock.Release(); }
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
