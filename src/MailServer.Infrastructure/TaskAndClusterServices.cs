using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Net.Http.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed class MaintenanceOptions
{
    public const string SectionName = "Maintenance";
    public int SoftDeleteRetentionDays { get; set; } = 30;
    public int PollIntervalSeconds { get; set; } = 30;
    public int TemporaryFileRetentionHours { get; set; } = 24;
    public int OrphanFileRetentionDays { get; set; } = 7;
}

public sealed class ClusterOptions
{
    public const string SectionName = "Cluster";
    public bool Enabled { get; set; }
    public bool RecoveryRequired { get; set; }
    public string NodeId { get; set; } = Environment.MachineName;
    public string NodeName { get; set; } = Environment.MachineName;
    public ClusterNodeRole Role { get; set; } = ClusterNodeRole.Primary;
    public string Endpoint { get; set; } = string.Empty;
    public int LeaseSeconds { get; set; } = 15;
    public int AcknowledgementTimeoutSeconds { get; set; } = 10;
    public bool AllowDegradedWrites { get; set; }
    public string WitnessEndpoint { get; set; } = string.Empty;
    public string PrimaryEndpoint { get; set; } = string.Empty;
    public int ReplicationPollSeconds { get; set; } = 2;
}

public sealed class ClusterTransportOptions
{
    public const string SectionName = "ClusterTransport";
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 2526;
    public string AdvertiseEndpoint { get; set; } = string.Empty;
    public string CertificatePath { get; set; } = "security-assets/cluster/node.crt";
    public string PrivateKeyPath { get; set; } = "security-assets/cluster/node.key";
    public string TrustedCaPath { get; set; } = "security-assets/cluster/ca.crt";
    public int ConnectTimeoutSeconds { get; set; } = 15;
    public int ContentChunkBytes { get; set; } = 64 * 1024;
}

public sealed class ClusterSyncThrottleOptions
{
    public const string SectionName = "ClusterSyncThrottle";
    public bool Enabled { get; set; } = true;
    public int SampleIntervalSeconds { get; set; } = 10;
    public int BusyHoldSeconds { get; set; } = 60;
    public int RecoverySamples { get; set; } = 3;
    public int NormalBatchSize { get; set; } = 200;
    public int BusyMetadataBatchSize { get; set; } = 20;
    public int CpuBusyPercent { get; set; } = 85;
}

public sealed record ClusterSyncThrottleDecision(
    bool IsBusy,
    bool AllowContentTransfer,
    int BatchSize,
    string? Reason,
    double Score,
    double CpuPercent,
    long ReplicationBacklog,
    int DeliveryBacklog,
    int RunningTasks);

public static class ClusterHostValidator
{
    public static bool IsNotJoinedYet(ClusterOptions cluster, ClusterTransportOptions transport)
    {
        if (!cluster.Enabled) return true;
        if (!transport.Enabled) return true;
        if (string.IsNullOrWhiteSpace(cluster.NodeId)) return true;
        if (string.IsNullOrWhiteSpace(transport.CertificatePath)
            || string.IsNullOrWhiteSpace(transport.PrivateKeyPath)
            || string.IsNullOrWhiteSpace(transport.TrustedCaPath))
            return true;
        return false;
    }

    public static IReadOnlyList<string> ValidateWitnessHost(
        ClusterOptions cluster,
        ClusterTransportOptions transport,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var errors = new List<string>();
        if (!cluster.Enabled) errors.Add("Cluster:Enabled must be true");
        if (cluster.Role != ClusterNodeRole.Witness)
            errors.Add("Cluster:Role must be Witness for 'mailserver run cluster'");
        if (string.IsNullOrWhiteSpace(cluster.NodeId)) errors.Add("Cluster:NodeId is required");
        if (!transport.Enabled) errors.Add("ClusterTransport:Enabled must be true");
        if (!Uri.TryCreate(transport.AdvertiseEndpoint, UriKind.Absolute, out var advertise)
            || advertise.Scheme != "tls")
            errors.Add("ClusterTransport:AdvertiseEndpoint must be an absolute tls:// URL");
        if (transport.Port is <= 0 or > 65535) errors.Add("ClusterTransport:Port must be between 1 and 65535");
        AddFileError(errors, "ClusterTransport:CertificatePath", transport.CertificatePath, fileExists);
        AddFileError(errors, "ClusterTransport:PrivateKeyPath", transport.PrivateKeyPath, fileExists);
        AddFileError(errors, "ClusterTransport:TrustedCaPath", transport.TrustedCaPath, fileExists);
        return errors;
    }

    private static void AddFileError(List<string> errors, string name, string value, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{name} is required");
        else if (!fileExists(Path.GetFullPath(value))) errors.Add($"{name} was not found: {value}");
    }
}

public sealed class ClusterTransportRuntimeState
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _sync = new();
    private CancellationTokenSource _stop = new();
    private volatile bool _stopRequested;

    public bool IsListening { get; set; }
    public string Status { get; set; } = "disabled";
    public string? LastError { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public bool StopRequested => _stopRequested;

    public void RequestStart()
    {
        lock (_sync)
        {
            _stopRequested = false;
            if (_stop.IsCancellationRequested)
            {
                _stop.Dispose();
                _stop = new CancellationTokenSource();
            }
        }
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public void RequestStop()
    {
        lock (_sync)
        {
            _stopRequested = true;
            _stop.Cancel();
        }
    }

    public CancellationToken GetStopToken()
    {
        lock (_sync) return _stop.Token;
    }

    public async Task WaitForStartRequestAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await _signal.WaitAsync(timeout, cancellationToken);
}

public sealed record WitnessLeaseRequest(
    string NodeId,
    int LeaseSeconds,
    long AppliedSequence = 0,
    long SafeSequence = 0,
    string? Endpoint = null,
    ClusterNodeRole Role = ClusterNodeRole.Standby);
public sealed record WitnessLeaseResponse(bool Granted, long Term, DateTimeOffset ExpiresAt, string? LeaderNodeId = null, long RequiredSafeSequence = 0);

public sealed class QuotaLedgerService(IDbContextFactory<MailDbContext> factory) : IQuotaLedgerService
{
    public async Task ApplyAsync(Guid accountId, Guid domainId, Guid? mailboxMessageId, long bytes,
        QuotaLedgerOperation operation, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (await db.QuotaLedger.AnyAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken)) return;
        var account = await db.Accounts.SingleAsync(x => x.Id == accountId, cancellationToken);
        var domain = await db.Domains.SingleAsync(x => x.Id == domainId, cancellationToken);
        var delta = operation == QuotaLedgerOperation.Remove ? -Math.Abs(bytes) : Math.Abs(bytes);
        account.UsedBytes = Math.Max(0, account.UsedBytes + delta);
        domain.UsedBytes = Math.Max(0, domain.UsedBytes + delta);
        db.QuotaLedger.Add(new QuotaLedgerEntry
        {
            IdempotencyKey = idempotencyKey,
            AccountId = accountId,
            DomainId = domainId,
            MailboxMessageId = mailboxMessageId,
            Operation = operation,
            Bytes = delta
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class QuotaReconciliationService(
    IDbContextFactory<MailDbContext> factory,
    IClock clock) : IQuotaReconciliationService
{
    public async Task<ScheduledTaskRunResult> ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var accounts = await db.Accounts.ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var account in accounts)
        {
            var actual = await db.MailboxMessages
                .Where(x => x.Mailbox.AccountId == account.Id)
                .SumAsync(x => (long?)x.Message.Size, cancellationToken) ?? 0;
            if (account.UsedBytes == actual) continue;
            account.UsedBytes = actual;
            changed++;
        }
        var domains = await db.Domains.ToListAsync(cancellationToken);
        foreach (var domain in domains)
        {
            var actual = accounts.Where(x => x.DomainId == domain.Id).Sum(x => x.UsedBytes);
            if (domain.UsedBytes == actual) continue;
            domain.UsedBytes = actual;
            changed++;
        }
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = "system",
            Action = "quota.reconcile",
            Resource = "quota:all",
            Detail = $"corrected={changed}; at={clock.UtcNow:O}"
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ScheduledTaskRunResult(accounts.Count + domains.Count, 0, $"Corrected {changed} counters.");
    }
}

public sealed class CleanupQueueService(
    IDbContextFactory<MailDbContext> factory,
    IMessageContentStore contentStore,
    IClock clock) : ICleanupQueueService, IRecycleBinService
{
    public async Task EnqueueAsync(CleanupQueueItem item, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (item.ResourceId is { } resourceId
            && await db.CleanupQueue.AnyAsync(x => x.ResourceType == item.ResourceType
                && x.ResourceId == resourceId && x.Status == CleanupQueueStatus.Pending, cancellationToken)) return;
        db.CleanupQueue.Add(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecycleBinItem>> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.CleanupQueue.AsNoTracking()
            .Where(x => x.Status == CleanupQueueStatus.Pending || x.Status == CleanupQueueStatus.Failed)
            .OrderBy(x => x.PurgeAfter).ToListAsync(cancellationToken);
        var resourceIds = rows.Where(x => x.ResourceId.HasValue).Select(x => x.ResourceId!.Value).Distinct().ToArray();
        var domainNames = (await db.Domains.IgnoreQueryFilters().AsNoTracking()
                .Where(x => resourceIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Name })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => x.Name);
        var accountNames = (await db.Accounts.IgnoreQueryFilters().AsNoTracking()
                .Include(x => x.Domain)
                .Where(x => resourceIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => $"{x.LocalPart}@{x.Domain.Name}");
        var mailboxNames = (await db.Mailboxes.IgnoreQueryFilters().AsNoTracking()
                .Include(x => x.Account).ThenInclude(x => x.Domain)
                .Where(x => resourceIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => $"{x.Account.LocalPart}@{x.Account.Domain.Name} / {x.Name}");
        var messageNames = (await db.MailboxMessages.IgnoreQueryFilters().AsNoTracking()
                .Include(x => x.Mailbox).ThenInclude(x => x.Account).ThenInclude(x => x.Domain)
                .Where(x => resourceIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, x => $"{x.Mailbox.Account.LocalPart}@{x.Mailbox.Account.Domain.Name} / {x.Mailbox.Name} / UID {x.Uid}");

        string DisplayName(CleanupQueueItem item)
        {
            if (item.ResourceId is not { } id) return item.ContentKey ?? item.ResourceType.ToString();
            return item.ResourceType switch
            {
                CleanupResourceType.Domain when domainNames.TryGetValue(id, out var name) => name,
                CleanupResourceType.Account when accountNames.TryGetValue(id, out var name) => name,
                CleanupResourceType.Mailbox when mailboxNames.TryGetValue(id, out var name) => name,
                CleanupResourceType.MailboxMessage when messageNames.TryGetValue(id, out var name) => name,
                _ => item.ContentKey ?? id.ToString()
            };
        }

        return rows.Select(x => new RecycleBinItem(x.Id, x.ResourceType, x.ResourceId,
            DisplayName(x), x.EstimatedBytes, x.CreatedAt, x.PurgeAfter, x.Status)).ToArray();
    }

    public async Task RestoreAsync(Guid queueId, string actor, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await db.CleanupQueue.SingleAsync(x => x.Id == queueId, cancellationToken);
        if (item.Status is CleanupQueueStatus.Completed or CleanupQueueStatus.Processing)
            throw new InvalidOperationException("The item can no longer be restored.");
        if (item.ResourceId is not { } id) throw new InvalidOperationException("The item has no restorable resource.");
        switch (item.ResourceType)
        {
            case CleanupResourceType.Domain:
                var domain = await db.Domains.IgnoreQueryFilters().SingleAsync(x => x.Id == id, cancellationToken);
                domain.DeletedAt = domain.PurgeAfter = null; domain.DeletedBy = null; domain.IsActive = true;
                foreach (var account in await db.Accounts.IgnoreQueryFilters().Where(x => x.DomainId == id).ToListAsync(cancellationToken))
                { account.DeletedAt = account.PurgeAfter = null; account.DeletedBy = null; account.IsEnabled = true; }
                var restoredAccountIds = await db.Accounts.IgnoreQueryFilters().Where(x => x.DomainId == id).Select(x => x.Id).ToListAsync(cancellationToken);
                var restoredMailboxes = await db.Mailboxes.IgnoreQueryFilters().Where(x => restoredAccountIds.Contains(x.AccountId)).ToListAsync(cancellationToken);
                foreach (var mailbox in restoredMailboxes)
                { mailbox.DeletedAt = mailbox.PurgeAfter = null; mailbox.DeletedBy = null; }
                var restoredMailboxIds = restoredMailboxes.Select(x => x.Id).ToArray();
                foreach (var mailboxMessage in await db.MailboxMessages.IgnoreQueryFilters().Where(x => restoredMailboxIds.Contains(x.MailboxId)).ToListAsync(cancellationToken))
                { mailboxMessage.DeletedAt = mailboxMessage.PurgeAfter = null; mailboxMessage.DeletedBy = null; }
                break;
            case CleanupResourceType.Account:
                var restoredAccount = await db.Accounts.IgnoreQueryFilters().SingleAsync(x => x.Id == id, cancellationToken);
                restoredAccount.DeletedAt = restoredAccount.PurgeAfter = null; restoredAccount.DeletedBy = null; restoredAccount.IsEnabled = true;
                var accountMailboxes = await db.Mailboxes.IgnoreQueryFilters().Where(x => x.AccountId == id).ToListAsync(cancellationToken);
                foreach (var accountMailbox in accountMailboxes)
                { accountMailbox.DeletedAt = accountMailbox.PurgeAfter = null; accountMailbox.DeletedBy = null; }
                var accountMailboxIds = accountMailboxes.Select(x => x.Id).ToArray();
                foreach (var accountMailboxMessage in await db.MailboxMessages.IgnoreQueryFilters().Where(x => accountMailboxIds.Contains(x.MailboxId)).ToListAsync(cancellationToken))
                { accountMailboxMessage.DeletedAt = accountMailboxMessage.PurgeAfter = null; accountMailboxMessage.DeletedBy = null; }
                break;
            case CleanupResourceType.Mailbox:
                var restoredMailbox = await db.Mailboxes.IgnoreQueryFilters().SingleAsync(x => x.Id == id, cancellationToken);
                restoredMailbox.DeletedAt = restoredMailbox.PurgeAfter = null; restoredMailbox.DeletedBy = null;
                break;
            case CleanupResourceType.MailboxMessage:
                var message = await db.MailboxMessages.IgnoreQueryFilters().SingleAsync(x => x.Id == id, cancellationToken);
                message.DeletedAt = message.PurgeAfter = null; message.DeletedBy = null;
                break;
            default:
                throw new InvalidOperationException("This item cannot be restored.");
        }
        item.Status = CleanupQueueStatus.Restored;
        item.CompletedAt = clock.UtcNow;
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "recycle.restore", Resource = $"cleanup:{queueId}", Detail = item.ResourceType.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task PurgeAsync(Guid queueId, CancellationToken cancellationToken) =>
        await PurgeOneAsync(queueId, force: true, cancellationToken);

    public async Task<ScheduledTaskRunResult> PurgeDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var ids = await db.CleanupQueue.AsNoTracking()
            .Where(x => (x.Status == CleanupQueueStatus.Pending || x.Status == CleanupQueueStatus.Failed)
                && x.PurgeAfter <= clock.UtcNow)
            .OrderBy(x => x.PurgeAfter).Take(Math.Clamp(batchSize, 1, 1000))
            .Select(x => x.Id).ToListAsync(cancellationToken);
        long released = 0;
        foreach (var id in ids) released += await PurgeOneAsync(id, force: false, cancellationToken);
        return new ScheduledTaskRunResult(ids.Count, released);
    }

    private async Task<long> PurgeOneAsync(Guid queueId, bool force, CancellationToken cancellationToken)
    {
        var contentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long released = 0;
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var item = await db.CleanupQueue.SingleAsync(x => x.Id == queueId, cancellationToken);
            if (item.Status == CleanupQueueStatus.Completed || item.Status == CleanupQueueStatus.Restored) return 0;
            if (!force && item.PurgeAfter > clock.UtcNow) return 0;
            item.Status = CleanupQueueStatus.Processing;
            item.Attempts++;
            try
            {
                if (item.ResourceId is { } id)
                {
                    switch (item.ResourceType)
                    {
                        case CleanupResourceType.MailboxMessage:
                            var mm = await db.MailboxMessages.IgnoreQueryFilters().Include(x => x.Message).Include(x => x.Mailbox).ThenInclude(x => x.Account)
                                .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                            if (mm is not null)
                            {
                                contentKeys.Add(mm.Message.ContentKey); released = mm.Message.Size;
                                mm.Mailbox.Account.UsedBytes = Math.Max(0, mm.Mailbox.Account.UsedBytes - released);
                                var domain = await db.Domains.IgnoreQueryFilters().SingleAsync(x => x.Id == mm.Mailbox.Account.DomainId, cancellationToken);
                                domain.UsedBytes = Math.Max(0, domain.UsedBytes - released);
                                db.MailboxMessages.Remove(mm);
                                db.QuotaLedger.Add(new QuotaLedgerEntry
                                {
                                    IdempotencyKey = $"cleanup:{item.Id:N}:{mm.Id:N}",
                                    AccountId = mm.Mailbox.AccountId,
                                    DomainId = mm.Mailbox.Account.DomainId,
                                    MailboxMessageId = mm.Id,
                                    Operation = QuotaLedgerOperation.Remove,
                                    Bytes = -released
                                });
                            }
                            break;
                        case CleanupResourceType.Mailbox:
                            var mailbox = await db.Mailboxes.IgnoreQueryFilters().Include(x => x.Account).ThenInclude(x => x.Domain)
                                .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                            if (mailbox is not null)
                            {
                                var mailboxMessages = await db.MailboxMessages.IgnoreQueryFilters().Where(x => x.MailboxId == id)
                                    .Include(x => x.Message).ToListAsync(cancellationToken);
                                released = mailboxMessages.Sum(x => x.Message.Size);
                                foreach (var value in mailboxMessages) contentKeys.Add(value.Message.ContentKey);
                                mailbox.Account.UsedBytes = Math.Max(0, mailbox.Account.UsedBytes - released);
                                mailbox.Account.Domain.UsedBytes = Math.Max(0, mailbox.Account.Domain.UsedBytes - released);
                                db.Mailboxes.Remove(mailbox);
                            }
                            break;
                        case CleanupResourceType.Account:
                            var account = await db.Accounts.IgnoreQueryFilters().Include(x => x.Domain).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                            if (account is not null)
                            {
                                released = account.UsedBytes;
                                var accountKeys = await db.MailboxMessages.IgnoreQueryFilters()
                                    .Where(x => x.Mailbox.AccountId == id).Select(x => x.Message.ContentKey).ToListAsync(cancellationToken);
                                foreach (var value in accountKeys) contentKeys.Add(value);
                                account.Domain.UsedBytes = Math.Max(0, account.Domain.UsedBytes - account.UsedBytes);
                                db.Accounts.Remove(account);
                            }
                            break;
                        case CleanupResourceType.Domain:
                            var domainToDelete = await db.Domains.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                            if (domainToDelete is not null)
                            {
                                released = domainToDelete.UsedBytes;
                                var domainKeys = await db.MailboxMessages.IgnoreQueryFilters()
                                    .Where(x => x.Mailbox.Account.DomainId == id).Select(x => x.Message.ContentKey).ToListAsync(cancellationToken);
                                foreach (var value in domainKeys) contentKeys.Add(value);
                                db.Domains.Remove(domainToDelete);
                            }
                            break;
                    }
                }
                item.Status = CleanupQueueStatus.Completed; item.CompletedAt = clock.UtcNow; item.LastError = null;
                await db.SaveChangesAsync(cancellationToken);
                foreach (var key in contentKeys)
                {
                    var messages = await db.Messages.Where(x => x.ContentKey == key).ToListAsync(cancellationToken);
                    foreach (var message in messages)
                    {
                        var referenced = await db.MailboxMessages.IgnoreQueryFilters().AnyAsync(x => x.MessageId == message.Id, cancellationToken)
                                         || await db.DeliveryJobs.AnyAsync(x => x.MessageId == message.Id, cancellationToken)
                                         || await db.SentCopyJobs.AnyAsync(x => x.MessageId == message.Id, cancellationToken);
                        if (!referenced) db.Messages.Remove(message);
                    }
                }
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                item.Status = CleanupQueueStatus.Failed; item.LastError = exception.Message;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                throw;
            }
        }
        foreach (var key in contentKeys) await contentStore.DeleteIfUnreferencedAsync(key, cancellationToken);
        return released;
    }
}

public sealed class ReplicationJournal(IDbContextFactory<MailDbContext> factory) : IReplicationJournal
{
    public async Task<long> AppendAsync(string entityType, string entityKey, ReplicationOperation operation,
        string payloadJson, string? contentKey, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entry = new ReplicationLog { BatchId = Guid.NewGuid(), EntityType = entityType, EntityKey = entityKey, Operation = operation, PayloadJson = payloadJson, ContentKey = contentKey };
        db.ReplicationLogs.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return entry.Sequence;
    }
}

public sealed class DatabaseReplicationTransport(
    IDbContextFactory<MailDbContext> factory,
    IOptions<ClusterOptions> options,
    ClusterRuntimeState runtime,
    IClock clock) : IReplicationTransport
{
    public async Task<bool> WaitForAcknowledgementAsync(long sequence, CancellationToken cancellationToken)
    {
        if (!runtime.Enabled || sequence <= 0) return true;
        var deadline = clock.UtcNow.AddSeconds(Math.Clamp(options.Value.AcknowledgementTimeoutSeconds, 1, 300));
        while (clock.UtcNow < deadline)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (await db.ReplicationCursors.AnyAsync(cursor =>
                    cursor.NodeId != options.Value.NodeId
                    && cursor.LastAppliedSequence >= sequence
                    && db.ClusterNodes.Any(node =>
                        node.Id == cursor.NodeId
                        && node.Role == ClusterNodeRole.Standby
                        && node.Status == ClusterNodeStatus.Healthy
                        && node.RevokedAt == null), cancellationToken))
                return true;
            await Task.Delay(100, cancellationToken);
        }
        return options.Value.AllowDegradedWrites;
    }
}

public sealed class ClusterSyncThrottle(
    IDbContextFactory<MailDbContext> factory,
    IOptions<ClusterSyncThrottleOptions> options,
    IClock clock)
{
    private readonly Queue<Sample> _samples = new();
    private readonly object _gate = new();
    private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTimeOffset _lastCpuAt = DateTimeOffset.UtcNow;
    private DateTimeOffset? _busyUntil;
    private int _recoverySamples;
    private int _recentFailures;
    private long _lastDurationMs;

    public void RecordSyncResult(bool success, long durationMilliseconds)
    {
        lock (_gate)
        {
            _lastDurationMs = durationMilliseconds;
            _recentFailures = success ? 0 : Math.Min(_recentFailures + 1, 10);
        }
    }

    public async Task<ClusterSyncThrottleDecision> EvaluateAsync(long appliedSequence, CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (!configured.Enabled)
        {
            return new ClusterSyncThrottleDecision(false, true, Math.Clamp(configured.NormalBatchSize, 1, 1000), null, 0, 0, 0, 0, 0);
        }

        var now = clock.UtcNow;
        var cpu = SampleCpu(now);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var latestSequence = await db.ReplicationLogs.MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? appliedSequence;
        var replicationBacklog = Math.Max(0, latestSequence - appliedSequence);
        var deliveryBacklog = await db.DeliveryJobs.CountAsync(x =>
            x.Status == DeliveryStatus.Pending
            || x.Status == DeliveryStatus.Deferred
            || x.Status == DeliveryStatus.Processing, cancellationToken);
        var runningTasks = await db.ScheduledTasks.CountAsync(x => x.Status == ScheduledTaskStatus.Running, cancellationToken);

        long duration;
        int failures;
        lock (_gate)
        {
            duration = _lastDurationMs;
            failures = _recentFailures;
        }

        var score = 0d;
        var reasons = new List<string>();
        if (cpu >= Math.Clamp(configured.CpuBusyPercent, 50, 100))
        {
            score += 1.2;
            reasons.Add($"CPU {cpu:F0}%");
        }
        if (deliveryBacklog >= 50)
        {
            score += deliveryBacklog >= 200 ? 1.6 : 0.9;
            reasons.Add($"delivery backlog {deliveryBacklog}");
        }
        if (replicationBacklog >= 500)
        {
            score += replicationBacklog >= 5000 ? 1.6 : 0.8;
            reasons.Add($"replication backlog {replicationBacklog}");
        }
        if (runningTasks > 0)
        {
            score += runningTasks >= 3 ? 1.2 : 0.6;
            reasons.Add($"running tasks {runningTasks}");
        }
        if (duration >= 10_000)
        {
            score += 0.8;
            reasons.Add($"last sync took {duration / 1000}s");
        }
        if (failures > 0)
        {
            score += Math.Min(1.5, failures * 0.5);
            reasons.Add($"recent sync failures {failures}");
        }

        var busy = score >= 2.0;
        lock (_gate)
        {
            AddSample(new Sample(now, cpu, replicationBacklog, deliveryBacklog, runningTasks, score));
            if (busy)
            {
                _busyUntil = now.AddSeconds(Math.Clamp(configured.BusyHoldSeconds, 10, 3600));
                _recoverySamples = 0;
            }
            else if (_busyUntil is not null && now < _busyUntil)
            {
                busy = true;
            }
            else if (_busyUntil is not null)
            {
                _recoverySamples++;
                busy = _recoverySamples < Math.Clamp(configured.RecoverySamples, 1, 20);
                if (!busy)
                {
                    _busyUntil = null;
                    _recoverySamples = 0;
                }
            }
        }

        var reason = busy
            ? (reasons.Count == 0 ? "waiting for stable recovery samples" : string.Join("; ", reasons))
            : null;
        return new ClusterSyncThrottleDecision(
            busy,
            !busy,
            busy ? Math.Clamp(configured.BusyMetadataBatchSize, 1, 100) : Math.Clamp(configured.NormalBatchSize, 1, 1000),
            reason,
            score,
            cpu,
            replicationBacklog,
            deliveryBacklog,
            runningTasks);
    }

    private double SampleCpu(DateTimeOffset now)
    {
        var process = Process.GetCurrentProcess();
        var currentCpu = process.TotalProcessorTime;
        lock (_gate)
        {
            var elapsed = Math.Max(1, (now - _lastCpuAt).TotalMilliseconds);
            var cpuElapsed = Math.Max(0, (currentCpu - _lastCpu).TotalMilliseconds);
            _lastCpu = currentCpu;
            _lastCpuAt = now;
            return Math.Clamp(cpuElapsed / elapsed / Math.Max(1, Environment.ProcessorCount) * 100d, 0d, 100d);
        }
    }

    private void AddSample(Sample sample)
    {
        _samples.Enqueue(sample);
        var cutoff = sample.At.AddMinutes(-5);
        while (_samples.Count > 0 && _samples.Peek().At < cutoff) _samples.Dequeue();
    }

    private sealed record Sample(DateTimeOffset At, double CpuPercent, long ReplicationBacklog, int DeliveryBacklog, int RunningTasks, double Score);
}

public sealed class ClusterLeaseService(
    IDbContextFactory<MailDbContext> factory,
    IOptionsMonitor<ClusterOptions> options,
    IClock clock,
    ClusterTransportClient transport,
    ReplicationSchemaFingerprintProvider schemaFingerprint,
    ClusterRuntimeState runtime,
    ILogger<ClusterLeaseService> logger) : IClusterLeaseService, IClusterHealthService
{
    private readonly object _witnessFailureLock = new();
    private DateTimeOffset? _lastWitnessWarningAt;
    private ClusterOptions Options => options.CurrentValue;

    public async Task<bool> IsWritableAsync(CancellationToken cancellationToken)
    {
        if (!runtime.Enabled) return true;
        if (runtime.RecoveryRequired) return false;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ClusterLeases.AnyAsync(x => x.Name == "primary" && x.OwnerNodeId == Options.NodeId && x.ExpiresAt > clock.UtcNow, cancellationToken);
    }

    public async Task<bool> TryAcquirePrimaryAsync(CancellationToken cancellationToken)
    {
        if (!runtime.Enabled) return true;
        if (runtime.RecoveryRequired) return false;
        if (Options.Role == ClusterNodeRole.Witness) return false;
        await using var clusterDb = await factory.CreateDbContextAsync(cancellationToken);
        var configuration = await clusterDb.ClusterConfigurations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var witnessNode = await clusterDb.ClusterNodes.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Role == ClusterNodeRole.Witness && x.RevokedAt == null, cancellationToken);
        var witnessEndpoint = Options.WitnessEndpoint;
        if (string.IsNullOrWhiteSpace(witnessEndpoint))
            witnessEndpoint = witnessNode?.Endpoint ?? string.Empty;
        if (string.IsNullOrWhiteSpace(witnessEndpoint))
        {
            if (configuration?.PrimaryNodeId != Options.NodeId) return false;
            await using var bootstrapDb = await factory.CreateDbContextAsync(cancellationToken);
            var bootstrapLease = await bootstrapDb.ClusterLeases.SingleOrDefaultAsync(x => x.Name == "primary", cancellationToken);
            if (bootstrapLease is null)
            {
                bootstrapLease = new ClusterLease { Name = "primary", OwnerNodeId = Options.NodeId, Term = 1 };
                bootstrapDb.ClusterLeases.Add(bootstrapLease);
            }
            bootstrapLease.ExpiresAt = clock.UtcNow.AddSeconds(Math.Clamp(Options.LeaseSeconds, 5, 300));
            await bootstrapDb.SaveChangesAsync(cancellationToken);
            return true;
        }
        await using var sequenceDb = await factory.CreateDbContextAsync(cancellationToken);
        var appliedSequence = await sequenceDb.ReplicationCursors
            .Where(x => x.NodeId == Options.NodeId)
            .Select(x => (long?)x.LastAppliedSequence)
            .SingleOrDefaultAsync(cancellationToken)
            ?? await sequenceDb.ReplicationLogs.MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? 0;
        var safeSequence = await sequenceDb.ReplicationLogs.MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? appliedSequence;
        var clusterId = configuration?.Id ?? Guid.Empty;
        WitnessLeaseResponse? witness;
        try
        {
            witness = await transport.RequestWitnessLeaseAsync(
                witnessEndpoint,
                new ClusterHello(
                    clusterId,
                    Options.NodeId,
                    Options.Role,
                    ClusterBinaryProtocol.ProtocolVersion,
                    typeof(ClusterLeaseService).Assembly.GetName().Version?.ToString() ?? "unknown",
                    appliedSequence,
                    safeSequence,
                    null,
                    await schemaFingerprint.GetAsync(cancellationToken)),
                new WitnessLeaseRequest(
                    Options.NodeId,
                    Math.Clamp(Options.LeaseSeconds, 5, 300),
                    appliedSequence,
                    safeSequence,
                    Options.Endpoint,
                    Options.Role),
                cancellationToken);
            lock (_witnessFailureLock) _lastWitnessWarningAt = null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (witnessNode?.Status == ClusterNodeStatus.Synchronizing
                && configuration?.PrimaryNodeId == Options.NodeId)
            {
                await RenewLocalPrimaryLeaseAsync(cancellationToken);
                logger.LogDebug(exception, "The witness at {Endpoint} has not activated yet.", witnessEndpoint);
                return true;
            }
            LogWitnessFailure(witnessEndpoint, exception);
            return false;
        }
        if (witness?.Granted != true || appliedSequence < witness.RequiredSafeSequence) return false;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var lease = await db.ClusterLeases.SingleOrDefaultAsync(x => x.Name == "primary", cancellationToken);
        if (lease is not null && lease.ExpiresAt > clock.UtcNow && lease.OwnerNodeId != Options.NodeId) return false;
        if (lease is null)
        {
            lease = new ClusterLease { Name = "primary", OwnerNodeId = Options.NodeId, Term = witness.Term, ExpiresAt = witness.ExpiresAt };
            db.ClusterLeases.Add(lease);
        }
        else if (lease.OwnerNodeId != Options.NodeId) { lease.OwnerNodeId = Options.NodeId; }
        lease.Term = witness.Term;
        lease.ExpiresAt = witness.ExpiresAt;
        await UpsertNodeAsync(db, ClusterNodeRole.Primary, ClusterNodeStatus.Healthy, cancellationToken);
        var localConfiguration = await db.ClusterConfigurations.SingleOrDefaultAsync(cancellationToken);
        if (localConfiguration is not null) localConfiguration.PrimaryNodeId = Options.NodeId;
        var activatedWitness = await db.ClusterNodes.SingleOrDefaultAsync(
            x => x.Role == ClusterNodeRole.Witness && x.RevokedAt == null,
            cancellationToken);
        if (activatedWitness is not null)
        {
            activatedWitness.Status = ClusterNodeStatus.Healthy;
            activatedWitness.InitializationProgress = 100;
            activatedWitness.LastHeartbeatAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task RenewLocalPrimaryLeaseAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var lease = await db.ClusterLeases.SingleOrDefaultAsync(x => x.Name == "primary", cancellationToken);
        if (lease is null)
        {
            lease = new ClusterLease { Name = "primary", OwnerNodeId = Options.NodeId, Term = 1 };
            db.ClusterLeases.Add(lease);
        }
        if (lease.OwnerNodeId != Options.NodeId && lease.ExpiresAt > clock.UtcNow)
            throw new InvalidOperationException("A different primary still owns the local lease.");
        lease.OwnerNodeId = Options.NodeId;
        lease.ExpiresAt = clock.UtcNow.AddSeconds(Math.Clamp(Options.LeaseSeconds, 5, 300));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("The witness has not activated yet; the existing local primary lease was retained.");
    }

    private void LogWitnessFailure(string endpoint, Exception exception)
    {
        var now = clock.UtcNow;
        var warn = false;
        lock (_witnessFailureLock)
        {
            if (_lastWitnessWarningAt is null || now - _lastWitnessWarningAt >= TimeSpan.FromMinutes(5))
            {
                _lastWitnessWarningAt = now;
                warn = true;
            }
        }
        if (warn)
            logger.LogWarning(exception, "Unable to acquire the cluster witness lease from {Endpoint}; this node remains read-only.", endpoint);
        else
            logger.LogDebug(exception, "Cluster witness lease retry failed for {Endpoint}.", endpoint);
    }

    public async Task<ClusterHealth> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var lastSequence = await db.ReplicationLogs.MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? 0;
        var cursor = await db.ReplicationCursors.MaxAsync(x => (long?)x.LastAppliedSequence, cancellationToken) ?? lastSequence;
        var writable = await IsWritableAsync(cancellationToken);
        return new ClusterHealth(runtime.Enabled, writable, Options.NodeId, writable ? ClusterNodeRole.Primary : Options.Role,
            !runtime.Enabled || writable, lastSequence, Math.Max(0, lastSequence - cursor));
    }

    private async Task UpsertNodeAsync(MailDbContext db, ClusterNodeRole role, ClusterNodeStatus status, CancellationToken cancellationToken)
    {
        var node = await db.ClusterNodes.SingleOrDefaultAsync(x => x.Id == Options.NodeId, cancellationToken);
        if (node is null)
        {
            node = new ClusterNode { Id = Options.NodeId, Name = Options.NodeName, Role = role };
            db.ClusterNodes.Add(node);
        }
        node.Role = role; node.Status = status; node.Endpoint = Options.Endpoint;
        node.Version = typeof(ClusterLeaseService).Assembly.GetName().Version?.ToString() ?? "unknown";
        node.LastHeartbeatAt = clock.UtcNow;
    }
}

public sealed class WitnessLeaseService(
    IDbContextFactory<MailDbContext> factory,
    IClock clock)
{
    public async Task<WitnessLeaseResponse> AcquireAsync(WitnessLeaseRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NodeId)) return new WitnessLeaseResponse(false, 0, clock.UtcNow);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var candidate = await db.ClusterNodes.SingleOrDefaultAsync(x => x.Id == request.NodeId, cancellationToken);
        if (candidate is null)
        {
            candidate = new ClusterNode { Id = request.NodeId, Name = request.NodeId, Role = request.Role };
            db.ClusterNodes.Add(candidate);
        }
        candidate.Role = request.Role;
        candidate.Endpoint = request.Endpoint ?? candidate.Endpoint;
        candidate.AppliedSequence = Math.Max(candidate.AppliedSequence, request.AppliedSequence);
        candidate.SafeSequence = Math.Max(candidate.SafeSequence, request.SafeSequence);
        candidate.LastHeartbeatAt = clock.UtcNow;
        candidate.Status = ClusterNodeStatus.Healthy;
        var requiredSafeSequence = await db.ClusterNodes.MaxAsync(x => (long?)x.SafeSequence, cancellationToken) ?? 0;
        var lease = await db.ClusterLeases.SingleOrDefaultAsync(x => x.Name == "witness-primary", cancellationToken);
        if (lease is not null && lease.ExpiresAt > clock.UtcNow && lease.OwnerNodeId != request.NodeId)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WitnessLeaseResponse(false, lease.Term, lease.ExpiresAt, lease.OwnerNodeId, requiredSafeSequence);
        }
        if (request.Role == ClusterNodeRole.Standby && request.AppliedSequence < requiredSafeSequence)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new WitnessLeaseResponse(false, lease?.Term ?? 0, lease?.ExpiresAt ?? clock.UtcNow, lease?.OwnerNodeId, requiredSafeSequence);
        }
        if (lease is null)
        {
            lease = new ClusterLease { Name = "witness-primary", OwnerNodeId = request.NodeId, Term = 1, ExpiresAt = clock.UtcNow };
            db.ClusterLeases.Add(lease);
        }
        else if (lease.OwnerNodeId != request.NodeId)
        {
            lease.OwnerNodeId = request.NodeId;
            lease.Term++;
            db.ClusterEvents.Add(new ClusterEvent { Type = "automatic-failover", NodeId = request.NodeId, Detail = $"Witness granted term {lease.Term}." });
        }
        lease.ExpiresAt = clock.UtcNow.AddSeconds(Math.Clamp(request.LeaseSeconds, 5, 300));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new WitnessLeaseResponse(true, lease.Term, lease.ExpiresAt, lease.OwnerNodeId, requiredSafeSequence);
    }
}

public sealed class CleanupTaskHandler(ICleanupQueueService cleanup) : IScheduledTaskHandler
{
    public string Type => "cleanup.recycle";
    public Task<ScheduledTaskRunResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken) =>
        cleanup.PurgeDueAsync(task.BatchSize, cancellationToken);
}

public sealed class MailboxFolderDeletionTaskHandler(MailboxFolderManagementService folders) : IScheduledTaskHandler
{
    public string Type => "mailbox.folder-delete";
    public async Task<ScheduledTaskRunResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        var processed = await folders.ProcessPendingAsync(task.BatchSize, cancellationToken);
        return new ScheduledTaskRunResult(processed, 0);
    }
}

public sealed class QuotaTaskHandler(IQuotaReconciliationService quota) : IScheduledTaskHandler
{
    public string Type => "quota.reconcile";
    public Task<ScheduledTaskRunResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken) => quota.ReconcileAsync(cancellationToken);
}

public sealed class CacheCleanupTaskHandler(
    IDbContextFactory<MailDbContext> factory,
    IOptions<StorageOptions> storage,
    IOptions<MaintenanceOptions> maintenance,
    IClock clock) : IScheduledTaskHandler
{
    public string Type => "cache.cleanup";
    public async Task<ScheduledTaskRunResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var expired = await db.MtaStsPolicyCaches.Where(x => x.ExpiresAt < clock.UtcNow).ToListAsync(cancellationToken);
        db.MtaStsPolicyCaches.RemoveRange(expired);
        var srs = await db.SrsAddressMappings.Where(x => x.ExpiresAt < clock.UtcNow).ToListAsync(cancellationToken);
        db.SrsAddressMappings.RemoveRange(srs);
        var attemptCutoff = clock.UtcNow.AddDays(-30);
        var expiredAttempts = await db.DeliveryAttempts
            .Where(x => x.CreatedAt < attemptCutoff)
            .OrderBy(x => x.CreatedAt)
            .Take(task.BatchSize)
            .ToListAsync(cancellationToken);
        db.DeliveryAttempts.RemoveRange(expiredAttempts);
        await db.SaveChangesAsync(cancellationToken);
        var removed = 0;
        var temporary = Path.Combine(Path.GetFullPath(storage.Value.RootPath), ".tmp");
        if (Directory.Exists(temporary))
        {
            var cutoff = clock.UtcNow.AddHours(-Math.Max(1, maintenance.Value.TemporaryFileRetentionHours)).UtcDateTime;
            foreach (var file in Directory.EnumerateFiles(temporary))
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                File.Delete(file); removed++;
            }
        }
        return new ScheduledTaskRunResult(expired.Count + srs.Count + removed, 0);
    }
}

public sealed class ReplicationCheckTaskHandler(
    IClusterHealthService health,
    IDbContextFactory<MailDbContext> factory,
    IClock clock,
    IOptions<ClusterOptions> options) : IScheduledTaskHandler
{
    public string Type => "cluster.verify";
    public async Task<ScheduledTaskRunResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        var value = await health.GetAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var cutoff = clock.UtcNow.AddSeconds(-Math.Max(15, options.Value.ReplicationPollSeconds * 3));
        var stale = await db.ClusterNodes
            .Where(x => x.RevokedAt == null && x.Id != options.Value.NodeId
                        && x.LastHeartbeatAt < cutoff && x.Status != ClusterNodeStatus.Unreachable)
            .ToListAsync(cancellationToken);
        foreach (var node in stale)
        {
            node.Status = ClusterNodeStatus.Unreachable;
            db.ClusterEvents.Add(new ClusterEvent { Type = "node-unreachable", NodeId = node.Id, Detail = $"Last heartbeat {node.LastHeartbeatAt:O}." });
            db.AuditRecords.Add(new AuditRecord { Actor = "system", Action = "cluster.node.unreachable", Resource = $"cluster-node:{node.Id}", Category = AuditCategory.Node, Status = AuditStatus.Warning });
        }
        if (stale.Count > 0) await db.SaveChangesAsync(cancellationToken);
        return new ScheduledTaskRunResult(1, 0, JsonSerializer.Serialize(value));
    }
}

public sealed class OrphanStorageTaskHandler(
    IDbContextFactory<MailDbContext> factory,
    IOptions<StorageOptions> storage,
    IOptions<MaintenanceOptions> maintenance,
    IClock clock) : IScheduledTaskHandler
{
    public string Type => "storage.orphan-scan";
    public async Task<ScheduledTaskRunResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(storage.Value.RootPath);
        if (!Directory.Exists(root)) return new ScheduledTaskRunResult(0, 0);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var referenced = (await db.Messages.AsNoTracking().Select(x => x.ContentKey).Distinct().ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = clock.UtcNow.AddDays(-Math.Max(1, maintenance.Value.OrphanFileRetentionDays)).UtcDateTime;
        var count = 0;
        long released = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.eml", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Path.GetFileNameWithoutExtension(path);
            if (referenced.Contains(key) || File.GetLastWriteTimeUtc(path) > cutoff) continue;
            released += new FileInfo(path).Length;
            File.Delete(path);
            count++;
            if (count >= Math.Clamp(task.BatchSize, 1, 1000)) break;
        }
        return new ScheduledTaskRunResult(count, released);
    }
}

public sealed class StorageIntegrityTaskHandler(
    IDbContextFactory<MailDbContext> factory,
    IOptions<StorageOptions> storage) : IScheduledTaskHandler
{
    public string Type => "storage.integrity-check";
    public async Task<ScheduledTaskRunResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var root = Path.GetFullPath(storage.Value.RootPath);
        var keys = await db.Messages.AsNoTracking().Select(x => x.ContentKey).Distinct().OrderBy(x => x)
            .Take(Math.Clamp(task.BatchSize, 1, 10000)).ToListAsync(cancellationToken);
        var missing = keys.Where(key => !File.Exists(Path.Combine(root, key[..2], key[2..4], $"{key}.eml"))).ToArray();
        if (missing.Length > 0)
            db.AuditRecords.Add(new AuditRecord
            {
                Actor = "system", Action = "storage.integrity.missing-content", Resource = "storage:messages",
                Detail = string.Join(',', missing.Take(50))
            });
        await db.SaveChangesAsync(cancellationToken);
        return new ScheduledTaskRunResult(keys.Count, 0, $"Missing {missing.Length} content files.");
    }
}

public sealed class ReplicationSnapshotTaskHandler(
    IDbContextFactory<MailDbContext> factory,
    ClusterRuntimeState runtime) : IScheduledTaskHandler
{
    public string Type => "cluster.snapshot";
    public async Task<ScheduledTaskRunResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        if (!runtime.Enabled) return new ScheduledTaskRunResult(0, 0, "Cluster is disabled.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.ReplicationLogs.AnyAsync(x => x.Operation == ReplicationOperation.Snapshot, cancellationToken))
            return new ScheduledTaskRunResult(0, 0, "Initial snapshot already exists.");
        var logs = new List<ReplicationLog>();
        var addMethod = typeof(ReplicationSnapshotTaskHandler).GetMethod(
            nameof(AddAsync),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(AddAsync));
        foreach (var entityType in ReplicationModelDescriptor.GetSnapshotEntityTypes(db.Model))
        {
            var addTask = (Task)(addMethod.MakeGenericMethod(entityType.ClrType)
                .Invoke(null, [db, logs, cancellationToken])
                ?? throw new InvalidOperationException($"Unable to snapshot {entityType.ClrType.Name}."));
            await addTask;
        }
        db.ReplicationLogs.AddRange(logs);
        await db.SaveChangesAsync(cancellationToken);
        return new ScheduledTaskRunResult(logs.Count, 0, "Initial application-level snapshot created.");
    }

    private static async Task AddAsync<T>(
        MailDbContext db,
        List<ReplicationLog> logs,
        CancellationToken cancellationToken) where T : class
    {
        var metadata = db.Model.FindEntityType(typeof(T));
        var primaryKey = metadata?.FindPrimaryKey();
        if (metadata is null || primaryKey is null) return;
        var rows = await db.Set<T>().IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            var payload = metadata.GetProperties().Where(x => !x.IsShadowProperty())
                .ToDictionary(x => x.Name, x => x.PropertyInfo?.GetValue(row));
            var key = string.Join('|', primaryKey.Properties.Select(x => x.PropertyInfo?.GetValue(row)?.ToString() ?? string.Empty));
            logs.Add(new ReplicationLog
            {
                BatchId = Guid.NewGuid(),
                EntityType = typeof(T).FullName ?? typeof(T).Name,
                EntityKey = key,
                Operation = ReplicationOperation.Snapshot,
                PayloadJson = JsonSerializer.Serialize(payload),
                ContentKey = row is StoredMessage message ? message.ContentKey : null
            });
        }
    }
}

public sealed class TaskExecutionService(
    IDbContextFactory<MailDbContext> factory,
    IEnumerable<IScheduledTaskHandler> handlers,
    IClock clock,
    IOptions<ClusterOptions> cluster,
    IClusterLeaseService clusterLease,
    ILogger<TaskExecutionService> logger) : ITaskScheduler, ITaskExecutionService
{
    private readonly Dictionary<string, IScheduledTaskHandler> _handlers = handlers.ToDictionary(x => x.Type);
    private string Owner => $"{cluster.Value.NodeId}:{Environment.ProcessId}";

    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken)
    {
        // A standby receives the primary's task identities and settings through
        // replication.  Seeding random local IDs races the initial snapshot.
        if (cluster.Value.Enabled && cluster.Value.Role == ClusterNodeRole.Standby) return;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var defaults = new[]
        {
            ("cache.cleanup", "缓存清理", false, 60, 500),
            ("cleanup.recycle", "回收队列清理", true, 24 * 60, 100),
            ("quota.reconcile", "配额重新计算", true, 30 * 24 * 60, 100),
            ("cluster.verify", "主备同步检查", true, 5, 100),
            ("cluster.snapshot", "主备初始快照", true, 525600, 100),
            ("storage.orphan-scan", "孤儿文件扫描", true, 24 * 60, 500),
            ("storage.integrity-check", "存储完整性检查", true, 24 * 60, 1000),
            ("mailbox.folder-delete", "文件夹邮件迁移", true, 1, 500)
        };
        var existing = await db.ScheduledTasks.Select(x => x.Type).ToListAsync(cancellationToken);
        foreach (var item in defaults.Where(x => !existing.Contains(x.Item1)))
            db.ScheduledTasks.Add(new ScheduledTask { Type = item.Item1, Name = item.Item2, IsClusterTask = item.Item3, IntervalMinutes = item.Item4, BatchSize = item.Item5 });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        await EnsureDefaultsAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var writable = !cluster.Value.Enabled || await clusterLease.IsWritableAsync(cancellationToken);
        var due = await db.ScheduledTasks.AsNoTracking()
            .Where(x => x.IsEnabled && x.Status != ScheduledTaskStatus.Paused && x.NextRunAt <= clock.UtcNow
                        && (!x.IsClusterTask || writable))
            .OrderBy(x => x.NextRunAt).Take(10).Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var id in due) await RunNowAsync(id, cancellationToken);
    }

    public async Task<Guid> RunNowAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var task = await db.ScheduledTasks.SingleAsync(x => x.Id == taskId, cancellationToken);
        if (task.IsClusterTask && cluster.Value.Enabled && !await clusterLease.IsWritableAsync(cancellationToken))
            throw new InvalidOperationException("Cluster task can only run on the writable primary node.");
        var lease = await db.TaskLeases.SingleOrDefaultAsync(x => x.ScheduledTaskId == taskId, cancellationToken);
        if (lease is not null && lease.ExpiresAt > clock.UtcNow && lease.Owner != Owner)
            throw new InvalidOperationException("Task is already running.");
        if (lease is null) { lease = new TaskLease { ScheduledTaskId = taskId, Owner = Owner, ExpiresAt = clock.UtcNow }; db.TaskLeases.Add(lease); }
        lease.Owner = Owner; lease.ExpiresAt = clock.UtcNow.AddSeconds(Math.Clamp(task.TimeoutSeconds, 10, 86400));
        task.Status = ScheduledTaskStatus.Running; task.LastRunAt = clock.UtcNow;
        var execution = new TaskExecution { ScheduledTaskId = taskId, NodeId = Owner };
        db.TaskExecutions.Add(execution);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        try
        {
            if (!_handlers.TryGetValue(task.Type, out var handler)) throw new InvalidOperationException($"No handler is registered for {task.Type}.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(task.TimeoutSeconds, 10, 86400)));
            var result = await handler.RunAsync(task, timeout.Token);
            execution.Status = TaskExecutionStatus.Succeeded; execution.ProcessedCount = result.ProcessedCount; execution.ReleasedBytes = result.ReleasedBytes;
            task.Status = ScheduledTaskStatus.Idle;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            execution.Status = TaskExecutionStatus.Failed; execution.Error = exception.Message; task.Status = ScheduledTaskStatus.Failed;
            logger.LogError(exception, "Scheduled task {TaskType} failed.", task.Type);
        }
        execution.CompletedAt = clock.UtcNow;
        task.NextRunAt = clock.UtcNow.AddMinutes(Math.Max(1, task.IntervalMinutes));
        db.TaskLeases.Remove(lease);
        await db.SaveChangesAsync(cancellationToken);
        return execution.Id;
    }

    public async Task<IReadOnlyList<ScheduledTask>> ListTasksAsync(CancellationToken cancellationToken)
    {
        await EnsureDefaultsAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ScheduledTasks.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TaskExecution>> ListExecutionsAsync(int take, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.TaskExecutions.AsNoTracking().Include(x => x.ScheduledTask)
            .OrderByDescending(x => x.StartedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(cancellationToken);
    }

    public async Task UpdateAsync(Guid taskId, bool enabled, int intervalMinutes, int timeoutSeconds, int batchSize, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await db.ScheduledTasks.SingleAsync(x => x.Id == taskId, cancellationToken);
        task.IsEnabled = enabled; task.Status = enabled ? ScheduledTaskStatus.Idle : ScheduledTaskStatus.Paused;
        task.IntervalMinutes = Math.Clamp(intervalMinutes, 1, 525600);
        task.TimeoutSeconds = Math.Clamp(timeoutSeconds, 10, 86400);
        task.BatchSize = Math.Clamp(batchSize, 1, 1000);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class MaintenanceBackgroundService(
    ITaskScheduler scheduler,
    IClusterLeaseService clusterLease,
    IOptions<MaintenanceOptions> options,
    ILogger<MaintenanceBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIterationAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Maintenance scheduler iteration failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Value.PollIntervalSeconds, 5, 3600)), stoppingToken);
        }
    }

    internal async Task RunIterationAsync(CancellationToken cancellationToken)
    {
        if (await clusterLease.TryAcquirePrimaryAsync(cancellationToken))
            await scheduler.RunDueAsync(cancellationToken);
    }
}

public sealed class ReplicationBackgroundService(
    IDbContextFactory<MailDbContext> factory,
    IMessageContentStore contentStore,
    IOptions<ClusterOptions> options,
    ClusterRuntimeState runtime,
    IClusterLeaseService lease,
    ClusterTransportClient transport,
    ReplicationSchemaFingerprintProvider schemaFingerprint,
    ClusterSyncThrottle throttle,
    IClock clock,
    ILogger<ReplicationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (runtime.Enabled
                    && options.Value.Role == ClusterNodeRole.Standby
                    && !await lease.IsWritableAsync(stoppingToken)
                    && !string.IsNullOrWhiteSpace(options.Value.PrimaryEndpoint))
                    await SynchronizeAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Replication synchronization failed.");
                await RecordErrorAsync(exception.Message, stoppingToken);
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.Value.ReplicationPollSeconds, 1, 300)), stoppingToken);
        }
    }

    private async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var cursorDb = await factory.CreateDbContextAsync(cancellationToken);
        var cursor = await cursorDb.ReplicationCursors.SingleOrDefaultAsync(x => x.NodeId == options.Value.NodeId, cancellationToken);
        if (cursor is null)
        {
            cursor = new ReplicationCursor { NodeId = options.Value.NodeId };
            cursorDb.ReplicationCursors.Add(cursor);
            await cursorDb.SaveChangesAsync(cancellationToken);
        }
        cursor.LastAttemptAt = clock.UtcNow;
        cursor.SyncStatus = ClusterSyncStatus.Synchronizing;
        cursor.PauseReason = null;
        await cursorDb.SaveChangesAsync(cancellationToken);

        var decision = await throttle.EvaluateAsync(cursor.LastAppliedSequence, cancellationToken);
        var hello = await BuildHelloAsync(cursor.LastAppliedSequence, cancellationToken);
        var logs = await transport.PullReplicationAsync(options.Value.PrimaryEndpoint, hello, cursor.LastAppliedSequence, decision.BatchSize, cancellationToken);
        var applied = 0;
        var contentDownloads = 0;
        var stoppedForContent = false;
        foreach (var entry in logs.OrderBy(x => x.Sequence))
        {
            if (!string.IsNullOrWhiteSpace(entry.ContentKey))
            {
                if (!decision.AllowContentTransfer)
                {
                    stoppedForContent = true;
                    break;
                }
                await EnsureContentAsync(entry.ContentKey, cancellationToken);
                contentDownloads++;
            }
            await ApplyAsync(entry, cancellationToken);
            cursor.LastAppliedSequence = entry.Sequence;
            cursor.LastSuccessSequence = entry.Sequence;
            cursor.UpdatedAt = clock.UtcNow;
            cursor.LastSuccessAt = clock.UtcNow;
            cursor.LastBatchCount = applied + 1;
            cursor.LastContentDownloads = contentDownloads;
            cursor.LastError = null;
            cursor.SyncStatus = ClusterSyncStatus.Synchronizing;
            await cursorDb.SaveChangesAsync(cancellationToken);
            await transport.AcknowledgeReplicationAsync(options.Value.PrimaryEndpoint, hello, cursor.LastAppliedSequence, cancellationToken);
            applied++;
        }
        stopwatch.Stop();
        cursor.LastDurationMilliseconds = stopwatch.ElapsedMilliseconds;
        cursor.LastBatchCount = applied;
        cursor.LastContentDownloads = contentDownloads;
        cursor.UpdatedAt = clock.UtcNow;
        cursor.PauseReason = stoppedForContent ? decision.Reason ?? "content transfer throttled" : null;
        cursor.SyncStatus = stoppedForContent
            ? ClusterSyncStatus.Throttled
            : logs.Count > applied
                ? ClusterSyncStatus.CatchingUp
                : ClusterSyncStatus.Idle;
        cursor.LastDetailJson = JsonSerializer.Serialize(new
        {
            startedAt = cursor.LastAttemptAt,
            finishedAt = clock.UtcNow,
            durationMilliseconds = stopwatch.ElapsedMilliseconds,
            applied,
            contentDownloads,
            lastSequence = cursor.LastAppliedSequence,
            throttled = stoppedForContent,
            decision.Reason,
            decision.Score,
            decision.CpuPercent,
            decision.ReplicationBacklog,
            decision.DeliveryBacklog,
            decision.RunningTasks
        });
        await cursorDb.SaveChangesAsync(cancellationToken);
        throttle.RecordSyncResult(true, stopwatch.ElapsedMilliseconds);
        await SendHeartbeatAsync(cursor.LastAppliedSequence, cancellationToken);
    }

    private async Task EnsureContentAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await using var existing = await contentStore.OpenReadAsync(key, cancellationToken);
            return;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        var hello = await BuildHelloAsync(0, cancellationToken);
        await using var stream = await transport.DownloadContentAsync(options.Value.PrimaryEndpoint, hello, key, cancellationToken);
        var saved = await contentStore.SaveAsync(stream, cancellationToken);
        if (!saved.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Replicated content hash does not match its key.");
    }

    private async Task ApplyAsync(ReplicationLog log, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var metadata = db.Model.GetEntityTypes().SingleOrDefault(x =>
            string.Equals(x.ClrType.FullName, log.EntityType, StringComparison.Ordinal));
        if (metadata is null) throw new InvalidOperationException($"Unknown replicated entity type {log.EntityType}.");
        using var document = JsonDocument.Parse(log.PayloadJson);
        var keys = metadata.FindPrimaryKey()?.Properties
            ?? throw new InvalidOperationException($"Replicated entity {log.EntityType} has no primary key.");
        var keyValues = keys.Select(property => ConvertValue(document.RootElement.GetProperty(property.Name), property.ClrType)).ToArray();
        using var scope = ReplicationApplyScope.Begin();
        var entity = await db.FindAsync(metadata.ClrType, keyValues, cancellationToken);
        if (entity is null && metadata.ClrType == typeof(ScheduledTask) && log.Operation != ReplicationOperation.Delete
            && document.RootElement.TryGetProperty(nameof(ScheduledTask.Type), out var taskTypeElement))
        {
            var taskType = taskTypeElement.GetString();
            var localTask = string.IsNullOrWhiteSpace(taskType)
                ? null
                : await db.ScheduledTasks.SingleOrDefaultAsync(x => x.Type == taskType, cancellationToken);
            if (localTask is not null)
            {
                db.TaskExecutions.RemoveRange(await db.TaskExecutions.Where(x => x.ScheduledTaskId == localTask.Id).ToListAsync(cancellationToken));
                var localLease = await db.TaskLeases.SingleOrDefaultAsync(x => x.ScheduledTaskId == localTask.Id, cancellationToken);
                if (localLease is not null) db.TaskLeases.Remove(localLease);
                db.ScheduledTasks.Remove(localTask);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        if (log.Operation == ReplicationOperation.Delete)
        {
            if (entity is not null) db.Remove(entity);
        }
        else
        {
            entity ??= Activator.CreateInstance(metadata.ClrType)
                ?? throw new InvalidOperationException($"Cannot create {log.EntityType}.");
            if (db.Entry(entity).State == EntityState.Detached) db.Add(entity);
            foreach (var property in metadata.GetProperties().Where(x => !x.IsShadowProperty()))
            {
                if (!document.RootElement.TryGetProperty(property.Name, out var value)) continue;
                db.Entry(entity).Property(property.Name).CurrentValue = ConvertValue(value, property.ClrType);
            }
        }
        var existingLog = await db.ReplicationLogs.SingleOrDefaultAsync(x => x.Sequence == log.Sequence, cancellationToken);
        if (existingLog is not null
            && (existingLog.EntityType != log.EntityType
                || existingLog.EntityKey != log.EntityKey
                || existingLog.Operation != log.Operation
                || existingLog.PayloadJson != log.PayloadJson))
        {
            db.ReplicationLogs.Remove(existingLog);
            await db.SaveChangesAsync(cancellationToken);
            existingLog = null;
        }
        if (existingLog is null)
        {
            db.ReplicationLogs.Add(new ReplicationLog
            {
                Sequence = log.Sequence,
                BatchId = log.BatchId,
                EntityType = log.EntityType,
                EntityKey = log.EntityKey,
                Operation = log.Operation,
                PayloadJson = log.PayloadJson,
                ContentKey = log.ContentKey,
                CreatedAt = log.CreatedAt
            });
        }
        var cursor = await db.ReplicationCursors.SingleOrDefaultAsync(x => x.NodeId == options.Value.NodeId, cancellationToken);
        if (cursor is null)
        {
            cursor = new ReplicationCursor { NodeId = options.Value.NodeId };
            db.ReplicationCursors.Add(cursor);
        }
        cursor.LastAppliedSequence = log.Sequence;
        cursor.LastSuccessSequence = log.Sequence;
        cursor.LastSuccessAt = clock.UtcNow;
        cursor.UpdatedAt = clock.UtcNow;
        cursor.LastError = null;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new InvalidOperationException(
                $"replication_conflict: could not apply {log.EntityType} key '{log.EntityKey}'. " +
                "The standby contains conflicting application data; reinitialize it and join the cluster again.",
                exception);
        }
    }

    internal Task ApplyEntryAsync(ReplicationLog log, CancellationToken cancellationToken) =>
        ApplyAsync(log, cancellationToken);

    private static object? ConvertValue(JsonElement element, Type type) =>
        element.ValueKind == JsonValueKind.Null ? null : JsonSerializer.Deserialize(element.GetRawText(), type);

    private async Task RecordErrorAsync(string error, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var cursor = await db.ReplicationCursors.SingleOrDefaultAsync(x => x.NodeId == options.Value.NodeId, cancellationToken);
        if (cursor is null) db.ReplicationCursors.Add(new ReplicationCursor { NodeId = options.Value.NodeId, LastError = error, UpdatedAt = clock.UtcNow, SyncStatus = ClusterSyncStatus.Failed });
        else { cursor.LastError = error; cursor.UpdatedAt = clock.UtcNow; cursor.SyncStatus = ClusterSyncStatus.Failed; }
        await db.SaveChangesAsync(cancellationToken);
        throttle.RecordSyncResult(false, 0);
    }

    private async Task SendHeartbeatAsync(long sequence, CancellationToken cancellationToken)
    {
        await transport.SendHeartbeatAsync(
            options.Value.PrimaryEndpoint,
            await BuildHelloAsync(sequence, cancellationToken),
            new ClusterHeartbeatWire(
                options.Value.NodeId,
                ClusterNodeStatus.Healthy,
                sequence,
                sequence,
                100,
                null,
                typeof(ReplicationBackgroundService).Assembly.GetName().Version?.ToString() ?? "unknown"),
            cancellationToken);
    }

    private async Task<ClusterHello> BuildHelloAsync(long sequence, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var clusterId = await db.ClusterConfigurations.AsNoTracking()
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(cancellationToken) ?? Guid.Empty;
        var packageHash = await db.ClusterNodes.AsNoTracking()
            .Where(x => x.Id == options.Value.NodeId)
            .Select(x => x.PluginPackageSetHash)
            .SingleOrDefaultAsync(cancellationToken);
        return new ClusterHello(
            clusterId,
            options.Value.NodeId,
            options.Value.Role,
            ClusterBinaryProtocol.ProtocolVersion,
            typeof(ReplicationBackgroundService).Assembly.GetName().Version?.ToString() ?? "unknown",
            sequence,
            sequence,
            packageHash,
            await schemaFingerprint.GetAsync(cancellationToken));
    }
}
