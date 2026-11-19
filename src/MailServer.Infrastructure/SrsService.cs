using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed record SrsDecodedAddress(string OriginalSender, int ForwardDepth, long DomainId);
public sealed record ForwardingTrace(long RuleId, int ForwardDepth);

public sealed class SrsService(IOptions<ForwardingOptions> options)
{
    public const string ForwardingTraceHeader = "X-MailServer-Forwarding-Trace";
    private const string GeneratedSecretKey = "forwarding:srs-secret";
    private readonly ForwardingOptions _options = options.Value;

    public string CreateAddress(
        MailDbContext db,
        MailDomain domain,
        string originalSender,
        int forwardDepth,
        DateTimeOffset? now = null)
    {
        var secret = ResolveSecret(db, createIfMissing: true);
        var normalizedSender = AddressParser.Normalize(originalSender);
        if (!AddressParser.TrySplit(normalizedSender, out _, out _))
            throw new FormatException("Forwarding sender is not a valid email address.");
        if (forwardDepth < 1 || forwardDepth > _options.MaxHops)
            throw new InvalidOperationException("Forwarding hop limit exceeded.");

        var createdAt = now ?? DateTimeOffset.UtcNow;
        var day = createdAt.ToUnixTimeSeconds() / 86_400;
        var nonce = Hex(RandomNumberGenerator.GetBytes(8));
        var signature = Sign(secret, day, forwardDepth, nonce, normalizedSender, domain.Name);
        var token = string.Create(
            CultureInfo.InvariantCulture,
            $"{day}={forwardDepth}={nonce}={signature}");
        db.SrsAddressMappings.Add(new SrsAddressMapping
        {
            Token = token,
            Domain = domain,
            OriginalSender = normalizedSender,
            ForwardDepth = forwardDepth,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(Math.Clamp(_options.MaxAgeDays, 1, 90))
        });
        return $"SRS0={token}@{domain.Name}";
    }

    public async Task<SrsDecodedAddress?> TryDecodeAsync(
        MailDbContext db,
        string address,
        CancellationToken cancellationToken)
    {
        var secret = await ResolveSecretAsync(db, createIfMissing: false, cancellationToken);
        if (secret is null
            || !AddressParser.TrySplit(address, out var localPart, out var domainName)
            || !localPart.StartsWith("srs0=", StringComparison.OrdinalIgnoreCase))
            return null;

        var token = localPart[5..];
        var pieces = token.Split('=', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pieces.Length != 4
            || !long.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var depth))
            return null;

        var mapping = await db.SrsAddressMappings.AsNoTracking()
            .Include(x => x.Domain)
            .SingleOrDefaultAsync(
                x => x.Token == token && x.Domain.Name == domainName,
                cancellationToken);
        if (mapping is null
            || mapping.ExpiresAt < DateTimeOffset.UtcNow
            || depth != mapping.ForwardDepth
            || depth < 1
            || depth > _options.MaxHops)
            return null;

        var expected = Sign(secret, day, depth, pieces[2], mapping.OriginalSender, mapping.Domain.Name);
        var actualBytes = Encoding.ASCII.GetBytes(pieces[3]);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes)
            ? new SrsDecodedAddress(mapping.OriginalSender, depth, mapping.DomainId)
            : null;
    }

    public string CreateForwardingTrace(
        MailDbContext db,
        long ruleId,
        int forwardDepth,
        string messageId)
    {
        if (ruleId == 0)
            throw new ArgumentException("Forwarding rule id is required.", nameof(ruleId));
        if (forwardDepth < 1 || forwardDepth > Math.Clamp(_options.MaxHops, 1, 20))
            throw new InvalidOperationException("Forwarding hop limit exceeded.");
        var normalizedMessageId = NormalizeMessageId(messageId);
        var secret = ResolveSecret(db, createIfMissing: true);
        var signature = SignTrace(secret, normalizedMessageId, ruleId, forwardDepth);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"v1;{forwardDepth};{ruleId};{signature}");
    }

    public async Task<IReadOnlyList<ForwardingTrace>> ReadForwardingTracesAsync(
        MailDbContext db,
        string? messageId,
        IEnumerable<string> headerValues,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return [];
        var secret = await ResolveSecretAsync(db, createIfMissing: false, cancellationToken);
        if (secret is null) return [];
        var normalizedMessageId = NormalizeMessageId(messageId);
        var traces = new List<ForwardingTrace>();
        foreach (var headerValue in headerValues)
        {
            var pieces = headerValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pieces.Length != 4
                || !pieces[0].Equals("v1", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out var depth)
                || depth < 1
                || depth > Math.Clamp(_options.MaxHops, 1, 20)
                || !long.TryParse(pieces[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ruleId))
                continue;
            var expected = SignTrace(secret, normalizedMessageId, ruleId, depth);
            var actualBytes = Encoding.ASCII.GetBytes(pieces[3]);
            var expectedBytes = Encoding.ASCII.GetBytes(expected);
            if (actualBytes.Length == expectedBytes.Length
                && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
            {
                traces.Add(new ForwardingTrace(ruleId, depth));
            }
        }
        return traces;
    }

    private string Sign(string secret, long day, int depth, string nonce, string sender, string domain)
    {
        var data = Encoding.UTF8.GetBytes($"{day}|{depth}|{nonce}|{sender}|{domain}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Hex(hmac.ComputeHash(data).AsSpan(0, 12));
    }

    private static string SignTrace(string secret, string messageId, long ruleId, int depth)
    {
        var data = Encoding.UTF8.GetBytes($"forward|{messageId}|{ruleId}|{depth}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Hex(hmac.ComputeHash(data).AsSpan(0, 12));
    }

    private static string NormalizeMessageId(string messageId)
    {
        var normalized = messageId.Trim().Trim('<', '>').Trim();
        if (normalized.Length is < 1 or > 998)
            throw new FormatException("Forwarded message id is invalid.");
        return normalized;
    }

    private string ResolveSecret(MailDbContext db, bool createIfMissing)
    {
        var configured = ValidateConfiguredSecret();
        if (configured is not null) return configured;

        var setting = db.SystemSettings.SingleOrDefault(x => x.Key == GeneratedSecretKey);
        if (setting is not null)
        {
            return setting.Value;
        }

        if (!createIfMissing)
        {
            throw new InvalidOperationException("SRS secret has not been generated.");
        }

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        db.SystemSettings.Add(new SystemSetting { Key = GeneratedSecretKey, Value = generated });
        return generated;
    }

    private async Task<string?> ResolveSecretAsync(
        MailDbContext db,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        var configured = ValidateConfiguredSecret();
        if (configured is not null) return configured;

        var setting = await db.SystemSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == GeneratedSecretKey, cancellationToken);
        if (setting is not null) return setting.Value;
        if (!createIfMissing) return null;

        var generated = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        db.SystemSettings.Add(new SystemSetting { Key = GeneratedSecretKey, Value = generated });
        return generated;
    }

    private string? ValidateConfiguredSecret()
    {
        if (string.IsNullOrWhiteSpace(_options.SrsSecret)) return null;
        if (Encoding.UTF8.GetByteCount(_options.SrsSecret) < 32)
            throw new InvalidOperationException("Forwarding:SrsSecret must contain at least 32 bytes.");
        return _options.SrsSecret;
    }

    private static string Hex(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(value);
}
