using DnsClient;

namespace MailServer.Infrastructure;

public sealed record DnsDiagnosticResult(
    string Domain,
    IReadOnlyList<string> Mx,
    IReadOnlyList<string> Spf,
    IReadOnlyList<string> Dmarc,
    IReadOnlyList<string> Dkim,
    IReadOnlyList<string> Warnings);

public sealed class DnsDiagnosticService(LookupClient client)
{
    public async Task<DnsDiagnosticResult> InspectAsync(
        string domain,
        string? selector,
        CancellationToken cancellationToken)
    {
        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        var mx = await QueryMxAsync(normalized, cancellationToken);
        var spf = (await QueryTxtAsync(normalized, cancellationToken))
            .Where(x => x.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var dmarc = (await QueryTxtAsync($"_dmarc.{normalized}", cancellationToken))
            .Where(x => x.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var dkim = string.IsNullOrWhiteSpace(selector)
            ? []
            : (await QueryTxtAsync($"{selector}._domainkey.{normalized}", cancellationToken)).ToArray();
        var warnings = new List<string>();
        if (mx.Count == 0) warnings.Add("No MX record found.");
        if (spf.Length == 0) warnings.Add("No SPF policy found.");
        if (dmarc.Length == 0) warnings.Add("No DMARC policy found.");
        if (!string.IsNullOrWhiteSpace(selector) && dkim.Length == 0) warnings.Add("No DKIM public key found.");
        return new DnsDiagnosticResult(normalized, mx, spf, dmarc, dkim, warnings);
    }

    private async Task<IReadOnlyList<string>> QueryMxAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.QueryAsync(name, QueryType.MX, cancellationToken: cancellationToken);
            return result.Answers.MxRecords()
                .OrderBy(x => x.Preference)
                .Select(x => $"{x.Preference} {x.Exchange.Value.TrimEnd('.')}")
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
            var result = await client.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken);
            return result.Answers.TxtRecords().SelectMany(x => x.Text).ToArray();
        }
        catch (DnsResponseException)
        {
            return [];
        }
    }
}
