using MailServer.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Utils;

namespace MailServer.Infrastructure;

public enum DeliveryNotificationKind { PermanentFailure, RetryExhausted, QuotaExceeded }

/// <summary>独立提交通知；空信封发件人确保通知不会继续产生退信。</summary>
public sealed class DeliveryNotificationService(
    IInboundMessageService inbound,
    SystemSettingsService settings,
    IConfiguration configuration,
    ILogger<DeliveryNotificationService> logger)
{
    public static DeliveryNotificationKind SelectKind(bool permanentFailure, string? enhancedStatusCode) =>
        (permanentFailure && enhancedStatusCode == "5.2.2") || (!permanentFailure && enhancedStatusCode == "4.2.2")
            ? DeliveryNotificationKind.QuotaExceeded
            : permanentFailure ? DeliveryNotificationKind.PermanentFailure : DeliveryNotificationKind.RetryExhausted;

    public async Task CreateAsync(string sender, string recipient, string reason, string queueId, int attempts,
        DeliveryNotificationKind kind, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sender)) return;
        try
        {
            var hostName = configuration["Worker:HostName"] ?? configuration["Smtp:HostName"] ?? "mail.localhost";
            var templates = await settings.GetMessageTemplateSettingsAsync(cancellationToken);
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["recipient"] = recipient, ["reason"] = reason, ["queueId"] = queueId,
                ["attempts"] = attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            var (subject, body) = kind switch
            {
                DeliveryNotificationKind.QuotaExceeded => (templates.QuotaExceededSubject!, templates.QuotaExceededBody!),
                DeliveryNotificationKind.PermanentFailure => (templates.PermanentFailureSubject, templates.PermanentFailureBody),
                _ => (templates.RetryExhaustedSubject, templates.RetryExhaustedBody)
            };
            var html = SystemSettingsService.RenderHtmlTemplate(body, variables);
            var message = new MimeMessage
            {
                Sender = new MailboxAddress(templates.SenderDisplayName, $"postmaster@{hostName}"),
                From = { new MailboxAddress(templates.SenderDisplayName, $"postmaster@{hostName}") },
                To = { MailboxAddress.Parse(sender) },
                Subject = SystemSettingsService.RenderTemplate(subject, variables),
                MessageId = MimeUtils.GenerateMessageId(hostName), Date = DateTimeOffset.UtcNow,
                Body = new BodyBuilder { TextBody = SystemSettingsService.HtmlToPlainText(html), HtmlBody = html }.ToMessageBody()
            };
            message.Headers.Add("Auto-Submitted", "auto-replied");
            await using var memory = new MemoryStream();
            await message.WriteToAsync(memory, cancellationToken);
            memory.Position = 0;
            await inbound.AcceptAsync(new InboundEnvelope(string.Empty, [sender], "system", "local", hostName)
                { SourceType = "bounce" },
                memory, cancellationToken);
        }
        catch (Exception exception) when (exception is MailboxQuotaExceededException or DomainQuotaExceededException)
        {
            logger.LogWarning("Delivery notification to {Sender} could not be stored because its destination quota was exceeded; reference={QueueId}.", sender, queueId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unable to create delivery notification to {Sender}; reference={QueueId}.", sender, queueId);
        }
    }
}
