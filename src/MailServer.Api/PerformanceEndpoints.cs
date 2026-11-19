using System.Text;
using System.Text.Json;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Api;

public static class PerformanceEndpoints
{
    public static void MapPerformanceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/operations/performance");
        group.MapGet("/operations", async (IDbContextFactory<MailDbContext> factory,
            DateTimeOffset? from, DateTimeOffset? to, string? module, string? operation, string? status,
            string? traceId, long? messageId, long? deliveryJobId, double? minimumMilliseconds,
            bool? slowOnly, string? cursor, CancellationToken token) =>
        {
            using var suppression = PerformanceTelemetry.Suppress();
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddDays(-1);
            if (end <= start || end - start > TimeSpan.FromDays(90)
                || minimumMilliseconds is < 0 || minimumMilliseconds is { } min && !double.IsFinite(min))
                return Results.BadRequest(new { error = "请选择不超过 90 天的有效时间范围和非负耗时。" });
            await using var db = await factory.CreateDbContextAsync(token);
            var query = db.PerformanceOperations.AsNoTracking().Where(x => x.StartedAt >= start && x.StartedAt < end);
            if (!string.IsNullOrWhiteSpace(module)) query = query.Where(x => x.Module == module);
            if (!string.IsNullOrWhiteSpace(operation)) query = query.Where(x => x.Name == operation);
            if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
            if (!string.IsNullOrWhiteSpace(traceId)) query = query.Where(x => x.TraceId == traceId || x.SourceTraceId == traceId);
            if (messageId is not null) query = query.Where(x => x.MessageId == messageId);
            if (deliveryJobId is not null) query = query.Where(x => x.DeliveryJobId == deliveryJobId);
            if (minimumMilliseconds is not null) query = query.Where(x => x.ProcessingMilliseconds >= minimumMilliseconds);
            if (slowOnly == true) query = query.Where(x => x.Slow);
            if (cursor is not null)
            {
                try
                {
                    if (cursor.Length > 512) throw new FormatException();
                    var position = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor)) ?? throw new FormatException();
                    query = query.Where(x => x.StartedAt < position.At || x.StartedAt == position.At && x.Id.CompareTo(position.Id) < 0);
                }
                catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
                { return Results.BadRequest(new { error = "分页游标无效。" }); }
            }
            var rows = await query.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id)
                .Select(x => new { x.Id, x.TraceId, x.Module, x.Name, x.Status, x.StartedAt, x.DurationMilliseconds,
                    x.ProcessingMilliseconds, x.SqlMilliseconds, x.SqlCount, x.Slow, x.Truncated, x.MessageId, x.DeliveryJobId })
                .Take(51).ToListAsync(token);
            var items = rows.Take(50).ToArray();
            var nextCursor = rows.Count <= 50 ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(items[^1].StartedAt, items[^1].Id)));
            return Results.Ok(new { items, nextCursor });
        }).RequirePermission(PermissionCodes.PerformanceRead);

        group.MapGet("/operations/{id:long}", async (long id, IDbContextFactory<MailDbContext> factory, CancellationToken token) =>
        {
            using var suppression = PerformanceTelemetry.Suppress();
            await using var db = await factory.CreateDbContextAsync(token);
            var row = await db.PerformanceOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
            if (row is null) return Results.NotFound();
            var stages = JsonSerializer.Deserialize<PerformanceStage[]>(row.DetailsJson) ?? [];
            row.DetailsJson = "";
            var related = await db.PerformanceOperations.AsNoTracking().Where(x => x.Id != id &&
                    (x.TraceId == row.SourceTraceId || x.SourceTraceId == row.TraceId
                        || row.MessageId != null && x.MessageId == row.MessageId))
                .OrderBy(x => x.StartedAt).Select(x => new { x.Id, x.TraceId, x.Module, x.Name, x.StartedAt, x.Status, x.Attempt })
                .Take(101).ToListAsync(token);
            var sourceAvailable = row.SourceTraceId != null && related.Any(x => x.TraceId == row.SourceTraceId);
            var fingerprints = stages.Where(x => x.SqlFingerprint != null).Select(x => x.SqlFingerprint!).Distinct().ToArray();
            var sql = await db.PerformanceSql.AsNoTracking().WhereStringIn(x => x.Fingerprint, fingerprints).ToListAsync(token);
            double covered = 0, lastEnd = 0;
            foreach (var stage in stages.OrderBy(x => x.OffsetMilliseconds))
            {
                var stageEnd = Math.Min(row.DurationMilliseconds, stage.OffsetMilliseconds + stage.DurationMilliseconds);
                covered += Math.Max(0, stageEnd - Math.Max(lastEnd, stage.OffsetMilliseconds));
                lastEnd = Math.Max(lastEnd, stageEnd);
            }
            return Results.Ok(new { operation = row, stages, related = related.Take(100), relatedTruncated = related.Count > 100,
                sourceAvailable, sql, unattributedMilliseconds = Math.Max(0, row.DurationMilliseconds - covered) });
        }).RequirePermission(PermissionCodes.PerformanceRead);

        group.MapGet("/summary", async (IDbContextFactory<MailDbContext> factory, string? kind,
            DateTimeOffset? from, DateTimeOffset? to, CancellationToken token) =>
        {
            using var suppression = PerformanceTelemetry.Suppress();
            var end = to ?? DateTimeOffset.UtcNow;
            var start = from ?? end.AddDays(-1);
            if (end <= start || end - start > TimeSpan.FromDays(30) || kind is not (null or "operation" or "stage" or "sql"))
                return Results.BadRequest(new { error = "统计范围不超过 30 天，类型为 operation、stage 或 sql。" });
            kind ??= "operation";
            await using var db = await factory.CreateDbContextAsync(token);
            var rows = await db.PerformanceMinutes.AsNoTracking().Where(x => x.Minute >= start && x.Minute < end && x.Kind == kind)
                .OrderByDescending(x => x.Minute).Take(20_001).ToListAsync(token);
            var rankings = rows.Take(20_000).GroupBy(x => (x.Module, x.Name)).Select(grouping =>
            {
                var histogram = new long[25];
                foreach (var item in grouping)
                {
                    var addition = JsonSerializer.Deserialize<long[]>(item.HistogramJson)!;
                    for (var i = 0; i < Math.Min(histogram.Length, addition.Length); i++) histogram[i] += addition[i];
                }
                return new { grouping.Key.Module, grouping.Key.Name, Count = grouping.Sum(x => x.Count),
                    Errors = grouping.Sum(x => x.Errors), Slow = grouping.Sum(x => x.Slow),
                    TotalMilliseconds = grouping.Sum(x => x.TotalMilliseconds), MaximumMilliseconds = grouping.Max(x => x.MaximumMilliseconds),
                    P50 = PerformanceCollector.Percentile(histogram, .50), P95 = PerformanceCollector.Percentile(histogram, .95),
                    P99 = PerformanceCollector.Percentile(histogram, .99) };
            }).OrderByDescending(x => x.TotalMilliseconds).Take(100).ToArray();
            var fingerprints = rankings.Select(x => x.Name).ToArray();
            var sql = kind == "sql" ? await db.PerformanceSql.AsNoTracking().WhereStringIn(x => x.Fingerprint, fingerprints)
                .ToListAsync(token) : [];
            var processes = await db.PerformanceProcesses.AsNoTracking().Where(x => x.LastSeenAt >= end.AddHours(-1)).ToListAsync(token);
            return Results.Ok(new { rankings, sql, processes, truncated = rows.Count > 20_000, approximatePercentiles = true });
        }).RequirePermission(PermissionCodes.PerformanceRead);

        group.MapGet("/settings", async (IDbContextFactory<MailDbContext> factory, CancellationToken token) =>
        {
            using var suppression = PerformanceTelemetry.Suppress();
            await using var db = await factory.CreateDbContextAsync(token);
            return Results.Ok(await ReadSettingsAsync(db, token));
        }).RequirePermission(PermissionCodes.PerformanceRead);
        group.MapPut("/settings", async (PerformanceSettings settings, HttpContext http,
            IDbContextFactory<MailDbContext> factory, PerformanceCollector collector, CancellationToken token) =>
        {
            try { settings.Validate(); }
            catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
            using var suppression = PerformanceTelemetry.Suppress();
            await using var db = await factory.CreateDbContextAsync(token);
            settings = settings with { DiagnosticUntil = (await ReadSettingsAsync(db, token)).DiagnosticUntil };
            await SaveSettingsAsync(db, settings, http, "performance.configure", token);
            collector.Settings = settings;
            return Results.Ok(settings);
        }).RequirePermission(PermissionCodes.PerformanceManage);
        group.MapPost("/diagnostic", async (DiagnosticRequest request, HttpContext http,
            IDbContextFactory<MailDbContext> factory, PerformanceCollector collector, CancellationToken token) =>
        {
            using var suppression = PerformanceTelemetry.Suppress();
            await using var db = await factory.CreateDbContextAsync(token);
            var settings = await ReadSettingsAsync(db, token);
            settings = settings with { DiagnosticUntil = request.Enabled ? DateTimeOffset.UtcNow.AddMinutes(15) : null };
            await SaveSettingsAsync(db, settings, http, request.Enabled ? "performance.diagnostic-start" : "performance.diagnostic-stop", token);
            collector.Settings = settings;
            return Results.Ok(settings);
        }).RequirePermission(PermissionCodes.PerformanceManage);
    }

    private static async Task<PerformanceSettings> ReadSettingsAsync(MailDbContext db, CancellationToken token)
    {
        var json = await db.SystemSettings.Where(x => x.Key == PerformanceStorage.SettingsKey).Select(x => x.Value).SingleOrDefaultAsync(token);
        return json is null ? new() : JsonSerializer.Deserialize<PerformanceSettings>(json)!;
    }
    private static async Task SaveSettingsAsync(MailDbContext db, PerformanceSettings settings, HttpContext http, string action, CancellationToken token)
    {
        var row = await db.SystemSettings.FindAsync([PerformanceStorage.SettingsKey], token);
        if (row is null) db.SystemSettings.Add(new SystemSetting { Key = PerformanceStorage.SettingsKey, Value = JsonSerializer.Serialize(settings) });
        else row.Value = JsonSerializer.Serialize(settings);
        db.AuditRecords.Add(new AuditRecord { Actor = http.User.Identity?.Name ?? "admin", Action = action,
            Resource = "performance", RequestId = http.TraceIdentifier, Detail = JsonSerializer.Serialize(settings) });
        await db.SaveChangesAsync(token);
    }
    private sealed record Cursor(DateTimeOffset At, long Id);
    public sealed record DiagnosticRequest(bool Enabled);
}
