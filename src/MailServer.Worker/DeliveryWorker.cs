using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.PluginSdk;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Utils;

namespace MailServer.Worker;

public sealed class DeliveryWorker(
    IDeliveryQueue queue,
    IMessageContentStore contentStore,
    IEnumerable<IOutboundTransport> transports,
    PluginPipeline pipeline,
    BounceService bounceService,
    SystemSettingsService settings,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    private readonly Dictionary<OutboundMode, IOutboundTransport> _transports = transports.ToDictionary(x => x.Mode);
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = await settings.GetDeliveryOptionsAsync(stoppingToken);
            IReadOnlyList<DeliveryJob> jobs;
            try
            {
                jobs = await queue.LeaseAsync(
                    _owner,
                    options.BatchSize,
                    TimeSpan.FromSeconds(options.LeaseSeconds),
                    stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Unable to lease delivery jobs.");
                await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
                continue;
            }

            if (jobs.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
                continue;
            }

            logger.LogInformation(
                "Delivery worker leased {Count} job(s).",
                jobs.Count);
            await Parallel.ForEachAsync(
                jobs,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(options.BatchSize, 8), CancellationToken = stoppingToken },
                (job, cancellationToken) => ProcessAsync(job, options, cancellationToken));
        }
    }

    private async ValueTask ProcessAsync(
        DeliveryJob job,
        DeliveryOptions options,
        CancellationToken cancellationToken)
    {
        DeliveryResult result;
        try
        {
            logger.LogInformation(
                "Delivery job {JobId} started: sender={Sender}, recipient={Recipient}, routeMode={RouteMode}, attempt={Attempt}.",
                job.Id,
                job.Sender,
                job.Recipient,
                job.RouteMode,
                job.Attempts);
            var decision = await pipeline.OnDeliveryAsync(
                new DeliveryContext(job.Id, job.Sender, job.Recipient, job.Attempts),
                cancellationToken);
            if (decision.Kind != PluginDecisionKind.Continue)
            {
                logger.LogInformation(
                    "Delivery job {JobId} was handled by plugin policy: decision={Decision}, message={Message}.",
                    job.Id,
                    decision.Kind,
                    decision.Message ?? string.Empty);
                result = decision.Kind == PluginDecisionKind.Defer
                    ? DeliveryResult.Temporary(decision.Message ?? "Deferred by delivery policy.")
                    : DeliveryResult.Permanent(decision.Message ?? "Rejected by delivery policy.");
            }
            else if (!_transports.TryGetValue(job.RouteMode, out var transport))
            {
                logger.LogWarning(
                    "Delivery job {JobId} has no outbound transport for routeMode={RouteMode}.",
                    job.Id,
                    job.RouteMode);
                result = DeliveryResult.Permanent($"No outbound transport for {job.RouteMode}.");
            }
            else
            {
                await using var content = await contentStore.OpenReadAsync(job.Message.ContentKey, cancellationToken);
                result = await transport.DeliverAsync(job, content, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unexpected failure processing delivery job {JobId}.", job.Id);
            result = DeliveryResult.Temporary(exception.Message);
        }

        await queue.CompleteAsync(job.Id, result, cancellationToken);
        await pipeline.OnDeliveryCompletedAsync(
            new DeliveryCompletedContext(
                job.Id,
                job.Sender,
                job.Recipient,
                job.Attempts,
                result.Succeeded,
                result.PermanentFailure,
                result.Response,
                result.RemoteHost),
            cancellationToken);
        if (result.Succeeded)
        {
            logger.LogInformation(
                "Delivery job {JobId} succeeded: recipient={Recipient}, remoteHost={RemoteHost}, response={Response}.",
                job.Id,
                job.Recipient,
                result.RemoteHost ?? string.Empty,
                result.Response);
        }
        else
        {
            logger.LogInformation(
                "Delivery job {JobId} did not succeed: recipient={Recipient}, permanentFailure={PermanentFailure}, response={Response}.",
                job.Id,
                job.Recipient,
                result.PermanentFailure,
                result.Response);
        }

        if (!result.Succeeded && (result.PermanentFailure || job.Attempts >= options.MaximumAttempts))
        {
            logger.LogWarning(
                "Delivery job {JobId} reached final failure state; creating bounce for sender={Sender}, recipient={Recipient}.",
                job.Id,
                job.Sender,
                job.Recipient);
            await bounceService.CreateAsync(job, result.Response, result.PermanentFailure, cancellationToken);
        }
    }
}

public sealed class BounceService(
    IInboundMessageService inbound,
    SystemSettingsService settings,
    IConfiguration configuration,
    ILogger<BounceService> logger)
{
    public async Task CreateAsync(
        DeliveryJob failedJob,
        string reason,
        bool permanentFailure,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(failedJob.Sender))
        {
            return;
        }

        var hostName = configuration["Worker:HostName"] ?? "mail.localhost";
        var templates = await settings.GetMessageTemplateSettingsAsync(cancellationToken);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["recipient"] = failedJob.Recipient,
            ["reason"] = reason,
            ["queueId"] = failedJob.Id.ToString("N"),
            ["attempts"] = failedJob.Attempts.ToString()
        };
        var bodyTemplate = permanentFailure
            ? templates.PermanentFailureBody
            : templates.RetryExhaustedBody;
        var subjectTemplate = permanentFailure
            ? templates.PermanentFailureSubject
            : templates.RetryExhaustedSubject;
        var htmlBody = SystemSettingsService.RenderHtmlTemplate(bodyTemplate, variables);
        var message = new MimeMessage
        {
            Sender = new MailboxAddress(templates.SenderDisplayName, $"postmaster@{hostName}"),
            From = { new MailboxAddress(templates.SenderDisplayName, $"postmaster@{hostName}") },
            To = { MailboxAddress.Parse(failedJob.Sender) },
            Subject = SystemSettingsService.RenderTemplate(subjectTemplate, variables),
            MessageId = MimeUtils.GenerateMessageId(hostName),
            Date = DateTimeOffset.UtcNow,
            Body = new BodyBuilder
            {
                TextBody = SystemSettingsService.HtmlToPlainText(htmlBody),
                HtmlBody = htmlBody
            }.ToMessageBody()
        };

        await using var memory = new MemoryStream();
        await message.WriteToAsync(memory, cancellationToken);
        memory.Position = 0;
        try
        {
            await inbound.AcceptAsync(
                new InboundEnvelope(
                    string.Empty,
                    [failedJob.Sender],
                    "system",
                    "local",
                    hostName),
                memory,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Unable to create bounce for delivery job {JobId}.", failedJob.Id);
        }
    }
}
