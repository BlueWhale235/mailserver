using System.Text;
using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MailServer.Infrastructure;

public sealed record OperationsMailQuery(string? MessageId, string? Sender, string? Recipient,
    string? AuthenticatedAccount, DateTimeOffset? From, DateTimeOffset? To,
    DeliveryStatus? DeliveryStatus, bool? Recycled, int Page = 1, int PageSize = 50);

public sealed record OperationsMailRow(Guid Id, string? InternetMessageId, string EnvelopeFrom,
    DateTimeOffset ReceivedAt, long Size, string? SourceType, string? AuthenticatedAccountName,
    string? SourceIp, int MailboxCopies, int RecycledCopies, int DeliveryJobs, int QuarantinedCopies);

public sealed record OperationsMailPage(IReadOnlyList<OperationsMailRow> Items, int TotalCount, int Page, int PageSize);
public sealed record OperationsMailboxCopy(Guid Id, Guid MailboxId, string MailboxName, string AccountAddress,
    long Uid, DateTimeOffset? DeletedAt);
public sealed record OperationsMailDetail(OperationsMailRow Message, IReadOnlyList<string> OriginalRecipients,
    IReadOnlyList<OperationsMailboxCopy> MailboxCopies, IReadOnlyList<string> DeliveryRecipients,
    IReadOnlyList<string> QuarantineRecipients, int OriginalRecipientCount, int MailboxCopyCount,
    int DeliveryRecipientCount, int QuarantineRecipientCount);
public sealed record OperationsRelatedPage<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);
public sealed record OperationsDeliveryRow(Guid Id, string Recipient, DeliveryStatus Status, int Attempts,
    string? LastError, DateTimeOffset CreatedAt);
public sealed record OperationsDeliveryPage(IReadOnlyList<OperationsDeliveryRow> Items, int TotalCount);
public sealed record OperationsAttemptRow(int AttemptNumber, bool Succeeded, bool PermanentFailure,
    string? RemoteHost, string? SmtpStatusCode, string? Response, DateTimeOffset CreatedAt);
public sealed record OperationsAttemptPage(IReadOnlyList<OperationsAttemptRow> Items, int TotalCount);
public sealed record OperationsMailContent(string FromHeader, string Subject, string Headers,
    string Text, bool Truncated, IReadOnlyList<string> Attachments);

public sealed class OperationsMailService(IDbContextFactory<MailDbContext> factory, IMessageContentStore contentStore)
{
    private static readonly SemaphoreSlim MimeGate = new(1, 1);
    private const int MaximumPreviewBytes = 256 * 1024;

    public async Task<OperationsMailPage> SearchAsync(OperationsMailQuery query, CancellationToken token)
    {
        var page = Math.Clamp(query.Page, 1, 100_000);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        await using var db = await factory.CreateDbContextAsync(token);
        IQueryable<StoredMessage> source = db.Messages.AsNoTracking();
        var id = MessageIdLookup.Normalize(query.MessageId);
        if (id is not null)
        {
            var hash = MessageIdLookup.Hash(id);
            source = source.Where(x => (x.InternetMessageIdHash == hash || x.InternetMessageIdHash == null)
                && (x.InternetMessageId == id || x.InternetMessageId == "<" + id + ">"));
        }
        else
        {
            var since = query.From ?? DateTimeOffset.UtcNow.AddDays(-7);
            source = source.Where(x => x.ReceivedAt >= since);
        }
        if (query.From is { } from) source = source.Where(x => x.ReceivedAt >= from);
        if (query.To is { } to) source = source.Where(x => x.ReceivedAt <= to);
        if (!string.IsNullOrWhiteSpace(query.Sender)) source = source.Where(x => x.EnvelopeFrom == query.Sender.Trim());
        if (!string.IsNullOrWhiteSpace(query.AuthenticatedAccount))
            source = source.Where(x => x.AuthenticatedAccountName == query.AuthenticatedAccount.Trim());
        if (!string.IsNullOrWhiteSpace(query.Recipient))
        {
            var recipient = query.Recipient.Trim();
            source = source.Where(x => db.MessageRecipients.Any(r => r.MessageId == x.Id && r.Address == recipient)
                || db.DeliveryJobs.Any(j => j.MessageId == x.Id && j.Recipient == recipient)
                || db.MailboxMessages.IgnoreQueryFilters().Any(m => m.MessageId == x.Id
                    && m.Mailbox.Account.LocalPart + "@" + m.Mailbox.Account.Domain.Name == recipient)
                || db.QuarantineRecords.IgnoreQueryFilters().Any(q => q.MessageId == x.Id
                    && q.RecipientsJson.Contains("\"" + recipient + "\"")));
        }
        if (query.DeliveryStatus is { } status)
            source = source.Where(x => db.DeliveryJobs.Any(j => j.MessageId == x.Id && j.Status == status));
        if (query.Recycled is true)
            source = source.Where(x => db.MailboxMessages.IgnoreQueryFilters().Any(m => m.MessageId == x.Id && m.DeletedAt != null));
        else if (query.Recycled is false)
            source = source.Where(x => db.MailboxMessages.IgnoreQueryFilters().Any(m => m.MessageId == x.Id && m.DeletedAt == null));
        var total = await source.CountAsync(token);
        var rows = await source.OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new OperationsMailRow(x.Id, x.InternetMessageId, x.EnvelopeFrom,
                x.ReceivedAt, x.Size, x.SourceType, x.AuthenticatedAccountName, x.SourceIp,
                db.MailboxMessages.IgnoreQueryFilters().Count(m => m.MessageId == x.Id && m.DeletedAt == null),
                db.MailboxMessages.IgnoreQueryFilters().Count(m => m.MessageId == x.Id && m.DeletedAt != null),
                db.DeliveryJobs.Count(j => j.MessageId == x.Id),
                db.QuarantineRecords.IgnoreQueryFilters().Count(q => q.MessageId == x.Id)))
            .ToArrayAsync(token);
        return new(rows, total, page, pageSize);
    }

    public async Task<OperationsMailDetail?> GetAsync(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var message = await db.Messages.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new OperationsMailRow(x.Id, x.InternetMessageId, x.EnvelopeFrom,
                x.ReceivedAt, x.Size, x.SourceType, x.AuthenticatedAccountName, x.SourceIp,
                db.MailboxMessages.IgnoreQueryFilters().Count(m => m.MessageId == x.Id && m.DeletedAt == null),
                db.MailboxMessages.IgnoreQueryFilters().Count(m => m.MessageId == x.Id && m.DeletedAt != null),
                db.DeliveryJobs.Count(j => j.MessageId == x.Id),
                db.QuarantineRecords.IgnoreQueryFilters().Count(q => q.MessageId == x.Id)))
            .SingleOrDefaultAsync(token);
        if (message is null) return null;
        var originals = await GetRecipientsAsync(id, "original", 1, 50, token);
        var copies = await GetCopiesAsync(id, 1, 50, token);
        var delivery = await GetRecipientsAsync(id, "delivery", 1, 50, token);
        var quarantine = await GetRecipientsAsync(id, "quarantine", 1, 50, token);
        return new(message, originals!.Items, copies!.Items, delivery!.Items, quarantine!.Items,
            originals.TotalCount, copies.TotalCount, delivery.TotalCount, quarantine.TotalCount);
    }

    public async Task<OperationsRelatedPage<string>?> GetRecipientsAsync(Guid id, string kind, int page, int pageSize, CancellationToken token)
    {
        if (kind is not ("original" or "delivery" or "quarantine")) return null;
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Messages.AnyAsync(x => x.Id == id, token)) return null;
        if (kind == "quarantine")
        {
            var records = await db.QuarantineRecords.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.MessageId == id).Select(x => x.RecipientsJson).ToArrayAsync(token);
            var recipients = records.SelectMany(value =>
            {
                try { return JsonSerializer.Deserialize<string[]>(value) ?? []; }
                catch (JsonException) { return Array.Empty<string>(); }
            }).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            return new(recipients.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), recipients.Length, page, pageSize);
        }
        IQueryable<string> source = kind == "original"
            ? db.MessageRecipients.AsNoTracking().Where(x => x.MessageId == id).Select(x => x.Address)
            : db.DeliveryJobs.AsNoTracking().Where(x => x.MessageId == id).Select(x => x.Recipient).Distinct();
        var count = await source.CountAsync(token);
        var rows = await source.OrderBy(x => x).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);
        return new(rows, count, page, pageSize);
    }

    public async Task<OperationsRelatedPage<OperationsMailboxCopy>?> GetCopiesAsync(Guid id, int page, int pageSize, CancellationToken token)
    {
        page = Math.Clamp(page, 1, 100_000);
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Messages.AnyAsync(x => x.Id == id, token)) return null;
        var source = db.MailboxMessages.IgnoreQueryFilters().AsNoTracking().Where(x => x.MessageId == id);
        var count = await source.CountAsync(token);
        var rows = await source.OrderBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new OperationsMailboxCopy(x.Id, x.MailboxId, x.Mailbox.Name,
                x.Mailbox.Account.LocalPart + "@" + x.Mailbox.Account.Domain.Name, x.Uid, x.DeletedAt))
            .ToArrayAsync(token);
        return new(rows, count, page, pageSize);
    }

    public async Task<OperationsDeliveryPage?> GetDeliveriesAsync(Guid id, int page, int pageSize, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Messages.AnyAsync(x => x.Id == id, token)) return null;
        var source = db.DeliveryJobs.AsNoTracking().Where(x => x.MessageId == id);
        var count = await source.CountAsync(token);
        var rows = await source.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((Math.Clamp(page, 1, 100_000) - 1) * Math.Clamp(pageSize, 1, 100))
            .Take(Math.Clamp(pageSize, 1, 100))
            .Select(x => new OperationsDeliveryRow(x.Id, x.Recipient, x.Status, x.Attempts, x.LastError, x.CreatedAt))
            .ToArrayAsync(token);
        return new(rows, count);
    }

    public async Task<OperationsAttemptPage?> GetAttemptsAsync(Guid id, Guid jobId, int page, int pageSize, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.DeliveryJobs.AnyAsync(x => x.Id == jobId && x.MessageId == id, token)) return null;
        var source = db.DeliveryAttempts.AsNoTracking().Where(x => x.DeliveryJobId == jobId);
        var count = await source.CountAsync(token);
        var rows = await source
            .OrderBy(x => x.AttemptNumber)
            .Skip((Math.Clamp(page, 1, 100_000) - 1) * Math.Clamp(pageSize, 1, 100))
            .Take(Math.Clamp(pageSize, 1, 100))
            .Select(x => new OperationsAttemptRow(x.AttemptNumber, x.Succeeded, x.PermanentFailure,
                x.RemoteHost, x.SmtpStatusCode, x.Response, x.CreatedAt))
            .ToArrayAsync(token);
        return new(rows, count);
    }

    public async Task<OperationsMailContent?> GetContentAsync(Guid id, string actor, string action, CancellationToken token)
    {
        await using var stream = await OpenAuditedAsync(id, actor, action, token);
        if (stream is null) return null;
        await MimeGate.WaitAsync(token);
        try
        {
            var message = await MimeMessage.LoadAsync(stream, token);
            var html = message.HtmlBody;
            var text = message.TextBody ?? (html is null ? string.Empty : WebUtility.HtmlDecode(
                Regex.Replace(Regex.Replace(html, "<(script|style)\\b[^>]*>.*?</\\1>", " ",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase), "<[^>]*>", " ", RegexOptions.Singleline)));
            var truncated = Encoding.UTF8.GetByteCount(text) > MaximumPreviewBytes;
            if (truncated) text = TruncateUtf8(text, MaximumPreviewBytes);
            var headers = string.Join("\r\n", message.Headers.Select(x => $"{x.Field}: {x.Value}"));
            if (Encoding.UTF8.GetByteCount(headers) > MaximumPreviewBytes)
                headers = TruncateUtf8(headers, MaximumPreviewBytes) + "\r\n[邮件头已截断]";
            return new(message.From.ToString(), message.Subject ?? string.Empty,
                headers, text, truncated, message.Attachments.Take(100)
                    .Select(x => x.ContentDisposition?.FileName ?? x.ContentType.Name ?? "附件").ToArray());
        }
        finally { MimeGate.Release(); }
    }

    public async Task<Stream?> OpenAuditedAsync(Guid id, string actor, string action, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var key = await db.Messages.AsNoTracking().Where(x => x.Id == id)
            .Select(x => x.ContentKey).SingleOrDefaultAsync(token);
        if (key is null) return null;
        Stream stream;
        try { stream = await contentStore.OpenReadAsync(key, token); }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return null; }
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = action,
            Resource = $"message:{id}", RelatedId = id.ToString() });
        try { await db.SaveChangesAsync(token); }
        catch { await stream.DisposeAsync(); throw; }
        return stream;
    }

    private static string TruncateUtf8(string text, int maximumBytes)
    {
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var bytes = rune.Utf8SequenceLength;
            if (length + bytes > maximumBytes) break;
            length += bytes;
        }
        var count = 0;
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > length) break;
            count += rune.Utf16SequenceLength;
            used += rune.Utf8SequenceLength;
        }
        return text[..count];
    }
}
