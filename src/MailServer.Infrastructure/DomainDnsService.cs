using System.Security.Cryptography;
using System.Text.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed record DomainDnsRecord(string Name, string Type, string Value, string ZoneFileValue);

public sealed record DomainDnsConfiguration(
    DomainDnsSettings Settings,
    IReadOnlyList<DomainDnsRecord> Records);

public sealed record DomainDnsValidationItem(
    string Kind,
    string Name,
    string Expected,
    IReadOnlyList<string> Actual,
    string Status);

public sealed record DomainDnsValidation(
    string Domain,
    IReadOnlyList<DomainDnsValidationItem> Items,
    IReadOnlyList<string> Warnings);

public sealed record DomainDnsCheckView(
    Guid DomainId,
    DomainDnsCheckStatus Status,
    DomainDnsValidation? Validation,
    string? LastError,
    DateTimeOffset? CheckedAt);

public sealed class DomainDnsService(
    IDbContextFactory<MailDbContext> dbFactory,
    DnsDiagnosticService diagnostics,
    IOptions<SecurityAssetsOptions> securityAssets)
{
    private readonly string _securityRoot = Path.GetFullPath(securityAssets.Value.RootPath);

    public async Task<DomainDnsConfiguration> GetAsync(Guid domainId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.AsNoTracking()
            .Include(x => x.DnsSettings)
            .SingleOrDefaultAsync(x => x.Id == domainId, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        var settings = domain.DnsSettings ?? CreateDefaults(domain.Id);
        return new DomainDnsConfiguration(settings, GenerateRecords(domain, settings));
    }

    public async Task<DomainDnsConfiguration> SaveAsync(
        Guid domainId,
        DomainDnsSettings input,
        string actor,
        CancellationToken cancellationToken)
    {
        Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.Include(x => x.DnsSettings)
            .SingleOrDefaultAsync(x => x.Id == domainId, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        var settings = domain.DnsSettings ?? CreateDefaults(domain.Id);
        settings.SpfIncludeMx = input.SpfIncludeMx;
        settings.SpfIncludeA = input.SpfIncludeA;
        settings.SpfIpv4 = NormalizeList(input.SpfIpv4);
        settings.SpfIpv6 = NormalizeList(input.SpfIpv6);
        settings.SpfIncludes = NormalizeList(input.SpfIncludes);
        settings.SpfCustomMechanisms = NormalizeList(input.SpfCustomMechanisms);
        settings.SpfAllPolicy = input.SpfAllPolicy.Trim().ToLowerInvariant();
        settings.DmarcPolicy = input.DmarcPolicy.Trim().ToLowerInvariant();
        settings.DmarcSubdomainPolicy = NullIfWhiteSpace(input.DmarcSubdomainPolicy)?.ToLowerInvariant();
        settings.DmarcPercentage = input.DmarcPercentage;
        settings.DmarcAggregateReportUris = NormalizeList(input.DmarcAggregateReportUris);
        settings.DmarcForensicReportUris = NormalizeList(input.DmarcForensicReportUris);
        settings.DmarcDkimAlignment = input.DmarcDkimAlignment.Trim().ToLowerInvariant();
        settings.DmarcSpfAlignment = input.DmarcSpfAlignment.Trim().ToLowerInvariant();
        settings.DmarcFailureOptions = string.IsNullOrWhiteSpace(input.DmarcFailureOptions)
            ? "0" : input.DmarcFailureOptions.Trim();
        settings.MtaStsMode = input.MtaStsMode.Trim().ToLowerInvariant();
        settings.MtaStsPolicyId = input.MtaStsMode == "none"
            && (input.MtaStsPolicyId.Length is < 1 or > 32
                || !input.MtaStsPolicyId.All(char.IsAsciiLetterOrDigit))
            ? "1" : input.MtaStsPolicyId.Trim();
        settings.MtaStsMaxAgeSeconds = input.MtaStsMode == "none"
            && input.MtaStsMaxAgeSeconds is < 0 or > 31_557_600
            ? 604_800 : input.MtaStsMaxAgeSeconds;
        settings.MtaStsMxPatterns = NormalizeList(input.MtaStsMxPatterns);
        settings.TlsReportUris = NormalizeList(input.TlsReportUris);
        settings.DaneEnabled = input.DaneEnabled;
        settings.DaneTlsaRecords = NormalizeList(input.DaneTlsaRecords);
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        if (domain.DnsSettings is null) db.DomainDnsSettings.Add(settings);
        AddAudit(db, actor, "domain.dns.update", $"domain:{domainId}");
        await db.SaveChangesAsync(cancellationToken);
        return new DomainDnsConfiguration(settings, GenerateRecords(domain, settings));
    }

    public async Task<string> GenerateDkimAsync(
        Guid domainId,
        string? selector,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalizedSelector = string.IsNullOrWhiteSpace(selector)
            ? $"s{DateTimeOffset.UtcNow:yyyyMM}"
            : selector.Trim().ToLowerInvariant();
        if (!IsValidSelector(normalizedSelector))
            throw new FormatException("DKIM selector must contain only letters, digits, and hyphens and be at most 63 characters.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Id == domainId, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        var directory = SecurityAssetFiles.GetDomainDirectory(_securityRoot, domain.Id);
        Directory.CreateDirectory(directory);
        SecurityAssetFiles.RestrictDirectory(directory);
        var privateKeyPath = Path.Combine(directory, $"dkim-{normalizedSelector}.pem");
        var temporaryPath = privateKeyPath + $".{Guid.NewGuid():N}.tmp";
        using var rsa = RSA.Create(2048);
        await File.WriteAllTextAsync(temporaryPath, rsa.ExportPkcs8PrivateKeyPem(), cancellationToken);
        SecurityAssetFiles.RestrictFile(temporaryPath);
        File.Move(temporaryPath, privateKeyPath, overwrite: true);
        SecurityAssetFiles.RestrictFile(privateKeyPath);

        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var oldPath = domain.DkimPrivateKeyPath;
        domain.DkimSelector = normalizedSelector;
        domain.DkimPrivateKeyPath = privateKeyPath;
        domain.DkimPublicKey = publicKey;
        AddAudit(db, actor, "domain.dkim.generate", $"domain:{domainId}");
        await db.SaveChangesAsync(cancellationToken);
        SecurityAssetFiles.DeleteManagedFile(_securityRoot, oldPath, privateKeyPath);
        return publicKey;
    }

    public async Task<DomainDnsValidation> ValidatePublishedAsync(
        Guid domainId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domain = await db.Domains.AsNoTracking()
            .Include(x => x.DnsSettings)
            .SingleOrDefaultAsync(x => x.Id == domainId, cancellationToken)
            ?? throw new KeyNotFoundException("Domain not found.");
        var settings = domain.DnsSettings ?? CreateDefaults(domain.Id);
        var expected = GenerateRecords(domain, settings);
        var actual = await diagnostics.InspectAsync(domain.Name, domain.DkimSelector, cancellationToken);
        var items = new List<DomainDnsValidationItem>();
        var expectedMx = domain.MailHostName ?? $"mail.{domain.Name}";
        items.Add(new DomainDnsValidationItem(
            "MX",
            domain.Name,
            expectedMx,
            actual.Mx,
            actual.Mx.Count == 0
                ? "missing"
                : actual.Mx.Any(value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
                    ?.Equals(expectedMx, StringComparison.OrdinalIgnoreCase) == true)
                    ? "match"
                    : "conflict"));
        items.AddRange(expected.Select(record =>
        {
            IReadOnlyList<string> values = record.Name.StartsWith("_dmarc.", StringComparison.OrdinalIgnoreCase)
                ? actual.Dmarc
                : record.Name.Contains("._domainkey.", StringComparison.OrdinalIgnoreCase)
                    ? actual.Dkim
                    : record.Name.StartsWith("_mta-sts.", StringComparison.OrdinalIgnoreCase)
                        ? actual.MtaSts
                        : record.Name.StartsWith("_smtp._tls.", StringComparison.OrdinalIgnoreCase)
                            ? actual.TlsRpt
                            : record.Type == "TLSA"
                                ? actual.Tlsa.Select(x => string.Join(' ', x.Split(' ').Skip(1))).ToArray()
                                : actual.Spf;
            var status = values.Count == 0
                ? "missing"
                : values.Any(x => NormalizeDnsValue(x) == NormalizeDnsValue(record.Value))
                    ? "match"
                    : "conflict";
            return new DomainDnsValidationItem(record.Type, record.Name, record.Value, values, status);
        }));
        return new DomainDnsValidation(domain.Name, items, actual.Warnings);
    }

    public async Task<DomainDnsCheckView> GetCachedCheckAsync(
        Guid domainId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Domains.AnyAsync(x => x.Id == domainId, cancellationToken))
            throw new KeyNotFoundException("Domain not found.");
        var check = await db.DomainDnsChecks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.DomainId == domainId, cancellationToken);
        return check is null
            ? new DomainDnsCheckView(domainId, DomainDnsCheckStatus.PendingConfiguration, null, null, null)
            : ToView(check);
    }

    public async Task<DomainDnsCheckView> CheckAndStoreAsync(
        Guid domainId,
        CancellationToken cancellationToken)
    {
        await SetCheckingAsync(domainId, cancellationToken);
        try
        {
            var validation = await ValidatePublishedAsync(domainId, cancellationToken);
            var status = DetermineCheckStatus(validation);
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var check = await db.DomainDnsChecks.SingleAsync(x => x.DomainId == domainId, cancellationToken);
            check.Status = status;
            check.DetailsJson = JsonSerializer.Serialize(validation);
            check.LastError = null;
            check.CheckedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return ToView(check);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            var check = await db.DomainDnsChecks.SingleAsync(x => x.DomainId == domainId, CancellationToken.None);
            check.Status = DomainDnsCheckStatus.Unhealthy;
            check.LastError = exception.Message;
            check.CheckedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            return ToView(check);
        }
    }

    public async Task<IReadOnlyList<DomainDnsCheckView>> CheckManyAsync(
        IReadOnlyCollection<Guid> domainIds,
        CancellationToken cancellationToken)
    {
        var ids = domainIds.Distinct().ToArray();
        if (ids.Length is < 1 or > 100) throw new InvalidOperationException("Select between 1 and 100 domains.");
        using var concurrency = new SemaphoreSlim(4, 4);
        var tasks = ids.Select(async id =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try { return await CheckAndStoreAsync(id, cancellationToken); }
            finally { concurrency.Release(); }
        });
        return await Task.WhenAll(tasks);
    }

    private async Task SetCheckingAsync(Guid domainId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Domains.AnyAsync(x => x.Id == domainId, cancellationToken))
            throw new KeyNotFoundException("Domain not found.");
        var check = await db.DomainDnsChecks.SingleOrDefaultAsync(x => x.DomainId == domainId, cancellationToken);
        if (check is null)
        {
            check = new DomainDnsCheck { DomainId = domainId };
            db.DomainDnsChecks.Add(check);
        }
        check.Status = DomainDnsCheckStatus.Checking;
        check.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static DomainDnsCheckView ToView(DomainDnsCheck check)
    {
        DomainDnsValidation? validation = null;
        if (!string.IsNullOrWhiteSpace(check.DetailsJson))
        {
            try { validation = JsonSerializer.Deserialize<DomainDnsValidation>(check.DetailsJson); }
            catch (JsonException) { }
        }
        return new DomainDnsCheckView(check.DomainId, check.Status, validation, check.LastError, check.CheckedAt);
    }

    internal static DomainDnsCheckStatus DetermineCheckStatus(DomainDnsValidation validation) =>
        validation.Items.All(x => string.Equals(x.Status, "match", StringComparison.OrdinalIgnoreCase))
            ? DomainDnsCheckStatus.Healthy
            : DomainDnsCheckStatus.Unhealthy;

    public static IReadOnlyList<DomainDnsRecord> GenerateRecords(MailDomain domain, DomainDnsSettings settings)
    {
        var spf = new List<string> { "v=spf1" };
        if (settings.SpfIncludeMx) spf.Add("mx");
        if (settings.SpfIncludeA) spf.Add("a");
        spf.AddRange(SplitList(settings.SpfIpv4).Select(x => $"ip4:{x}"));
        spf.AddRange(SplitList(settings.SpfIpv6).Select(x => $"ip6:{x}"));
        spf.AddRange(SplitList(settings.SpfIncludes).Select(x => $"include:{x}"));
        spf.AddRange(SplitList(settings.SpfCustomMechanisms));
        spf.Add(settings.SpfAllPolicy);

        var dmarc = new List<string> { "v=DMARC1", $"p={settings.DmarcPolicy}" };
        if (!string.IsNullOrWhiteSpace(settings.DmarcSubdomainPolicy))
            dmarc.Add($"sp={settings.DmarcSubdomainPolicy}");
        dmarc.Add($"pct={settings.DmarcPercentage}");
        if (!string.IsNullOrWhiteSpace(settings.DmarcAggregateReportUris))
            dmarc.Add($"rua={string.Join(',', SplitList(settings.DmarcAggregateReportUris))}");
        if (!string.IsNullOrWhiteSpace(settings.DmarcForensicReportUris))
            dmarc.Add($"ruf={string.Join(',', SplitList(settings.DmarcForensicReportUris))}");
        dmarc.Add($"adkim={settings.DmarcDkimAlignment}");
        dmarc.Add($"aspf={settings.DmarcSpfAlignment}");
        dmarc.Add($"fo={settings.DmarcFailureOptions}");

        var result = new List<DomainDnsRecord>
        {
            CreateTxt(domain.Name, string.Join(' ', spf)),
            CreateTxt($"_dmarc.{domain.Name}", string.Join("; ", dmarc))
        };
        if (!string.IsNullOrWhiteSpace(domain.DkimSelector) && !string.IsNullOrWhiteSpace(domain.DkimPublicKey))
        {
            result.Add(CreateTxt(
                $"{domain.DkimSelector}._domainkey.{domain.Name}",
                $"v=DKIM1; k=rsa; p={domain.DkimPublicKey}"));
        }
        if (!settings.MtaStsMode.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(CreateTxt(
                $"_mta-sts.{domain.Name}",
                $"v=STSv1; id={settings.MtaStsPolicyId};"));
        }
        if (!string.IsNullOrWhiteSpace(settings.TlsReportUris))
        {
            result.Add(CreateTxt(
                $"_smtp._tls.{domain.Name}",
                $"v=TLSRPTv1; rua={string.Join(',', SplitList(settings.TlsReportUris))}"));
        }
        if (settings.DaneEnabled)
        {
            var mailHost = domain.MailHostName ?? $"mail.{domain.Name}";
            result.AddRange(SplitList(settings.DaneTlsaRecords).Select(value =>
                new DomainDnsRecord(
                    $"_25._tcp.{mailHost}",
                    "TLSA",
                    value,
                    $"_25._tcp.{mailHost}. IN TLSA {value}")));
        }
        return result;
    }

    private static DomainDnsRecord CreateTxt(string name, string value) =>
        new(name, "TXT", value, $"{name}. 3600 IN TXT {ToZoneTxt(value)}");

    private static string ToZoneTxt(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var chunks = Enumerable.Range(0, (escaped.Length + 199) / 200)
            .Select(index => escaped.Substring(index * 200, Math.Min(200, escaped.Length - index * 200)));
        return string.Join(' ', chunks.Select(x => $"\"{x}\""));
    }

    private static DomainDnsSettings CreateDefaults(Guid domainId) => new() { DomainId = domainId };

    private static void Validate(DomainDnsSettings settings)
    {
        if (settings.SpfAllPolicy is not ("+all" or "-all" or "~all" or "?all"))
            throw new FormatException("SPF all policy must be +all, -all, ~all, or ?all.");
        if (settings.DmarcPolicy is not ("none" or "quarantine" or "reject"))
            throw new FormatException("DMARC policy must be none, quarantine, or reject.");
        if (settings.DmarcSubdomainPolicy is not null
            && settings.DmarcSubdomainPolicy is not ("" or "none" or "quarantine" or "reject"))
            throw new FormatException("DMARC subdomain policy is invalid.");
        if (settings.DmarcPercentage is < 0 or > 100)
            throw new FormatException("DMARC percentage must be between 0 and 100.");
        if (settings.DmarcDkimAlignment is not ("r" or "s") || settings.DmarcSpfAlignment is not ("r" or "s"))
            throw new FormatException("DMARC alignment must be r or s.");
        if (settings.MtaStsMode is not ("none" or "testing" or "enforce"))
            throw new FormatException("MTA-STS mode must be none, testing, or enforce.");
        if (settings.MtaStsMode != "none"
            && (settings.MtaStsPolicyId.Length is < 1 or > 32
                || !settings.MtaStsPolicyId.All(char.IsAsciiLetterOrDigit)))
            throw new FormatException("MTA-STS policy id must contain 1-32 ASCII letters or digits.");
        if (settings.MtaStsMode != "none" && settings.MtaStsMaxAgeSeconds is < 0 or > 31_557_600)
            throw new FormatException("MTA-STS max_age must be between 0 and 31557600.");
        if (settings.MtaStsMode != "none" && SplitList(settings.MtaStsMxPatterns).Length == 0)
            throw new FormatException("MTA-STS requires at least one MX pattern.");
        if (settings.DaneEnabled && SplitList(settings.DaneTlsaRecords).Length == 0)
            throw new FormatException("DANE requires at least one TLSA record.");
    }

    private static bool IsValidSelector(string selector) =>
        selector.Length is > 0 and <= 63 && selector.All(x => char.IsAsciiLetterOrDigit(x) || x == '-');

    private static string NormalizeList(string value) => string.Join('\n', SplitList(value).Distinct(StringComparer.OrdinalIgnoreCase));
    private static string[] SplitList(string? value) =>
        (value ?? string.Empty).Split([',', ';', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string NormalizeDnsValue(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void AddAudit(MailDbContext db, string actor, string action, string resource) =>
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = action, Resource = resource });
}
