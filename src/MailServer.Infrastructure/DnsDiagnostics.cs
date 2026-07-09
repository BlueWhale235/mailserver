using DnsClient;
using System.Net;
using System.Net.Sockets;

namespace MailServer.Infrastructure;

public sealed record DnsDiagnosticResult(
    string Domain,
    IReadOnlyList<string> Mx,
    IReadOnlyList<string> Spf,
    IReadOnlyList<string> Dmarc,
    IReadOnlyList<string> Dkim,
    IReadOnlyList<string> MtaSts,
    IReadOnlyList<string> TlsRpt,
    IReadOnlyList<string> Tlsa,
    IReadOnlyList<MxConnectivityDiagnostic> MxConnectivity,
    IReadOnlyList<string> Warnings);

public sealed record MxConnectivityDiagnostic(
    string Host,
    string Address,
    string AddressFamily,
    int Port,
    bool Success,
    string? Error);

internal sealed record MxRecordDiagnostic(int Preference, string Host);

internal sealed record MxConnectivityProbeResult(bool Success, string? Error);

public sealed class DnsDiagnosticService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
    private readonly LookupClient _client;
    private readonly Func<IPAddress, int, CancellationToken, Task<MxConnectivityProbeResult>> _probe;

    public DnsDiagnosticService(LookupClient client)
        : this(client, ProbeSmtpPortAsync)
    {
    }

    internal DnsDiagnosticService(
        LookupClient client,
        Func<IPAddress, int, CancellationToken, Task<MxConnectivityProbeResult>> probe)
    {
        _client = client;
        _probe = probe;
    }

    public async Task<DnsDiagnosticResult> InspectAsync(
        string domain,
        string? selector,
        CancellationToken cancellationToken)
    {
        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var mxRecords = await QueryMxRecordsAsync(normalized, cancellationToken);
        var mx = mxRecords.Select(x => $"{x.Preference} {x.Host}").ToArray();
        var spf = (await QueryTxtAsync(normalized, cancellationToken))
            .Where(x => x.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var dmarc = (await QueryTxtAsync($"_dmarc.{normalized}", cancellationToken))
            .Where(x => x.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var dkim = string.IsNullOrWhiteSpace(selector)
            ? []
            : (await QueryTxtAsync($"{selector}._domainkey.{normalized}", cancellationToken)).ToArray();
        var mtaSts = (await QueryTxtAsync($"_mta-sts.{normalized}", cancellationToken))
            .Where(x => x.StartsWith("v=STSv1;", StringComparison.Ordinal))
            .ToArray();
        var tlsRpt = (await QueryTxtAsync($"_smtp._tls.{normalized}", cancellationToken))
            .Where(x => x.StartsWith("v=TLSRPTv1;", StringComparison.Ordinal))
            .ToArray();
        var tlsa = new List<string>();
        foreach (var host in mxRecords.Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var response = await _client.QueryAsync($"_25._tcp.{host}", QueryType.TLSA, cancellationToken: cancellationToken);
                tlsa.AddRange(response.Answers.TlsaRecords().Select(x =>
                    $"{host} {(int)x.CertificateUsage} {(int)x.Selector} {(int)x.MatchingType} {x.CertificateAssociationDataAsString}"));
            }
            catch (DnsResponseException)
            {
            }
        }
        var connectivity = await QueryMxConnectivityAsync(
            mxRecords.Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            cancellationToken);
        var warnings = new List<string>();
        if (mx.Length == 0) warnings.Add("No MX record found.");
        if (spf.Length == 0) warnings.Add("No SPF policy found.");
        if (dmarc.Length == 0) warnings.Add("No DMARC policy found.");
        if (!string.IsNullOrWhiteSpace(selector) && dkim.Length == 0) warnings.Add("No DKIM public key found.");
        warnings.AddRange(BuildConnectivityWarnings(mxRecords, connectivity));
        return new DnsDiagnosticResult(normalized, mx, spf, dmarc, dkim, mtaSts, tlsRpt, tlsa, connectivity, warnings);
    }

    private async Task<IReadOnlyList<MxRecordDiagnostic>> QueryMxRecordsAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.QueryAsync(name, QueryType.MX, cancellationToken: cancellationToken);
            return result.Answers.MxRecords()
                .OrderBy(x => x.Preference)
                .Select(x => new MxRecordDiagnostic(x.Preference, x.Exchange.Value.TrimEnd('.').ToLowerInvariant()))
                .ToArray();
        }
        catch (DnsResponseException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<string>> QueryTxtAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);
            return result.Answers.TxtRecords().Select(x => string.Concat(x.Text)).ToArray();
        }
        catch (DnsResponseException)
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<MxConnectivityDiagnostic>> QueryMxConnectivityAsync(
        IReadOnlyList<string> hosts,
        CancellationToken cancellationToken)
    {
        var result = new List<MxConnectivityDiagnostic>();
        foreach (var host in hosts)
        {
            var addresses = await QueryAddressesAsync(host, cancellationToken);
            foreach (var address in addresses)
            {
                var probe = await _probe(address, 25, cancellationToken);
                result.Add(new MxConnectivityDiagnostic(
                    host,
                    address.ToString(),
                    address.AddressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4",
                    25,
                    probe.Success,
                    probe.Error));
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<IPAddress>> QueryAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var result = new List<IPAddress>();
        try
        {
            var a = await _client.QueryAsync(host, QueryType.A, cancellationToken: cancellationToken);
            result.AddRange(a.Answers.ARecords().Select(x => x.Address));
        }
        catch (DnsResponseException)
        {
        }

        try
        {
            var aaaa = await _client.QueryAsync(host, QueryType.AAAA, cancellationToken: cancellationToken);
            result.AddRange(aaaa.Answers.AaaaRecords().Select(x => x.Address));
        }
        catch (DnsResponseException)
        {
        }

        return result
            .Distinct()
            .OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();
    }

    internal static IReadOnlyList<string> BuildConnectivityWarnings(
        IReadOnlyList<MxRecordDiagnostic> mxRecords,
        IReadOnlyList<MxConnectivityDiagnostic> connectivity)
    {
        var warnings = new List<string>();
        if (mxRecords.Count == 0)
        {
            return warnings;
        }

        var hostsWithAddresses = connectivity.Select(x => x.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var host in mxRecords.Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!hostsWithAddresses.Contains(host))
            {
                warnings.Add($"MX host {host} has no A or AAAA records.");
            }
        }

        foreach (var item in connectivity.Where(x => x.AddressFamily == "IPv6" && !x.Success))
        {
            warnings.Add($"MX host {item.Host} has IPv6 address {item.Address}, but SMTP port 25 is not reachable over IPv6.");
        }

        if (connectivity.Count > 0 && connectivity.All(x => !x.Success))
        {
            warnings.Add("No MX host accepted a TCP connection on SMTP port 25 from this diagnostic environment.");
        }

        return warnings;
    }

    private static async Task<MxConnectivityProbeResult> ProbeSmtpPortAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            using var tcp = new TcpClient(address.AddressFamily);
            await tcp.ConnectAsync(address, port, timeout.Token).AsTask().WaitAsync(ConnectTimeout, cancellationToken);
            return new MxConnectivityProbeResult(true, null);
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or OperationCanceledException)
        {
            return new MxConnectivityProbeResult(false, exception.Message);
        }
    }
}
