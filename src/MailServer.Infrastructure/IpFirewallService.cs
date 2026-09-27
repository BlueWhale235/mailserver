using System.Net;
using System.Net.Sockets;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

public sealed class IpFirewallService(
    IDbContextFactory<MailDbContext> dbFactory,
    ILogger<IpFirewallService> logger,
    ConfigurationSnapshotCache? cache = null,
    ConfigurationVersionService? versions = null)
{
    private sealed record ParsedNetwork(byte[] Address, int Prefix);
    private sealed record FirewallSnapshot(IReadOnlyList<(string Cidr, ParsedNetwork Network)> Rules, bool Oversized);

    public async Task<bool> IsBlockedAsync(string remoteAddress, CancellationToken cancellationToken)
    {
        var address = IpFirewallRules.TryParseRemoteAddress(remoteAddress);
        return address is not null && await IsBlockedAsync(address, cancellationToken);
    }

    public async Task<bool> IsBlockedAsync(IPAddress address, CancellationToken cancellationToken)
    {
        if (cache is not null)
        {
            var snapshot = await cache.GetAsync(
                "firewall:enabled",
                ConfigurationScopes.Firewall,
                LoadSnapshotAsync,
                cancellationToken);
            if (snapshot.Oversized) return await IsBlockedBatchedAsync(address, cancellationToken);
            return MatchesSnapshot(snapshot, address);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rules = await db.FirewallRules.AsNoTracking()
            .Where(x => x.IsEnabled)
            .Select(x => x.Cidr)
            .ToListAsync(cancellationToken);
        foreach (var rule in rules)
        {
            if (IpFirewallRules.Matches(rule, address))
            {
                logger.LogWarning("Connection from {RemoteAddress} was blocked by firewall rule {Rule}.", address, rule);
                return true;
            }
        }

        return false;
    }

    private async Task<(FirewallSnapshot Value, long Size)> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var count = await db.FirewallRules.AsNoTracking().CountAsync(x => x.IsEnabled, cancellationToken);
        var estimate = 256L + count * 160L;
        if (cache is not null && estimate > cache.Options.MaximumEntryBytes)
            return (new FirewallSnapshot([], true), estimate);
        var cidrs = await db.FirewallRules.AsNoTracking().Where(x => x.IsEnabled)
            .OrderBy(x => x.Id).Select(x => x.Cidr).ToListAsync(cancellationToken);
        var rules = cidrs.Select(x => (x, ParseNetwork(x))).ToArray();
        return (new FirewallSnapshot(rules, false), estimate);
    }

    private async Task<bool> IsBlockedBatchedAsync(IPAddress address, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = versions is null ? null : await versions.ReadAsync(cancellationToken);
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            const int batchSize = 256;
            string? matchedCidr = null;
            for (var offset = 0; ; offset += batchSize)
            {
                var batch = await db.FirewallRules.AsNoTracking().Where(x => x.IsEnabled)
                    .OrderBy(x => x.Id).Skip(offset).Take(batchSize).Select(x => x.Cidr).ToListAsync(cancellationToken);
                foreach (var cidr in batch)
                {
                    if (Matches(ParseNetwork(cidr), address))
                    {
                        matchedCidr = cidr;
                        break;
                    }
                }
                if (matchedCidr is not null || batch.Count < batchSize) break;
            }
            var after = versions is null ? before : await versions.ReadAsync(cancellationToken);
            if (before?.Firewall != after?.Firewall) continue;
            if (matchedCidr is not null)
                logger.LogWarning("Connection from {RemoteAddress} was blocked by firewall rule {Rule}.", address, matchedCidr);
            return matchedCidr is not null;
        }
        throw new InvalidOperationException("Firewall rules changed repeatedly during bounded evaluation.");
    }

    private bool MatchesSnapshot(FirewallSnapshot snapshot, IPAddress address)
    {
        foreach (var rule in snapshot.Rules)
        {
            if (!Matches(rule.Network, address)) continue;
            logger.LogWarning("Connection from {RemoteAddress} was blocked by firewall rule {Rule}.", address, rule.Cidr);
            return true;
        }
        return false;
    }

    private static ParsedNetwork ParseNetwork(string cidr)
    {
        var normalized = IpFirewallRules.NormalizeCidr(cidr);
        var slash = normalized.IndexOf('/');
        return new ParsedNetwork(
            IPAddress.Parse(normalized[..slash]).GetAddressBytes(),
            int.Parse(normalized[(slash + 1)..]));
    }

    private static bool Matches(ParsedNetwork network, IPAddress address)
    {
        var addressBytes = address.GetAddressBytes();
        if (network.Address.Length != addressBytes.Length) return false;
        var fullBytes = network.Prefix / 8;
        var remainingBits = network.Prefix % 8;
        for (var index = 0; index < fullBytes; index++)
            if (network.Address[index] != addressBytes[index]) return false;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (network.Address[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }
}

public static class IpFirewallRules
{
    public static string NormalizeCidr(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new FormatException("IP 或 CIDR 不能为空。");

        var trimmed = value.Trim();
        var slash = trimmed.IndexOf('/');
        var addressPart = slash < 0 ? trimmed : trimmed[..slash];
        if (!IPAddress.TryParse(addressPart, out var address))
            throw new FormatException("请输入有效的 IP 地址或 CIDR 网段。");

        var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = maxPrefix;
        if (slash >= 0)
        {
            if (!int.TryParse(trimmed[(slash + 1)..], out prefix) || prefix < 0 || prefix > maxPrefix)
                throw new FormatException($"CIDR 前缀必须在 0 到 {maxPrefix} 之间。");
        }

        return $"{address}/{prefix}";
    }

    public static bool Matches(string cidr, IPAddress address)
    {
        var normalized = NormalizeCidr(cidr);
        var slash = normalized.IndexOf('/');
        var network = IPAddress.Parse(normalized[..slash]);
        var prefix = int.Parse(normalized[(slash + 1)..]);
        if (network.AddressFamily != address.AddressFamily)
        {
            return false;
        }

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        var fullBytes = prefix / 8;
        var remainingBits = prefix % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != addressBytes[i]) return false;
        }

        if (remainingBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }

    public static IPAddress? TryParseRemoteAddress(string remoteAddress)
    {
        if (string.IsNullOrWhiteSpace(remoteAddress)) return null;
        var trimmed = remoteAddress.Trim();
        if (IPEndPoint.TryParse(trimmed, out var endpoint)) return endpoint.Address;
        if (IPAddress.TryParse(trimmed.Trim('[', ']'), out var direct)) return direct;

        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && trimmed.IndexOf(':') == colon && IPAddress.TryParse(trimmed[..colon], out var ipv4))
        {
            return ipv4;
        }

        return null;
    }
}
