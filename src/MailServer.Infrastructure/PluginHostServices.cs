using System.Text;
using System.Text.Json;
using MailServer.Domain;
using MailServer.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace MailServer.Infrastructure;

/// <summary>
/// 插件发信适配器。插件请求会重新进入入站链路，确保规则、配额、队列和审计一致。
/// </summary>
public sealed class PluginMailSender(IInboundMessageService inboundMessages) : IPluginMailSender
{
    public async Task<PluginMailSendResult> SendAsync(
        PluginMailSendRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Sender)) throw new ArgumentException("Sender is required.", nameof(request));
        if (request.Recipients.Count == 0) throw new ArgumentException("At least one recipient is required.", nameof(request));
        if (!request.Content.CanRead) throw new ArgumentException("Content stream must be readable.", nameof(request));

        // 这里不绕过 InboundMessageService，避免插件直接写队列导致本地投递、转发或 Sent 副本逻辑缺失。
        var acceptance = await inboundMessages.AcceptAsync(
            new InboundEnvelope(
                AddressParser.Normalize(request.Sender),
                request.Recipients.Select(AddressParser.Normalize).ToArray(),
                request.AuthenticatedUser ?? "plugin",
                string.IsNullOrWhiteSpace(request.RemoteAddress) ? "plugin" : request.RemoteAddress,
                string.IsNullOrWhiteSpace(request.HeloName) ? "plugin.local" : request.HeloName)
                { SourceType = "plugin" },
            request.Content,
            cancellationToken);

        return new PluginMailSendResult(
            acceptance.MessageId,
            acceptance.ContentKey,
            acceptance.LocalDeliveries,
            acceptance.QueuedDeliveries);
    }
}

/// <summary>
/// 插件队列控制适配器，限制插件只能通过公开语义修改投递任务。
/// </summary>
public sealed class PluginDeliveryQueue(
    IDbContextFactory<MailDbContext> dbFactory,
    IDeliveryQueue deliveryQueue) : IPluginDeliveryQueue
{
    public async Task<PluginDeliveryJobInfo?> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.DeliveryJobs.AsNoTracking()
            .Where(x => x.Id == jobId)
            .Select(x => new PluginDeliveryJobInfo(
                x.Id,
                x.MessageId,
                x.Sender,
                x.Recipient,
                x.Status.ToString(),
                x.Attempts,
                x.NextAttemptAt,
                x.LastError))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task RetryAsync(Guid jobId, CancellationToken cancellationToken) =>
        deliveryQueue.RetryNowAsync(jobId, cancellationToken);

    public async Task DeferAsync(Guid jobId, TimeSpan delay, string reason, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DeliveryJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken)
            ?? throw new KeyNotFoundException("Delivery job not found.");
        var now = DateTimeOffset.UtcNow;
        // 插件延期会释放当前租约，使 Worker 按新的 NextAttemptAt 重新领取。
        job.Status = DeliveryStatus.Deferred;
        job.NextAttemptAt = now.Add(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.LastError = reason;
        job.UpdatedAt = now;
        AddAudit(db, "plugin", "plugin.delivery.defer", $"delivery:{job.Id}", reason);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task FailPermanentAsync(Guid jobId, string reason, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DeliveryJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken)
            ?? throw new KeyNotFoundException("Delivery job not found.");
        var now = DateTimeOffset.UtcNow;
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            DeliveryJobId = job.Id,
            AttemptNumber = job.Attempts,
            Succeeded = false,
            PermanentFailure = true,
            Response = reason
        });
        job.Status = DeliveryStatus.Failed;
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.LastError = reason;
        job.UpdatedAt = now;
        AddAudit(db, "plugin", "plugin.delivery.fail", $"delivery:{job.Id}", reason);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelAsync(Guid jobId, string reason, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DeliveryJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken)
            ?? throw new KeyNotFoundException("Delivery job not found.");
        AddAudit(db, "plugin", "plugin.delivery.cancel", $"delivery:{job.Id}", reason);
        db.DeliveryJobs.Remove(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AddNoteAsync(Guid jobId, string note, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.DeliveryJobs.AnyAsync(x => x.Id == jobId, cancellationToken))
            throw new KeyNotFoundException("Delivery job not found.");
        AddAudit(db, "plugin", "plugin.delivery.note", $"delivery:{jobId}", note);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void AddAudit(MailDbContext db, string actor, string action, string resource, string? detail) =>
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor,
            Action = action,
            Resource = resource,
            Detail = detail
        });
}

/// <summary>
/// 插件目录查询适配器，返回稳定 DTO，避免插件依赖内部 EF 实体。
/// </summary>
public sealed class PluginDirectory(IDbContextFactory<MailDbContext> dbFactory) : IPluginDirectory
{
    public async Task<PluginDomainInfo?> FindDomainAsync(string domainName, CancellationToken cancellationToken)
    {
        var normalized = domainName.Trim().TrimEnd('.').ToLowerInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Domains.AsNoTracking()
            .Where(x => x.Name == normalized)
            .Select(x => new PluginDomainInfo(x.Id, x.Name, x.IsActive, x.QuotaBytes))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PluginAccountInfo?> FindAccountAsync(string address, CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(address, out var localPart, out var domain)) return null;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Accounts.AsNoTracking()
            .Where(x => x.LocalPart == localPart && x.Domain.Name == domain)
            .Select(x => new PluginAccountInfo(
                x.Id,
                x.DomainId,
                x.LocalPart + "@" + x.Domain.Name,
                x.IsEnabled,
                x.QuotaBytes))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PluginRecipientResolution> ResolveRecipientAsync(string address, CancellationToken cancellationToken)
    {
        var normalized = AddressParser.TrySplit(address, out var localPart, out var domain)
            ? $"{localPart}@{domain}"
            : address;
        if (!AddressParser.TrySplit(normalized, out localPart, out domain))
            return new PluginRecipientResolution(address, false, null, null);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var account = await db.Accounts.AsNoTracking()
            .Where(x => x.LocalPart == localPart && x.Domain.Name == domain && x.IsEnabled && x.Domain.IsActive)
            .Select(x => new { x.Id, x.DomainId })
            .SingleOrDefaultAsync(cancellationToken);
        if (account is not null) return new PluginRecipientResolution(normalized, true, account.Id, account.DomainId);

        var alias = await db.Aliases.AsNoTracking()
            .Where(x => x.SourceLocalPart == localPart
                        && x.Domain.Name == domain
                        && x.Domain.IsActive
                        && x.IsEnabled
                        && x.Mode != MailAliasMode.Disabled
                        && x.TargetAccountId != null
                        && x.TargetAccount!.IsEnabled
                        && x.TargetAccount.Domain.IsActive)
            .Select(x => new { AccountId = x.TargetAccountId, x.TargetAccount!.DomainId })
            .SingleOrDefaultAsync(cancellationToken);
        return alias is null
            ? new PluginRecipientResolution(normalized, false, null, null)
            : new PluginRecipientResolution(normalized, true, alias.AccountId, alias.DomainId);
    }
}

public sealed class PluginMessageStore(
    IDbContextFactory<MailDbContext> dbFactory,
    IMessageContentStore contentStore,
    ILogger<PluginMessageStore> logger) : IPluginMessageStore
{
    public async Task<PluginStoredMessageInfo?> GetAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Messages.AsNoTracking()
            .Where(x => x.Id == messageId)
            .Select(x => new PluginStoredMessageInfo(
                x.Id,
                x.ContentKey,
                x.EnvelopeFrom,
                x.InternetMessageId,
                x.Size,
                x.ReceivedAt))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<Stream> OpenReadAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var info = await GetRequiredAsync(messageId, cancellationToken);
        return await contentStore.OpenReadAsync(info.ContentKey, cancellationToken);
    }

    public async Task<IReadOnlyList<PluginMessageHeader>> ReadHeadersAsync(Guid messageId, CancellationToken cancellationToken)
    {
        await using var stream = await OpenReadAsync(messageId, cancellationToken);
        var message = await LoadMimeMessageWithFallbackAsync(stream, cancellationToken);
        return message.Headers
            .Select(x => new PluginMessageHeader(x.Field, x.Value))
            .ToArray();
    }

    public async Task ReplaceContentAsync(Guid messageId, Stream content, CancellationToken cancellationToken)
    {
        if (!content.CanRead) throw new ArgumentException("Content stream must be readable.", nameof(content));
        var descriptor = await contentStore.SaveAsync(content, cancellationToken);
        string? oldContentKey = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var message = await db.Messages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken)
                ?? throw new KeyNotFoundException("Message not found.");
            oldContentKey = message.ContentKey;
            message.ContentKey = descriptor.Key;
            message.Size = descriptor.Size;
            message.InternetMessageId = await ReadInternetMessageIdAsync(descriptor.Key, cancellationToken);
            message.InternetMessageIdHash = MessageIdLookup.Hash(message.InternetMessageId);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await contentStore.DeleteIfUnreferencedAsync(descriptor.Key, cancellationToken);
            throw;
        }

        if (!string.IsNullOrWhiteSpace(oldContentKey))
            await contentStore.DeleteIfUnreferencedAsync(oldContentKey, cancellationToken);
    }

    public async Task UpsertHeadersAsync(
        Guid messageId,
        IReadOnlyList<PluginMessageHeaderUpdate> headers,
        CancellationToken cancellationToken)
    {
        if (headers.Count == 0) return;
        foreach (var header in headers)
            if (string.IsNullOrWhiteSpace(header.Name))
                throw new ArgumentException("Header name is required.", nameof(headers));

        MimeMessage message;
        await using (var current = await OpenReadAsync(messageId, cancellationToken))
        {
            message = await LoadMimeMessageWithFallbackAsync(current, cancellationToken);
        }
        foreach (var header in headers)
        {
            while (message.Headers.Contains(header.Name))
                message.Headers.Remove(header.Name);
            message.Headers.Add(header.Name, header.Value);
        }

        var updated = new MemoryStream();
        await message.WriteToAsync(updated, cancellationToken);
        updated.Position = 0;
        await ReplaceContentAsync(messageId, updated, cancellationToken);
    }

    private async Task<PluginStoredMessageInfo> GetRequiredAsync(Guid messageId, CancellationToken cancellationToken) =>
        await GetAsync(messageId, cancellationToken) ?? throw new KeyNotFoundException("Message not found.");

    private async Task<string?> ReadInternetMessageIdAsync(string contentKey, CancellationToken cancellationToken)
    {
        await using var stream = await contentStore.OpenReadAsync(contentKey, cancellationToken);
        return (await LoadMimeMessageWithFallbackAsync(stream, cancellationToken)).MessageId;
    }

    private async Task<MimeMessage> LoadMimeMessageWithFallbackAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            return await MimeMessage.LoadAsync(stream, cancellationToken);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException)
        {
            logger.LogInformation(exception, "Plugin message store could not parse MIME content; using body-only fallback.");
            if (stream.CanSeek) stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            return new MimeMessage { Body = new TextPart("plain") { Text = await reader.ReadToEndAsync(cancellationToken) } };
        }
    }
}

public sealed class PluginMailboxStore(
    IDbContextFactory<MailDbContext> dbFactory,
    IMailboxService mailboxes) : IPluginMailboxStore
{
    public async Task<IReadOnlyList<PluginMailboxInfo>> ListMailboxesAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await EnsureActiveAccountAsync(accountId, cancellationToken);
        return (await mailboxes.ListAsync(accountId, cancellationToken)).Select(ToPluginMailbox).ToArray();
    }

    public async Task<IReadOnlyList<PluginMailboxInfo>> ListMailboxesAsync(
        string accountAddress,
        CancellationToken cancellationToken) =>
        await ListMailboxesAsync(await ResolveActiveAccountIdAsync(accountAddress, cancellationToken), cancellationToken);

    public async Task<PluginMailboxInfo> CreateMailboxAsync(
        Guid accountId,
        string mailboxName,
        CancellationToken cancellationToken)
    {
        await EnsureActiveAccountAsync(accountId, cancellationToken);
        var created = await mailboxes.CreateAsync(accountId, mailboxName, cancellationToken);
        await AddAuditAsync("plugin.mailbox.folder.create", $"account:{accountId}", created.Name, cancellationToken);
        return ToPluginMailbox(created);
    }

    public async Task<PluginMailboxInfo> CreateMailboxAsync(
        string accountAddress,
        string mailboxName,
        CancellationToken cancellationToken) =>
        await CreateMailboxAsync(await ResolveActiveAccountIdAsync(accountAddress, cancellationToken), mailboxName, cancellationToken);

    public async Task<IReadOnlyList<PluginMailboxMessageInfo>> ListMessagesAsync(
        Guid accountId,
        string mailboxName,
        CancellationToken cancellationToken)
    {
        var mailbox = await ResolveMailboxAsync(accountId, mailboxName, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.MailboxMessages.AsNoTracking()
            .Include(x => x.Message)
            .Where(x => x.MailboxId == mailbox.Id)
            .OrderBy(x => x.Uid)
            .ToListAsync(cancellationToken);
        return rows.Select(row => ToPluginMessage(mailbox, row)).ToArray();
    }

    public async Task<IReadOnlyList<PluginMailboxMessageInfo>> ListMessagesAsync(
        string accountAddress,
        string mailboxName,
        CancellationToken cancellationToken) =>
        await ListMessagesAsync(await ResolveActiveAccountIdAsync(accountAddress, cancellationToken), mailboxName, cancellationToken);

    public Task<PluginMailboxOperationResult> MoveAsync(
        Guid accountId,
        string sourceMailboxName,
        PluginMailboxMessageSelector selector,
        string destinationMailboxName,
        CancellationToken cancellationToken) =>
        CopyOrMoveAsync(accountId, sourceMailboxName, selector, destinationMailboxName, move: true, cancellationToken);

    public Task<PluginMailboxOperationResult> CopyAsync(
        Guid accountId,
        string sourceMailboxName,
        PluginMailboxMessageSelector selector,
        string destinationMailboxName,
        CancellationToken cancellationToken) =>
        CopyOrMoveAsync(accountId, sourceMailboxName, selector, destinationMailboxName, move: false, cancellationToken);

    public Task<PluginMailboxOperationResult> SetFlagsAsync(
        Guid accountId,
        string mailboxName,
        PluginMailboxMessageSelector selector,
        IReadOnlyCollection<string> flags,
        CancellationToken cancellationToken) =>
        ChangeFlagsAsync(accountId, mailboxName, selector, "FLAGS", flags, cancellationToken);

    public Task<PluginMailboxOperationResult> AddFlagsAsync(
        Guid accountId,
        string mailboxName,
        PluginMailboxMessageSelector selector,
        IReadOnlyCollection<string> flags,
        CancellationToken cancellationToken) =>
        ChangeFlagsAsync(accountId, mailboxName, selector, "+FLAGS", flags, cancellationToken);

    public Task<PluginMailboxOperationResult> RemoveFlagsAsync(
        Guid accountId,
        string mailboxName,
        PluginMailboxMessageSelector selector,
        IReadOnlyCollection<string> flags,
        CancellationToken cancellationToken) =>
        ChangeFlagsAsync(accountId, mailboxName, selector, "-FLAGS", flags, cancellationToken);

    public Task<PluginMailboxOperationResult> MarkReadAsync(
        Guid accountId,
        string mailboxName,
        PluginMailboxMessageSelector selector,
        CancellationToken cancellationToken) =>
        AddFlagsAsync(accountId, mailboxName, selector, ["\\Seen"], cancellationToken);

    public Task<PluginMailboxOperationResult> MarkUnreadAsync(
        Guid accountId,
        string mailboxName,
        PluginMailboxMessageSelector selector,
        CancellationToken cancellationToken) =>
        RemoveFlagsAsync(accountId, mailboxName, selector, ["\\Seen"], cancellationToken);

    private async Task<PluginMailboxOperationResult> CopyOrMoveAsync(
        Guid accountId,
        string sourceMailboxName,
        PluginMailboxMessageSelector selector,
        string destinationMailboxName,
        bool move,
        CancellationToken cancellationToken)
    {
        var source = await ResolveMailboxAsync(accountId, sourceMailboxName, cancellationToken);
        var target = await ResolveMailboxAsync(accountId, destinationMailboxName, cancellationToken);
        var selected = await ResolveSelectorAsync(source.Id, selector, cancellationToken);
        var result = await mailboxes.CopyAsync(source.Id, BuildUidSet(selected), useUid: true, target.Name, move, cancellationToken);
        await AddAuditAsync(
            move ? "plugin.mailbox.move" : "plugin.mailbox.copy",
            $"mailbox:{source.Id}",
            $"{source.Name}->{target.Name}; uids={string.Join(',', result.SourceUids)}",
            cancellationToken);
        return new PluginMailboxOperationResult(
            target.Id,
            target.Name,
            result.TargetUidValidity,
            result.SourceUids,
            result.TargetUids,
            selected.Select(x => x.MailboxMessageId).ToArray());
    }

    private async Task<PluginMailboxOperationResult> ChangeFlagsAsync(
        Guid accountId,
        string mailboxName,
        PluginMailboxMessageSelector selector,
        string operation,
        IReadOnlyCollection<string> flags,
        CancellationToken cancellationToken)
    {
        var mailbox = await ResolveMailboxAsync(accountId, mailboxName, cancellationToken);
        var selected = await ResolveSelectorAsync(mailbox.Id, selector, cancellationToken);
        var normalizedFlags = NormalizeFlags(flags);
        await mailboxes.StoreFlagsAsync(mailbox.Id, BuildUidSet(selected), useUid: true, operation, normalizedFlags, cancellationToken);
        await AddAuditAsync(
            "plugin.mailbox.flags",
            $"mailbox:{mailbox.Id}",
            $"{operation} {string.Join(' ', normalizedFlags)}; uids={string.Join(',', selected.Select(x => x.Uid))}",
            cancellationToken);
        var summary = await mailboxes.SelectAsync(accountId, mailbox.Name, cancellationToken)
            ?? throw new KeyNotFoundException("Mailbox not found.");
        return new PluginMailboxOperationResult(
            mailbox.Id,
            mailbox.Name,
            summary.UidValidity,
            selected.Select(x => x.Uid).ToArray(),
            [],
            selected.Select(x => x.MailboxMessageId).ToArray());
    }

    private async Task<Guid> ResolveActiveAccountIdAsync(string accountAddress, CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(accountAddress, out var localPart, out var domain))
            throw new ArgumentException("Account address is invalid.", nameof(accountAddress));
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Accounts.AsNoTracking()
            .Where(x => x.LocalPart == localPart && x.Domain.Name == domain && x.IsEnabled && x.Domain.IsActive)
            .Select(x => x.Id)
            .SingleOrDefaultAsync(cancellationToken)
            is { } id && id != Guid.Empty
                ? id
                : throw new KeyNotFoundException("Active local account not found.");
    }

    private async Task EnsureActiveAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Accounts.AsNoTracking()
                .AnyAsync(x => x.Id == accountId && x.IsEnabled && x.Domain.IsActive, cancellationToken))
            throw new KeyNotFoundException("Active local account not found.");
    }

    private async Task<MailboxSummary> ResolveMailboxAsync(
        Guid accountId,
        string mailboxName,
        CancellationToken cancellationToken)
    {
        await EnsureActiveAccountAsync(accountId, cancellationToken);
        return await mailboxes.SelectAsync(accountId, mailboxName, cancellationToken)
            ?? throw new KeyNotFoundException("Mailbox not found.");
    }

    private async Task<IReadOnlyList<SelectedMailboxMessage>> ResolveSelectorAsync(
        Guid mailboxId,
        PluginMailboxMessageSelector selector,
        CancellationToken cancellationToken)
    {
        var hasUids = selector.Uids is { Count: > 0 };
        var hasIds = selector.MailboxMessageIds is { Count: > 0 };
        if (hasUids == hasIds)
            throw new ArgumentException("Provide either UIDs or mailbox message ids.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.MailboxMessages.AsNoTracking().Where(x => x.MailboxId == mailboxId);
        List<SelectedMailboxMessage> rows;
        if (hasUids)
        {
            var requested = selector.Uids!.Distinct().ToArray();
            rows = await query.Where(x => requested.Contains(x.Uid))
                .Select(x => new SelectedMailboxMessage(x.Id, x.Uid))
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            if (rows.Count != requested.Length)
                throw new KeyNotFoundException("One or more UIDs were not found in the source mailbox.");
        }
        else
        {
            var requested = selector.MailboxMessageIds!.Distinct().ToArray();
            rows = await query.Where(x => requested.Contains(x.Id))
                .Select(x => new SelectedMailboxMessage(x.Id, x.Uid))
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            if (rows.Count != requested.Length)
                throw new KeyNotFoundException("One or more mailbox message ids were not found in the source mailbox.");
        }
        return rows;
    }

    private async Task AddAuditAsync(
        string action,
        string resource,
        string detail,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = "plugin",
            Action = action,
            Resource = resource,
            Detail = detail
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string BuildUidSet(IEnumerable<SelectedMailboxMessage> selected) =>
        string.Join(',', selected.Select(x => x.Uid));

    private static IReadOnlyList<string> NormalizeFlags(IReadOnlyCollection<string> flags) =>
        flags.SelectMany(x => x.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static PluginMailboxInfo ToPluginMailbox(MailboxSummary mailbox) =>
        new(mailbox.Id, mailbox.Name, mailbox.Exists, mailbox.Unseen, mailbox.UidValidity, mailbox.UidNext, mailbox.HighestModSeq);

    private static PluginMailboxMessageInfo ToPluginMessage(MailboxSummary mailbox, MailboxMessage message) =>
        new(
            message.Id,
            message.MessageId,
            mailbox.Id,
            mailbox.Name,
            message.Uid,
            NormalizeFlags(message.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            message.ModSeq,
            message.InternalDate,
            message.Message.Size,
            message.Message.ContentKey);

    private sealed record SelectedMailboxMessage(Guid MailboxMessageId, long Uid);
}

public sealed class PluginAuditWriter(IDbContextFactory<MailDbContext> dbFactory) : IPluginAuditWriter
{
    public async Task WriteAsync(
        string actor,
        string action,
        string resource,
        string? detail,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = string.IsNullOrWhiteSpace(actor) ? "plugin" : actor,
            Action = action,
            Resource = resource,
            Detail = detail,
            RemoteAddress = remoteAddress
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class PluginStateStore(IDbContextFactory<MailDbContext> dbFactory) : IPluginStateStore
{
    private const string Prefix = "plugin.state.";

    public async Task<JsonElement?> GetAsync(string pluginId, string key, CancellationToken cancellationToken)
    {
        var settingKey = BuildKey(pluginId, key);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var value = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == settingKey)
            .Select(x => x.Value)
            .SingleOrDefaultAsync(cancellationToken);
        return value is null ? null : JsonSerializer.Deserialize<JsonElement>(value);
    }

    public async Task SetAsync(string pluginId, string key, JsonElement value, CancellationToken cancellationToken)
    {
        var settingKey = BuildKey(pluginId, key);
        var json = value.GetRawText();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == settingKey, cancellationToken);
        if (setting is null) db.SystemSettings.Add(new SystemSetting { Key = settingKey, Value = json });
        else setting.Value = json;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string pluginId, string key, CancellationToken cancellationToken)
    {
        var settingKey = BuildKey(pluginId, key);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == settingKey, cancellationToken);
        if (setting is null) return;
        db.SystemSettings.Remove(setting);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string BuildKey(string pluginId, string key)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("Plugin id is required.", nameof(pluginId));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("State key is required.", nameof(key));
        if (pluginId.Length > 128 || key.Length > 160) throw new ArgumentException("Plugin state key is too long.");
        if (pluginId.Any(IsInvalidKeyCharacter) || key.Any(IsInvalidKeyCharacter))
            throw new ArgumentException("Plugin state key contains an invalid character.");
        return Prefix + pluginId + "." + key;
    }

    private static bool IsInvalidKeyCharacter(char character) =>
        !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or ':');
}
