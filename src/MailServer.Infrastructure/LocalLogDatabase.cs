using System.Globalization;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

public sealed record LocalLogSettingsView(string Machine, string Path, int RetentionDays, long FailedWrites, string? LastError);

/// <summary>Each machine owns its SQLite log file; no business database connection is used here.</summary>
public sealed class LocalLogDatabase
{
    internal LocalLogWriter? Writer { get; set; }
    private readonly object _initializationGate = new();
    private readonly DbContextOptions<LocalLogDbContext> _options;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<LocalLogDatabase>? _logger;
    private bool _initialized;
    private long _failedWrites;
    private string? _lastError;
    public string Path { get; }

    public LocalLogDatabase(IConfiguration configuration, ILogger<LocalLogDatabase> logger)
    {
        _configuration = configuration;
        _logger = logger;
        SqliteNative.Initialize();
        var configured = configuration["LocalLogs:Path"];
        var business = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder();
        if (string.IsNullOrWhiteSpace(configured) && !string.Equals(configuration["Database:Provider"], "MySql", StringComparison.OrdinalIgnoreCase))
        {
            business.ConnectionString = configuration.GetConnectionString("MailDatabase") ?? "Data Source=data/mailserver.db";
            if (business.DataSource != ":memory:") configured = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(business.DataSource))!, "logs.db");
        }
        Path = System.IO.Path.GetFullPath(configured ?? "data/logs.db");
        if (!string.Equals(configuration["Database:Provider"], "MySql", StringComparison.OrdinalIgnoreCase))
        {
            business.ConnectionString = configuration.GetConnectionString("MailDatabase") ?? "Data Source=data/mailserver.db";
            if (business.DataSource != ":memory:" && string.Equals(Path, System.IO.Path.GetFullPath(business.DataSource), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("LocalLogs:Path must be different from the business database path.");
        }
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path, DefaultTimeout = 5, Pooling = true };
        _options = new DbContextOptionsBuilder<LocalLogDbContext>().UseSqlite(connection.ToString())
            .AddInterceptors(LocalLogSqliteConnectionInterceptor.Instance).Options;
        ValidateRetention(configuration.GetValue("LocalLogs:RetentionDays", 30));
    }

    // Explicit options support isolated embedded stores and tests without a production fallback.
    public LocalLogDatabase(DbContextOptions<LocalLogDbContext> options)
    {
        _options = options;
        Path = "embedded";
    }

    public LocalLogDbContext CreateDbContext()
    {
        using var suppression = PerformanceTelemetry.Suppress();
        var db = new LocalLogDbContext(_options);
        try
        {
            lock (_initializationGate)
            {
                if (!_initialized)
                {
                    using var schemaLease = Path == "embedded" ? null : AcquireSchemaLease();
                    db.Database.EnsureCreated();
                    if (db.Database.IsSqlite()) db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                    _initialized = true;
                }
            }
            return db;
        }
        catch (Exception error) { ReportFailure(error); return db; }
    }

    private FileStream AcquireSchemaLease()
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            try { return new FileStream(Path + ".schema.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
        }
    }

    public void ReportFailure(Exception error)
    {
        Interlocked.Increment(ref _failedWrites);
        _lastError = error.GetType().Name;
        _logger?.LogWarning("Local SQLite log write failed ({ErrorType}); the committed business operation is retained.", _lastError);
    }

    public static void ValidateRetention(int days)
    {
        if (days is < 1 or > 3650) throw new ArgumentOutOfRangeException(nameof(days), "日志保留天数必须为 1～3650 天。");
    }

    internal async Task<int> RetentionDaysAsync(LocalLogDbContext db, CancellationToken token)
    {
        var value = await db.Settings.Where(x => x.Key == "retention-days").Select(x => x.Value).SingleOrDefaultAsync(token);
        var days = value is null ? _configuration?.GetValue("LocalLogs:RetentionDays", 30) ?? 30 : int.Parse(value, CultureInfo.InvariantCulture);
        ValidateRetention(days);
        return days;
    }

    public async Task<LocalLogSettingsView> SettingsAsync(CancellationToken token)
    {
        await using var db = CreateDbContext();
        return new(Environment.MachineName, Path, await RetentionDaysAsync(db, token), Interlocked.Read(ref _failedWrites), _lastError);
    }

    public async Task<LocalLogSettingsView> SetRetentionAsync(int days, string actor, CancellationToken token)
    {
        ValidateRetention(days);
        await using var db = CreateDbContext();
        var row = await db.Settings.FindAsync(["retention-days"], token);
        if (row is null) db.Settings.Add(new LocalLogSetting { Key = "retention-days", Value = days.ToString(CultureInfo.InvariantCulture) });
        else row.Value = days.ToString(CultureInfo.InvariantCulture);
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "settings.local-logs.update", Resource = "local-logs", Detail = $"RetentionDays={days}" });
        await db.SaveChangesAsync(token);
        return await SettingsAsync(token);
    }
}

public sealed class LocalLogRetentionService(LocalLogDatabase database, IClock clock, ILogger<LocalLogRetentionService> logger,
    LocalLogMaintenanceLease? maintenanceLease = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (maintenanceLease?.TryAcquire() != false) await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Local log retention cleanup failed; it will be retried."); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<int> CleanupAsync(CancellationToken token)
    {
        using var suppression = PerformanceTelemetry.Suppress();
        await using var db = database.CreateDbContext();
        var cutoff = clock.UtcNow.AddDays(-await database.RetentionDaysAsync(db, token));
        var deleted = 0;
        deleted += await DeleteBatchesAsync(db.AuditRecords.Where(x => x.CreatedAt < cutoff), db.AuditRecords, x => x.Id, token);
        deleted += await DeleteBatchesAsync(db.DeliveryAttempts.Where(x => x.CreatedAt < cutoff), db.DeliveryAttempts, x => x.Id, token);
        // Running executions remain available until completion; abandoned executions still expire by age.
        deleted += await DeleteBatchesAsync(db.TaskExecutions.Where(x => (x.CompletedAt ?? x.StartedAt) < cutoff), db.TaskExecutions, x => x.Id, token);
        deleted += await DeleteBatchesAsync(db.PerformanceOperations.Where(x => x.StartedAt < cutoff), db.PerformanceOperations, x => x.Id, token);
        deleted += await DeleteBatchesAsync(db.PerformanceMinutes.Where(x => x.Minute < cutoff), db.PerformanceMinutes, x => x.Id, token);
        deleted += await db.PerformanceSql.Where(x => x.LastSeenAt < cutoff).ExecuteDeleteAsync(token);
        deleted += await db.PerformanceProcesses.Where(x => x.LastSeenAt < cutoff).ExecuteDeleteAsync(token);
        return deleted;
    }

    private static async Task<int> DeleteBatchesAsync<T>(IQueryable<T> expired, DbSet<T> all,
        System.Linq.Expressions.Expression<Func<T, long>> key, CancellationToken token) where T : class
    {
        var count = 0;
        while (true)
        {
            var ids = await expired.OrderBy(key).Select(key).Take(1000).ToArrayAsync(token);
            if (ids.Length == 0) return count;
            var contains = System.Linq.Expressions.Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(long)],
                System.Linq.Expressions.Expression.Constant(ids), key.Body);
            count += await all.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(contains, key.Parameters)).ExecuteDeleteAsync(token);
        }
    }
}
