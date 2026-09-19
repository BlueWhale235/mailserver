using System.Data;
using System.Text;
using System.Text.Json;
using MailServer.Domain;
using MailServer.PluginSdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace MailServer.Infrastructure;

public sealed class QuarantineService(
    IDbContextFactory<MailDbContext> dbFactory,
    IMessageContentStore contentStore,
    IClock clock,
    ILogger<QuarantineService> logger) : IQuarantineService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<QuarantineRecord>> CreateAsync(
        QuarantineCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Sender)) throw new ArgumentException("Sender is required.", nameof(request));
        if (request.Recipients.Count == 0) throw new ArgumentException("At least one recipient is required.", nameof(request));
        if (!request.Content.CanRead) throw new ArgumentException("Content stream must be readable.", nameof(request));

        var descriptor = await contentStore.SaveAsync(request.Content, cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var domains = await ResolveRecipientDomainsAsync(db, request.Recipients, cancellationToken);
            if (domains.Count == 0)
                throw new InvalidOperationException("No active local recipient domain was found for quarantine.");

            var message = new StoredMessage
            {
                ContentKey = descriptor.Key,
                EnvelopeFrom = AddressParser.Normalize(request.Sender),
                InternetMessageId = await ReadInternetMessageIdAsync(descriptor.Key, cancellationToken),
                Size = descriptor.Size,
                ReceivedAt = clock.UtcNow
            };
            db.Messages.Add(message);

            var records = new List<QuarantineRecord>();
            foreach (var group in domains.GroupBy(x => x.DomainId))
            {
                var recipients = group.Select(x => x.Recipient).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
                var record = new QuarantineRecord
                {
                    DomainId = group.Key,
                    Message = message,
                    Sender = message.EnvelopeFrom,
                    RecipientsJson = JsonSerializer.Serialize(recipients, JsonOptions),
                    RemoteAddress = request.RemoteAddress,
                    PluginId = NullIfWhiteSpace(request.PluginId),
                    PluginName = NullIfWhiteSpace(request.PluginName),
                    Reason = NullIfWhiteSpace(request.Reason) ?? "插件策略要求隔离审核",
                    QuarantinedAt = clock.UtcNow
                };
                records.Add(record);
                db.QuarantineRecords.Add(record);
                AddAudit(db, "plugin", "quarantine.create", $"quarantine:{record.Id}", record.Reason, request.RemoteAddress);
            }

            await db.SaveChangesAsync(cancellationToken);
            return records;
        }
        catch
        {
            await contentStore.DeleteIfUnreferencedAsync(descriptor.Key, cancellationToken);
            throw;
        }
    }

    public async Task<PagedQuarantineResult> SearchAsync(
        Guid domainId,
        string? search,
        QuarantineStatus? status,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 5, 100);
        var normalizedSearch = NullIfWhiteSpace(search);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.QuarantineRecords.AsNoTracking()
            .Include(x => x.Message)
            .Where(x => x.DomainId == domainId);
        if (status is not null) query = query.Where(x => x.Status == status);
        if (normalizedSearch is not null)
        {
            query = query.Where(x =>
                x.Sender.Contains(normalizedSearch)
                || x.RecipientsJson.Contains(normalizedSearch)
                || x.Reason.Contains(normalizedSearch)
                || (x.PluginName != null && x.PluginName.Contains(normalizedSearch))
                || (x.PluginId != null && x.PluginId.Contains(normalizedSearch)));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.QuarantinedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedQuarantineResult(rows.Select(ToListItem).ToArray(), page, pageSize, total);
    }

    public async Task<QuarantineDetailView> GetRawAsync(Guid domainId, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.QuarantineRecords.AsNoTracking()
            .Include(x => x.Message)
            .SingleOrDefaultAsync(x => x.Id == id && x.DomainId == domainId, cancellationToken)
            ?? throw new KeyNotFoundException("Quarantine record not found.");

        await using var content = await contentStore.OpenReadAsync(record.Message.ContentKey, cancellationToken);
        using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return new QuarantineDetailView(ToListItem(record), $"quarantine-{record.Id:N}.eml", await reader.ReadToEndAsync(cancellationToken));
    }

    public async Task ReleaseAsync(Guid domainId, Guid id, string actor, string? note, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var record = await db.QuarantineRecords
            .Include(x => x.Message)
            .SingleOrDefaultAsync(x => x.Id == id && x.DomainId == domainId, cancellationToken)
            ?? throw new KeyNotFoundException("Quarantine record not found.");
        if (record.Status != QuarantineStatus.PendingReview)
            throw new InvalidOperationException("Only pending quarantine records can be released.");

        var recipients = ReadRecipients(record).ToArray();
        if (recipients.Length == 0) throw new InvalidOperationException("Quarantine record has no recipients.");
        foreach (var recipient in recipients)
        {
            var account = await ResolveActiveAccountAsync(db, domainId, recipient, cancellationToken);
            if (account.UsedBytes + record.Message.Size > account.QuotaBytes
                || account.Domain.UsedBytes + record.Message.Size > account.Domain.QuotaBytes)
                throw new InvalidOperationException($"Quota exceeded for {recipient}.");

            var mailbox = await db.Mailboxes.SingleOrDefaultAsync(
                x => x.AccountId == account.Id && x.Name == "INBOX",
                cancellationToken);
            if (mailbox is null)
            {
                mailbox = new Mailbox { Account = account, Name = "INBOX" };
                db.Mailboxes.Add(mailbox);
            }

            await MailboxService.EnsureUidNextAtLeastAsync(db, mailbox, cancellationToken);
            var item = new MailboxMessage
            {
                Mailbox = mailbox,
                Message = record.Message,
                Uid = mailbox.UidNext++,
                ModSeq = ++mailbox.HighestModSeq,
                InternalDate = record.QuarantinedAt
            };
            db.MailboxMessages.Add(item);
            account.UsedBytes += record.Message.Size;
            account.Domain.UsedBytes += record.Message.Size;
            db.QuotaLedger.Add(new QuotaLedgerEntry
            {
                IdempotencyKey = $"quarantine-release:add:{record.Id:N}:{item.Id:N}",
                AccountId = account.Id,
                DomainId = account.DomainId,
                MailboxMessageId = item.Id,
                Operation = QuotaLedgerOperation.Add,
                Bytes = record.Message.Size
            });
        }

        record.Status = QuarantineStatus.Released;
        record.ReviewedBy = actor;
        record.ReviewedAt = clock.UtcNow;
        record.ReviewNote = NullIfWhiteSpace(note);
        AddAudit(db, actor, "quarantine.release", $"quarantine:{record.Id}", record.ReviewNote, null);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid domainId, Guid id, string actor, string? note, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.QuarantineRecords.SingleOrDefaultAsync(
                x => x.Id == id && x.DomainId == domainId,
                cancellationToken)
            ?? throw new KeyNotFoundException("Quarantine record not found.");
        if (record.Status != QuarantineStatus.PendingReview)
            throw new InvalidOperationException("Only pending quarantine records can be deleted.");
        record.Status = QuarantineStatus.Deleted;
        record.ReviewedBy = actor;
        record.ReviewedAt = clock.UtcNow;
        record.ReviewNote = NullIfWhiteSpace(note);
        AddAudit(db, actor, "quarantine.delete", $"quarantine:{record.Id}", record.ReviewNote, null);
        await db.SaveChangesAsync(cancellationToken);
    }

    internal async Task<IReadOnlyList<PluginQuarantineRecord>> ListPendingForPluginAsync(
        string pluginId,
        string? domainName,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.QuarantineRecords.AsNoTracking()
            .Include(x => x.Domain)
            .Where(x => x.PluginId == pluginId && x.Status == QuarantineStatus.PendingReview);
        if (!string.IsNullOrWhiteSpace(domainName))
        {
            var normalized = domainName.Trim().TrimEnd('.').ToLowerInvariant();
            query = query.Where(x => x.Domain.Name == normalized);
        }

        var records = await query.OrderByDescending(x => x.QuarantinedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        return records.Select(ToPluginRecord).ToArray();
    }

    private async Task<string?> ReadInternetMessageIdAsync(string contentKey, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await contentStore.OpenReadAsync(contentKey, cancellationToken);
            return (await MimeMessage.LoadAsync(stream, cancellationToken)).MessageId;
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IOException)
        {
            logger.LogInformation(exception, "Could not parse quarantined message headers.");
            return null;
        }
    }

    private static async Task<IReadOnlyList<ResolvedRecipient>> ResolveRecipientDomainsAsync(
        MailDbContext db,
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken)
    {
        var result = new List<ResolvedRecipient>();
        foreach (var raw in recipients)
        {
            if (!AddressParser.TrySplit(raw, out _, out var domainName)) continue;
            var domain = await db.Domains.AsNoTracking()
                .Where(x => x.Name == domainName && x.IsActive)
                .Select(x => new { x.Id })
                .SingleOrDefaultAsync(cancellationToken);
            if (domain is not null)
                result.Add(new ResolvedRecipient(AddressParser.Normalize(raw), domain.Id));
        }
        return result;
    }

    private static async Task<MailAccount> ResolveActiveAccountAsync(
        MailDbContext db,
        Guid domainId,
        string recipient,
        CancellationToken cancellationToken)
    {
        if (!AddressParser.TrySplit(recipient, out var localPart, out _))
            throw new InvalidOperationException($"Invalid recipient address: {recipient}");
        return await db.Accounts.Include(x => x.Domain)
            .SingleOrDefaultAsync(
                x => x.DomainId == domainId
                     && x.LocalPart == localPart
                     && x.IsEnabled
                     && x.Domain.IsActive,
                cancellationToken)
            ?? throw new InvalidOperationException($"Active local account not found for {recipient}.");
    }

    private static QuarantineListItem ToListItem(QuarantineRecord record) =>
        new(
            record.Id,
            record.DomainId,
            record.MessageId,
            record.Sender,
            ReadRecipients(record).ToArray(),
            record.RemoteAddress,
            record.PluginId,
            record.PluginName,
            record.Reason,
            record.Status,
            record.QuarantinedAt,
            record.ReviewedBy,
            record.ReviewedAt,
            record.ReviewNote,
            record.Message.Size,
            record.Message.InternetMessageId);

    private static PluginQuarantineRecord ToPluginRecord(QuarantineRecord record) =>
        new(
            record.Id,
            record.DomainId,
            record.MessageId,
            record.Sender,
            ReadRecipients(record).ToArray(),
            record.RemoteAddress,
            record.Reason,
            record.Status.ToString(),
            record.QuarantinedAt);

    private static IEnumerable<string> ReadRecipients(QuarantineRecord record) =>
        JsonSerializer.Deserialize<string[]>(record.RecipientsJson, JsonOptions) ?? [];

    private static void AddAudit(MailDbContext db, string actor, string action, string resource, string? detail, string? remoteAddress) =>
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = string.IsNullOrWhiteSpace(actor) ? "system" : actor,
            Action = action,
            Resource = resource,
            Detail = detail,
            RemoteAddress = remoteAddress
        });

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record ResolvedRecipient(string Recipient, Guid DomainId);
}

public sealed class PluginQuarantineStore(QuarantineService quarantine) : IPluginQuarantineStore
{
    public async Task<IReadOnlyList<PluginQuarantineRecord>> CreateAsync(
        string pluginId,
        PluginQuarantineCreateRequest request,
        CancellationToken cancellationToken)
    {
        var records = await quarantine.CreateAsync(
            new QuarantineCreateRequest(
                request.Sender,
                request.Recipients,
                request.RemoteAddress,
                request.Content,
                pluginId,
                pluginId,
                request.Reason),
            cancellationToken);
        return records.Select(record => new PluginQuarantineRecord(
            record.Id,
            record.DomainId,
            record.MessageId,
            record.Sender,
            JsonSerializer.Deserialize<string[]>(record.RecipientsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [],
            record.RemoteAddress,
            record.Reason,
            record.Status.ToString(),
            record.QuarantinedAt)).ToArray();
    }

    public Task<IReadOnlyList<PluginQuarantineRecord>> ListPendingAsync(
        string pluginId,
        string? domainName,
        CancellationToken cancellationToken) =>
        quarantine.ListPendingForPluginAsync(pluginId, domainName, cancellationToken);

    public Task ReleaseAsync(string pluginId, Guid quarantineId, string? note, CancellationToken cancellationToken) =>
        ReleaseOrDeleteAsync(pluginId, quarantineId, note, release: true, cancellationToken);

    public Task DeleteAsync(string pluginId, Guid quarantineId, string? note, CancellationToken cancellationToken) =>
        ReleaseOrDeleteAsync(pluginId, quarantineId, note, release: false, cancellationToken);

    private async Task ReleaseOrDeleteAsync(
        string pluginId,
        Guid quarantineId,
        string? note,
        bool release,
        CancellationToken cancellationToken)
    {
        var pending = await quarantine.ListPendingForPluginAsync(pluginId, null, cancellationToken);
        var record = pending.SingleOrDefault(x => x.Id == quarantineId)
            ?? throw new KeyNotFoundException("Quarantine record not found for this plugin.");
        if (release) await quarantine.ReleaseAsync(record.DomainId, quarantineId, "plugin:" + pluginId, note, cancellationToken);
        else await quarantine.DeleteAsync(record.DomainId, quarantineId, "plugin:" + pluginId, note, cancellationToken);
    }
}
