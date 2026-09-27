using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.PluginSdk;
using MailServer.PluginSdk.Delivery;
using Microsoft.Extensions.Options;

namespace MailServer.Worker;

/// <summary>
/// 外发投递后台服务。它只租约并处理队列任务，任务状态转换由 IDeliveryQueue 统一落库。
/// </summary>
public sealed class DeliveryWorker(
    IDeliveryQueue queue,
    IMessageContentStore contentStore,
    IOutboundTransport transport,
    PluginPipeline pipeline,
    BounceService bounceService,
    SystemSettingsService settings,
    ILogger<DeliveryWorker> logger) : BackgroundService
{
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
            // 单批最多 8 个并发，避免 DNS/SMTP 连接风暴；更高吞吐应通过多实例和队列租约扩展。
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
                "Direct MX delivery job {JobId} started: sender={Sender}, recipient={Recipient}, attempt={Attempt}.",
                job.Id,
                job.Sender,
                job.Recipient,
                job.Attempts);
            var decision = await pipeline.OnDeliveryAsync(
                new DeliveryContext(job.Id, job.Sender, job.Recipient, job.Attempts),
                cancellationToken);
            if (decision.Kind != PluginDecisionKind.Continue)
            {
                // 投递插件在真正连接远端前运行，可用于合规拦截、维护窗口暂停或自定义路由禁用。
                logger.LogInformation(
                    "Delivery job {JobId} was handled by plugin policy: decision={Decision}, message={Message}.",
                    job.Id,
                    decision.Kind,
                    decision.Message ?? string.Empty);
                result = decision.Kind == PluginDecisionKind.Defer
                    ? DeliveryResult.Temporary(decision.Message ?? "Deferred by delivery policy.")
                    : DeliveryResult.Permanent(decision.Message ?? "Rejected by delivery policy.");
            }
            else
            {
                // 传输实现只负责一次投递尝试；成功/失败、退避和退信由队列和 Worker 统一处理。
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
            // 最终失败才生成退信，避免临时网络故障时提前通知发件人。
            logger.LogWarning(
                "Delivery job {JobId} reached final failure state; creating bounce for sender={Sender}, recipient={Recipient}.",
                job.Id,
                job.Sender,
                job.Recipient);
            await bounceService.CreateAsync(job, result.Response, result.PermanentFailure, cancellationToken, result.Diagnostic?.EnhancedStatusCode);
        }
    }
}

/// <summary>
/// 退信生成服务。退信重新走入站链路，保证它也会被存储、规则处理和审计记录。
/// </summary>
public sealed class BounceService(DeliveryNotificationService notifications)
{
    public Task CreateAsync(DeliveryJob failedJob, string reason, bool permanentFailure,
        CancellationToken cancellationToken, string? enhancedStatusCode = null) =>
        notifications.CreateAsync(failedJob.Sender, failedJob.Recipient, reason, failedJob.Id.ToString("N"),
            failedJob.Attempts, DeliveryNotificationService.SelectKind(permanentFailure, enhancedStatusCode), cancellationToken);
}
