using DnsClient;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;

namespace MailServer.Infrastructure;

public sealed class RelaySmtpTransport(
    IOptions<RelayOptions> options,
    IDbContextFactory<MailDbContext> dbFactory,
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
            var message = await OutboundMessage.LoadAndSignAsync(content, job.Sender, dbFactory, cancellationToken);
            using var client = new SmtpClient();
            await client.ConnectAsync(
                _options.Host,
                _options.Port,
                _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto,
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
            return DeliveryResult.Success(response, _options.Host);
        }
        catch (SmtpCommandException exception)
        {
            logger.LogWarning(exception, "Relay rejected delivery job {JobId}", job.Id);
            var permanent = (int)exception.StatusCode >= 500;
            return permanent
                ? DeliveryResult.Permanent(exception.Message, _options.Host)
                : DeliveryResult.Temporary(exception.Message, _options.Host);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Relay delivery job {JobId} failed", job.Id);
            return DeliveryResult.Temporary(exception.Message, _options.Host);
        }
    }

    private static MailboxAddress ParseSender(string sender) =>
        string.IsNullOrWhiteSpace(sender) ? new MailboxAddress(string.Empty, "postmaster@localhost") : MailboxAddress.Parse(sender);
}

public sealed class DirectMxTransport(
    LookupClient lookupClient,
    IDbContextFactory<MailDbContext> dbFactory,
    ILogger<DirectMxTransport> logger) : IOutboundTransport
{
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
        try
        {
            var response = await lookupClient.QueryAsync(domain, QueryType.MX, cancellationToken: cancellationToken);
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
            return DeliveryResult.Temporary($"MX lookup failed: {exception.Message}");
        }

        var messageBytes = await ReadAllAsync(content, cancellationToken);
        DeliveryResult? lastResult = null;
        foreach (var host in hosts)
        {
            try
            {
                await using var messageStream = new MemoryStream(messageBytes, writable: false);
                var message = await OutboundMessage.LoadAndSignAsync(messageStream, job.Sender, dbFactory, cancellationToken);
                using var client = new SmtpClient();
                client.Timeout = 60_000;
                await client.ConnectAsync(host, 25, SecureSocketOptions.StartTlsWhenAvailable, cancellationToken);
                var response = await client.SendAsync(
                    message,
                    MailboxAddress.Parse(job.Sender),
                    [MailboxAddress.Parse(job.Recipient)],
                    cancellationToken);
                await client.DisconnectAsync(true, cancellationToken);
                return DeliveryResult.Success(response, host);
            }
            catch (SmtpCommandException exception)
            {
                logger.LogWarning(exception, "MX host {Host} rejected delivery job {JobId}", host, job.Id);
                if ((int)exception.StatusCode >= 500)
                {
                    return DeliveryResult.Permanent(exception.Message, host);
                }

                lastResult = DeliveryResult.Temporary(exception.Message, host);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "MX host {Host} failed delivery job {JobId}", host, job.Id);
                lastResult = DeliveryResult.Temporary(exception.Message, host);
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

internal static class OutboundMessage
{
    public static async Task<MimeMessage> LoadAndSignAsync(
        Stream content,
        string sender,
        IDbContextFactory<MailDbContext> dbFactory,
        CancellationToken cancellationToken)
    {
        var message = await MimeMessage.LoadAsync(content, cancellationToken);
        if (!AddressParser.TrySplit(sender, out _, out var domainName))
        {
            return message;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
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
