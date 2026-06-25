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
    IOptions<DeliveryOptions> options,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
    private readonly DeliveryOptions _options = options.Value;
    private readonly Dictionary<OutboundMode, IOutboundTransport> _transports = transports.ToDictionary(x => x.Mode);
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<DeliveryJob> jobs;
            try
            {
                jobs = await queue.LeaseAsync(
                    _owner,
                    _options.BatchSize,
                    TimeSpan.FromSeconds(_options.LeaseSeconds),
                    stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Unable to lease delivery jobs.");
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
                continue;
            }

            if (jobs.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
                continue;
            }

            await Parallel.ForEachAsync(
                jobs,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(_options.BatchSize, 8), CancellationToken = stoppingToken },
                ProcessAsync);
        }
    }

    private async ValueTask ProcessAsync(DeliveryJob job, CancellationToken cancellationToken)
    {
        DeliveryResult result;
        try
        {
            var decision = await pipeline.OnDeliveryAsync(
                new DeliveryContext(job.Id, job.Sender, job.Recipient, job.Attempts),
                cancellationToken);
            if (decision.Kind != PluginDecisionKind.Continue)
            {
                result = decision.Kind == PluginDecisionKind.Defer
                    ? DeliveryResult.Temporary(decision.Message ?? "Deferred by delivery policy.")
                    : DeliveryResult.Permanent(decision.Message ?? "Rejected by delivery policy.");
            }
            else if (!_transports.TryGetValue(job.RouteMode, out var transport))
            {
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
        if (!result.Succeeded && (result.PermanentFailure || job.Attempts >= _options.MaximumAttempts))
        {
            await bounceService.CreateAsync(job, result.Response, cancellationToken);
        }
    }
}

public sealed class BounceService(
    IInboundMessageService inbound,
    IConfiguration configuration,
    ILogger<BounceService> logger)
{
    public async Task CreateAsync(DeliveryJob failedJob, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(failedJob.Sender))
        {
            return;
        }

        var hostName = configuration["Worker:HostName"] ?? "mail.localhost";
        var message = new MimeMessage
        {
            Sender = new MailboxAddress("Mail Delivery Subsystem", $"postmaster@{hostName}"),
            From = { new MailboxAddress("Mail Delivery Subsystem", $"postmaster@{hostName}") },
            To = { MailboxAddress.Parse(failedJob.Sender) },
            Subject = "Delivery Status Notification (Failure)",
            MessageId = MimeUtils.GenerateMessageId(hostName),
            Date = DateTimeOffset.UtcNow,
            Body = new TextPart("plain")
            {
                Text = $"""
                    This is an automatically generated delivery status notification.

                    Delivery to {failedJob.Recipient} failed permanently.

                    Remote response:
                    {reason}

                    Original queue id: {failedJob.Id:N}
                    """
            }
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
