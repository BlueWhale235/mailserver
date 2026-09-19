using System.Data;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailServer.Infrastructure;

/// <summary>
/// IMAP 邮箱状态服务，集中维护文件夹、UID、flag、MODSEQ 和软删除语义。
/// </summary>
public sealed partial class MailboxService(
    IDbContextFactory<MailDbContext> dbFactory,
    IMessageContentStore contentStore,
    SystemSettingsService systemSettings,
    SentCopyService sentCopies,
    IOptions<MaintenanceOptions>? maintenance = null,
    MailboxFolderManagementService? folderManagement = null) : IMailboxService
{
    private static long _lastUidValidity = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // 同一邮箱的写操作需要串行化，避免 UIDNEXT、序号和 MODSEQ 在并发 IMAP 命令下错乱。
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> MailboxLocks = new();
    private const string SubscriptionPrefix = "imap.subscription.";
    private const string DefaultSubscriptionSeedPrefix = "imap.default-subscription-seeded.";
    private const string SuppressedDefaultFolderPrefix = "imap.default-folder-suppressed.";
    public MailboxService(
        IDbContextFactory<MailDbContext> dbFactory,
        IMessageContentStore contentStore,
        SystemSettingsService systemSettings)
        : this(
            dbFactory,
            contentStore,
            systemSettings,
            new SentCopyService(
                dbFactory,
                contentStore,
                Microsoft.Extensions.Options.Options.Create(new MailRulesOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SentCopyService>.Instance),
            Microsoft.Extensions.Options.Options.Create(new MaintenanceOptions()),
            null)
    {
    }

    public async Task<IReadOnlyList<MailboxSummary>> ListAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var mailboxes = await db.Mailboxes
            .Where(x => x.AccountId == accountId)
            .ToListAsync(cancellationToken);
        var result = new List<MailboxSummary>(mailboxes.Count);
        foreach (var mailbox in mailboxes)
        {
            result.Add(await SummarizeAsync(db, mailbox, cancellationToken));
        }

        return await OrderForAccountAsync(accountId, result, cancellationToken);
    }

    public async Task EnsureDefaultFoldersAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domainId = await db.Accounts.AsNoTracking()
            .Where(x => x.Id == accountId)
            .Select(x => (Guid?)x.DomainId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Account not found.");
        var settings = await systemSettings.GetDomainMailboxSettingsAsync(domainId, cancellationToken);
        var existingMailboxes = await db.Mailboxes.IgnoreQueryFilters()
            .Where(x => x.AccountId == accountId)
            .ToListAsync(cancellationToken);
        var seen = new HashSet<string>(
            existingMailboxes.Where(x => x.DeletedAt == null).Select(x => x.Name),
            StringComparer.OrdinalIgnoreCase);
        var defaultFolders = settings.DefaultFolders.Select(MailboxNames.Normalize).ToArray();
        var subscriptionKeys = await db.SystemSettings
            .Where(x => x.Key.StartsWith(SubscriptionAccountPrefix(accountId)))
            .Select(x => x.Key)
            .ToListAsync(cancellationToken);
        var seedKeys = await db.SystemSettings
            .Where(x => x.Key.StartsWith(DefaultSubscriptionSeedAccountPrefix(accountId)))
            .Select(x => x.Key)
            .ToListAsync(cancellationToken);
        var subscriptions = subscriptionKeys.ToHashSet(StringComparer.Ordinal);
        var seeded = seedKeys.ToHashSet(StringComparer.Ordinal);
        var suppressedKeys = await db.SystemSettings
            .Where(x => x.Key.StartsWith(SuppressedDefaultFolderAccountPrefix(accountId)))
            .Select(x => x.Key)
            .ToListAsync(cancellationToken);
        var suppressed = suppressedKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var normalized in defaultFolders)
        {
            if (!MailboxNames.IsSystemFolder(normalized)
                && suppressed.Contains(SuppressedDefaultFolderKey(accountId, normalized)))
                continue;
            // 默认文件夹统一走 Normalize，避免大小写或分隔符差异造成重复文件夹。
            if (seen.Add(normalized))
            {
                var deleted = existingMailboxes.FirstOrDefault(x =>
                    x.DeletedAt != null && x.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                if (deleted is null)
                {
                    db.Mailboxes.Add(new Mailbox { AccountId = accountId, Name = normalized });
                }
                else
                {
                    deleted.DeletedAt = null;
                    deleted.DeletedBy = null;
                    deleted.PurgeAfter = null;
                    var cleanupItems = await db.CleanupQueue
                        .Where(x => x.ResourceType == CleanupResourceType.Mailbox
                            && x.ResourceId == deleted.Id
                            && (x.Status == CleanupQueueStatus.Pending || x.Status == CleanupQueueStatus.Failed))
                        .ToListAsync(cancellationToken);
                    foreach (var item in cleanupItems)
                    {
                        item.Status = CleanupQueueStatus.Restored;
                        item.CompletedAt = DateTimeOffset.UtcNow;
                    }
                }
            }

            // 每个默认文件夹只自动订阅一次；用户后续主动取消订阅时不会在下次登录被恢复。
            var seedKey = DefaultSubscriptionSeedKey(accountId, normalized);
            if (!seeded.Add(seedKey)) continue;
            var subscriptionKey = SubscriptionKey(accountId, normalized);
            if (subscriptions.Add(subscriptionKey))
            {
                db.SystemSettings.Add(new SystemSetting
                {
                    Key = subscriptionKey,
                    Value = JsonSerializer.Serialize(normalized)
                });
            }
            db.SystemSettings.Add(new SystemSetting { Key = seedKey, Value = "true" });
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<MailboxSummary?> SelectAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var mailbox = await FindMailboxAsync(db, accountId, MailboxNames.Normalize(mailboxName), cancellationToken);
        return mailbox is null ? null : await SummarizeAsync(db, mailbox, cancellationToken);
    }

    public async Task<MailboxSummary> CreateAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        var normalized = MailboxNames.Normalize(mailboxName);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await FindMailboxAsync(db, accountId, normalized, cancellationToken);
        if (existing is not null)
        {
            if (existing.PendingDeletionJobId is not null)
                throw new InvalidOperationException("Mailbox deletion is in progress.");
            return await SummarizeAsync(db, existing, cancellationToken);
        }

        var mailbox = new Mailbox { AccountId = accountId, Name = normalized, UidValidity = NewUidValidity() };
        db.Mailboxes.Add(mailbox);
        await db.SaveChangesAsync(cancellationToken);
        return await SummarizeAsync(db, mailbox, cancellationToken);
    }

    public async Task DeleteAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        var normalized = MailboxNames.Normalize(mailboxName);
        if (MailboxNames.IsSystemFolder(normalized))
        {
            throw new InvalidOperationException($"System mailbox '{normalized}' cannot be deleted.");
        }

        if (folderManagement is not null)
        {
            var job = await folderManagement.RequestAccountDeletionAsync(accountId, normalized, "imap", cancellationToken);
            throw new InvalidOperationException($"Mailbox deletion is scheduled (job {job.Id}); messages are being moved to INBOX.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var mailbox = await FindMailboxAsync(db, accountId, normalized, cancellationToken)
            ?? throw new KeyNotFoundException("Mailbox not found.");
        if (await HasChildMailboxAsync(db, accountId, mailbox.Name, cancellationToken))
        {
            throw new InvalidOperationException("Mailbox has child mailboxes.");
        }

        var now = DateTimeOffset.UtcNow;
        var purgeAfter = now.AddDays(Math.Clamp(maintenance?.Value.SoftDeleteRetentionDays ?? 30, 1, 3650));
        // IMAP 删除文件夹走软删除与清理队列，给管理端恢复和延迟清理留下窗口。
        mailbox.DeletedAt = now;
        mailbox.DeletedBy = "imap";
        mailbox.PurgeAfter = purgeAfter;
        db.CleanupQueue.Add(new CleanupQueueItem
        {
            ResourceType = CleanupResourceType.Mailbox,
            ResourceId = mailbox.Id,
            AccountId = accountId,
            CreatedAt = now,
            PurgeAfter = purgeAfter
        });
        db.SystemSettings.RemoveRange(await db.SystemSettings
            .Where(x => x.Key == SubscriptionKey(accountId, mailbox.Name))
            .ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RenameAsync(
        Guid accountId,
        string existingMailboxName,
        string newMailboxName,
        CancellationToken cancellationToken)
    {
        var existingName = MailboxNames.Normalize(existingMailboxName);
        var targetName = MailboxNames.Normalize(newMailboxName);
        if (MailboxNames.IsSystemFolder(existingName))
        {
            throw new InvalidOperationException($"System mailbox '{existingName}' cannot be renamed.");
        }
        if (MailboxNames.IsSystemFolder(targetName))
        {
            throw new InvalidOperationException($"Cannot rename a mailbox to system mailbox '{targetName}'.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (await FindMailboxAsync(db, accountId, targetName, cancellationToken) is not null)
        {
            throw new InvalidOperationException("Destination mailbox already exists.");
        }

        var existing = await FindMailboxAsync(db, accountId, existingName, cancellationToken)
            ?? throw new KeyNotFoundException("Mailbox not found.");
        var oldName = existing.Name;
        var mailboxes = await db.Mailboxes
            .Where(x => x.AccountId == accountId)
            .ToListAsync(cancellationToken);
        var oldPrefix = oldName + "/";
        if (mailboxes.Any(x => x.PendingDeletionJobId is not null
            && (MailboxNames.Equals(x.Name, oldName)
                || x.Name.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)
                || targetName.StartsWith(x.Name + "/", StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidOperationException("A mailbox in the renamed tree is being deleted.");
        }
        // IMAP 允许重命名自定义父文件夹时联动其子文件夹。
        foreach (var mailbox in mailboxes)
        {
            if (mailbox.Id == existing.Id)
            {
                mailbox.Name = targetName;
            }
            else if (mailbox.Name.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                mailbox.Name = targetName + mailbox.Name[oldName.Length..];
            }
        }

        await RenameSubscriptionsAsync(db, accountId, existingName, targetName, renameChildren: true, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SubscribeAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        var normalized = MailboxNames.Normalize(mailboxName);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await FindMailboxAsync(db, accountId, normalized, cancellationToken) is null)
        {
            throw new KeyNotFoundException("Mailbox not found.");
        }

        var key = SubscriptionKey(accountId, normalized);
        var setting = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = JsonSerializer.Serialize(normalized) });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task UnsubscribeAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken)
    {
        var key = SubscriptionKey(accountId, MailboxNames.Normalize(mailboxName));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting is null) return;
        db.SystemSettings.Remove(setting);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailboxSummary>> ListSubscribedAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var prefix = SubscriptionAccountPrefix(accountId);
        var names = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key.StartsWith(prefix))
            .Select(x => x.Value)
            .ToListAsync(cancellationToken);
        var result = new List<MailboxSummary>();
        foreach (var value in names)
        {
            var name = JsonSerializer.Deserialize<string>(value) ?? string.Empty;
            var mailbox = await FindMailboxAsync(db, accountId, name, cancellationToken);
            if (mailbox is not null)
            {
                result.Add(await SummarizeAsync(db, mailbox, cancellationToken));
            }
        }

        return await OrderForAccountAsync(accountId, result, cancellationToken);
    }

    public async Task<IReadOnlyList<MailboxItem>> FetchAsync(
        Guid mailboxId,
        string sequenceSet,
        bool useUid,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var messages = await db.MailboxMessages
            .Where(x => x.MailboxId == mailboxId)
            .Include(x => x.Message)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        return SelectMessages(messages, sequenceSet, useUid).ToArray();
    }

    public async Task<IReadOnlyList<long>> SearchAsync(
        Guid mailboxId,
        string criteria,
        bool useUid,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var messages = await db.MailboxMessages
            .Where(x => x.MailboxId == mailboxId)
            .Include(x => x.Message)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        var normalized = criteria.Trim().ToUpperInvariant();
        IEnumerable<(MailboxMessage Message, long Sequence)> selected = messages.Select((message, index) => (message, index + 1L));
        if (normalized == "ALL")
        {
            // ALL is already the default selected set.
        }
        else if (TryParseDateSearch(criteria, out var operatorName, out var date))
        {
            selected = operatorName switch
            {
                "SINCE" => selected.Where(x => x.Message.InternalDate.LocalDateTime.Date >= date),
                "BEFORE" => selected.Where(x => x.Message.InternalDate.LocalDateTime.Date < date),
                "ON" => selected.Where(x => x.Message.InternalDate.LocalDateTime.Date == date),
                _ => selected
            };
        }
        else if (TryParseHeaderSearch(criteria, out var charset, out var headerName, out var headerNeedle))
        {
            if (!IsSupportedSearchCharset(charset))
            {
                throw new InvalidOperationException($"Unsupported search charset: {charset}.");
            }

            var normalizedNeedle = NormalizeSearchText(headerNeedle);
            var matches = new List<(MailboxMessage Message, long Sequence)>();
            foreach (var item in selected)
            {
                await using var content = await contentStore.OpenReadAsync(item.Message.Message.ContentKey, cancellationToken);
                var message = await MimeMessage.LoadAsync(content, cancellationToken);
                var values = headerName.Equals("SUBJECT", StringComparison.OrdinalIgnoreCase)
                    ? [message.Subject ?? string.Empty]
                    : message.Headers
                        .Where(x => x.Field.Equals(headerName, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Value)
                        .ToArray();
                if (values.Any(value => NormalizeSearchText(UnfoldHeaderValue(value)).Contains(normalizedNeedle, StringComparison.Ordinal)))
                {
                    matches.Add(item);
                }
            }

            selected = matches;
        }
        else if (TryParseTextSearch(criteria, out charset, out var bodyNeedle))
        {
            if (!IsSupportedSearchCharset(charset))
            {
                throw new InvalidOperationException($"Unsupported search charset: {charset}.");
            }

            var normalizedNeedle = NormalizeSearchText(bodyNeedle);
            var matches = new List<(MailboxMessage Message, long Sequence)>();
            foreach (var item in selected)
            {
                await using var content = await contentStore.OpenReadAsync(item.Message.Message.ContentKey, cancellationToken);
                var message = await MimeMessage.LoadAsync(content, cancellationToken);
                if (NormalizeSearchText(ExtractSearchableBody(message)).Contains(normalizedNeedle, StringComparison.Ordinal))
                {
                    matches.Add(item);
                }
            }

            selected = matches;
        }
        else
        {
            selected = normalized switch
            {
                "SEEN" => selected.Where(x => HasFlag(x.Message.Flags, "\\Seen")),
                "UNSEEN" => selected.Where(x => !HasFlag(x.Message.Flags, "\\Seen")),
                "DELETED" => selected.Where(x => HasFlag(x.Message.Flags, "\\Deleted")),
                "UNDELETED" => selected.Where(x => !HasFlag(x.Message.Flags, "\\Deleted")),
                "FLAGGED" => selected.Where(x => HasFlag(x.Message.Flags, "\\Flagged")),
                "UNFLAGGED" => selected.Where(x => !HasFlag(x.Message.Flags, "\\Flagged")),
                "ANSWERED" => selected.Where(x => HasFlag(x.Message.Flags, "\\Answered")),
                "UNANSWERED" => selected.Where(x => !HasFlag(x.Message.Flags, "\\Answered")),
                _ => selected
            };
        }
        return selected.Select(x => useUid ? x.Message.Uid : x.Sequence).ToArray();
    }

    public async Task StoreFlagsAsync(
        Guid mailboxId,
        string sequenceSet,
        bool useUid,
        string operation,
        IReadOnlyCollection<string> flags,
        CancellationToken cancellationToken)
    {
        var mailboxLock = MailboxLocks.GetOrAdd(mailboxId, _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var mailbox = await db.Mailboxes.SingleAsync(x => x.Id == mailboxId, cancellationToken);
            var messages = await db.MailboxMessages
                .Where(x => x.MailboxId == mailboxId)
                .Include(x => x.Message)
                .Include(x => x.Mailbox)
                .Include(x => x.Mailbox)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            var selectedIds = SelectMessages(messages, sequenceSet, useUid).Select(x => x.Id).ToHashSet();
            foreach (var message in messages.Where(x => selectedIds.Contains(x.Id)))
            {
                var current = ParseFlags(message.Flags);
                if (operation.StartsWith('+'))
                {
                    current.UnionWith(flags);
                }
                else if (operation.StartsWith('-'))
                {
                    current.ExceptWith(flags);
                }
                else
                {
                    current = new HashSet<string>(flags, StringComparer.OrdinalIgnoreCase);
                }

                message.Flags = string.Join(' ', current.Order(StringComparer.OrdinalIgnoreCase));
                message.ModSeq = ++mailbox.HighestModSeq;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            mailboxLock.Release();
        }
    }

    public async Task MarkSeenAsync(
        Guid mailboxId,
        IReadOnlyCollection<Guid> mailboxMessageIds,
        CancellationToken cancellationToken)
    {
        if (mailboxMessageIds.Count == 0)
        {
            return;
        }

        await StoreFlagsForIdsAsync(mailboxId, mailboxMessageIds, ["\\Seen"], cancellationToken);
    }

    public async Task<IReadOnlyList<ExpungedMailboxItem>> ExpungeAsync(Guid mailboxId, CancellationToken cancellationToken)
    {
        var mailboxLock = MailboxLocks.GetOrAdd(mailboxId, _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var messages = await db.MailboxMessages
                .Where(x => x.MailboxId == mailboxId)
                .Include(x => x.Message)
                .Include(x => x.Mailbox)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            var expunged = messages
                .Select((message, index) => new { Message = message, Sequence = index + 1L })
                .Where(x => HasFlag(x.Message.Flags, "\\Deleted"))
                .Select(x => new ExpungedMailboxItem(x.Sequence, x.Message.Uid))
                .ToArray();
            var now = DateTimeOffset.UtcNow;
            var purgeAfter = now.AddDays(Math.Clamp(maintenance?.Value.SoftDeleteRetentionDays ?? 30, 1, 3650));
            foreach (var message in messages.Where(x => HasFlag(x.Flags, "\\Deleted")))
            {
                message.DeletedAt = now;
                message.DeletedBy = "imap";
                message.PurgeAfter = purgeAfter;
                db.CleanupQueue.Add(new CleanupQueueItem
                {
                    ResourceType = CleanupResourceType.MailboxMessage,
                    ResourceId = message.Id,
                    ContentKey = message.Message.ContentKey,
                    AccountId = message.Mailbox.AccountId,
                    EstimatedBytes = message.Message.Size,
                    CreatedAt = now,
                    PurgeAfter = purgeAfter
                });
            }
            await db.SaveChangesAsync(cancellationToken);
            return expunged;
        }
        finally
        {
            mailboxLock.Release();
        }
    }

    public async Task<CopyMoveResult> CopyAsync(
        Guid mailboxId,
        string sequenceSet,
        bool useUid,
        string destination,
        bool move,
        CancellationToken cancellationToken)
    {
        var mailboxLock = MailboxLocks.GetOrAdd(mailboxId, _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var source = await db.Mailboxes.Include(x => x.Account).ThenInclude(x => x.Domain)
                .SingleAsync(x => x.Id == mailboxId, cancellationToken);
            var target = await FindMailboxAsync(db, source.AccountId, MailboxNames.Normalize(destination), cancellationToken)
                ?? throw new KeyNotFoundException("Destination mailbox does not exist.");
            if (target.PendingDeletionJobId is not null)
                throw new InvalidOperationException("Destination mailbox deletion is in progress.");
            var messages = await db.MailboxMessages
                .Where(x => x.MailboxId == mailboxId)
                .Include(x => x.Message)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            var selected = SelectMessages(messages, sequenceSet, useUid).ToArray();
            var selectedIds = selected.Select(x => x.Id).ToHashSet();
            var sourceUids = new List<long>();
            var targetUids = new List<long>();
            await EnsureUidNextAtLeastAsync(db, target, cancellationToken);
            var movingToTrash = move && target.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase);
            var movingFromTrash = move && source.Name.Equals("Trash", StringComparison.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;
            var scheduledDeleteAt = now.AddDays(Math.Clamp(maintenance?.Value.SoftDeleteRetentionDays ?? 30, 1, 3650));
            var copyBytes = selected.Sum(x => x.Size);
            if (!move)
            {
                if (source.Account.UsedBytes + copyBytes > source.Account.QuotaBytes)
                    throw new InvalidOperationException("Mailbox quota exceeded.");
                if (source.Account.Domain.UsedBytes + copyBytes > source.Account.Domain.QuotaBytes)
                    throw new InvalidOperationException("Domain quota exceeded.");
            }
            foreach (var message in messages.Where(x => selectedIds.Contains(x.Id)))
            {
                var targetUid = target.UidNext++;
                sourceUids.Add(message.Uid);
                targetUids.Add(targetUid);
                var targetMessage = new MailboxMessage
                {
                    MailboxId = target.Id,
                    MessageId = message.MessageId,
                    Uid = targetUid,
                    ModSeq = ++target.HighestModSeq,
                    Flags = message.Flags,
                    InternalDate = message.InternalDate,
                    ScheduledDeleteAt = movingToTrash ? scheduledDeleteAt : null
                };
                db.MailboxMessages.Add(targetMessage);
                if (!move)
                {
                    db.QuotaLedger.Add(new QuotaLedgerEntry
                    {
                        IdempotencyKey = $"mailbox-message:add:{targetMessage.Id:N}",
                        AccountId = source.AccountId,
                        DomainId = source.Account.DomainId,
                        MailboxMessageId = targetMessage.Id,
                        Operation = QuotaLedgerOperation.Add,
                        Bytes = message.Message.Size
                    });
                }
                if (movingToTrash)
                {
                    db.CleanupQueue.Add(new CleanupQueueItem
                    {
                        ResourceType = CleanupResourceType.MailboxMessage,
                        ResourceId = targetMessage.Id,
                        ContentKey = message.Message.ContentKey,
                        AccountId = source.AccountId,
                        EstimatedBytes = message.Message.Size,
                        CreatedAt = now,
                        PurgeAfter = scheduledDeleteAt
                    });
                }
                if (move)
                {
                    if (movingFromTrash)
                    {
                        var pending = await db.CleanupQueue
                            .Where(x => x.ResourceType == CleanupResourceType.MailboxMessage
                                && x.ResourceId == message.Id
                                && (x.Status == CleanupQueueStatus.Pending || x.Status == CleanupQueueStatus.Failed))
                            .ToListAsync(cancellationToken);
                        foreach (var item in pending)
                        {
                            item.Status = CleanupQueueStatus.Restored;
                            item.CompletedAt = now;
                        }
                    }
                    db.MailboxMessages.Remove(message);
                }
            }
            if (!move)
            {
                source.Account.UsedBytes += copyBytes;
                source.Account.Domain.UsedBytes += copyBytes;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var expunged = move
                ? selected.Select(x => new ExpungedMailboxItem(x.Sequence, x.Uid)).ToArray()
                : [];
            return new CopyMoveResult(source.UidValidity, target.UidValidity, sourceUids, targetUids, expunged);
        }
        finally
        {
            mailboxLock.Release();
        }
    }

    public async Task<AppendResult> AppendAsync(
        Guid accountId,
        string mailboxName,
        Stream content,
        IReadOnlyCollection<string> flags,
        DateTimeOffset? internalDate,
        CancellationToken cancellationToken)
        => await sentCopies.AppendAsync(
            accountId,
            mailboxName,
            content,
            flags,
            internalDate,
            cancellationToken);

    public async Task<Stream> OpenMessageAsync(Guid mailboxMessageId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = await db.MailboxMessages
            .Where(x => x.Id == mailboxMessageId)
            .Select(x => x.Message.ContentKey)
            .SingleAsync(cancellationToken);
        return await contentStore.OpenReadAsync(key, cancellationToken);
    }

    private static async Task<MailboxSummary> SummarizeAsync(
        MailDbContext db,
        Mailbox mailbox,
        CancellationToken cancellationToken)
    {
        var messages = db.MailboxMessages.Where(x => x.MailboxId == mailbox.Id);
        var exists = await messages.LongCountAsync(cancellationToken);
        var flags = await messages.Select(x => x.Flags).ToListAsync(cancellationToken);
        var unseen = flags.LongCount(x => !HasFlag(x, "\\Seen"));
        return new MailboxSummary(mailbox.Id, mailbox.Name, exists, unseen, mailbox.UidValidity, mailbox.UidNext, mailbox.HighestModSeq);
    }

    private static async Task<Mailbox?> FindMailboxAsync(
        MailDbContext db,
        Guid accountId,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        var mailboxes = await db.Mailboxes
            .Where(x => x.AccountId == accountId)
            .ToListAsync(cancellationToken);
        return mailboxes.SingleOrDefault(x => MailboxNames.Equals(x.Name, normalizedName));
    }

    internal static async Task EnsureUidNextAtLeastAsync(
        MailDbContext db,
        Mailbox mailbox,
        CancellationToken cancellationToken)
    {
        var next = (await db.MailboxMessages
            .Where(x => x.MailboxId == mailbox.Id)
            .MaxAsync(x => (long?)x.Uid, cancellationToken) ?? 0) + 1;
        if (mailbox.UidNext < next)
        {
            mailbox.UidNext = next;
        }
    }

    private static async Task<bool> HasChildMailboxAsync(
        MailDbContext db,
        Guid accountId,
        string mailboxName,
        CancellationToken cancellationToken)
    {
        var prefix = mailboxName + "/";
        return (await db.Mailboxes
                .Where(x => x.AccountId == accountId)
                .Select(x => x.Name)
                .ToListAsync(cancellationToken))
            .Any(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string SubscriptionAccountPrefix(Guid accountId) => $"{SubscriptionPrefix}{accountId:N}.";

    private static string DefaultSubscriptionSeedAccountPrefix(Guid accountId) =>
        $"{DefaultSubscriptionSeedPrefix}{accountId:N}.";

    internal static string SubscriptionKey(Guid accountId, string mailboxName) =>
        SubscriptionAccountPrefix(accountId) + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(MailboxNames.Normalize(mailboxName)));

    internal static string DefaultSubscriptionSeedKey(Guid accountId, string mailboxName) =>
        DefaultSubscriptionSeedAccountPrefix(accountId) + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(MailboxNames.Normalize(mailboxName)));

    internal static string SuppressedDefaultFolderAccountPrefix(Guid accountId) =>
        $"{SuppressedDefaultFolderPrefix}{accountId:N}.";

    internal static string SuppressedDefaultFolderKey(Guid accountId, string mailboxName) =>
        SuppressedDefaultFolderAccountPrefix(accountId) + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(MailboxNames.Normalize(mailboxName)));

    private async Task<IReadOnlyList<MailboxSummary>> OrderForAccountAsync(
        Guid accountId,
        IEnumerable<MailboxSummary> mailboxes,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domainId = await db.Accounts.AsNoTracking().Where(x => x.Id == accountId)
            .Select(x => x.DomainId).SingleAsync(cancellationToken);
        var settings = await systemSettings.GetDomainMailboxSettingsAsync(domainId, cancellationToken);
        var order = settings.DefaultFolders.Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);
        return mailboxes
            .OrderBy(x => order.TryGetValue(x.Name, out var index) ? index : int.MaxValue)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task RenameSubscriptionsAsync(
        MailDbContext db,
        Guid accountId,
        string oldName,
        string newName,
        bool renameChildren,
        CancellationToken cancellationToken)
    {
        var prefix = SubscriptionAccountPrefix(accountId);
        var rows = await db.SystemSettings
            .Where(x => x.Key.StartsWith(prefix))
            .ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            var subscribed = JsonSerializer.Deserialize<string>(row.Value) ?? string.Empty;
            string? replacement = null;
            if (MailboxNames.Equals(subscribed, oldName))
            {
                replacement = newName;
            }
            else if (renameChildren && subscribed.StartsWith(oldName + "/", StringComparison.OrdinalIgnoreCase))
            {
                replacement = newName + subscribed[oldName.Length..];
            }

            if (replacement is null) continue;
            db.SystemSettings.Remove(row);
            db.SystemSettings.Add(new SystemSetting
            {
                Key = SubscriptionKey(accountId, replacement),
                Value = JsonSerializer.Serialize(replacement)
            });
        }
    }

    private static long NewUidValidity()
    {
        while (true)
        {
            var current = Volatile.Read(ref _lastUidValidity);
            var next = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), current + 1);
            if (Interlocked.CompareExchange(ref _lastUidValidity, next, current) == current)
            {
                return next;
            }
        }
    }

    private static IEnumerable<MailboxItem> SelectMessages(
        IReadOnlyList<MailboxMessage> messages,
        string sequenceSet,
        bool useUid)
    {
        var maximum = useUid ? messages.Select(x => x.Uid).DefaultIfEmpty(0).Max() : messages.Count;
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var sequence = index + 1L;
            var value = useUid ? message.Uid : sequence;
            if (SequenceSet.Contains(sequenceSet, value, maximum))
            {
                yield return new MailboxItem(
                    message.Id,
                    sequence,
                    message.Uid,
                    message.Flags,
                    message.ModSeq,
                    message.InternalDate,
                    message.Message?.Size ?? 0,
                    message.Message?.ContentKey ?? string.Empty);
            }
        }
    }

    private static HashSet<string> ParseFlags(string flags) =>
        new(flags.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    private static bool HasFlag(string flags, string flag) => ParseFlags(flags).Contains(flag);

    private async Task StoreFlagsForIdsAsync(
        Guid mailboxId,
        IReadOnlyCollection<Guid> mailboxMessageIds,
        IReadOnlyCollection<string> flags,
        CancellationToken cancellationToken)
    {
        var mailboxLock = MailboxLocks.GetOrAdd(mailboxId, _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var mailbox = await db.Mailboxes.SingleAsync(x => x.Id == mailboxId, cancellationToken);
            var selectedIds = mailboxMessageIds.ToHashSet();
            var messages = await db.MailboxMessages
                .Where(x => x.MailboxId == mailboxId && selectedIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            foreach (var message in messages)
            {
                var current = ParseFlags(message.Flags);
                var before = current.Count;
                current.UnionWith(flags);
                if (current.Count != before)
                {
                    message.Flags = string.Join(' ', current.Order(StringComparer.OrdinalIgnoreCase));
                    message.ModSeq = ++mailbox.HighestModSeq;
                }
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            mailboxLock.Release();
        }
    }

    private static bool TryParseDateSearch(string criteria, out string operatorName, out DateTime date)
    {
        var tokens = criteria.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        operatorName = tokens.Length == 2 ? tokens[0].ToUpperInvariant() : string.Empty;
        date = default;
        return tokens.Length == 2
               && (operatorName is "SINCE" or "BEFORE" or "ON")
               && DateTime.TryParseExact(
                   tokens[1],
                   "d-MMM-yyyy",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AllowWhiteSpaces,
                   out date);
    }

    private static bool TryParseHeaderSearch(string criteria, out string charset, out string headerName, out string needle)
    {
        var tokens = TokenizeSearchCriteria(criteria);
        charset = "UTF-8";
        headerName = "SUBJECT";
        needle = string.Empty;
        if (tokens.Count == 2 && tokens[0].Equals("SUBJECT", StringComparison.OrdinalIgnoreCase))
        {
            needle = tokens[1];
            return true;
        }

        if (tokens.Count == 3 && tokens[0].Equals("HEADER", StringComparison.OrdinalIgnoreCase))
        {
            headerName = tokens[1];
            needle = tokens[2];
            return true;
        }

        if (tokens.Count == 4
            && tokens[0].Equals("CHARSET", StringComparison.OrdinalIgnoreCase)
            && tokens[2].Equals("SUBJECT", StringComparison.OrdinalIgnoreCase))
        {
            charset = tokens[1];
            needle = tokens[3];
            return true;
        }

        if (tokens.Count == 5
            && tokens[0].Equals("CHARSET", StringComparison.OrdinalIgnoreCase)
            && tokens[2].Equals("HEADER", StringComparison.OrdinalIgnoreCase))
        {
            charset = tokens[1];
            headerName = tokens[3];
            needle = tokens[4];
            return true;
        }

        return false;
    }

    private static bool TryParseTextSearch(string criteria, out string charset, out string needle)
    {
        var tokens = TokenizeSearchCriteria(criteria);
        charset = "UTF-8";
        needle = string.Empty;
        if (tokens.Count == 2 && (tokens[0].Equals("TEXT", StringComparison.OrdinalIgnoreCase)
                                  || tokens[0].Equals("BODY", StringComparison.OrdinalIgnoreCase)))
        {
            needle = tokens[1];
            return true;
        }

        if (tokens.Count == 4
            && tokens[0].Equals("CHARSET", StringComparison.OrdinalIgnoreCase)
            && (tokens[2].Equals("TEXT", StringComparison.OrdinalIgnoreCase)
                || tokens[2].Equals("BODY", StringComparison.OrdinalIgnoreCase)))
        {
            charset = tokens[1];
            needle = tokens[3];
            return true;
        }

        return false;
    }

    private static bool IsSupportedSearchCharset(string charset) =>
        charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase)
        || charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSearchText(string value)
    {
        var compatibility = value.Normalize(NormalizationForm.FormKC).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(compatibility.Length);
        foreach (var character in compatibility)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }

    private static string UnfoldHeaderValue(string value) =>
        value.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal);

    private static string ExtractSearchableBody(MimeMessage message)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(message.TextBody))
        {
            values.Add(message.TextBody);
        }

        if (!string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            values.Add(StripHtml(message.HtmlBody));
        }

        if (values.Count == 0 && message.Body is not null)
        {
            CollectTextParts(message.Body, values);
        }

        return string.Join('\n', values.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static void CollectTextParts(MimeEntity entity, List<string> values)
    {
        if (entity is TextPart textPart)
        {
            try
            {
                var text = textPart.IsHtml ? StripHtml(textPart.Text) : textPart.Text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    values.Add(text);
                }
            }
            catch (Exception) when (textPart.Content is not null)
            {
                using var memory = new MemoryStream();
                textPart.Content.DecodeTo(memory);
                var fallback = Encoding.UTF8.GetString(memory.ToArray());
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    values.Add(textPart.IsHtml ? StripHtml(fallback) : fallback);
                }
            }
        }
        else if (entity is Multipart multipart)
        {
            foreach (var child in multipart)
            {
                CollectTextParts(child, values);
            }
        }
        else if (entity is MessagePart { Message.Body: not null } messagePart)
        {
            CollectTextParts(messagePart.Message.Body, values);
        }
    }

    private static string StripHtml(string html)
    {
        var withoutDangerousBlocks = HtmlStyleRegex().Replace(HtmlScriptRegex().Replace(html, " "), " ");
        var builder = new StringBuilder(withoutDangerousBlocks.Length);
        var inTag = false;
        foreach (var character in withoutDangerousBlocks)
        {
            if (character == '<')
            {
                inTag = true;
                builder.Append(' ');
                continue;
            }

            if (character == '>')
            {
                inTag = false;
                builder.Append(' ');
                continue;
            }

            if (!inTag)
            {
                builder.Append(character);
            }
        }

        return WebUtility.HtmlDecode(builder.ToString());
    }

    [GeneratedRegex(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlScriptRegex();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlStyleRegex();

    private static IReadOnlyList<string> TokenizeSearchCriteria(string criteria)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaped = false;
        foreach (var character in criteria)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (quoted && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}

internal static class SequenceSet
{
    public static bool Contains(string expression, long value, long maximum)
    {
        foreach (var segment in expression.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = segment.Split(':', 2);
            var start = Parse(range[0], maximum);
            var end = range.Length == 1 ? start : Parse(range[1], maximum);
            if (value >= Math.Min(start, end) && value <= Math.Max(start, end))
            {
                return true;
            }
        }

        return false;
    }

    private static long Parse(string value, long maximum) =>
        value == "*" ? maximum : long.TryParse(value, out var parsed) ? parsed : -1;
}
