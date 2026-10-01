using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

public sealed class PerformanceStorage(
    IDbContextFactory<MailDbContext> factory,
    PerformanceCollector collector,
    ResourceRuntime resources,
    ILogger<PerformanceStorage> logger) : BackgroundService
{
    public const string SettingsKey = "performance.settings";
    private DateTimeOffset _lastConfiguration;
    private DateTimeOffset _lastCleanup;
    private DateTimeOffset _lastHeartbeat;
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        PerformanceTelemetry.Collector = collector;
        return base.StartAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await FlushAsync(stoppingToken);
            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await FlushAsync(timeout.Token);
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (ReferenceEquals(PerformanceTelemetry.Collector, collector)) PerformanceTelemetry.Collector = null;
        await base.StopAsync(cancellationToken);
    }

    internal async Task FlushAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        token = deadline.Token;
        using var suppression = PerformanceTelemetry.Suppress();
        var operations = collector.DrainOperations();
        var (minutes, sql) = collector.DrainAggregates();
        if (operations.Count == 0 && minutes.Count == 0 && sql.Count == 0
            && DateTimeOffset.UtcNow - _lastHeartbeat < TimeSpan.FromSeconds(10)
            && DateTimeOffset.UtcNow - _lastConfiguration < TimeSpan.FromSeconds(10))
            return;
        var persisted = false;
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            db.Database.SetCommandTimeout(3);
            if (DateTimeOffset.UtcNow - _lastConfiguration > TimeSpan.FromSeconds(10))
            {
                var observedSettings = collector.Settings;
                var json = await db.SystemSettings.Where(x => x.Key == SettingsKey).Select(x => x.Value).SingleOrDefaultAsync(token);
                if (json is not null)
                {
                    var settings = JsonSerializer.Deserialize<PerformanceSettings>(json)!;
                    settings.Validate();
                    collector.ApplyStoredSettings(observedSettings, settings);
                }
                collector.BudgetBytes = resources.Snapshot().ProfileId == "512mb" ? 2 * 1024 * 1024 : 8 * 1024 * 1024;
                _lastConfiguration = DateTimeOffset.UtcNow;
            }
            db.PerformanceOperations.AddRange(operations);
            if (minutes.Count > 0)
            {
                var earliest = minutes.Min(x => x.Minute);
                var existing = await db.PerformanceMinutes.Where(x => x.ProcessId == collector.ProcessId && x.Minute >= earliest)
                    .ToListAsync(token);
                var map = existing.ToDictionary(x => (x.Minute, x.Kind, x.Module, x.Name));
                foreach (var row in minutes)
                {
                    if (!map.TryGetValue((row.Minute, row.Kind, row.Module, row.Name), out var previous)) db.PerformanceMinutes.Add(row);
                    else
                    {
                        previous.Count += row.Count; previous.Errors += row.Errors; previous.Slow += row.Slow;
                        previous.TotalMilliseconds += row.TotalMilliseconds;
                        previous.MaximumMilliseconds = Math.Max(previous.MaximumMilliseconds, row.MaximumMilliseconds);
                        var buckets = JsonSerializer.Deserialize<long[]>(previous.HistogramJson)!;
                        var addition = JsonSerializer.Deserialize<long[]>(row.HistogramJson)!;
                        for (var i = 0; i < buckets.Length; i++) buckets[i] += addition[i];
                        previous.HistogramJson = JsonSerializer.Serialize(buckets);
                    }
                }
            }
            if (sql.Count > 0)
            {
                foreach (var item in sql)
                {
                    // API and worker processes discover the same fingerprint independently.
                    // An atomic upsert avoids losing an entire diagnostic batch to a PK race.
                    if (db.Database.IsSqlite())
                        await db.Database.ExecuteSqlInterpolatedAsync($"""
                            INSERT INTO performance_sql (Fingerprint, Provider, Template, LastSeenAt)
                            VALUES ({item.Fingerprint}, {item.Provider}, {item.Template}, {item.LastSeenAt.UtcTicks})
                            ON CONFLICT(Fingerprint) DO UPDATE SET LastSeenAt = MAX(LastSeenAt, excluded.LastSeenAt)
                            """, token);
                    else
                        await db.Database.ExecuteSqlInterpolatedAsync($"""
                            INSERT INTO performance_sql (`Fingerprint`, `Provider`, `Template`, `LastSeenAt`)
                            VALUES ({item.Fingerprint}, {item.Provider}, {item.Template}, {item.LastSeenAt.UtcDateTime})
                            ON DUPLICATE KEY UPDATE `LastSeenAt` = GREATEST(`LastSeenAt`, {item.LastSeenAt.UtcDateTime})
                            """, token);
                }
            }
            var process = await db.PerformanceProcesses.FindAsync([collector.ProcessId], token);
            var status = collector.ProcessStatus();
            if (process is null) db.PerformanceProcesses.Add(status);
            else db.Entry(process).CurrentValues.SetValues(status);
            await db.SaveChangesAsync(token);
            persisted = true;
            _lastHeartbeat = DateTimeOffset.UtcNow;
            collector.StorageUnavailable = false;
            if (DateTimeOffset.UtcNow - _lastCleanup > TimeSpan.FromMinutes(1))
            {
                await CleanupAsync(db, collector.Settings, token);
                _lastCleanup = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception error)
        {
            if (!collector.StorageUnavailable && error is not OperationCanceledException)
                logger.LogWarning("Performance storage unavailable ({ErrorType}); diagnostic records may be dropped.", error.GetType().Name);
            collector.StorageUnavailable = true;
            if (!persisted) collector.LostAggregates(minutes.Sum(x => x.Count));
        }
        finally { collector.ReleaseOperations(operations.Count, !persisted); }
    }

    private static async Task CleanupAsync(MailDbContext db, PerformanceSettings settings, CancellationToken token)
    {
        var detailBefore = DateTimeOffset.UtcNow.AddDays(-settings.DetailDays);
        var aggregateBefore = DateTimeOffset.UtcNow.AddDays(-settings.AggregateDays);
        while (!token.IsCancellationRequested)
        {
            var expired = await db.PerformanceOperations.Where(x => x.StartedAt < detailBefore).OrderBy(x => x.StartedAt)
                .Select(x => x.Id).Take(1000).ToArrayAsync(token);
            if (expired.Length == 0) break;
            await db.PerformanceOperations.Where(x => expired.Contains(x.Id)).ExecuteDeleteAsync(token);
        }
        var bytes = await db.PerformanceOperations.SumAsync(x => (long?)x.DetailBytes, token) ?? 0;
        while (bytes > settings.DetailBudgetBytes && !token.IsCancellationRequested)
        {
            var oldest = await db.PerformanceOperations.OrderBy(x => x.StartedAt)
                .Select(x => new { x.Id, x.DetailBytes }).Take(1000).ToArrayAsync(token);
            if (oldest.Length == 0) break;
            var ids = new List<Guid>();
            foreach (var item in oldest)
            {
                ids.Add(item.Id); bytes -= item.DetailBytes;
                if (bytes <= settings.DetailBudgetBytes) break;
            }
            await db.PerformanceOperations.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(token);
        }
        while (!token.IsCancellationRequested)
        {
            var oldMinutes = await db.PerformanceMinutes.Where(x => x.Minute < aggregateBefore).Select(x => x.Id).Take(1000).ToArrayAsync(token);
            if (oldMinutes.Length == 0) break;
            await db.PerformanceMinutes.Where(x => oldMinutes.Contains(x.Id)).ExecuteDeleteAsync(token);
        }
        var oldSql = await db.PerformanceSql.Where(x => x.LastSeenAt < aggregateBefore).Select(x => x.Fingerprint).Take(1000).ToArrayAsync(token);
        if (oldSql.Length > 0) await db.PerformanceSql.Where(x => oldSql.Contains(x.Fingerprint)).ExecuteDeleteAsync(token);
        await db.PerformanceProcesses.Where(x => x.LastSeenAt < aggregateBefore).ExecuteDeleteAsync(token);
    }
}
