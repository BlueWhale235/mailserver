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
    public const string Routing = "routing";
    public static readonly string[] All = [Rules, Firewall, Settings, Routing];
}

public sealed record ConfigurationVersionsSnapshot(long Rules, long Firewall, long Settings, long Routing = 0)
{
    public long For(string scope) => scope switch
    {
        ConfigurationScopes.Rules => Rules,
        ConfigurationScopes.Firewall => Firewall,
        ConfigurationScopes.Settings => Settings,
        ConfigurationScopes.Routing => Routing,
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
            rows.GetValueOrDefault(ConfigurationScopes.Settings),
            rows.GetValueOrDefault(ConfigurationScopes.Routing));
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
            versionRows.GetValueOrDefault(ConfigurationScopes.Settings),
            versionRows.GetValueOrDefault(ConfigurationScopes.Routing));
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
            new ConfigurationVersionsSnapshot(x.RulesVersion, x.FirewallVersion, x.SettingsVersion, x.RoutingVersion),
            x.EstimatedCacheBytes,
            x.EntryCount,
            x.Hits,
            x.Misses,
            x.ReloadFailures,
            x.LastError,
            x.LastSeenAt < staleCutoff ? "stale"
                : x.Roles == "api" ? "not-applicable"
                : x.RulesVersion == saved.Rules && x.FirewallVersion == saved.Firewall
                    && x.SettingsVersion == saved.Settings && x.RoutingVersion == saved.Routing
                    ? "current" : "lagging")).ToArray());
    }
}

// Request-local bypass keeps administrative reads fresh in a shared run-all host.
public sealed class ConfigurationCacheBypass : IDisposable
{
    private static readonly AsyncLocal<bool> Current = new();
    private readonly bool _previous = Current.Value;
    public static bool IsActive => Current.Value;
    public ConfigurationCacheBypass() => Current.Value = true;
    public void Dispose() => Current.Value = _previous;
}

public sealed class ConfigurationSnapshotCache : IDisposable
{
    internal static event Action<long>? RoutingCommitted;
    internal static void NotifyRoutingCommitted(long version) => RoutingCommitted?.Invoke(version);
    private sealed record CacheValue(object Value, long Version, DateTimeOffset LoadedAt, long Size);
    private sealed record Failure(int Count, DateTimeOffset RetryAt, Exception Error);
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheValue> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<CacheValue>> _loads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Failure> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly LoadAdmission _loadLimit;
    private readonly ILogger<ConfigurationSnapshotCache> _logger;
    private readonly Meter _meter = new("MailServer.ConfigurationCache", "1.0.0");
    private readonly Counter<long> _hitCounter, _missCounter, _evictionCounter, _failureCounter;
    private readonly Histogram<double> _loadDuration;
    private long _bytes, _hits, _misses, _reloadFailures;
    private bool _disposed;
    private string? _lastError;
    public ConfigurationCacheOptions Options { get; }
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long ReloadFailures => Interlocked.Read(ref _reloadFailures);
    public string? LastError { get { lock (_sync) return _lastError; } }
    public int EntryCount { get { lock (_sync) return _entries.Count; } }
    public int ActiveLoads { get { lock (_sync) return _loads.Count; } }
    public long EstimatedBytes { get { lock (_sync) return _bytes; } }
    public ConfigurationVersionsSnapshot Versions
    {
        get { lock (_sync) return new(_versions.GetValueOrDefault(ConfigurationScopes.Rules),
            _versions.GetValueOrDefault(ConfigurationScopes.Firewall), _versions.GetValueOrDefault(ConfigurationScopes.Settings),
            _versions.GetValueOrDefault(ConfigurationScopes.Routing)); }
    }
    public ConfigurationSnapshotCache(IOptions<ConfigurationCacheOptions> options, ILogger<ConfigurationSnapshotCache> logger)
    {
        Options = options.Value;
        _logger = logger;
        _loadLimit = new(Math.Clamp(Options.MaximumConcurrentLoads, 1, 32));
        RoutingCommitted += ObserveRoutingCommitted;
        _hitCounter = _meter.CreateCounter<long>("mailserver.config_cache.hits");
        _missCounter = _meter.CreateCounter<long>("mailserver.config_cache.misses");
        _evictionCounter = _meter.CreateCounter<long>("mailserver.config_cache.evictions");
        _failureCounter = _meter.CreateCounter<long>("mailserver.config_cache.reload_failures");
        _loadDuration = _meter.CreateHistogram<double>("mailserver.config_cache.load_duration_ms");
        _meter.CreateObservableGauge("mailserver.config_cache.snapshot_age_seconds", () =>
        {
            lock (_sync) return _entries.Count == 0 ? 0 : _entries.Values.Max(x => (DateTimeOffset.UtcNow - x.LoadedAt).TotalSeconds);
        });
    }
    private void ObserveRoutingCommitted(long version) => ObserveCommittedVersion(ConfigurationScopes.Routing, version);
    public void ObserveVersions(ConfigurationVersionsSnapshot versions)
    {
        lock (_sync)
        {
            _versions[ConfigurationScopes.Rules] = Math.Max(versions.Rules, _versions.GetValueOrDefault(ConfigurationScopes.Rules));
            _versions[ConfigurationScopes.Firewall] = Math.Max(versions.Firewall, _versions.GetValueOrDefault(ConfigurationScopes.Firewall));
            _versions[ConfigurationScopes.Settings] = Math.Max(versions.Settings, _versions.GetValueOrDefault(ConfigurationScopes.Settings));
            _versions[ConfigurationScopes.Routing] = Math.Max(versions.Routing, _versions.GetValueOrDefault(ConfigurationScopes.Routing));
        }
    }
    public void ObserveCommittedVersion(string scope, long version)
    {
        lock (_sync) _versions[scope] = Math.Max(version, _versions.GetValueOrDefault(scope));
    }
    public void Resize(long size, long entrySize)
    {
        lock (_sync)
        {
            Options.SizeLimitBytes = Math.Max(0, size);
            Options.MaximumEntryBytes = Math.Max(0, Math.Min(size, entrySize));
            foreach (var key in _entries.Where(x => x.Value.Size > Options.MaximumEntryBytes).Select(x => x.Key).ToArray()) Remove(key);
            while (_bytes > Options.SizeLimitBytes && _lru.First is not null) Remove(_lru.First.Value);
        }
    }
    public void Reconfigure(long size, long entrySize, int pollSeconds, int refreshSeconds,
        int concurrentLoads, int fallbackBatchSize)
    {
        lock (_sync)
        {
            Options.VersionPollSeconds = pollSeconds;
            Options.AbsoluteRefreshSeconds = refreshSeconds;
            Options.MaximumConcurrentLoads = concurrentLoads;
            Options.RuleFallbackBatchSize = fallbackBatchSize;
            _loadLimit.Resize(concurrentLoads);
            Resize(size, entrySize);
        }
    }
    private void Remove(string key)
    {
        if (!_entries.Remove(key, out var entry)) return;
        _bytes -= entry.Size;
        _lru.Remove(_nodes[key]);
        _nodes.Remove(key);
        _evictionCounter.Add(1);
    }
    public async Task<T> GetAsync<T>(string key, string scope,
        Func<CancellationToken, Task<(T Value, long Size)>> loader, CancellationToken cancellationToken,
        TimeSpan? maximumAge = null) where T : class
    {
        if (!Options.Enabled || ConfigurationCacheBypass.IsActive) return (await loader(cancellationToken)).Value;
        cancellationToken.ThrowIfCancellationRequested();
        CacheValue? stale;
        TaskCompletionSource<CacheValue> source;
        bool owner = false;
        long version;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            version = _versions.GetValueOrDefault(scope);
            _entries.TryGetValue(key, out stale);
            if (stale is not null && stale.Version == version
                && DateTimeOffset.UtcNow - stale.LoadedAt < (maximumAge is null
                    ? TimeSpan.FromSeconds(Math.Max(1, Options.AbsoluteRefreshSeconds))
                    : TimeSpan.FromSeconds(Math.Max(1, Math.Min(Options.AbsoluteRefreshSeconds, maximumAge.Value.TotalSeconds)))))
            {
                _lru.Remove(_nodes[key]);
                _lru.AddLast(_nodes[key]);
                Interlocked.Increment(ref _hits);
                _hitCounter.Add(1);
                return (T)stale.Value;
            }
            if (_failures.TryGetValue(scope, out var failure) && DateTimeOffset.UtcNow < failure.RetryAt)
            {
                if (stale is not null) return (T)stale.Value;
                throw new InvalidOperationException("Configuration temporarily unavailable; retry later.", failure.Error);
            }
            Interlocked.Increment(ref _misses);
            _missCounter.Add(1);
            if (!_loads.TryGetValue(key, out source!))
            {
                source = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _loads.Add(key, source);
                owner = true;
                // Observe faults even if all request waiters cancel before the load finishes.
                _ = source.Task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        if (owner) _ = LoadAsync(key, scope, version, loader, source);
        try { return (T)(await source.Task.WaitAsync(cancellationToken)).Value; }
        catch (Exception error) when (error is not OperationCanceledException && stale is not null)
        {
            return (T)stale.Value;
        }
    }
    private async Task LoadAsync<T>(string key, string scope, long version,
        Func<CancellationToken, Task<(T Value, long Size)>> loader, TaskCompletionSource<CacheValue> source) where T : class
    {
        using var performance = PerformanceTelemetry.Stage("cache.load");
        IDisposable? admission = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using (PerformanceTelemetry.Stage("cache.admission"))
                admission = await _loadLimit.AcquireAsync(_lifetime.Token);
            var loaded = await loader(_lifetime.Token);
            var value = new CacheValue(loaded.Value, version, DateTimeOffset.UtcNow, Math.Max(1, loaded.Size));
            lock (_sync)
            {
                if (!_disposed && value.Size <= Options.MaximumEntryBytes && value.Size <= Options.SizeLimitBytes)
                {
                    Remove(key);
                    while (_bytes + value.Size > Options.SizeLimitBytes && _lru.First is not null) Remove(_lru.First.Value);
                    _entries[key] = value;
                    _nodes[key] = _lru.AddLast(key);
                    _bytes += value.Size;
                }
                _failures.Remove(scope);
                _lastError = _failures.Values.FirstOrDefault()?.Error.Message;
                _loads.Remove(key);
                source.TrySetResult(value);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_sync) { _loads.Remove(key); source.TrySetCanceled(); }
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref _reloadFailures);
            _failureCounter.Add(1);
            lock (_sync)
            {
                var count = Math.Min(5, (_failures.GetValueOrDefault(scope)?.Count ?? 0) + 1);
                _failures[scope] = new(count, DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, 3 * Math.Pow(2, count - 1))), error);
                _lastError = error.Message;
                _loads.Remove(key);
                source.TrySetException(error);
            }
            _logger.LogWarning(error, "Configuration reload failed for {Scope}; retry is backed off.", scope);
        }
        finally
        {
            admission?.Dispose();
            _loadDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
    }
    public void Dispose()
    {
        RoutingCommitted -= ObserveRoutingCommitted;
        lock (_sync) { _disposed = true; _entries.Clear(); _nodes.Clear(); _lru.Clear(); _bytes = 0; }
        _lifetime.Cancel();
        // In-flight loads own their admission permits until they complete.
        _meter.Dispose();
    }

    private sealed class LoadAdmission(int limit)
    {
        private readonly object _sync = new();
        private int _limit = limit;
        private int _active;
        private TaskCompletionSource _changed = NewSignal();
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Resize(int limit)
        {
            lock (_sync) { _limit = Math.Clamp(limit, 1, 32); Signal(); }
        }
        public async Task<IDisposable> AcquireAsync(CancellationToken token)
        {
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_active < _limit) { _active++; return new Permit(this); }
                    wait = _changed.Task;
                }
                await wait.WaitAsync(token);
            }
        }
        private void Release()
        {
            lock (_sync) { _active--; Signal(); }
        }
        private void Signal()
        {
            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }
        private sealed class Permit(LoadAdmission owner) : IDisposable
        {
            private int _disposed;
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(); }
        }
    }
}

public sealed class ConfigurationCacheMonitor(
    IDbContextFactory<MailDbContext> dbFactory,
    ConfigurationVersionService versions,
    ConfigurationSnapshotCache cache,
    IOptions<ConfigurationCacheOptions> options,
    ILogger<ConfigurationCacheMonitor> logger,
    ResourceSettingsService resources,
    ResourceRuntime runtime,
    ConnectionProtectionSettingsService protectionSettings,
    ConnectionProtection protection) : BackgroundService
{
    private readonly string _processId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private long _settingsVersion = -1;
    private DateTimeOffset _lastResourceRead = DateTimeOffset.MinValue;
    private bool _heartbeatRequested;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await RefreshAsync(cancellationToken); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            runtime.MarkFailed();
            logger.LogWarning(error, "Resource settings unavailable during startup; configured limits remain active.");
        }
        await base.StartAsync(cancellationToken);
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        var current = await versions.ReadAsync(token);
        if (current.Settings != _settingsVersion || DateTimeOffset.UtcNow - _lastResourceRead >= TimeSpan.FromSeconds(60))
        {
            var resourceSettings = await resources.ReadAsync(token);
            var connectionSettings = await protectionSettings.ReadAsync(token);
            runtime.Apply(resourceSettings, cache);
            protection.Apply(connectionSettings);
            _heartbeatRequested = true;
            _settingsVersion = current.Settings;
            _lastResourceRead = DateTimeOffset.UtcNow;
        }
        cache.ObserveVersions(current);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextHeartbeat = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
                if (_heartbeatRequested || DateTimeOffset.UtcNow >= nextHeartbeat)
                {
                    await WriteHeartbeatAsync(stoppingToken);
                    _heartbeatRequested = false;
                    nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, options.Value.StatusHeartbeatSeconds));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                runtime.MarkFailed();
                _settingsVersion = -1;
                logger.LogError(exception, "Configuration version polling failed; existing snapshots remain active.");
                try { await WriteHeartbeatAsync(stoppingToken); }
                catch (Exception heartbeatError) when (heartbeatError is not OperationCanceledException)
                { logger.LogDebug(heartbeatError, "Unable to report resource state."); }
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, cache.Options.VersionPollSeconds)), stoppingToken);
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
        status.RoutingVersion = current.Routing;
        status.EstimatedCacheBytes = cache.EstimatedBytes;
        status.EntryCount = cache.EntryCount;
        status.Hits = cache.Hits;
        status.Misses = cache.Misses;
        status.ReloadFailures = cache.ReloadFailures;
        status.LastError = cache.LastError;
        status.ResourceStatusJson = System.Text.Json.JsonSerializer.Serialize(runtime.Snapshot() with
        {
            ProtectionConfigurationId = System.Text.Json.JsonSerializer.Serialize(protection.Settings),
            ProtectedSources = protection.TrackedSources
        });
        var cutoff = now.AddHours(-Math.Max(1, options.Value.ProcessRetentionHours));
        await db.ConfigurationProcessStatuses.Where(x => x.LastSeenAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Runtime resource sample: role={Role} rssBytes={RssBytes} managedBytes={ManagedBytes} heapBytes={HeapBytes} gc0={Gc0} gc1={Gc1} gc2={Gc2} cacheBytes={CacheBytes}",
            runtime.Role, Environment.WorkingSet, GC.GetTotalMemory(false), GC.GetGCMemoryInfo().HeapSizeBytes,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), cache.EstimatedBytes);
    }

    private string ResolveRoles()
    {
        return runtime.Role == "all" ? "api,smtp,imap,worker" : runtime.Role;
    }
}
