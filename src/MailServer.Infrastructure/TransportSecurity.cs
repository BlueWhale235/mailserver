using Microsoft.Extensions.Caching.Memory;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DnsClient;
using DnsClient.Protocol;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed class TransportSecurityOptions
{
    public const string SectionName = "TransportSecurity";
    public string TrustedDnssecResolvers { get; set; } = string.Empty;
    public int PolicyFetchTimeoutSeconds { get; set; } = 60;
    public int PolicyRefreshHours { get; set; } = 24;
    public string ReportOrganizationName { get; set; } = "MailServer";
    public string ReportContactInfo { get; set; } = "postmaster@localhost";
}

public sealed record MtaStsPolicy(
    string Domain,
    string PolicyId,
    string Mode,
    IReadOnlyList<string> MxPatterns,
    int MaxAgeSeconds,
    DateTimeOffset ExpiresAt)
{
    public bool MatchesMx(string host) => MxPatterns.Any(pattern => MxMatches(pattern, host));

    internal static bool MxMatches(string pattern, string host)
    {
        pattern = pattern.TrimEnd('.').ToLowerInvariant();
        host = host.TrimEnd('.').ToLowerInvariant();
        return pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.Length > pattern.Length - 1 && host.EndsWith(pattern[1..], StringComparison.Ordinal)
            : host.Equals(pattern, StringComparison.Ordinal);
    }
}

public sealed record DanePolicy(string Host, IReadOnlyList<TlsaAssociation> Associations);

public sealed record TlsaAssociation(int Usage, int Selector, int MatchingType, byte[] Data)
{
    public string PolicyString => $"{Usage} {Selector} {MatchingType} {Convert.ToHexString(Data)}";
}

public sealed class TransportSecurityPolicyService(
    LookupClient dns,
    IDbContextFactory<MailDbContext> dbFactory,
    IOptions<TransportSecurityOptions> options,
    ILogger<TransportSecurityPolicyService> logger,
    TlsEventWriter? eventWriter = null) : IDisposable
{
    private sealed record CachedPolicy(MtaStsPolicy? Policy);
    private readonly MemoryCache _policies = new(new MemoryCacheOptions { SizeLimit = 4096 });
    private readonly AsyncKeyedLock<string> _policyLoads = new();
    private readonly TransportSecurityOptions _options = options.Value;
    private readonly HashSet<IPAddress> _trustedResolvers = ParseResolvers(options.Value.TrustedDnssecResolvers);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(Math.Clamp(options.Value.PolicyFetchTimeoutSeconds, 5, 60))
    };

    public async Task<DanePolicy?> ResolveDaneAsync(string host, CancellationToken cancellationToken)
    {
        if (_trustedResolvers.Count == 0) return null;
        var response = await dns.QueryAsync($"_25._tcp.{host.TrimEnd('.')}", QueryType.TLSA, cancellationToken: cancellationToken);
        if (!IsTrustedDnssecResponse(response))
        {
            logger.LogWarning(
                "Ignoring TLSA response for {Host}: resolver={Resolver}, authenticated={Authenticated}.",
                host,
                response.NameServer.Address,
                response.Header.IsAuthenticData);
            return null;
        }

        var records = response.Answers.TlsaRecords()
            .Where(x => (int)x.CertificateUsage is >= 0 and <= 3
                        && (int)x.Selector is >= 0 and <= 1
                        && (int)x.MatchingType is >= 0 and <= 2)
            .Select(x => new TlsaAssociation(
                (int)x.CertificateUsage,
                (int)x.Selector,
                (int)x.MatchingType,
                x.CertificateAssociationData.ToArray()))
            .ToArray();
        return records.Length == 0 ? null : new DanePolicy(host, records);
    }

    public bool IsTrustedDnssecResponse(IDnsQueryResponse response)
    {
        var resolverAddress = IPAddress.TryParse(response.NameServer.Address, out var parsedResolver)
            ? parsedResolver
            : null;
        return resolverAddress is not null
               && _trustedResolvers.Contains(resolverAddress)
               && response.Header.IsAuthenticData;
    }

    public async Task<MtaStsPolicy?> ResolveMtaStsAsync(string domain, CancellationToken cancellationToken)
    {
        domain = domain.TrimEnd('.').ToLowerInvariant();
        if (_policies.TryGetValue(domain, out CachedPolicy? cached)) return cached!.Policy;
        using var lease = await _policyLoads.AcquireAsync(domain, cancellationToken);
        if (_policies.TryGetValue(domain, out cached)) return cached!.Policy;
        var policy = await ResolveMtaStsCoreAsync(domain, cancellationToken);
        var lifetime = policy is null ? TimeSpan.FromSeconds(60) : policy.ExpiresAt - DateTimeOffset.UtcNow;
        if (lifetime > TimeSpan.Zero)
            _policies.Set(domain, new CachedPolicy(policy), new MemoryCacheEntryOptions { Size = 1,
                AbsoluteExpirationRelativeToNow = lifetime < TimeSpan.FromMinutes(10) ? lifetime : TimeSpan.FromMinutes(10) });
        return policy;
    }

    public void Dispose() { _policies.Dispose(); _http.Dispose(); }

    private async Task<MtaStsPolicy?> ResolveMtaStsCoreAsync(string domain, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var cached = await db.MtaStsPolicyCaches.SingleOrDefaultAsync(x => x.Domain == domain, cancellationToken);
        string? policyId = null;
        try
        {
            var response = await dns.QueryAsync($"_mta-sts.{domain}", QueryType.TXT, cancellationToken: cancellationToken);
            var records = response.Answers.TxtRecords()
                .Select(x => string.Concat(x.Text))
                .Where(x => x.StartsWith("v=STSv1;", StringComparison.Ordinal))
                .ToArray();
            if (records.Length == 1) policyId = ParseTag(records[0], "id");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "MTA-STS TXT lookup failed for {Domain}.", domain);
        }

        if (cached is not null && cached.ExpiresAt > now
            && (policyId is null || policyId == cached.PolicyId))
            return FromCache(cached);
        if (policyId is null)
            return cached is not null && cached.ExpiresAt > now ? FromCache(cached) : null;

        try
        {
            using var response = await _http.GetAsync(
                $"https://mta-sts.{domain}/.well-known/mta-sts.txt",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentType?.MediaType != "text/plain")
                throw new InvalidDataException("MTA-STS endpoint did not return HTTP 200 text/plain.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var limited = new StreamReader(stream);
            var body = new char[65_537];
            var length = await limited.ReadBlockAsync(body.AsMemory(), cancellationToken);
            if (length > 65_536) throw new InvalidDataException("MTA-STS policy exceeds 64 KiB.");
            var policy = ParsePolicy(domain, policyId, new string(body, 0, length), now);
            cached ??= new MtaStsPolicyCache { Domain = domain, PolicyId = policyId, Mode = policy.Mode, MxPatterns = string.Empty };
            cached.PolicyId = policy.PolicyId;
            cached.Mode = policy.Mode;
            cached.MxPatterns = string.Join('\n', policy.MxPatterns);
            cached.MaxAgeSeconds = policy.MaxAgeSeconds;
            cached.FetchedAt = now;
            cached.ExpiresAt = policy.ExpiresAt;
            cached.LastRefreshAttemptAt = now;
            cached.LastError = null;
            if (db.Entry(cached).State == EntityState.Detached) db.MtaStsPolicyCaches.Add(cached);
            await db.SaveChangesAsync(cancellationToken);
            return policy;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "MTA-STS policy fetch failed for {Domain}.", domain);
            if (cached is not null)
            {
                cached.LastRefreshAttemptAt = now;
                cached.LastError = exception.Message;
                await db.SaveChangesAsync(cancellationToken);
                if (cached.ExpiresAt > now) return FromCache(cached);
            }
            return null;
        }
    }

    public async Task RecordEventAsync(
        string domain,
        string mxHost,
        string policyType,
        string policyString,
        string resultType,
        string? failureReason,
        CancellationToken cancellationToken)
    {
        var entry = new TlsDeliveryEvent
        {
            PolicyDomain = domain,
            MxHost = mxHost,
            PolicyType = policyType,
            PolicyString = policyString,
            ResultType = resultType,
            FailureReason = failureReason ?? string.Empty
        };
        try
        {
            if (eventWriter?.IsRunning == true) { await eventWriter.EnqueueAsync(entry, cancellationToken); return; }
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            db.TlsDeliveryEvents.Add(entry);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception error)
        {
            // Reporting occurs after SMTP DATA may already have been accepted. A failed
            // telemetry write must never turn that success into a duplicate delivery retry.
            logger.LogWarning(error, "Unable to record TLS delivery event for {Domain}.", domain);
        }
    }

    public async Task<IReadOnlyList<string>> ResolveTlsReportUrisAsync(
        string domain,
        CancellationToken cancellationToken)
    {
        var response = await dns.QueryAsync($"_smtp._tls.{domain}", QueryType.TXT, cancellationToken: cancellationToken);
        var records = response.Answers.TxtRecords()
            .Select(x => string.Concat(x.Text))
            .Where(x => x.StartsWith("v=TLSRPTv1;", StringComparison.Ordinal))
            .ToArray();
        if (records.Length != 1) return [];
        var rua = ParseTag(records[0], "rua");
        return string.IsNullOrWhiteSpace(rua)
            ? []
            : rua.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static MtaStsPolicy ParsePolicy(string domain, string policyId, string text, DateTimeOffset now)
    {
        string? version = null;
        string? mode = null;
        int? maxAge = null;
        var mx = new List<string>();
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine)) continue;
            var separator = rawLine.IndexOf(':');
            if (separator <= 0) throw new FormatException("Malformed MTA-STS policy line.");
            var name = rawLine[..separator].Trim().ToLowerInvariant();
            var value = rawLine[(separator + 1)..].Trim();
            switch (name)
            {
                case "version" when version is null: version = value; break;
                case "mode" when mode is null: mode = value.ToLowerInvariant(); break;
                case "max_age" when maxAge is null && int.TryParse(value, out var parsed): maxAge = parsed; break;
                case "mx": mx.Add(value.ToLowerInvariant()); break;
            }
        }
        if (version != "STSv1" || mode is not ("none" or "testing" or "enforce")
            || maxAge is null or < 0 or > 31_557_600 || (mode != "none" && mx.Count == 0))
            throw new FormatException("Invalid MTA-STS policy.");
        return new MtaStsPolicy(domain, policyId, mode, mx, maxAge.Value, now.AddSeconds(maxAge.Value));
    }

    public static bool ValidateDaneCertificate(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors policyErrors,
        DanePolicy policy)
    {
        if (certificate is null) return false;
        var leaf = new X509Certificate2(certificate);
        foreach (var association in policy.Associations)
        {
            var candidates = association.Usage is 0 or 2 && chain is not null
                ? chain.ChainElements.Cast<X509ChainElement>().Select(x => x.Certificate)
                : [leaf];
            foreach (var candidate in candidates)
            {
                var selected = association.Selector == 1
                    ? ExportSubjectPublicKeyInfo(candidate)
                    : candidate.RawData;
                var compared = association.MatchingType switch
                {
                    0 => selected,
                    1 => SHA256.HashData(selected),
                    2 => SHA512.HashData(selected),
                    _ => []
                };
                if (CryptographicOperations.FixedTimeEquals(compared, association.Data)
                    && (association.Usage is 2 or 3 || policyErrors == SslPolicyErrors.None))
                    return true;
            }
        }
        return false;
    }

    private static MtaStsPolicy FromCache(MtaStsPolicyCache value) =>
        new(value.Domain, value.PolicyId, value.Mode, value.MxPatterns.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            value.MaxAgeSeconds, value.ExpiresAt);

    private static string? ParseTag(string value, string name) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Split('=', 2, StringSplitOptions.TrimEntries))
            .FirstOrDefault(x => x.Length == 2 && x[0].Equals(name, StringComparison.OrdinalIgnoreCase))?[1];

    private static HashSet<IPAddress> ParseResolvers(string value) =>
        value.Split([',', ';', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => IPAddress.TryParse(x, out var address) ? address : null)
            .Where(x => x is not null)
            .Cast<IPAddress>()
            .ToHashSet();

    private static byte[] ExportSubjectPublicKeyInfo(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null) return rsa.ExportSubjectPublicKeyInfo();
        using var ecdsa = certificate.GetECDsaPublicKey();
        if (ecdsa is not null) return ecdsa.ExportSubjectPublicKeyInfo();
        using var dsa = certificate.GetDSAPublicKey();
        if (dsa is not null) return dsa.ExportSubjectPublicKeyInfo();
        throw new CryptographicException("Unsupported certificate public key algorithm.");
    }
}
