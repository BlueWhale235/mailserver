using System.Net;
using System.Net.Sockets;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

public sealed class IpFirewallService(
    IDbContextFactory<MailDbContext> dbFactory,
    ILogger<IpFirewallService> logger)
{
    public async Task<bool> IsBlockedAsync(string remoteAddress, CancellationToken cancellationToken)
    {
        var address = IpFirewallRules.TryParseRemoteAddress(remoteAddress);
        return address is not null && await IsBlockedAsync(address, cancellationToken);
    }

    public async Task<bool> IsBlockedAsync(IPAddress address, CancellationToken cancellationToken)
    {
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
