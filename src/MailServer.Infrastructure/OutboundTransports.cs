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

namespace MailServer.Infrastructure;

public sealed class RelaySmtpTransport(
    IOptions<RelayOptions> options,
    IDbContextFactory<MailDbContext> dbFactory,
    SrsService srs,
    ILogger<RelaySmtpTransport> logger) : IOutboundTransport
{
    private readonly RelayOptions _options = options.Value;

    public OutboundMode Mode => OutboundMode.Relay;

    public async Task<DeliveryResult> DeliverAsync(
        DeliveryJob job,
        Stream content,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = await OutboundMessage.LoadAndSignAsync(content, job, dbFactory, srs, cancellationToken);
            var profile = await OutboundDeliveryProfile.ResolveAsync(dbFactory, job, cancellationToken);
            using var client = new SmtpClient();
            client.LocalDomain = profile.HeloName;
            logger.LogInformation(
                "Relay delivery job {JobId} connecting to {Host}:{Port}: sender={Sender}, recipient={Recipient}, security={Security}, helo={HeloName}, sourceIps={SourceIps}.",
                job.Id,
                _options.Host,
                _options.Port,
                job.Sender,
                job.Recipient,
                _options.EffectiveSecurity,
                profile.HeloName,
                string.Join(',', profile.SourceAddresses.Select(x => x.ToString())));
            await OutboundDeliveryProfile.ConnectAsync(
                client,
                _options.Host,
                _options.Port,
                ResolveSecureSocketOptions(_options),
                profile.SourceAddresses,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(_options.UserName))
            {
                await client.AuthenticateAsync(_options.UserName, _options.Password ?? string.Empty, cancellationToken);
            }

            var response = await client.SendAsync(
                message,
                ParseSender(job.Sender),
                [MailboxAddress.Parse(job.Recipient)],
                cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
            logger.LogInformation(
                "Relay delivery job {JobId} succeeded via {Host}: recipient={Recipient}, response={Response}.",
                job.Id,
                _options.Host,
                job.Recipient,
                response);
            return DeliveryResult.Success(response, _options.Host);
        }
        catch (SmtpCommandException exception)
        {
            logger.LogWarning(
                exception,
                "Relay rejected delivery job {JobId}: sender={Sender}, recipient={Recipient}, host={Host}.",
                job.Id,
                job.Sender,
                job.Recipient,
                _options.Host);
            var permanent = (int)exception.StatusCode >= 500;
            return permanent
                ? DeliveryResult.Permanent(exception.Message, _options.Host)
                : DeliveryResult.Temporary(exception.Message, _options.Host);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Relay delivery job {JobId} failed: sender={Sender}, recipient={Recipient}, host={Host}.",
                job.Id,
                job.Sender,
                job.Recipient,
                _options.Host);
            return DeliveryResult.Temporary(exception.Message, _options.Host);
        }
    }

    private static MailboxAddress ParseSender(string sender) =>
        string.IsNullOrWhiteSpace(sender) ? new MailboxAddress(string.Empty, "postmaster@localhost") : MailboxAddress.Parse(sender);

    internal static SecureSocketOptions ResolveSecureSocketOptions(RelayOptions options) =>
        options.EffectiveSecurity switch
        {
            RelaySecurity.Auto => SecureSocketOptions.Auto,
            RelaySecurity.Plain => SecureSocketOptions.None,
            RelaySecurity.StartTls => SecureSocketOptions.StartTls,
            RelaySecurity.StartTlsWhenAvailable => SecureSocketOptions.StartTlsWhenAvailable,
            RelaySecurity.ImplicitTls => SecureSocketOptions.SslOnConnect,
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.EffectiveSecurity, null)
        };
}

public sealed class DirectMxTransport(
    LookupClient lookupClient,
    IDbContextFactory<MailDbContext> dbFactory,
    SrsService srs,
    TransportSecurityPolicyService transportSecurity,
    ILogger<DirectMxTransport> logger) : IOutboundTransport
{
    internal const int DeliveryPort = 25;
    internal const SecureSocketOptions DeliverySecurity = SecureSocketOptions.StartTlsWhenAvailable;

    public OutboundMode Mode => OutboundMode.DirectMx;

    public async Task<DeliveryResult> DeliverAsync(
        DeliveryJob job,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(job.Recipient, out _, out var domain))
        {
            return DeliveryResult.Permanent("Invalid recipient address.");
        }

        IReadOnlyList<string> hosts;
        var mxDnssecAuthenticated = false;
        try
        {
            var response = await lookupClient.QueryAsync(domain, QueryType.MX, cancellationToken: cancellationToken);
            mxDnssecAuthenticated = transportSecurity.IsTrustedDnssecResponse(response);
            hosts = response.Answers.MxRecords()
                .OrderBy(x => x.Preference)
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
            return DeliveryResult.Temporary($"MX lookup failed: {exception.Message}");
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
        var stsPolicy = await transportSecurity.ResolveMtaStsAsync(domain, cancellationToken);
        DeliveryResult? lastResult = null;
        foreach (var host in hosts)
        {
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
                await RecordTlsEventAsync(
                    host, "sts", policyString, "validation-failure",
                    "MX host does not match the MTA-STS policy.");
                if (stsPolicy.Mode == "enforce")
                {
                    lastResult = DeliveryResult.Temporary("MX host does not match the enforced MTA-STS policy.", host);
                    continue;
                }
            }
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
                    string.Join(',', profile.SourceAddresses.Select(x => x.ToString())));
                await OutboundDeliveryProfile.ConnectAsync(
                    client,
                    host,
                    DeliveryPort,
                    deliverySecurity,
                    profile.SourceAddresses,
                    cancellationToken);
                var response = await client.SendAsync(
                    message,
                    MailboxAddress.Parse(job.Sender),
                    [MailboxAddress.Parse(job.Recipient)],
                    cancellationToken);
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
                return DeliveryResult.Success(response, $"{host}:{DeliveryPort}");
            }
            catch (SmtpCommandException exception)
            {
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
                    return DeliveryResult.Permanent(exception.Message, $"{host}:{DeliveryPort}");
                }

                lastResult = DeliveryResult.Temporary(exception.Message, $"{host}:{DeliveryPort}");
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
                lastResult = DeliveryResult.Temporary(exception.Message, $"{host}:{DeliveryPort}");
            }
        }

        return lastResult ?? DeliveryResult.Temporary("No MX host accepted the message.");
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

    public static async Task ConnectAsync(
        SmtpClient client,
        string host,
        int port,
        SecureSocketOptions options,
        IReadOnlyList<IPAddress> sourceAddresses,
        CancellationToken cancellationToken)
    {
        if (sourceAddresses.Count == 0)
        {
            await client.ConnectAsync(host, port, options, cancellationToken);
            return;
        }

        var remoteAddresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        Exception? lastException = null;
        foreach (var sourceAddress in sourceAddresses)
        {
            foreach (var remoteAddress in remoteAddresses.Where(x => x.AddressFamily == sourceAddress.AddressFamily))
            {
                var socket = new Socket(remoteAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(sourceAddress, 0));
                    await socket.ConnectAsync(new IPEndPoint(remoteAddress, port), cancellationToken);
                    await client.ConnectAsync(socket, host, port, options, cancellationToken);
                    return;
                }
                catch (Exception exception) when (exception is SocketException or IOException or TimeoutException or InvalidOperationException)
                {
                    lastException = exception;
                    socket.Dispose();
                }
            }
        }

        try
        {
            await client.ConnectAsync(host, port, options, cancellationToken);
        }
        catch (Exception exception) when (
            lastException is not null &&
            exception is SocketException or IOException or TimeoutException or InvalidOperationException)
        {
            throw new AggregateException("All configured outbound source addresses failed, and the system default connection also failed.", lastException, exception);
        }
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
