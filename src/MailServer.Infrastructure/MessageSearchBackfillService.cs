using System.Text.Json;
using System.Text;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailServer.Infrastructure;

public sealed class MessageSearchBackfillService(
    IDbContextFactory<MailDbContext> dbFactory,
    IMessageContentStore contentStore,
    IOptions<MessageSearchOptions> options,
    ILogger<MessageSearchBackfillService> logger,
    ResourceRuntime? resources = null,
    SearchBackfillWakeSignal? wake = null) : BackgroundService
{
    public const int ParserVersion = 2;
    private const string CursorKey = "internal.search.backfill.cursor.v2";
    private sealed record ScanCursor(DateTimeOffset ReceivedAt, Guid Id);
    public const int MaximumTextBytes = 512 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!options.Value.Enabled || resources?.AdaptiveOptions is { Enabled: true, PauseBackfill: true })
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }
            if (resources is not null && resources.LargeMime.Available == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }
            try
            {
                var observedWake = wake?.Version ?? 0;
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var count = Math.Clamp(options.Value.BackfillBatchSize, 1, 16);
                var now = DateTimeOffset.UtcNow;
                var checkpoint = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == CursorKey, stoppingToken);
                ScanCursor? cursor = null;
                if (!string.IsNullOrEmpty(checkpoint?.Value))
                    try { cursor = JsonSerializer.Deserialize<ScanCursor>(checkpoint.Value); }
                    catch (JsonException) { /* A damaged checkpoint causes a safe rescan. */ }
                var retries = await db.MessageSearchRecords.AsNoTracking()
                    .Where(x => x.ParserVersion == ParserVersion && x.Status == "Failed" && x.RetryAfter <= now)
                    .Join(db.Messages.AsNoTracking(), row => row.MessageId, message => message.Id,
                        (row, message) => new { message.Id, message.ContentKey, message.ReceivedAt, FromScan = false })
                    .OrderBy(x => x.ReceivedAt).ThenBy(x => x.Id).Take(count).ToListAsync(stoppingToken);
                var scan = db.Messages.AsNoTracking().Where(message => !db.MessageSearchRecords.Any(row =>
                    row.MessageId == message.Id && row.ParserVersion == ParserVersion));
                if (cursor is not null)
                    scan = scan.Where(message => message.ReceivedAt > cursor.ReceivedAt
                        || (message.ReceivedAt == cursor.ReceivedAt && message.Id.CompareTo(cursor.Id) > 0));
                var newMessages = await scan.OrderBy(message => message.ReceivedAt).ThenBy(message => message.Id)
                    .Take(count - retries.Count)
                    .Select(message => new { message.Id, message.ContentKey, message.ReceivedAt, FromScan = true })
                    .ToListAsync(stoppingToken);
                var messages = retries.Concat(newMessages).ToList();
                foreach (var message in messages)
                {
                    using var permit = resources?.LargeMime.TryAcquire();
                    if (resources is not null && permit is null) break;
                    using var operation = PerformanceTelemetry.Operation("worker", "search.backfill");
                    PerformanceTelemetry.Resource(messageId: message.Id);
                    MessageSearchRecord record;
                    try
                    {
                        await using var stream = await contentStore.OpenReadAsync(message.ContentKey, stoppingToken);
                        using var parsed = await PerformanceTelemetry.MeasureAsync("search.mime-parse",
                            () => MimeMessage.LoadAsync(stream, true, stoppingToken));
                        // Reading and normalizing a many-megabyte text part just to
                        // discard it at 512 KiB can exhaust a small host. Retain
                        // headers and mark the body incomplete; source fallback
                        // preserves search results for these oversized messages.
                        var oversizedText = parsed.BodyParts.OfType<TextPart>().Any(part =>
                            part.Content?.Stream is { CanSeek: true } partStream
                            && partStream.Length > MaximumTextBytes);
                        using (PerformanceTelemetry.Stage("search.normalize"))
                            record = BuildRecord(message.Id, parsed, omitBody: oversizedText);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        PerformanceTelemetry.Fail(error);
                        logger.LogWarning(error, "Message {MessageId} could not be indexed; search will use the source.", message.Id);
                        record = new MessageSearchRecord { MessageId = message.Id, ParserVersion = ParserVersion,
                            Status = "Failed", Complete = false, RetryAfter = now.AddMinutes(10) };
                    }
                    var existing = await db.MessageSearchRecords.SingleOrDefaultAsync(x => x.MessageId == record.MessageId, stoppingToken);
                    if (existing is null) db.MessageSearchRecords.Add(record);
                    else db.Entry(existing).CurrentValues.SetValues(record);
                    try { await db.SaveChangesAsync(stoppingToken); }
                    catch (DbUpdateException)
                    {
                        // Another process may have indexed the same message first.
                        db.ChangeTracker.Clear();
                        if (!await db.MessageSearchRecords.AnyAsync(x => x.MessageId == record.MessageId, stoppingToken))
                            throw;
                    }
                    if (message.FromScan)
                    {
                        // The record (including its retry state) is durable before advancing the cursor.
                        checkpoint = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == CursorKey, stoppingToken);
                        if (checkpoint is null) { checkpoint = new SystemSetting { Key = CursorKey, Value = "" }; db.SystemSettings.Add(checkpoint); }
                        checkpoint.Value = JsonSerializer.Serialize(new ScanCursor(message.ReceivedAt, message.Id));
                        try { await db.SaveChangesAsync(stoppingToken); }
                        catch (DbUpdateException) { db.ChangeTracker.Clear(); /* Another process owns the checkpoint; rescan is safe. */ }
                    }
                    db.ChangeTracker.Clear();
                }
                if (messages.Count == 0 && cursor is not null)
                {
                    checkpoint = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == CursorKey, stoppingToken);
                    if (checkpoint is not null) { checkpoint.Value = ""; await db.SaveChangesAsync(stoppingToken); }
                }
                var delay = messages.Count == 0 ? TimeSpan.FromSeconds(30)
                    : TimeSpan.FromSeconds(Math.Clamp(options.Value.BackfillDelaySeconds, 1, 60));
                // Only empty polling can be expedited; the nonempty batch pacing remains mandatory.
                if (messages.Count == 0 && wake is not null) await wake.WaitForChangeAsync(observedWake, delay, stoppingToken);
                else await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Message search backfill paused after an error.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    internal static MessageSearchRecord BuildRecord(Guid messageId, MimeMessage message, bool omitBody = false)
    {
        var headers = message.Headers.GroupBy(x => x.Field, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key,
                group => group.Select(x => MailboxService.NormalizeSearchText(
                    MailboxService.UnfoldHeaderValue(x.Value))).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var subject = MailboxService.NormalizeSearchText(MailboxService.UnfoldHeaderValue(message.Subject ?? string.Empty));
        var headerJson = JsonSerializer.Serialize(headers);
        var body = omitBody ? string.Empty : MailboxService.NormalizeSearchText(MailboxService.ExtractSearchableBody(message));
        var headerBytes = Encoding.UTF8.GetByteCount(headerJson) + Encoding.UTF8.GetByteCount(subject);
        var complete = !omitBody && headerBytes + Encoding.UTF8.GetByteCount(body) <= MaximumTextBytes;
        if (headerBytes > MaximumTextBytes)
        {
            headerJson = "{}";
            subject = string.Empty;
            body = string.Empty;
        }
        else if (!complete)
            body = Utf8Prefix(body, MaximumTextBytes - headerBytes);
        return new MessageSearchRecord
        {
            MessageId = messageId,
            ParserVersion = ParserVersion,
            Status = complete ? "Ready" : headerBytes <= MaximumTextBytes ? "HeadersReady" : "Incomplete",
            Subject = subject,
            HeadersJson = headerJson,
            Body = body,
            Complete = complete
        };
    }

    private static string Utf8Prefix(string value, int budget)
    {
        var low = 0;
        var high = Math.Min(value.Length, budget);
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, middle)) <= budget) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && char.IsHighSurrogate(value[low - 1])) low--;
        return value[..low];
    }
}
