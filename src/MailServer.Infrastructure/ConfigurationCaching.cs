using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public static class ConfigurationScopes
{
    public const string Rules = "rules";
    public const string Firewall = "firewall";
    public const string Settings = "settings";
    public static readonly string[] All = [Rules, Firewall, Settings];
}

public sealed record ConfigurationVersionsSnapshot(long Rules, long Firewall, long Settings)
{
    public long For(string scope) => scope switch
    {
        ConfigurationScopes.Rules => Rules,
        ConfigurationScopes.Firewall => Firewall,
        ConfigurationScopes.Settings => Settings,
        _ => 0
    };
}

public sealed record ConfigurationProcessStatusView(
    string ProcessId,
    string InstanceName,
    IReadOnlyList<string> Roles,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeenAt,
    ConfigurationVersionsSnapshot AppliedVersions,
    long EstimatedCacheBytes,
    int EntryCount,
    long Hits,
    long Misses,
    long ReloadFailures,
    string? LastError,
    string State);

public sealed record ConfigurationCacheStatusView(
    ConfigurationVersionsSnapshot SavedVersions,
    long SizeLimitBytes,
    int ProcessStaleSeconds,
    IReadOnlyList<ConfigurationProcessStatusView> Processes);

public sealed class ConfigurationVersionService(
    IDbContextFactory<MailDbContext> dbFactory,
    IOptions<ConfigurationCacheOptions> options,
    ILogger<ConfigurationVersionService> logger)
{
    public async Task<ConfigurationVersionsSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ConfigurationVersions.AsNoTracking().ToDictionaryAsync(
            x => x.Scope, x => x.Version, StringComparer.OrdinalIgnoreCase, cancellationToken);
        return new ConfigurationVersionsSnapshot(
            rows.GetValueOrDefault(ConfigurationScopes.Rules),
            rows.GetValueOrDefault(ConfigurationScopes.Firewall),
            rows.GetValueOrDefault(ConfigurationScopes.Settings));
    }

    public static async Task<long> BumpAsync(
        MailDbContext db,
        string scope,
        CancellationToken cancellationToken)
    {
        if (db.Database.IsRelational())
        {
            var now = DateTimeOffset.UtcNow;
            var changed = await db.ConfigurationVersions.Where(x => x.Scope == scope)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Version, x => x.Version + 1)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (changed == 0)
            {
                // EnsureCreated-based test and embedded databases do not execute seed SQL from migrations.
                db.ConfigurationVersions.Add(new ConfigurationVersion { Scope = scope, Version = 1, UpdatedAt = now });
                return 1;
            }
            return await db.ConfigurationVersions.AsNoTracking().Where(x => x.Scope == scope)
                .Select(x => x.Version).SingleAsync(cancellationToken);
        }
        var row = await db.ConfigurationVersions.SingleOrDefaultAsync(x => x.Scope == scope, cancellationToken);
        if (row is null)
        {
            row = new ConfigurationVersion { Scope = scope, Version = 1, UpdatedAt = DateTimeOffset.UtcNow };
            db.ConfigurationVersions.Add(row);
        }
        else
        {
            row.Version++;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        return row.Version;
    }

    public async Task<ConfigurationCacheStatusView> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var versionRows = await db.ConfigurationVersions.AsNoTracking().ToDictionaryAsync(
            x => x.Scope, x => x.Version, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var saved = new ConfigurationVersionsSnapshot(
            versionRows.GetValueOrDefault(ConfigurationScopes.Rules),
            versionRows.GetValueOrDefault(ConfigurationScopes.Firewall),
            versionRows.GetValueOrDefault(ConfigurationScopes.Settings));
        var statuses = await db.ConfigurationProcessStatuses.AsNoTracking()
            .OrderBy(x => x.InstanceName).ThenBy(x => x.ProcessId)
            .ToListAsync(cancellationToken);
        logger.LogDebug("Read configuration cache status for {ProcessCount} process(es).", statuses.Count);
        var staleCutoff = DateTimeOffset.UtcNow.AddSeconds(-Math.Max(5, options.Value.ProcessStaleSeconds));
        return new ConfigurationCacheStatusView(saved, options.Value.SizeLimitBytes, options.Value.ProcessStaleSeconds, statuses.Select(x => new ConfigurationProcessStatusView(
            x.ProcessId,
            x.InstanceName,
            x.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            x.StartedAt,
            x.LastSeenAt,
            new ConfigurationVersionsSnapshot(x.RulesVersion, x.FirewallVersion, x.SettingsVersion),
            x.EstimatedCacheBytes,
            x.EntryCount,
            x.Hits,
            x.Misses,
            x.ReloadFailures,
            x.LastError,
            x.LastSeenAt < staleCutoff ? "stale"
                : x.RulesVersion == saved.Rules && x.FirewallVersion == saved.Firewall && x.SettingsVersion == saved.Settings
                    ? "current" : "lagging")).ToArray());
    }
}

public sealed class ConfigurationSnapshotCache : IDisposable
{
    private sealed record CacheValue(object Value, long Version, DateTimeOffset LoadedAt, long Size);
    private sealed record EntryInfo(long Size, object Token, DateTimeOffset LoadedAt);

    private readonly MemoryCache _cache;
    private readonly ConfigurationCacheOptions _options;
    private readonly SemaphoreSlim _loadLimit;
    private readonly ConcurrentDictionary<string, Task<CacheValue>> _loads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EntryInfo> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ConfigurationSnapshotCache> _logger;
    private readonly Meter _meter = new("MailServer.ConfigurationCache", "1.0.0");
    private readonly Counter<long> _hitCounter;
    private readonly Counter<long> _missCounter;
    private readonly Counter<long> _evictionCounter;
    private readonly Counter<long> _failureCounter;
    private readonly Histogram<double> _loadDuration;
    private long _hits;
    private long _misses;
    private long _reloadFailures;
    private string? _lastError;

    public ConfigurationSnapshotCache(
        IOptions<ConfigurationCacheOptions> options,
        ILogger<ConfigurationSnapshotCache> logger)
    {
        _options = options.Value;
        _logger = logger;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = Math.Max(1, _options.SizeLimitBytes) });
        _loadLimit = new SemaphoreSlim(Math.Clamp(_options.MaximumConcurrentLoads, 1, 32));
        _hitCounter = _meter.CreateCounter<long>("mailserver.config_cache.hits");
        _missCounter = _meter.CreateCounter<long>("mailserver.config_cache.misses");
        _evictionCounter = _meter.CreateCounter<long>("mailserver.config_cache.evictions");
        _failureCounter = _meter.CreateCounter<long>("mailserver.config_cache.reload_failures");
        _loadDuration = _meter.CreateHistogram<double>("mailserver.config_cache.load_duration_ms");
        _meter.CreateObservableGauge("mailserver.config_cache.snapshot_age_seconds", () =>
            _entries.IsEmpty ? 0 : _entries.Values.Max(x => Math.Max(0, (DateTimeOffset.UtcNow - x.LoadedAt).TotalSeconds)));
        foreach (var scope in ConfigurationScopes.All) _versions[scope] = 0;
    }

    public ConfigurationCacheOptions Options => _options;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long ReloadFailures => Interlocked.Read(ref _reloadFailures);
    public string? LastError => Volatile.Read(ref _lastError);
    public int EntryCount => _entries.Count;
    public long EstimatedBytes => _entries.Values.Sum(x => x.Size);
    public ConfigurationVersionsSnapshot Versions => new(
        _versions.GetValueOrDefault(ConfigurationScopes.Rules),
        _versions.GetValueOrDefault(ConfigurationScopes.Firewall),
        _versions.GetValueOrDefault(ConfigurationScopes.Settings));

    public void ObserveVersions(ConfigurationVersionsSnapshot versions)
    {
        _versions[ConfigurationScopes.Rules] = versions.Rules;
        _versions[ConfigurationScopes.Firewall] = versions.Firewall;
        _versions[ConfigurationScopes.Settings] = versions.Settings;
    }

    public void ObserveCommittedVersion(string scope, long version) => _versions[scope] = version;

    public async Task<T> GetAsync<T>(
        string key,
        string scope,
        Func<CancellationToken, Task<(T Value, long Size)>> loader,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!_options.Enabled)
        {
            var uncached = await loader(cancellationToken);
            return uncached.Value;
        }

        var expectedVersion = _versions.GetValueOrDefault(scope);
        CacheValue? stale = null;
        if (_cache.TryGetValue(key, out CacheValue? existing) && existing is not null)
        {
            stale = existing;
            if (existing.Version == expectedVersion
                && DateTimeOffset.UtcNow - existing.LoadedAt < TimeSpan.FromSeconds(Math.Max(1, _options.AbsoluteRefreshSeconds)))
            {
                Interlocked.Increment(ref _hits);
                _hitCounter.Add(1, new KeyValuePair<string, object?>("scope", scope));
                return (T)existing.Value;
            }
        }

        Interlocked.Increment(ref _misses);
        _missCounter.Add(1, new KeyValuePair<string, object?>("scope", scope));
        var task = _loads.GetOrAdd(key, _ => LoadAsync(key, scope, expectedVersion, loader, cancellationToken));
        try
        {
            return (T)(await task.WaitAsync(cancellationToken)).Value;
        }
        catch (Exception exception) when (exception is not OperationCanceledException && stale is not null)
        {
            _logger.LogError(exception, "Configuration snapshot {CacheKey} could not be refreshed; using version {Version}.", key, stale.Version);
            return (T)stale.Value;
        }
        finally
        {
            if (task.IsCompleted) _loads.TryRemove(new KeyValuePair<string, Task<CacheValue>>(key, task));
        }
    }

    private async Task<CacheValue> LoadAsync<T>(
        string key,
        string scope,
        long version,
        Func<CancellationToken, Task<(T Value, long Size)>> loader,
        CancellationToken cancellationToken)
        where T : class
    {
        await _loadLimit.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var loaded = await loader(cancellationToken);
            var size = Math.Max(1, loaded.Size);
            var value = new CacheValue(loaded.Value, version, DateTimeOffset.UtcNow, size);
            if (size <= _options.MaximumEntryBytes && size <= _options.SizeLimitBytes)
            {
                var token = new object();
                var options = new MemoryCacheEntryOptions().SetSize(size).RegisterPostEvictionCallback((cacheKey, _, _, _) =>
                {
                    if (cacheKey is string text
                        && _entries.TryGetValue(text, out var current)
                        && ReferenceEquals(current.Token, token))
                    {
                        _entries.TryRemove(text, out _);
                    }
                    _evictionCounter.Add(1, new KeyValuePair<string, object?>("scope", scope));
                });
                _entries[key] = new EntryInfo(size, token, value.LoadedAt);
                _cache.Set(key, value, options);
            }
            Volatile.Write(ref _lastError, null);
            return value;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Interlocked.Increment(ref _reloadFailures);
            _failureCounter.Add(1, new KeyValuePair<string, object?>("scope", scope));
            Volatile.Write(ref _lastError, exception.Message);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            _loadDuration.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("scope", scope));
            _loadLimit.Release();
        }
    }

    public void Dispose()
    {
        _cache.Dispose();
        _loadLimit.Dispose();
        _meter.Dispose();
    }
}

public sealed class ConfigurationCacheMonitor(
    IDbContextFactory<MailDbContext> dbFactory,
    ConfigurationVersionService versions,
    ConfigurationSnapshotCache cache,
    IConfiguration configuration,
    IOptions<ConfigurationCacheOptions> options,
    ILogger<ConfigurationCacheMonitor> logger) : BackgroundService
{
    private readonly string _processId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromSeconds(Math.Max(1, options.Value.VersionPollSeconds));
        var nextHeartbeat = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                cache.ObserveVersions(await versions.ReadAsync(stoppingToken));
                if (DateTimeOffset.UtcNow >= nextHeartbeat)
                {
                    await WriteHeartbeatAsync(stoppingToken);
                    nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, options.Value.StatusHeartbeatSeconds));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Configuration version polling failed; existing snapshots remain active.");
            }
            await Task.Delay(poll, stoppingToken);
        }
    }

    private async Task WriteHeartbeatAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var status = await db.ConfigurationProcessStatuses.SingleOrDefaultAsync(x => x.ProcessId == _processId, cancellationToken);
        if (status is null)
        {
            status = new ConfigurationProcessStatus { ProcessId = _processId, InstanceName = Environment.MachineName, Roles = ResolveRoles(), StartedAt = _startedAt };
            db.ConfigurationProcessStatuses.Add(status);
        }
        var current = cache.Versions;
        status.LastSeenAt = now;
        status.RulesVersion = current.Rules;
        status.FirewallVersion = current.Firewall;
        status.SettingsVersion = current.Settings;
        status.EstimatedCacheBytes = cache.EstimatedBytes;
        status.EntryCount = cache.EntryCount;
        status.Hits = cache.Hits;
        status.Misses = cache.Misses;
        status.ReloadFailures = cache.ReloadFailures;
        status.LastError = cache.LastError;
        var cutoff = now.AddHours(-Math.Max(1, options.Value.ProcessRetentionHours));
        await db.ConfigurationProcessStatuses.Where(x => x.LastSeenAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private string ResolveRoles()
    {
        var name = AppDomain.CurrentDomain.FriendlyName;
        if (name.Contains("Api", StringComparison.OrdinalIgnoreCase))
            return configuration.GetValue("RunAll:Enabled", false) ? "api,smtp,imap,worker" : "api";
        if (name.Contains("Smtp", StringComparison.OrdinalIgnoreCase)) return "smtp";
        if (name.Contains("Imap", StringComparison.OrdinalIgnoreCase)) return "imap";
        if (name.Contains("Worker", StringComparison.OrdinalIgnoreCase)) return "worker";
        return "unknown";
    }
}
