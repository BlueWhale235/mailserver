using System.Data;
using System.Text.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

public sealed record MailboxFolderDeletionPreview(
    string FolderName,
    int AffectedAccountCount,
    int AffectedMailboxCount,
    long AffectedMessageCount,
    int AffectedRuleCount);

public sealed record MailboxFolderDeletionJobView(
    Guid Id,
    string FolderName,
    MailboxFolderDeletionStatus Status,
    int AffectedAccountCount,
    int AffectedMailboxCount,
    int AffectedRuleCount,
    long TotalMessages,
    long ProcessedMessages,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record DomainMailboxFoldersView(
    IReadOnlyList<string> SystemFolders,
    IReadOnlyList<string> ExtraFolders,
    IReadOnlyList<MailboxFolderDeletionJobView> DeletionJobs);

public sealed class MailboxFolderManagementService(
    IDbContextFactory<MailDbContext> dbFactory,
    SystemSettingsService systemSettings,
    ConfigurationSnapshotCache? configurationCache = null)
{
    private const string GlobalMailboxFoldersKey = "mailbox-defaults:folders";

    public async Task<DomainMailboxFoldersView> GetAsync(Guid domainId, CancellationToken cancellationToken)
    {
        var settings = await systemSettings.GetDomainMailboxSettingsAsync(domainId, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var jobEntities = await db.MailboxFolderDeletionJobs.AsNoTracking()
            .Where(x => x.DomainId == domainId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .ToListAsync(cancellationToken);
        return new DomainMailboxFoldersView(
            MailboxNames.SystemDefaults,
            settings.DefaultFolders.Where(x => !MailboxNames.IsSystemFolder(x)).ToArray(),
            jobEntities.Select(ToView).ToArray());
    }

    public async Task<DomainMailboxFoldersView> AddAsync(
        Guid domainId,
        string folderName,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = MailboxNames.Normalize(folderName);
        if (MailboxNames.IsSystemFolder(normalized))
            throw new InvalidOperationException("System folders already exist and cannot be added again.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await MailboxWriteCoordinator.BeginAsync(db, [domainId], cancellationToken);
        var configured = await GetConfiguredFoldersAsync(db, domainId, cancellationToken);
        if (configured.Any(x => x.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The folder already exists in this domain configuration.");
        if (await HasOverlappingActiveJobAsync(db, domainId, null, normalized, cancellationToken))
            throw new InvalidOperationException("An overlapping folder deletion task is still active.");

        configured.Add(normalized);
        await SaveConfiguredFoldersAsync(db, domainId, configured, cancellationToken);
        var accounts = await db.Accounts.Where(x => x.DomainId == domainId).Select(x => x.Id).ToListAsync(cancellationToken);
        var existing = await db.Mailboxes.IgnoreQueryFilters()
            .Where(x => accounts.Contains(x.AccountId) && x.Name == normalized)
            .ToListAsync(cancellationToken);
        foreach (var accountId in accounts)
        {
            var mailbox = existing.FirstOrDefault(x => x.AccountId == accountId);
            if (mailbox is null)
            {
                mailbox = new Mailbox { AccountId = accountId, Name = normalized };
                db.Mailboxes.Add(mailbox);
            }
            else if (mailbox.DeletedAt is not null)
            {
                mailbox.DeletedAt = null;
                mailbox.DeletedBy = null;
                mailbox.PurgeAfter = null;
                foreach (var cleanup in await db.CleanupQueue.Where(x =>
                             x.ResourceType == CleanupResourceType.Mailbox
                             && x.ResourceId == mailbox.Id
                             && (x.Status == CleanupQueueStatus.Pending || x.Status == CleanupQueueStatus.Failed))
                         .ToListAsync(cancellationToken))
                {
                    cleanup.Status = CleanupQueueStatus.Restored;
                    cleanup.CompletedAt = DateTimeOffset.UtcNow;
                }
            }

            var suppressionKey = MailboxService.SuppressedDefaultFolderKey(accountId, normalized);
            db.SystemSettings.RemoveRange(await db.SystemSettings.Where(x => x.Key == suppressionKey).ToListAsync(cancellationToken));
            await EnsureSettingAsync(db, MailboxService.SubscriptionKey(accountId, normalized), JsonSerializer.Serialize(normalized), cancellationToken);
            await EnsureSettingAsync(db, MailboxService.DefaultSubscriptionSeedKey(accountId, normalized), "true", cancellationToken);
        }

        AddAudit(db, actor, "mailbox-folder.add", $"domain:{domainId}", normalized);
        var settingsVersion = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Settings, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        configurationCache?.ObserveCommittedVersion(ConfigurationScopes.Settings, settingsVersion);
        return await GetAsync(domainId, cancellationToken);
    }

    public async Task<DomainMailboxFoldersView> ReorderAsync(
        Guid domainId,
        IReadOnlyList<string> extraFolders,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = extraFolders.Select(MailboxNames.Normalize).ToArray();
        if (normalized.Any(MailboxNames.IsSystemFolder)
            || normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new InvalidOperationException("Folder order contains a duplicate or system folder.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var current = (await GetConfiguredFoldersAsync(db, domainId, cancellationToken))
            .Where(x => !MailboxNames.IsSystemFolder(x)).ToArray();
        if (!current.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(normalized))
            throw new InvalidOperationException("Folder order must contain every configured extra folder exactly once.");
        await SaveConfiguredFoldersAsync(db, domainId, normalized.ToList(), cancellationToken);
        AddAudit(db, actor, "mailbox-folder.reorder", $"domain:{domainId}", string.Join(',', normalized));
        var settingsVersion = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Settings, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        configurationCache?.ObserveCommittedVersion(ConfigurationScopes.Settings, settingsVersion);
        return await GetAsync(domainId, cancellationToken);
    }

    public async Task<MailboxFolderDeletionPreview> PreviewAsync(
        Guid domainId,
        string folderName,
        CancellationToken cancellationToken)
    {
        var normalized = ValidateDeletable(folderName);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Domains.AnyAsync(x => x.Id == domainId, cancellationToken))
            throw new KeyNotFoundException("Domain not found.");
        return await PreviewCoreAsync(db, domainId, null, normalized, cancellationToken);
    }

    public async Task<MailboxFolderDeletionJobView> RequestDomainDeletionAsync(
        Guid domainId,
        string folderName,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = ValidateDeletable(folderName);
        return await RequestDeletionAsync(domainId, null, normalized, actor, removeFromConfiguration: true, cancellationToken);
    }

    public async Task<MailboxFolderDeletionJobView> RequestAccountDeletionAsync(
        Guid accountId,
        string folderName,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = ValidateDeletable(folderName);
        await using var lookup = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domainId = await lookup.Accounts.Where(x => x.Id == accountId).Select(x => (Guid?)x.DomainId)
            .SingleOrDefaultAsync(cancellationToken) ?? throw new KeyNotFoundException("Account not found.");
        return await RequestDeletionAsync(domainId, accountId, normalized, actor, removeFromConfiguration: false, cancellationToken);
    }

    public async Task<MailboxFolderDeletionJobView> RetryAsync(
        Guid domainId,
        Guid jobId,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.MailboxFolderDeletionJobs.SingleOrDefaultAsync(x => x.Id == jobId && x.DomainId == domainId, cancellationToken)
            ?? throw new KeyNotFoundException("Folder deletion task not found.");
        if (job.Status != MailboxFolderDeletionStatus.Failed)
            throw new InvalidOperationException("Only failed folder deletion tasks can be retried.");
        job.Status = MailboxFolderDeletionStatus.Pending;
        job.LastError = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await WakeTaskAsync(db, cancellationToken);
        AddAudit(db, actor, "mailbox-folder.delete-retry", $"domain:{domainId}", $"job={jobId}");
        await db.SaveChangesAsync(cancellationToken);
        return ToView(job);
    }

    public async Task<int> ProcessPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        batchSize = Math.Clamp(batchSize, 1, 5000);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var jobId = await db.MailboxFolderDeletionJobs.AsNoTracking()
            .Where(x => x.Status == MailboxFolderDeletionStatus.Pending || x.Status == MailboxFolderDeletionStatus.Running)
            .OrderBy(x => x.CreatedAt)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (jobId is null) return 0;

        try
        {
            var writeDomainId = await db.MailboxFolderDeletionJobs.AsNoTracking().Where(x => x.Id == jobId.Value)
                .Select(x => x.DomainId).SingleAsync(cancellationToken);
            await using var transaction = await MailboxWriteCoordinator.BeginAsync(db, [writeDomainId], cancellationToken);
            var job = await db.MailboxFolderDeletionJobs.SingleAsync(x => x.Id == jobId.Value, cancellationToken);
            job.Status = MailboxFolderDeletionStatus.Running;
            job.StartedAt ??= DateTimeOffset.UtcNow;
            job.Attempts++;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            var mailboxIds = await db.Mailboxes.Where(x => x.PendingDeletionJobId == job.Id).Select(x => x.Id).ToArrayAsync(cancellationToken);
            var messages = await db.MailboxMessages
                .Where(x => mailboxIds.Contains(x.MailboxId))
                .Include(x => x.Mailbox)
                .OrderBy(x => x.MailboxId).ThenBy(x => x.Uid)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
            var inboxes = await db.Mailboxes.Where(x =>
                    messages.Select(message => message.Mailbox.AccountId).Contains(x.AccountId)
                    && x.Name == "INBOX")
                .ToDictionaryAsync(x => x.AccountId, cancellationToken);
            foreach (var message in messages)
            {
                if (!inboxes.TryGetValue(message.Mailbox.AccountId, out var inbox))
                    throw new InvalidOperationException($"INBOX is missing for account {message.Mailbox.AccountId}.");
                await MailboxService.EnsureUidNextAtLeastAsync(db, inbox, cancellationToken);
                message.MailboxId = inbox.Id;
                message.Uid = MailboxWriteAccounting.TakeUid(inbox);
                message.ModSeq = ++inbox.HighestModSeq;
                message.ScheduledDeleteAt = null;
            }
            job.ProcessedMessages += messages.Count;

            var remaining = await db.MailboxMessages.CountAsync(x => mailboxIds.Contains(x.MailboxId), cancellationToken) - messages.Count;
            if (remaining == 0)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var mailbox in await db.Mailboxes.Where(x => x.PendingDeletionJobId == job.Id).ToListAsync(cancellationToken))
                {
                    mailbox.PendingDeletionJobId = null;
                    mailbox.DeletedAt = now;
                    mailbox.DeletedBy = "mailbox-folder-delete-task";
                    mailbox.PurgeAfter = now.AddDays(30);
                    db.CleanupQueue.Add(new CleanupQueueItem
                    {
                        ResourceType = CleanupResourceType.Mailbox,
                        ResourceId = mailbox.Id,
                        AccountId = mailbox.AccountId,
                        DomainId = job.DomainId,
                        CreatedAt = now,
                        PurgeAfter = mailbox.PurgeAfter.Value
                    });
                    db.SystemSettings.RemoveRange(await db.SystemSettings
                        .Where(x => x.Key == MailboxService.SubscriptionKey(mailbox.AccountId, mailbox.Name))
                        .ToListAsync(cancellationToken));
                }
                job.Status = MailboxFolderDeletionStatus.Completed;
                job.CompletedAt = now;
                job.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return messages.Count;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await using var failureDb = await dbFactory.CreateDbContextAsync(cancellationToken);
            var job = await failureDb.MailboxFolderDeletionJobs.SingleAsync(x => x.Id == jobId.Value, cancellationToken);
            job.Status = MailboxFolderDeletionStatus.Failed;
            job.LastError = exception.Message;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await failureDb.SaveChangesAsync(cancellationToken);
            return 0;
        }
    }

    private async Task<MailboxFolderDeletionJobView> RequestDeletionAsync(
        Guid domainId,
        Guid? accountId,
        string normalized,
        string actor,
        bool removeFromConfiguration,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await MailboxWriteCoordinator.BeginAsync(db, [domainId], cancellationToken);
        var existing = await db.MailboxFolderDeletionJobs.FirstOrDefaultAsync(x =>
            x.DomainId == domainId && x.AccountId == accountId
            && x.FolderName == normalized
            && x.Status != MailboxFolderDeletionStatus.Completed, cancellationToken);
        if (existing is not null) return ToView(existing);
        if (await HasOverlappingActiveJobAsync(db, domainId, accountId, normalized, cancellationToken))
            throw new InvalidOperationException("An overlapping folder deletion task is already active.");

        var preview = await PreviewCoreAsync(db, domainId, accountId, normalized, cancellationToken);
        var job = new MailboxFolderDeletionJob
        {
            DomainId = domainId,
            AccountId = accountId,
            FolderName = normalized,
            AffectedAccountCount = preview.AffectedAccountCount,
            AffectedMailboxCount = preview.AffectedMailboxCount,
            AffectedRuleCount = preview.AffectedRuleCount,
            TotalMessages = preview.AffectedMessageCount,
            RequestedBy = actor
        };
        db.MailboxFolderDeletionJobs.Add(job);
        var mailboxes = await MatchingMailboxes(db, domainId, accountId, normalized).ToListAsync(cancellationToken);
        foreach (var mailbox in mailboxes) mailbox.PendingDeletionJobId = job.Id;

        if (removeFromConfiguration)
        {
            var configured = await GetConfiguredFoldersAsync(db, domainId, cancellationToken);
            configured.RemoveAll(x => FolderMatches(x, normalized));
            await SaveConfiguredFoldersAsync(db, domainId, configured, cancellationToken);
            await RedirectRulesAsync(db, domainId, normalized, cancellationToken);
        }
        else if (accountId is not null)
        {
            await EnsureSettingAsync(db, MailboxService.SuppressedDefaultFolderKey(accountId.Value, normalized), "true", cancellationToken);
        }

        if (mailboxes.Count == 0)
        {
            job.Status = MailboxFolderDeletionStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            await WakeTaskAsync(db, cancellationToken);
        }
        AddAudit(db, actor, "mailbox-folder.delete-request", $"domain:{domainId}", $"folder={normalized};job={job.Id}");
        long? settingsVersion = null;
        long? rulesVersion = null;
        if (removeFromConfiguration)
        {
            settingsVersion = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Settings, cancellationToken);
            rulesVersion = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Rules, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (settingsVersion is not null) configurationCache?.ObserveCommittedVersion(ConfigurationScopes.Settings, settingsVersion.Value);
        if (rulesVersion is not null) configurationCache?.ObserveCommittedVersion(ConfigurationScopes.Rules, rulesVersion.Value);
        return ToView(job);
    }

    private static async Task RedirectRulesAsync(MailDbContext db, Guid domainId, string folderName, CancellationToken cancellationToken)
    {
        var rules = await db.MailRules.Include(x => x.Actions).Where(x => x.DomainId == domainId).ToListAsync(cancellationToken);
        foreach (var rule in rules)
        {
            var changed = false;
            if (FolderMatches(rule.TargetMailboxName, folderName))
            {
                rule.TargetMailboxName = "INBOX";
                changed = true;
            }
            foreach (var action in rule.Actions.Where(x => x.Kind == MailRuleActionKind.MoveToFolder))
            {
                var values = JsonSerializer.Deserialize<string[]>(action.ValuesJson) ?? [];
                var updated = values.Select(value => FolderMatches(value, folderName) ? "INBOX" : value).ToArray();
                if (!values.SequenceEqual(updated, StringComparer.OrdinalIgnoreCase))
                {
                    action.ValuesJson = JsonSerializer.Serialize(updated);
                    changed = true;
                }
            }
            if (changed) rule.UpdatedAt = DateTimeOffset.UtcNow;
        }
        foreach (var sentCopy in await db.SentCopyJobs.Where(x =>
                     x.Account.DomainId == domainId && x.State != SentCopyState.Completed).ToListAsync(cancellationToken))
        {
            if (FolderMatches(sentCopy.TargetMailboxName, folderName)) sentCopy.TargetMailboxName = "INBOX";
        }
    }

    private static async Task<MailboxFolderDeletionPreview> PreviewCoreAsync(
        MailDbContext db,
        Guid domainId,
        Guid? accountId,
        string folderName,
        CancellationToken cancellationToken)
    {
        var mailboxes = MatchingMailboxes(db, domainId, accountId, folderName);
        var ids = await mailboxes.Select(x => x.Id).ToArrayAsync(cancellationToken);
        var accounts = await mailboxes.Select(x => x.AccountId).Distinct().CountAsync(cancellationToken);
        var messages = await db.MailboxMessages.LongCountAsync(x => ids.Contains(x.MailboxId), cancellationToken);
        var ruleCount = accountId is null
            ? (await db.MailRules.Include(x => x.Actions).Where(x => x.DomainId == domainId).ToListAsync(cancellationToken))
                .Count(rule => FolderMatches(rule.TargetMailboxName, folderName)
                    || rule.Actions.Where(x => x.Kind == MailRuleActionKind.MoveToFolder)
                        .SelectMany(x => JsonSerializer.Deserialize<string[]>(x.ValuesJson) ?? [])
                        .Any(value => FolderMatches(value, folderName)))
            : 0;
        return new MailboxFolderDeletionPreview(folderName, accounts, ids.Length, messages, ruleCount);
    }

    private static IQueryable<Mailbox> MatchingMailboxes(MailDbContext db, Guid domainId, Guid? accountId, string folderName)
    {
        var prefix = folderName + "/";
        var query = db.Mailboxes.Where(x => x.Account.DomainId == domainId && (x.Name == folderName || x.Name.StartsWith(prefix)));
        if (accountId is not null) query = query.Where(x => x.AccountId == accountId.Value);
        return query;
    }

    private static async Task<bool> HasOverlappingActiveJobAsync(
        MailDbContext db,
        Guid domainId,
        Guid? accountId,
        string folderName,
        CancellationToken cancellationToken)
    {
        var jobs = await db.MailboxFolderDeletionJobs.AsNoTracking().Where(x =>
                x.DomainId == domainId
                && (accountId == null || x.AccountId == null || x.AccountId == accountId)
                && x.Status != MailboxFolderDeletionStatus.Completed)
            .Select(x => x.FolderName).ToListAsync(cancellationToken);
        return jobs.Any(existing => FolderMatches(existing, folderName) || FolderMatches(folderName, existing));
    }

    private static bool FolderMatches(string candidate, string root) =>
        candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static string ValidateDeletable(string folderName)
    {
        var normalized = MailboxNames.Normalize(folderName);
        if (MailboxNames.IsSystemFolder(normalized))
            throw new InvalidOperationException("System folders cannot be deleted.");
        return normalized;
    }

    private static MailboxFolderDeletionJobView ToView(MailboxFolderDeletionJob job) => new(
        job.Id, job.FolderName, job.Status, job.AffectedAccountCount, job.AffectedMailboxCount,
        job.AffectedRuleCount, job.TotalMessages, job.ProcessedMessages, job.LastError, job.CreatedAt, job.CompletedAt);

    private static async Task<List<string>> GetConfiguredFoldersAsync(MailDbContext db, Guid domainId, CancellationToken cancellationToken)
    {
        if (!await db.Domains.AnyAsync(x => x.Id == domainId, cancellationToken))
            throw new KeyNotFoundException("Domain not found.");
        var domainValue = await db.SystemSettings.Where(x => x.Key == SystemSettingsService.DomainMailboxFoldersKey(domainId))
            .Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        var value = domainValue ?? await db.SystemSettings.Where(x => x.Key == GlobalMailboxFoldersKey)
            .Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        try
        {
            return MailboxNames.NormalizeDefaults(JsonSerializer.Deserialize<string[]>(value ?? "[]") ?? []).ToList();
        }
        catch (JsonException)
        {
            return MailboxNames.SystemDefaults.ToList();
        }
    }

    private static async Task SaveConfiguredFoldersAsync(MailDbContext db, Guid domainId, IReadOnlyList<string> folders, CancellationToken cancellationToken)
    {
        var normalized = MailboxNames.NormalizeDefaults(folders);
        var key = SystemSettingsService.DomainMailboxFoldersKey(domainId);
        var setting = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting is null) db.SystemSettings.Add(new SystemSetting { Key = key, Value = JsonSerializer.Serialize(normalized) });
        else setting.Value = JsonSerializer.Serialize(normalized);
    }

    private static async Task EnsureSettingAsync(MailDbContext db, string key, string value, CancellationToken cancellationToken)
    {
        if (!await db.SystemSettings.AnyAsync(x => x.Key == key, cancellationToken))
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
    }

    private static async Task WakeTaskAsync(MailDbContext db, CancellationToken cancellationToken)
    {
        var task = await db.ScheduledTasks.SingleOrDefaultAsync(x => x.Type == "mailbox.folder-delete", cancellationToken);
        if (task is not null) task.NextRunAt = DateTimeOffset.UtcNow;
    }

    private static void AddAudit(MailDbContext db, string actor, string action, string resource, string? detail) =>
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = action, Resource = resource, Detail = detail });
}
