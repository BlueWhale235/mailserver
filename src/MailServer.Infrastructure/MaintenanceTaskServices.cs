using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Net.Http.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

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

public sealed class RecycleBinConflictException(string message) : Exception(message);

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
        return await MapRowsAsync(db, rows, cancellationToken);
    }

    public async Task<(IReadOnlyList<RecycleBinItem> Items, int TotalCount)> SearchAsync(
        string? search, CleanupResourceType? resourceType, CleanupQueueStatus? status,
        DateTimeOffset? deletedFrom, DateTimeOffset? deletedTo,
        DateTimeOffset? purgeFrom, DateTimeOffset? purgeTo,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.CleanupQueue.AsNoTracking()
            .Where(x => x.Status == CleanupQueueStatus.Pending || x.Status == CleanupQueueStatus.Failed);
        if (resourceType is { } type) query = query.Where(x => x.ResourceType == type);
        if (status is { } currentStatus) query = query.Where(x => x.Status == currentStatus);
        if (deletedFrom is { } start) query = query.Where(x => x.CreatedAt >= start);
        if (deletedTo is { } end) query = query.Where(x => x.CreatedAt < end);
        if (purgeFrom is { } purgeStart) query = query.Where(x => x.PurgeAfter >= purgeStart);
        if (purgeTo is { } purgeEnd) query = query.Where(x => x.PurgeAfter < purgeEnd);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var isId = Guid.TryParse(term, out var parsedId);
            query = query.Where(x =>
                (isId && (x.Id == parsedId || x.ResourceId == parsedId))
                || (x.ContentKey != null && x.ContentKey.Contains(term))
                || (x.ResourceType == CleanupResourceType.Domain && db.Domains.IgnoreQueryFilters()
                    .Any(domain => domain.Id == x.ResourceId && domain.Name.Contains(term)))
                || (x.ResourceType == CleanupResourceType.Account && db.Accounts.IgnoreQueryFilters()
                    .Any(account => account.Id == x.ResourceId
                        && (account.LocalPart.Contains(term) || account.Domain.Name.Contains(term))))
                || (x.ResourceType == CleanupResourceType.Mailbox && db.Mailboxes.IgnoreQueryFilters()
                    .Any(mailbox => mailbox.Id == x.ResourceId
                        && (mailbox.Name.Contains(term) || mailbox.Account.LocalPart.Contains(term)
                            || mailbox.Account.Domain.Name.Contains(term))))
                || (x.ResourceType == CleanupResourceType.MailboxMessage && db.MailboxMessages.IgnoreQueryFilters()
                    .Any(message => message.Id == x.ResourceId
                        && (message.Message.EnvelopeFrom.Contains(term)
                            || message.Mailbox.Name.Contains(term) || message.Mailbox.Account.LocalPart.Contains(term)
                            || message.Mailbox.Account.Domain.Name.Contains(term)))));
        }
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.PurgeAfter).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return (await MapRowsAsync(db, rows, cancellationToken), totalCount);
    }

    private async Task<IReadOnlyList<RecycleBinItem>> MapRowsAsync(
        MailDbContext db, IReadOnlyList<CleanupQueueItem> rows, CancellationToken cancellationToken)
    {
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
        var messageDetails = (await db.MailboxMessages.IgnoreQueryFilters().AsNoTracking()
                .Include(x => x.Mailbox).ThenInclude(x => x.Account).ThenInclude(x => x.Domain)
                .Include(x => x.Message)
                .Where(x => resourceIds.Contains(x.Id))
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id);
        var subjects = new Dictionary<Guid, string>();
        var senders = new Dictionary<Guid, string>();
        foreach (var message in messageDetails.Values)
        {
            var envelopeSender = CleanLabel(message.Message.EnvelopeFrom, "（未知发件人）", 120);
            try
            {
                await using var content = await contentStore.OpenReadAsync(message.Message.ContentKey, cancellationToken);
                var headers = await new MimeParser(content).ParseHeadersAsync(cancellationToken);
                subjects[message.Id] = CleanLabel(headers[HeaderId.Subject], "（无主题）", 120);
                senders[message.Id] = CleanLabel(headers[HeaderId.From], envelopeSender, 120);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                subjects[message.Id] = "（主题不可读取）";
                senders[message.Id] = envelopeSender;
            }
        }

        string DisplayName(CleanupQueueItem item)
        {
            if (item.ResourceId is not { } id) return item.ContentKey ?? item.ResourceType.ToString();
            return item.ResourceType switch
            {
                CleanupResourceType.Domain when domainNames.TryGetValue(id, out var name) => name,
                CleanupResourceType.Account when accountNames.TryGetValue(id, out var name) => name,
                CleanupResourceType.Mailbox when mailboxNames.TryGetValue(id, out var name) => name,
                CleanupResourceType.MailboxMessage when messageDetails.TryGetValue(id, out var message) =>
                    $"{message.Mailbox.Account.LocalPart}@{message.Mailbox.Account.Domain.Name} / {message.Mailbox.Name} / UID {message.Uid}",
                _ => item.ContentKey ?? id.ToString()
            };
        }

        return rows.Select(x =>
        {
            var message = x.ResourceType == CleanupResourceType.MailboxMessage && x.ResourceId is { } id
                ? messageDetails.GetValueOrDefault(id) : null;
            return new RecycleBinItem(x.Id, x.ResourceType, x.ResourceId,
                DisplayName(x), x.EstimatedBytes, x.CreatedAt, x.PurgeAfter, x.Status,
                message is null ? null : senders[message.Id],
                message is null ? null : subjects[message.Id], message?.Message.ReceivedAt);
        }).ToArray();
    }

    private static string CleanLabel(string? value, string fallback, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var cleaned = string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(cleaned)) return fallback;
        return cleaned.Length <= limit ? cleaned : cleaned[..limit] + "…";
    }

    public async Task RestoreAsync(Guid queueId, string actor, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var item = await db.CleanupQueue.SingleOrDefaultAsync(x => x.Id == queueId, cancellationToken)
            ?? throw new KeyNotFoundException("Recycle-bin item not found.");
        if (item.Status is not (CleanupQueueStatus.Pending or CleanupQueueStatus.Failed))
            throw new RecycleBinConflictException("该对象已进入清理或恢复流程，请刷新回收站。");
        if (item.PurgeAfter <= clock.UtcNow)
            throw new RecycleBinConflictException("保留期已结束，请刷新回收站确认清理状态。");
        if (item.ResourceId is not { } id)
            throw new RecycleBinConflictException("该对象没有可恢复的数据库记录。");
        switch (item.ResourceType)
        {
            case CleanupResourceType.Domain:
                var domain = await db.Domains.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (domain?.DeletedAt is null) throw new RecycleBinConflictException("域已恢复或被清理，请刷新回收站。");
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
                var restoredAccount = await db.Accounts.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (restoredAccount?.DeletedAt is null) throw new RecycleBinConflictException("账号已恢复或被清理，请刷新回收站。");
                if (!await db.Domains.IgnoreQueryFilters().AnyAsync(x => x.Id == restoredAccount.DomainId && x.DeletedAt == null, cancellationToken))
                    throw new RecycleBinConflictException("所属域尚未恢复，请先恢复域。");
                restoredAccount.DeletedAt = restoredAccount.PurgeAfter = null; restoredAccount.DeletedBy = null; restoredAccount.IsEnabled = true;
                var accountMailboxes = await db.Mailboxes.IgnoreQueryFilters().Where(x => x.AccountId == id).ToListAsync(cancellationToken);
                foreach (var accountMailbox in accountMailboxes)
                { accountMailbox.DeletedAt = accountMailbox.PurgeAfter = null; accountMailbox.DeletedBy = null; }
                var accountMailboxIds = accountMailboxes.Select(x => x.Id).ToArray();
                foreach (var accountMailboxMessage in await db.MailboxMessages.IgnoreQueryFilters().Where(x => accountMailboxIds.Contains(x.MailboxId)).ToListAsync(cancellationToken))
                { accountMailboxMessage.DeletedAt = accountMailboxMessage.PurgeAfter = null; accountMailboxMessage.DeletedBy = null; }
                break;
            case CleanupResourceType.Mailbox:
                var restoredMailbox = await db.Mailboxes.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (restoredMailbox?.DeletedAt is null) throw new RecycleBinConflictException("邮箱文件夹已恢复或被清理，请刷新回收站。");
                if (!await db.Accounts.IgnoreQueryFilters().AnyAsync(x => x.Id == restoredMailbox.AccountId
                    && x.DeletedAt == null && x.Domain.DeletedAt == null, cancellationToken))
                    throw new RecycleBinConflictException("所属账号或域尚未恢复，请先恢复上级对象。");
                restoredMailbox.DeletedAt = restoredMailbox.PurgeAfter = null; restoredMailbox.DeletedBy = null;
                break;
            case CleanupResourceType.MailboxMessage:
                var message = await db.MailboxMessages.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (message is null) throw new RecycleBinConflictException("邮件已恢复或被清理，请刷新回收站。");
                var sourceMailbox = await db.Mailboxes.IgnoreQueryFilters()
                    .SingleOrDefaultAsync(x => x.Id == message.MailboxId
                        && x.DeletedAt == null && x.Account.DeletedAt == null && x.Account.Domain.DeletedAt == null,
                        cancellationToken);
                if (sourceMailbox is null)
                    throw new RecycleBinConflictException("所属文件夹、账号或域尚未恢复，请先恢复上级对象。");
                var inTrash = sourceMailbox.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase);
                if (message.DeletedAt is null && (!inTrash || message.ScheduledDeleteAt is null))
                    throw new RecycleBinConflictException("邮件已恢复或被清理，请刷新回收站。");
                if (inTrash)
                {
                    var inbox = await db.Mailboxes.SingleOrDefaultAsync(x => x.AccountId == sourceMailbox.AccountId
                        && x.Name == "INBOX" && x.DeletedAt == null && x.PendingDeletionJobId == null, cancellationToken);
                    if (inbox is null)
                        throw new RecycleBinConflictException("收件箱不存在或正在删除，暂时无法恢复邮件。");
                    await MailboxService.EnsureUidNextAtLeastAsync(db, inbox, cancellationToken);
                    message.MailboxId = inbox.Id;
                    message.Uid = inbox.UidNext++;
                    message.ModSeq = ++inbox.HighestModSeq;
                }
                message.Flags = string.Join(' ', message.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(flag => !flag.Equals("\\Deleted", StringComparison.OrdinalIgnoreCase)));
                message.ScheduledDeleteAt = null;
                message.DeletedAt = message.PurgeAfter = null; message.DeletedBy = null;
                foreach (var duplicate in await db.CleanupQueue.Where(x => x.Id != queueId
                    && x.ResourceType == CleanupResourceType.MailboxMessage && x.ResourceId == id
                    && (x.Status == CleanupQueueStatus.Pending || x.Status == CleanupQueueStatus.Failed))
                    .ToListAsync(cancellationToken))
                {
                    duplicate.Status = CleanupQueueStatus.Restored;
                    duplicate.CompletedAt = clock.UtcNow;
                }
                break;
            default:
                throw new RecycleBinConflictException("该对象类型不支持恢复。");
        }
        item.Status = CleanupQueueStatus.Restored;
        item.CompletedAt = clock.UtcNow;
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "recycle.restore", Resource = $"cleanup:{queueId}", Detail = item.ResourceType.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task PurgeAsync(Guid queueId, string actor, CancellationToken cancellationToken) =>
        await PurgeOneAsync(queueId, force: true, cancellationToken, actor);

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

    private async Task<long> PurgeOneAsync(Guid queueId, bool force, CancellationToken cancellationToken, string? actor = null)
    {
        var contentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long released = 0;
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var item = await db.CleanupQueue.SingleOrDefaultAsync(x => x.Id == queueId, cancellationToken)
                ?? throw new KeyNotFoundException("Recycle-bin item not found.");
            if (item.Status is CleanupQueueStatus.Completed or CleanupQueueStatus.Restored or CleanupQueueStatus.Processing)
            {
                if (force) throw new RecycleBinConflictException("该对象已进入清理或恢复流程，请刷新回收站。");
                return 0;
            }
            if (!force && item.PurgeAfter > clock.UtcNow) return 0;
            item.Status = CleanupQueueStatus.Processing;
            item.Attempts++;
            try
            {
                if (item.ResourceType == CleanupResourceType.ContentFile && !string.IsNullOrWhiteSpace(item.ContentKey))
                    contentKeys.Add(item.ContentKey);
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
                if (actor is not null)
                    db.AuditRecords.Add(new AuditRecord
                    {
                        Actor = actor,
                        Action = "recycle.purge",
                        Resource = $"cleanup:{queueId}",
                        Detail = $"{item.ResourceType}; database cleanup completed; unreferenced content files are removed after commit.",
                        Category = AuditCategory.Delete,
                        Status = AuditStatus.Succeeded,
                        RelatedId = item.ResourceId?.ToString()
                    });
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
    private sealed record ScanProgress(string? LastKey = null, long Scanned = 0,
        long CompletedCycles = 0, DateTimeOffset? LastCompletedAt = null);
    public string Type => "storage.integrity-check";
    public async Task<ScheduledTaskRunResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var root = Path.GetFullPath(storage.Value.RootPath);
        var scheduled = await db.ScheduledTasks.SingleAsync(x => x.Id == task.Id, cancellationToken);
        var progress = JsonSerializer.Deserialize<ScanProgress>(scheduled.SettingsJson) ?? new ScanProgress();
        var batchSize = Math.Clamp(task.BatchSize, 1, 10000);
        var keys = await db.Database.SqlQuery<string>($"""
            SELECT DISTINCT ContentKey AS Value FROM stored_messages
            WHERE ContentKey > {progress.LastKey ?? string.Empty}
            ORDER BY ContentKey LIMIT {batchSize}
            """).ToListAsync(cancellationToken);
        var missing = keys.Where(key => !File.Exists(Path.Combine(root, key[..2], key[2..4], $"{key}.eml"))).ToArray();
        if (missing.Length > 0)
            db.AuditRecords.Add(new AuditRecord
            {
                Actor = "system", Action = "storage.integrity.missing-content", Resource = "storage:messages",
                Detail = string.Join(',', missing.Take(50))
            });
        var finished = keys.Count < batchSize;
        var next = finished
            ? new ScanProgress(null, 0, progress.CompletedCycles + 1, DateTimeOffset.UtcNow)
            : progress with { LastKey = keys[^1], Scanned = progress.Scanned + keys.Count };
        scheduled.SettingsJson = JsonSerializer.Serialize(next);
        await db.SaveChangesAsync(cancellationToken);
        return new ScheduledTaskRunResult(keys.Count, 0,
            $"Scanned {keys.Count} content keys; missing {missing.Length}; "
            + (finished ? $"cycle {next.CompletedCycles} complete." : $"cycle progress {next.Scanned}."));
    }
}

public sealed class TaskBusyException : Exception
{
    public TaskBusyException() : base("Task is already running.") { }
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
        foreach (var id in due)
        {
            try { await RunNowAsync(id, cancellationToken); }
            catch (TaskBusyException) { /* Another worker already owns this task. Continue with the remaining due tasks. */ }
        }
    }

    public async Task<Guid> RunNowAsync(Guid taskId, CancellationToken cancellationToken)
        => await RunNowAsync(taskId, cancellationToken, null);

    public async Task<Guid> RunNowAsync(Guid taskId, CancellationToken cancellationToken, string? actor)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var task = await db.ScheduledTasks.SingleOrDefaultAsync(x => x.Id == taskId, cancellationToken)
            ?? throw new KeyNotFoundException("Scheduled task not found.");
        var lease = await db.TaskLeases.SingleOrDefaultAsync(x => x.ScheduledTaskId == taskId, cancellationToken);
        if (lease is not null && lease.ExpiresAt > clock.UtcNow)
            throw new TaskBusyException();
        if (lease is null) { lease = new TaskLease { ScheduledTaskId = taskId, Owner = Owner, ExpiresAt = clock.UtcNow }; db.TaskLeases.Add(lease); }
        lease.Owner = Owner; lease.ExpiresAt = clock.UtcNow.AddSeconds(Math.Clamp(task.TimeoutSeconds, 10, 86400));
        task.Status = ScheduledTaskStatus.Running; task.LastRunAt = clock.UtcNow;
        var execution = new TaskExecution { ScheduledTaskId = taskId };
        db.TaskExecutions.Add(execution);
        AuditRecord? manualAudit = null;
        if (actor is not null)
        {
            manualAudit = new AuditRecord
            {
                Actor = actor,
                Action = "task.run",
                Resource = $"task:{taskId}",
                RelatedId = execution.Id.ToString(),
                Detail = $"Manual execution requested for {task.Type}.",
                Category = AuditCategory.BackgroundTask,
                Status = AuditStatus.Running
            };
            db.AuditRecords.Add(manualAudit);
        }
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
        if (manualAudit is not null)
        {
            manualAudit.Status = execution.Status == TaskExecutionStatus.Succeeded ? AuditStatus.Succeeded : AuditStatus.Failed;
            manualAudit.Detail = $"Manual execution {execution.Status}; executionId={execution.Id}.";
        }
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

    public async Task<(IReadOnlyList<TaskExecution> Items, int TotalCount)> SearchExecutionsAsync(
        Guid? taskId, TaskExecutionStatus? status, DateTimeOffset? from, DateTimeOffset? to,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.TaskExecutions.AsNoTracking().AsQueryable();
        if (taskId is { } id) query = query.Where(x => x.ScheduledTaskId == id);
        if (status is { } currentStatus) query = query.Where(x => x.Status == currentStatus);
        if (from is { } start) query = query.Where(x => x.StartedAt >= start);
        if (to is { } end) query = query.Where(x => x.StartedAt < end);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, totalCount);
    }

    public async Task<TaskExecution?> GetExecutionAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.TaskExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public async Task UpdateAsync(Guid taskId, bool enabled, int intervalMinutes, int timeoutSeconds, int batchSize,
        CancellationToken cancellationToken, string? actor = null)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var task = await db.ScheduledTasks.SingleOrDefaultAsync(x => x.Id == taskId, cancellationToken)
            ?? throw new KeyNotFoundException("Scheduled task not found.");
        if (task.Status == ScheduledTaskStatus.Running
            || await db.TaskLeases.AnyAsync(x => x.ScheduledTaskId == taskId && x.ExpiresAt > clock.UtcNow, cancellationToken))
            throw new TaskBusyException();
        var before = new { task.IsEnabled, task.IntervalMinutes, task.TimeoutSeconds, task.BatchSize };
        task.IsEnabled = enabled; task.Status = enabled ? ScheduledTaskStatus.Idle : ScheduledTaskStatus.Paused;
        task.IntervalMinutes = Math.Clamp(intervalMinutes, 1, 525600);
        task.TimeoutSeconds = Math.Clamp(timeoutSeconds, 10, 86400);
        task.BatchSize = Math.Clamp(batchSize, 1, 1000);
        if (actor is not null)
            db.AuditRecords.Add(new AuditRecord
            {
                Actor = actor,
                Action = "task.update",
                Resource = $"task:{taskId}",
                RelatedId = taskId.ToString(),
                Detail = $"{task.Type}: enabled {before.IsEnabled}->{task.IsEnabled}; interval {before.IntervalMinutes}->{task.IntervalMinutes}; timeout {before.TimeoutSeconds}->{task.TimeoutSeconds}; batch {before.BatchSize}->{task.BatchSize}.",
                Category = AuditCategory.BackgroundTask,
                Status = AuditStatus.Succeeded
            });
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
