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
    IOptions<DeliveryOptions> deliveryDefaults,
    ILogger<DeliveryWorker> logger,
    ResourceRuntime? resources = null,
    DeliveryWakeSignal? deliveryWake = null) : BackgroundService
{
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:{System.Guid.NewGuid()}";
    private readonly AdjustableConcurrencyLimit _deliveryLimit = resources?.Delivery ?? new(8);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        resources?.SetServiceState("worker", "working");
        var running = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                running.RemoveAll(task => task.IsCompleted);
                if (_deliveryLimit.Available == 0)
                {
                    resources?.Telemetry.RecordOutboundDemand();
                    await Task.WhenAny(running.Append(Task.Delay(250, stoppingToken)));
                    continue;
                }
                DeliveryOptions options;
                try
                {
                    options = await settings.GetDeliveryOptionsAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Unable to read delivery settings; using configured defaults for this poll.");
                    options = deliveryDefaults.Value;
                }
                IReadOnlyList<DeliveryLease> jobs;
                var observedVersion = deliveryWake?.Version ?? 0;
                var permits = new List<IDisposable>();
                try
                {
                    for (var index = 0; index < Math.Max(1, options.BatchSize); index++)
                    {
                        var permit = _deliveryLimit.TryAcquire();
                        if (permit is null) break;
                        permits.Add(permit);
                    }
                    if (permits.Count == 0) continue;
                    jobs = await queue.LeaseAsync(
                        _owner,
                        permits.Count,
                        TimeSpan.FromSeconds(options.LeaseSeconds),
                        stoppingToken);
                }
                catch (Exception exception)
                {
                    foreach (var permit in permits) permit.Dispose();
                    if (stoppingToken.IsCancellationRequested) break;
                    logger.LogError(exception, "Unable to lease delivery jobs.");
                    await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
                    continue;
                }
                foreach (var permit in permits.Skip(jobs.Count)) permit.Dispose();

                if (jobs.Count == 0)
                {
                    if (deliveryWake is null)
                        await Task.Delay(TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
                    else
                        await deliveryWake.WaitForChangeAsync(observedVersion,
                            TimeSpan.FromSeconds(options.PollIntervalSeconds), stoppingToken);
                    continue;
                }

                logger.LogInformation(
                    "Delivery worker leased {Count} job(s).",
                    jobs.Count);
                var batches = new Dictionary<long, DeliveryBatch>();
                if (transport is IBatchOutboundTransport batchTransport)
                {
                    foreach (var group in jobs.GroupBy(x => (x.Job.MessageId, x.Job.Sender, x.Job.RouteDomainId,
                        x.Job.OriginRuleId, x.Job.ForwardDepth, Domain: RecipientDomain(x.Job.Recipient))))
                    {
                        var members = group.ToArray();
                        if (members.Length < 2) continue;
                        var batch = new DeliveryBatch(members.Length, batchTransport, contentStore);
                        foreach (var member in members) batches[member.Id] = batch;
                    }
                }
                for (var index = 0; index < jobs.Count; index++)
                    running.Add(ProcessLimitedAsync(jobs[index], options, permits[index], stoppingToken, batches.GetValueOrDefault(jobs[index].Id)));
            }
        }
        finally
        {
            resources?.SetServiceState("worker", "stopped");
            await Task.WhenAll(running);
        }
    }

    private async Task ProcessLimitedAsync(DeliveryLease lease, DeliveryOptions options, IDisposable permit, CancellationToken token, DeliveryBatch? batch = null)
    {
        using (permit)
        {
            using var performance = PerformanceTelemetry.Operation("worker", "delivery.attempt", lease.Job.SourceTraceId ?? lease.Job.Message.SourceTraceId);
            PerformanceTelemetry.Resource(lease.Job.Message.Id, lease.Job.Id, lease.Job.Attempts);
            if (performance is not null)
            {
                performance.SetQueueTimes(lease.QueueWaitMilliseconds, lease.RetryBackoffMilliseconds);
            }
            try { await ProcessAsync(lease, options, token, batch); }
            catch (OperationCanceledException error) when (token.IsCancellationRequested) { performance?.Fail(error); }
            catch (Exception error) { performance?.Fail(error); logger.LogError(error, "Delivery task {JobId} failed without stopping the worker.", lease.Id); }
            finally { batch?.Skip(lease.Id); }
        }
    }

    private async ValueTask ProcessAsync(
        DeliveryLease lease,
        DeliveryOptions options,
        CancellationToken cancellationToken,
        DeliveryBatch? batch = null)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var job = lease.Job;
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var leaseLost = 0;
        var renewal = RenewLeaseAsync(
            lease,
            TimeSpan.FromSeconds(options.LeaseSeconds),
            attemptCancellation,
            () => Interlocked.Exchange(ref leaseLost, 1));
        try
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
                    attemptCancellation.Token);
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
                    if (batch is not null) result = await batch.DeliverAsync(job, attemptCancellation.Token);
                    else
                    {
                        await using var content = await contentStore.OpenReadAsync(job.Message.ContentKey, attemptCancellation.Token);
                        result = await transport.DeliverAsync(job, content, attemptCancellation.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (Volatile.Read(ref leaseLost) == 1 && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Delivery job {JobId} stopped because its lease was lost.", job.Id);
                attemptCancellation.Cancel();
                await renewal;
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Unexpected failure processing delivery job {JobId}.", job.Id);
                result = DeliveryResult.Temporary(exception.Message);
            }

            DeliveryCompletion completion;
            if (!result.Succeeded) PerformanceTelemetry.Status("error");
            if (Volatile.Read(ref leaseLost) == 1) return;
            try
            {
                completion = await queue.CompleteAsync(lease, result, attemptCancellation.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Unable to complete delivery job {JobId}; the lease will be allowed to expire.", job.Id);
                attemptCancellation.Cancel();
                await renewal;
                return;
            }
            attemptCancellation.Cancel();
            await renewal;
            if (!completion.Applied) return;
            if (result.Succeeded) resources?.Telemetry.RecordCompletion(false, stopwatch.Elapsed);
            try
            {
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
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Delivery completion plugin failed for job {JobId}.", job.Id);
            }
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

            if (!result.Succeeded && completion.FinalFailure)
            {
                // 最终失败才生成退信，避免临时网络故障时提前通知发件人。
                logger.LogWarning(
                    "Delivery job {JobId} reached final failure state; creating bounce for sender={Sender}, recipient={Recipient}.",
                    job.Id,
                    job.Sender,
                    job.Recipient);
                try
                {
                    await bounceService.CreateAsync(job, result.Response, result.PermanentFailure, cancellationToken, result.Diagnostic?.EnhancedStatusCode);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Unable to create final bounce for delivery job {JobId}.", job.Id);
                }
            }
        }
        finally
        {
            attemptCancellation.Cancel();
            await renewal;
        }
    }

    private static string RecipientDomain(string recipient)
    {
        var at = recipient.LastIndexOf('@');
        return at < 0 ? recipient : recipient[(at + 1)..].TrimEnd('.').ToLowerInvariant();
    }

    // Each member keeps its own plugin decision, lease renewal, completion and bounce.
    // Only members which pass policy join the shared SMTP transaction.
    private sealed class DeliveryBatch(int count, IBatchOutboundTransport transport, IMessageContentStore store)
    {
        private readonly object _gate = new();
        private readonly HashSet<long> _arrived = [];
        private readonly List<(DeliveryJob Job, CancellationToken Token)> _ready = [];
        private readonly TaskCompletionSource<IReadOnlyDictionary<long, DeliveryResult>> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Skip(long id) => Arrive(id, null, default);
        public async Task<DeliveryResult> DeliverAsync(DeliveryJob job, CancellationToken token)
        {
            Arrive(job.Id, job, token);
            var results = await _completed.Task.WaitAsync(token);
            return results[job.Id];
        }
        private void Arrive(long id, DeliveryJob? job, CancellationToken token)
        {
            bool start;
            lock (_gate)
            {
                if (!_arrived.Add(id)) return;
                if (job is not null) _ready.Add((job, token));
                start = _arrived.Count == count;
            }
            if (start) _ = SendAsync();
        }
        private async Task SendAsync()
        {
            if (_ready.Count == 0) { _completed.TrySetResult(new Dictionary<long, DeliveryResult>()); return; }
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_ready.Select(x => x.Token).ToArray());
            try
            {
                await using var content = await store.OpenReadAsync(_ready[0].Job.Message.ContentKey, lifetime.Token);
                _completed.TrySetResult(await transport.DeliverBatchAsync(_ready.Select(x => x.Job).ToArray(), content, lifetime.Token));
            }
            catch (OperationCanceledException) { _completed.TrySetCanceled(lifetime.Token); }
            catch (Exception error)
            {
                _completed.TrySetResult(_ready.ToDictionary(x => x.Job.Id, _ => DeliveryResult.Temporary(error.Message)));
            }
        }
    }

    private async Task RenewLeaseAsync(
        DeliveryLease lease,
        TimeSpan duration,
        CancellationTokenSource attemptCancellation,
        Action markLost)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, duration.TotalSeconds / 3));
        try
        {
            while (!attemptCancellation.IsCancellationRequested)
            {
                await Task.Delay(interval, attemptCancellation.Token);
                if (!await queue.RenewAsync(lease, duration, attemptCancellation.Token))
                {
                    markLost();
                    logger.LogWarning("Delivery job {JobId} lost its lease during renewal.", lease.Job.Id);
                    attemptCancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            markLost();
            logger.LogError(exception, "Delivery job {JobId} lease renewal failed.", lease.Job.Id);
            attemptCancellation.Cancel();
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
        notifications.CreateAsync(failedJob.Sender, failedJob.Recipient, reason, failedJob.Id.ToString(),
            failedJob.Attempts, DeliveryNotificationService.SelectKind(permanentFailure, enhancedStatusCode), cancellationToken);
}
