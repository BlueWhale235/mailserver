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
    private sealed record ParsedNetwork(byte[] Address, int FullBytes, byte PartialMask);
    private sealed record FirewallSnapshot(NetworkIndex Index, bool Oversized);
    // Path-compressed radix trees use at most two nodes per network, regardless of prefix length.
    private sealed class NetworkIndex
    {
        private sealed class Node(byte[] address, int bits, string? rule)
        {
            internal readonly byte[] Address = address;
            internal readonly int Bits = bits;
            internal string? Rule = rule;
            internal Node? Zero, One;
        }
        private readonly Dictionary<IPAddress, string> _hosts = [];
        private Node? _ipv4, _ipv6;
        private static int Bit(byte[] bytes, int bit) => (bytes[bit / 8] >> (7 - bit % 8)) & 1;
        internal void Add(string cidr, ParsedNetwork network)
        {
            var bits = network.FullBytes * 8 + System.Numerics.BitOperations.PopCount(network.PartialMask);
            if (bits == network.Address.Length * 8) { _hosts[new IPAddress(network.Address)] = cidr; return; }
            if (network.Address.Length == 4) _ipv4 = Insert(_ipv4, network.Address, bits, cidr);
            else _ipv6 = Insert(_ipv6, network.Address, bits, cidr);
        }
        private static Node Insert(Node? node, byte[] address, int bits, string rule)
        {
            if (node is null) return new(address, bits, rule);
            var common = 0;
            while (common < Math.Min(node.Bits, bits) && Bit(node.Address, common) == Bit(address, common)) common++;
            if (common < node.Bits)
            {
                var parent = new Node(address, common, common == bits ? rule : null);
                if (Bit(node.Address, common) == 0) parent.Zero = node; else parent.One = node;
                if (common < bits)
                {
                    if (Bit(address, common) == 0) parent.Zero = new(address, bits, rule);
                    else parent.One = new(address, bits, rule);
                }
                return parent;
            }
            if (bits == node.Bits) node.Rule = rule;
            else if (Bit(address, node.Bits) == 0) node.Zero = Insert(node.Zero, address, bits, rule);
            else node.One = Insert(node.One, address, bits, rule);
            return node;
        }
        internal string? Match(IPAddress address, byte[] bytes)
        {
            if (_hosts.TryGetValue(address, out var exact)) return exact;
            var node = bytes.Length == 4 ? _ipv4 : _ipv6;
            var compared = 0;
            while (node is not null)
            {
                while (compared < node.Bits)
                {
                    if (Bit(bytes, compared) != Bit(node.Address, compared)) return null;
                    compared++;
                }
                if (node.Rule is not null) return node.Rule;
                node = Bit(bytes, node.Bits) == 0 ? node.Zero : node.One;
            }
            return null;
        }
    }

    public async Task<bool> IsBlockedAsync(string remoteAddress, CancellationToken cancellationToken)
    {
        var address = IpFirewallRules.TryParseRemoteAddress(remoteAddress);
        return address is not null && await IsBlockedAsync(address, cancellationToken);
    }

    public async Task<bool> IsBlockedAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var addressBytes = address.GetAddressBytes();
        if (cache is not null)
        {
            var snapshot = await cache.GetAsync(
                "firewall:enabled",
                ConfigurationScopes.Firewall,
                LoadSnapshotAsync,
                cancellationToken);
            if (snapshot.Oversized) return await IsBlockedBatchedAsync(address, addressBytes, cancellationToken);
            return MatchesSnapshot(snapshot, address, addressBytes);
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
        var estimate = 256L + count * 384L;
        if (cache is not null && estimate > cache.Options.MaximumEntryBytes)
            return (new FirewallSnapshot(new NetworkIndex(), true), 256);
        var cidrs = await db.FirewallRules.AsNoTracking().Where(x => x.IsEnabled)
            .OrderBy(x => x.Id).Select(x => x.Cidr).ToListAsync(cancellationToken);
        var index = new NetworkIndex();
        foreach (var cidr in cidrs) index.Add(cidr, ParseNetwork(cidr));
        return (new FirewallSnapshot(index, false), estimate);
    }

    private async Task<bool> IsBlockedBatchedAsync(IPAddress address, byte[] addressBytes, CancellationToken cancellationToken)
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
                    if (Matches(ParseNetwork(cidr), addressBytes))
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

    private bool MatchesSnapshot(FirewallSnapshot snapshot, IPAddress address, byte[] addressBytes)
    {
        var rule = snapshot.Index.Match(address, addressBytes);
        if (rule is null) return false;
        logger.LogWarning("Connection from {RemoteAddress} was blocked by firewall rule {Rule}.", address, rule);
        return true;
    }

    private static ParsedNetwork ParseNetwork(string cidr)
    {
        var normalized = IpFirewallRules.NormalizeCidr(cidr);
        var slash = normalized.IndexOf('/');
        var prefix = int.Parse(normalized[(slash + 1)..]);
        var remainingBits = prefix % 8;
        return new ParsedNetwork(IPAddress.Parse(normalized[..slash]).GetAddressBytes(),
            prefix / 8, remainingBits == 0 ? (byte)0 : (byte)(0xff << (8 - remainingBits)));
    }

    private static bool Matches(ParsedNetwork network, ReadOnlySpan<byte> addressBytes)
    {
        if (network.Address.Length != addressBytes.Length) return false;
        for (var index = 0; index < network.FullBytes; index++)
            if (network.Address[index] != addressBytes[index]) return false;
        return network.PartialMask == 0 ||
            (network.Address[network.FullBytes] & network.PartialMask) ==
            (addressBytes[network.FullBytes] & network.PartialMask);
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
