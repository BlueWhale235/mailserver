using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailServer.Infrastructure;

public sealed class TlsReportBackgroundService(
    IDbContextFactory<MailDbContext> dbFactory,
    TransportSecurityPolicyService policies,
    IInboundMessageService inbound,
    IOptions<TransportSecurityOptions> options,
    ILogger<TlsReportBackgroundService> logger) : BackgroundService
{
    private readonly TransportSecurityOptions _options = options.Value;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CreateReportsAsync(stoppingToken);
                await DeliverReportsAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "TLS reporting cycle failed.");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }

    internal async Task CreateReportsAsync(CancellationToken cancellationToken)
    {
        var end = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var start = end.AddDays(-1);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var events = await db.TlsDeliveryEvents
            .Where(x => !x.Reported && x.OccurredAt >= start && x.OccurredAt < end)
            .ToListAsync(cancellationToken);
        foreach (var domainGroup in events.GroupBy(x => x.PolicyDomain, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> uris;
            try { uris = await policies.ResolveTlsReportUrisAsync(domainGroup.Key, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "TLS-RPT discovery failed for {Domain}.", domainGroup.Key);
                continue;
            }
            if (uris.Count == 0) continue;
            var json = BuildReport(domainGroup.Key, start, end, domainGroup.ToArray());
            foreach (var uri in uris.Where(IsSupportedReportUri))
            {
                db.TlsReportDeliveries.Add(new TlsReportDelivery
                {
                    PolicyDomain = domainGroup.Key,
                    ReportUri = uri,
                    PeriodStart = start,
                    PeriodEnd = end,
                    ReportJson = json,
                    NextAttemptAt = DateTimeOffset.UtcNow
                });
            }
            foreach (var item in domainGroup) item.Reported = true;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    internal async Task DeliverReportsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var reportIds = await db.TlsReportDeliveries.AsNoTracking()
            .Where(x => x.CompletedAt == null && x.NextAttemptAt <= now && x.Attempts < 5
                && (x.LeaseUntil == null || x.LeaseUntil <= now))
            .OrderBy(x => x.NextAttemptAt)
            .ThenBy(x => x.PeriodStart)
            .ThenBy(x => x.Id)
            .Take(50)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        foreach (var reportId in reportIds)
        {
            var claimOwner = Guid.NewGuid().ToString("N");
            var claimedAt = DateTimeOffset.UtcNow;
            var claimed = await db.TlsReportDeliveries
                .Where(x => x.Id == reportId && x.CompletedAt == null && x.NextAttemptAt <= claimedAt
                    && x.Attempts < 5 && (x.LeaseUntil == null || x.LeaseUntil <= claimedAt))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LeaseOwner, claimOwner)
                    .SetProperty(x => x.LeaseUntil, claimedAt.AddMinutes(15)), cancellationToken);
            if (claimed == 0) continue;
            var report = await db.TlsReportDeliveries.AsNoTracking()
                .SingleAsync(x => x.Id == reportId, cancellationToken);
            try
            {
                if (report.ReportUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    using var content = new StringContent(report.ReportJson, Encoding.UTF8);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/tlsrpt+json");
                    using var response = await _http.PostAsync(report.ReportUri, content, cancellationToken);
                    response.EnsureSuccessStatusCode();
                }
                else
                {
                    await QueueMailReportAsync(report, cancellationToken);
                }
                await db.TlsReportDeliveries
                    .Where(x => x.Id == reportId && x.LeaseOwner == claimOwner)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.CompletedAt, DateTimeOffset.UtcNow)
                        .SetProperty(x => x.LastError, (string?)null)
                        .SetProperty(x => x.LeaseOwner, (string?)null)
                        .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var attempts = report.Attempts + 1;
                await db.TlsReportDeliveries
                    .Where(x => x.Id == reportId && x.LeaseOwner == claimOwner)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Attempts, attempts)
                        .SetProperty(x => x.LastError, exception.Message.Length <= 4096 ? exception.Message : exception.Message[..4096])
                        .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddHours(Math.Pow(2, attempts)))
                        .SetProperty(x => x.LeaseOwner, (string?)null)
                        .SetProperty(x => x.LeaseUntil, (DateTimeOffset?)null), cancellationToken);
            }
        }
    }

    private async Task QueueMailReportAsync(TlsReportDelivery report, CancellationToken cancellationToken)
    {
        var address = report.ReportUri["mailto:".Length..];
        var submitter = _options.ReportContactInfo.Contains('@')
            ? _options.ReportContactInfo
            : "postmaster@localhost";
        var message = new MimeMessage
        {
            From = { MailboxAddress.Parse(submitter) },
            To = { MailboxAddress.Parse(address) },
            Subject = $"Report Domain: {report.PolicyDomain} Submitter: {_options.ReportOrganizationName}"
        };
        message.Headers.Add("TLS-Report-Domain", report.PolicyDomain);
        message.Headers.Add("TLS-Report-Submitter", submitter[(submitter.IndexOf('@') + 1)..]);
        var machinePart = new MimePart("application", "tlsrpt+json")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(report.ReportJson))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            FileName = $"{report.PolicyDomain}!{report.PeriodStart:yyyy-MM-dd}!{report.PeriodEnd:yyyy-MM-dd}.json"
        };
        var multipart = new Multipart("report")
        {
            new TextPart("plain") { Text = $"TLS aggregate report for {report.PolicyDomain}." },
            machinePart
        };
        multipart.ContentType.Parameters["report-type"] = "tlsrpt";
        message.Body = multipart;
        await using var memory = new MemoryStream();
        await message.WriteToAsync(memory, cancellationToken);
        memory.Position = 0;
        await inbound.AcceptAsync(
            new InboundEnvelope(submitter, [address], "tls-report", "local", "localhost")
                { SourceType = "system" },
            memory,
            cancellationToken);
    }

    internal string BuildReport(
        string domain,
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<TlsDeliveryEvent> events)
    {
        var policies = events.GroupBy(x => new { x.PolicyType, x.PolicyString, x.MxHost }).Select(group => new
        {
            Policy = new
            {
                PolicyType = group.Key.PolicyType,
                PolicyString = string.IsNullOrEmpty(group.Key.PolicyString) ? Array.Empty<string>() : new[] { group.Key.PolicyString },
                PolicyDomain = domain,
                MxHost = new[] { group.Key.MxHost }
            },
            Summary = new
            {
                TotalSuccessfulSessionCount = group.Count(x => x.ResultType == "success"),
                TotalFailureSessionCount = group.Count(x => x.ResultType != "success")
            },
            FailureDetails = group.Where(x => x.ResultType != "success")
                .GroupBy(x => new { x.ResultType, x.FailureReason })
                .Select(failure => new
                {
                    ResultType = failure.Key.ResultType,
                    FailedSessionCount = failure.Count(),
                    FailureReasonCode = failure.Key.FailureReason
                })
        });
        return JsonSerializer.Serialize(new
        {
            OrganizationName = _options.ReportOrganizationName,
            DateRange = new { StartDatetime = start, EndDatetime = end },
            ContactInfo = _options.ReportContactInfo,
            ReportId = Guid.NewGuid().ToString(),
            Policies = policies
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower });
    }

    private static bool IsSupportedReportUri(string uri) =>
        uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
