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

public sealed class TaskExecutionService(
    IDbContextFactory<MailDbContext> factory,
    IEnumerable<IScheduledTaskHandler> handlers,
    IClock clock,
    ILogger<TaskExecutionService> logger) : ITaskScheduler, ITaskExecutionService
{
    private readonly Dictionary<string, IScheduledTaskHandler> _handlers = handlers.ToDictionary(x => x.Type);
    private static string Owner => $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var defaults = new[]
        {
            ("cache.cleanup", "缓存清理", 60, 500),
            ("cleanup.recycle", "回收队列清理", 24 * 60, 100),
            ("quota.reconcile", "配额重新计算", 30 * 24 * 60, 100),
            ("storage.orphan-scan", "孤儿文件扫描", 24 * 60, 500),
            ("storage.integrity-check", "存储完整性检查", 24 * 60, 1000),
            ("mailbox.folder-delete", "文件夹邮件迁移", 1, 500)
        };
        var existing = await db.ScheduledTasks.Select(x => x.Type).ToListAsync(cancellationToken);
        foreach (var item in defaults.Where(x => !existing.Contains(x.Item1)))
            db.ScheduledTasks.Add(new ScheduledTask { Type = item.Item1, Name = item.Item2, IntervalMinutes = item.Item3, BatchSize = item.Item4 });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RunDueAsync(CancellationToken cancellationToken)
    {
        await EnsureDefaultsAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var due = await db.ScheduledTasks.AsNoTracking()
            .Where(x => x.IsEnabled && x.Status != ScheduledTaskStatus.Paused && x.NextRunAt <= clock.UtcNow)
            .OrderBy(x => x.NextRunAt).Take(10).Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var id in due) await RunNowAsync(id, cancellationToken);
    }

    public async Task<Guid> RunNowAsync(Guid taskId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var task = await db.ScheduledTasks.SingleAsync(x => x.Id == taskId, cancellationToken);
        var lease = await db.TaskLeases.SingleOrDefaultAsync(x => x.ScheduledTaskId == taskId, cancellationToken);
        if (lease is not null && lease.ExpiresAt > clock.UtcNow && lease.Owner != Owner)
            throw new InvalidOperationException("Task is already running.");
        if (lease is null) { lease = new TaskLease { ScheduledTaskId = taskId, Owner = Owner, ExpiresAt = clock.UtcNow }; db.TaskLeases.Add(lease); }
        lease.Owner = Owner; lease.ExpiresAt = clock.UtcNow.AddSeconds(Math.Clamp(task.TimeoutSeconds, 10, 86400));
        task.Status = ScheduledTaskStatus.Running; task.LastRunAt = clock.UtcNow;
        var execution = new TaskExecution { ScheduledTaskId = taskId };
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
        await scheduler.RunDueAsync(cancellationToken);
    }
}
