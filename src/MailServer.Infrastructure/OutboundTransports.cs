using DnsClient;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;
using MimeKit.Utils;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Diagnostics;

namespace MailServer.Infrastructure;

/// <summary>
/// 直接查询收件域 MX 并投递到 25 端口的传输实现，同时处理 MTA-STS、DANE 和 TLS-RPT 记录。
/// </summary>
public sealed class DirectMxTransport(
    LookupClient lookupClient,
    IDbContextFactory<MailDbContext> dbFactory,
    SrsService srs,
    TransportSecurityPolicyService transportSecurity,
    ILogger<DirectMxTransport> logger,
    RelaySettingsService? relaySettings = null,
    RelayRootService? relay = null) : IOutboundTransport
{
    internal static string? ExtractEnhancedStatusCode(string response)
    {
        var match = System.Text.RegularExpressions.Regex.Match(response, @"(?<![\d.])[245]\.\d{1,3}\.\d{1,3}(?![\d.])",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }

    internal const int DeliveryPort = 25;
    internal const SecureSocketOptions DeliverySecurity = SecureSocketOptions.StartTlsWhenAvailable;

    public async Task<DeliveryResult> DeliverAsync(
        DeliveryJob job,
        Stream content,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!AddressParser.TrySplit(job.Recipient, out _, out var domain))
        {
            return DeliveryResult.Permanent("Invalid recipient address.", diagnostic: new DeliveryDiagnostic("SMTP", DurationMilliseconds: stopwatch.ElapsedMilliseconds, FailureCategory: "invalid-recipient"));
        }

        IReadOnlyList<string> hosts;
        var mxDnssecAuthenticated = false;
        try
        {
            // MX 解析失败通常是临时性 DNS 问题；没有 MX 时按 SMTP 标准回退到域名本身。
            var response = await lookupClient.QueryAsync(domain, QueryType.MX, cancellationToken: cancellationToken);
            mxDnssecAuthenticated = transportSecurity.IsTrustedDnssecResponse(response);
            var mxRecords = response.Answers.MxRecords().ToArray();
            if (mxRecords.Any(x => x.Exchange.Value == "."))
            {
                return DeliveryResult.Permanent(
                    "Recipient domain publishes Null MX and does not accept email.",
                    diagnostic: new DeliveryDiagnostic("DNS", DurationMilliseconds: stopwatch.ElapsedMilliseconds, FailureCategory: "null-mx"));
            }
            hosts = mxRecords
                .GroupBy(x => x.Preference)
                .OrderBy(group => group.Key)
                .SelectMany(group => group.OrderBy(_ => Random.Shared.Next()))
                .Select(x => x.Exchange.Value.TrimEnd('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (hosts.Count == 0)
            {
                hosts = [domain];
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "MX lookup failed for delivery job {JobId}: recipient={Recipient}, domain={Domain}.",
                job.Id,
                job.Recipient,
                domain);
            return DeliveryResult.Temporary($"MX lookup failed: {exception.Message}", diagnostic: new DeliveryDiagnostic("DNS", DurationMilliseconds: stopwatch.ElapsedMilliseconds, FailureCategory: "dns-error"));
        }

        logger.LogInformation(
            "Direct MX delivery job {JobId} resolved {HostCount} host(s) for domain={Domain}, recipient={Recipient}.",
            job.Id,
            hosts.Count,
            domain,
            job.Recipient);
        var messageBytes = await ReadAllAsync(content, cancellationToken);
        var suppressTlsReporting = Encoding.ASCII.GetString(
                messageBytes,
                0,
                Math.Min(messageBytes.Length, 64 * 1024))
            .Contains("TLS-Report-Submitter:", StringComparison.OrdinalIgnoreCase);
        // TLS 报告本身不再触发 TLS-RPT 事件，避免报告投递失败形成反馈循环。
        Task RecordTlsEventAsync(
            string mxHost,
            string policyType,
            string policyString,
            string resultType,
            string? failureReason) =>
            suppressTlsReporting
                ? Task.CompletedTask
                : transportSecurity.RecordEventAsync(
                    domain, mxHost, policyType, policyString, resultType, failureReason, cancellationToken);
        var profile = await OutboundDeliveryProfile.ResolveAsync(dbFactory, job, cancellationToken);
        var useRelay = relaySettings is not null && (await relaySettings.ReadAsync(cancellationToken)).SendEnabled;
        var stsPolicy = await transportSecurity.ResolveMtaStsAsync(domain, cancellationToken);
        return await DeliverAcrossMxAsync(hosts, async host =>
        {
            // DANE 只有在 MX 查询具备可信 DNSSEC 结果时才启用；否则优先使用 MTA-STS 策略。
            var danePolicy = mxDnssecAuthenticated
                ? await transportSecurity.ResolveDaneAsync(host, cancellationToken)
                : null;
            var policyType = danePolicy is not null ? "tlsa" : stsPolicy is not null ? "sts" : "no-policy-found";
            var policyString = danePolicy is not null
                ? string.Join(';', danePolicy.Associations.Select(x => x.PolicyString))
                : stsPolicy is not null
                    ? $"version: STSv1; mode: {stsPolicy.Mode}; mx: {string.Join(',', stsPolicy.MxPatterns)}"
                    : string.Empty;
            if (danePolicy is null && stsPolicy is not null && !stsPolicy.MatchesMx(host))
            {
                // enforce 模式必须阻断不匹配的 MX；testing 模式只记录事件并继续尝试。
                await RecordTlsEventAsync(
                    host, "sts", policyString, "validation-failure",
                    "MX host does not match the MTA-STS policy.");
                if (stsPolicy.Mode == "enforce")
                {
                    return DeliveryResult.Temporary("MX host does not match the enforced MTA-STS policy.", host, new DeliveryDiagnostic("TLS", DurationMilliseconds: stopwatch.ElapsedMilliseconds, FailureCategory: "mta-sts-policy"));
                }
            }
            string? remoteAddress = null;
            try
            {
                await using var messageStream = new MemoryStream(messageBytes, writable: false);
                var message = await OutboundMessage.LoadAndSignAsync(messageStream, job, dbFactory, srs, cancellationToken);
                using var client = new SmtpClient();
                client.Timeout = 60_000;
                client.LocalDomain = profile.HeloName;
                SslPolicyErrors? testingPolicyErrors = null;
                if (danePolicy is not null)
                {
                    client.ServerCertificateValidationCallback = (_, certificate, chain, errors) =>
                        TransportSecurityPolicyService.ValidateDaneCertificate(certificate, chain, errors, danePolicy);
                }
                else if (stsPolicy?.Mode == "testing")
                {
                    client.ServerCertificateValidationCallback = (_, _, _, errors) =>
                    {
                        testingPolicyErrors = errors;
                        return true;
                    };
                }
                var deliverySecurity = danePolicy is not null || stsPolicy?.Mode == "enforce"
                    ? SecureSocketOptions.StartTls
                    : DeliverySecurity;
                logger.LogInformation(
                    "Direct MX delivery job {JobId} connecting to {Host}:{Port} for recipient={Recipient}, security={Security}, helo={HeloName}, sourceIps={SourceIps}.",
                    job.Id,
                    host,
                    DeliveryPort,
                    job.Recipient,
                    deliverySecurity,
                    profile.HeloName,
                    useRelay ? "relay-assigned" : string.Join(',', profile.SourceAddresses.Select(x => x.ToString())));
                await using var relayConnection = useRelay
                    ? await (relay ?? throw new IOException("Relay process is not available."))
                        .OpenOutboundAsync(host, cancellationToken)
                    : null;
                if (relayConnection is not null)
                {
                    await client.ConnectAsync(relayConnection.Stream, host, DeliveryPort, deliverySecurity, cancellationToken);
                    remoteAddress = $"{relayConnection.RemoteAddress}（中继出口 {relayConnection.SourceAddress}）";
                }
                else
                {
                    remoteAddress = (await OutboundDeliveryProfile.ConnectAsync(
                        client, host, DeliveryPort, deliverySecurity,
                        profile.SourceAddresses, cancellationToken)).ToString();
                }
                var response = await client.SendAsync(
                    message,
                    MailboxAddress.Parse(job.Sender),
                    [MailboxAddress.Parse(job.Recipient)],
                    cancellationToken);
                var tlsProtocol = client.IsSecure ? client.SslProtocol.ToString() : null;
                var tlsCipherSuite = client.IsSecure ? client.SslCipherSuite.ToString() : null;
                await client.DisconnectAsync(true, cancellationToken);
                await RecordTlsEventAsync(
                    host,
                    policyType,
                    policyString,
                    testingPolicyErrors is not null and not SslPolicyErrors.None
                        ? "certificate-not-trusted"
                        : "success",
                    testingPolicyErrors?.ToString());
                logger.LogInformation(
                    "Direct MX delivery job {JobId} succeeded via {Host}:{Port}: recipient={Recipient}, response={Response}.",
                    job.Id,
                    host,
                    DeliveryPort,
                    job.Recipient,
                    response);
                return DeliveryResult.Success(response, $"{host}:{DeliveryPort}", new DeliveryDiagnostic("SMTP", RemoteAddress: remoteAddress, SmtpStatusCode: "250", TlsProtocol: tlsProtocol, TlsCipherSuite: tlsCipherSuite, DurationMilliseconds: stopwatch.ElapsedMilliseconds, SessionSummary: response));
            }
            catch (SmtpCommandException exception)
            {
                // 单个 MX 的 5xx 通常代表该主机明确拒绝本次投递，直接返回永久失败。
                await RecordTlsEventAsync(host, policyType, policyString, "smtp-error", exception.Message);
                logger.LogWarning(
                    exception,
                    "MX host {Host}:{Port} rejected delivery job {JobId}: sender={Sender}, recipient={Recipient}.",
                    host,
                    DeliveryPort,
                    job.Id,
                    job.Sender,
                    job.Recipient);
                if ((int)exception.StatusCode >= 500)
                {
                    return DeliveryResult.Permanent(exception.Message, $"{host}:{DeliveryPort}", new DeliveryDiagnostic("SMTP", RemoteAddress: remoteAddress?.ToString(), SmtpStatusCode: ((int)exception.StatusCode).ToString(), EnhancedStatusCode: ExtractEnhancedStatusCode(exception.Message), DurationMilliseconds: stopwatch.ElapsedMilliseconds, FailureCategory: "smtp-permanent", SessionSummary: exception.Message));
                }

                return DeliveryResult.Temporary(exception.Message, $"{host}:{DeliveryPort}", new DeliveryDiagnostic("SMTP", RemoteAddress: remoteAddress?.ToString(), SmtpStatusCode: ((int)exception.StatusCode).ToString(), EnhancedStatusCode: ExtractEnhancedStatusCode(exception.Message), DurationMilliseconds: stopwatch.ElapsedMilliseconds, FailureCategory: "smtp-temporary", SessionSummary: exception.Message));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var resultType = exception switch
                {
                    System.Security.Authentication.AuthenticationException => "certificate-not-trusted",
                    NotSupportedException when exception.Message.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)
                        => "starttls-not-supported",
                    _ => "validation-failure"
                };
                await RecordTlsEventAsync(host, policyType, policyString, resultType, exception.Message);
                logger.LogWarning(
                    exception,
                    "MX host {Host}:{Port} failed delivery job {JobId}: sender={Sender}, recipient={Recipient}.",
                    host,
                    DeliveryPort,
                    job.Id,
                    job.Sender,
                    job.Recipient);
                var stage = exception is System.Security.Authentication.AuthenticationException
                    || exception is NotSupportedException && exception.Message.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)
                    ? "TLS" : "CONNECT";
                return DeliveryResult.Temporary(exception.Message, $"{host}:{DeliveryPort}", new DeliveryDiagnostic(stage, DurationMilliseconds: stopwatch.ElapsedMilliseconds, FailureCategory: resultType, SessionSummary: exception.Message));
            }
        }, () => DeliveryResult.Temporary("No MX host accepted the message.", diagnostic: new DeliveryDiagnostic("CONNECT", DurationMilliseconds: stopwatch.ElapsedMilliseconds, FailureCategory: "mx-exhausted")));
    }

    internal static async Task<DeliveryResult> DeliverAcrossMxAsync(
        IReadOnlyList<string> hosts,
        Func<string, Task<DeliveryResult>> deliverToHost,
        Func<DeliveryResult> noHostsResult)
    {
        DeliveryResult? lastResult = null;
        foreach (var host in hosts)
        {
            var result = await deliverToHost(host);
            if (result.Succeeded || result.PermanentFailure) return result;
            lastResult = result;
        }
        return lastResult ?? noHostsResult();
    }

    private static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken cancellationToken)
    {
        await using var memory = new MemoryStream();
        await content.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }
}

internal sealed record OutboundDeliveryProfile(
    string HeloName,
    IReadOnlyList<IPAddress> SourceAddresses)
{
    public static async Task<OutboundDeliveryProfile> ResolveAsync(
        IDbContextFactory<MailDbContext> dbFactory,
        DeliveryJob job,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Domains.AsNoTracking();
        MailDomain? domain = job.RouteDomainId is { } routeDomainId
            ? await query.SingleOrDefaultAsync(x => x.Id == routeDomainId, cancellationToken)
            : null;
        if (domain is null && AddressParser.TrySplit(job.Sender, out _, out var senderDomain))
        {
            domain = await query.SingleOrDefaultAsync(x => x.Name == senderDomain, cancellationToken);
        }

        var heloName = domain is null
            ? "mail.localhost"
            : FirstNonEmpty(domain.SmtpHostName, domain.MailHostName, $"mail.{domain.Name}")!;
        var sourceAddresses = domain?.OutboundIpAddresses?
            .Split(['\r', '\n', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .Where(address => address is not null)
            .Cast<IPAddress>()
            .DistinctBy(address => address.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        return new OutboundDeliveryProfile(heloName, sourceAddresses);
    }

    public static async Task<IPAddress> ConnectAsync(
        SmtpClient client,
        string host,
        int port,
        SecureSocketOptions options,
        IReadOnlyList<IPAddress> sourceAddresses,
        CancellationToken cancellationToken,
        IReadOnlyList<IPAddress>? resolvedAddresses = null)
    {
        var remoteAddresses = resolvedAddresses ?? await Dns.GetHostAddressesAsync(host, cancellationToken);
        Exception? lastException = null;
        foreach (var sourceAddress in sourceAddresses.Cast<IPAddress?>().Append(null))
        {
            foreach (var remoteAddress in remoteAddresses.Where(x => sourceAddress is null || x.AddressFamily == sourceAddress.AddressFamily))
            {
                var socket = new Socket(remoteAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    if (sourceAddress is not null) socket.Bind(new IPEndPoint(sourceAddress, 0));
                    await socket.ConnectAsync(new IPEndPoint(remoteAddress, port), cancellationToken);
                    await client.ConnectAsync(socket, host, port, options, cancellationToken);
                    return remoteAddress;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastException = exception;
                    socket.Dispose();
                }
            }
        }
        throw new IOException($"All IP addresses for MX host {host} failed.", lastException);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

}

internal static class OutboundMessage
{
    public static Task<MimeMessage> LoadAndSignAsync(
        Stream content,
        string sender,
        IDbContextFactory<MailDbContext> dbFactory,
        CancellationToken cancellationToken) =>
        LoadAndSignAsync(content, sender, null, dbFactory, null, cancellationToken);

    public static Task<MimeMessage> LoadAndSignAsync(
        Stream content,
        DeliveryJob job,
        IDbContextFactory<MailDbContext> dbFactory,
        SrsService srs,
        CancellationToken cancellationToken) =>
        LoadAndSignAsync(content, job.Sender, job, dbFactory, srs, cancellationToken);

    private static async Task<MimeMessage> LoadAndSignAsync(
        Stream content,
        string sender,
        DeliveryJob? job,
        IDbContextFactory<MailDbContext> dbFactory,
        SrsService? srs,
        CancellationToken cancellationToken)
    {
        var message = await MimeMessage.LoadAsync(content, cancellationToken);
        var hasSenderDomain = AddressParser.TrySplit(sender, out _, out var domainName);
        var hasForwardingTrace = job?.OriginRuleId is not null && job.ForwardDepth > 0 && srs is not null;
        if (!hasSenderDomain && !hasForwardingTrace)
        {
            return message;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (hasForwardingTrace)
        {
            message.MessageId ??= MimeUtils.GenerateMessageId();
            message.Headers.Add(
                SrsService.ForwardingTraceHeader,
                srs!.CreateForwardingTrace(db, job!.OriginRuleId!.Value, job.ForwardDepth, message.MessageId));
            await db.SaveChangesAsync(cancellationToken);
        }

        if (!hasSenderDomain) return message;
        var domain = await db.Domains.SingleOrDefaultAsync(x => x.Name == domainName, cancellationToken);
        if (domain is null
            || string.IsNullOrWhiteSpace(domain.DkimSelector)
            || string.IsNullOrWhiteSpace(domain.DkimPrivateKeyPath)
            || !File.Exists(domain.DkimPrivateKeyPath))
        {
            return message;
        }

        var signer = new DkimSigner(domain.DkimPrivateKeyPath, domain.Name, domain.DkimSelector)
        {
            SignatureAlgorithm = DkimSignatureAlgorithm.RsaSha256,
            AgentOrUserIdentifier = $"@{domain.Name}"
        };
        message.Prepare(EncodingConstraint.EightBit);
        signer.Sign(
            message,
            [
                HeaderId.From,
                HeaderId.To,
                HeaderId.Cc,
                HeaderId.Subject,
                HeaderId.Date,
                HeaderId.MessageId,
                HeaderId.MimeVersion,
                HeaderId.ContentType
            ]);
        return message;
    }
}
