using System.Text.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MailServer.Infrastructure;

public sealed record ResourceProfile(string Id, string Label, long CacheBytes, long EntryBytes,
    int DeliveryConcurrency, int SmtpConnections, int ImapConnections)
{
    public ResourceValues Values => new(CacheBytes, EntryBytes, DeliveryConcurrency, SmtpConnections,
        ImapConnections, 3, 60, 2, 64);
}

public sealed record ResourceValues(long CacheBytes, long EntryBytes, int DeliveryConcurrency,
    int SmtpConnections, int ImapConnections, int VersionPollSeconds, int AbsoluteRefreshSeconds,
    int MaximumConcurrentLoads, int RuleFallbackBatchSize)
{
    public void Validate()
    {
        if (CacheBytes <= 0 || EntryBytes <= 0 || EntryBytes > CacheBytes
            || DeliveryConcurrency <= 0 || SmtpConnections <= 0 || ImapConnections <= 0
            || VersionPollSeconds <= 0 || AbsoluteRefreshSeconds < VersionPollSeconds
            || MaximumConcurrentLoads is < 1 or > 32 || RuleFallbackBatchSize is < 1 or > 512)
            throw new ArgumentException("资源参数必须为有效正整数，单项预算不得超过总预算，强制刷新不得早于检查周期。");
    }
}

public static class ResourceProfiles
{
    private const long MiB = 1024 * 1024;
    public static readonly IReadOnlyList<ResourceProfile> All = Array.AsReadOnly(new[]
    {
        new ResourceProfile("512mb", "极小 · 512 MB", 4 * MiB, MiB / 2, 1, 8, 16),
        new ResourceProfile("1gb", "小型 · 1 GB", 8 * MiB, MiB, 2, 16, 32),
        new ResourceProfile("2gb", "轻量 · 2 GB", 16 * MiB, 2 * MiB, 4, 32, 64),
        new ResourceProfile("4gb", "标准 · 4 GB", 32 * MiB, 4 * MiB, 8, 64, 128),
        new ResourceProfile("8gb", "增强 · 8 GB", 64 * MiB, 8 * MiB, 16, 1024, 256),
        new ResourceProfile("16gb", "大型 · 16 GB", 128 * MiB, 16 * MiB, 32, 2048, 512),
        new ResourceProfile("32gb", "超大型 · 32 GB", 256 * MiB, 32 * MiB, 64, 4096, 1024)
    });
    public static ResourceProfile Get(string id) => All.FirstOrDefault(x => x.Id == id)
        ?? throw new ArgumentException("请选择有效的内存档位。", nameof(id));
}

// Decreasing the limit never revokes existing permits. New work waits for capacity to return.
public sealed class AdjustableConcurrencyLimit(int limit)
{
    private readonly object _sync = new();
    private int _limit = Math.Max(1, limit);
    private int _active;
    private int _waiting;
    private TaskCompletionSource _capacityChanged = NewSource();
    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Limit { get { lock (_sync) return _limit; } }
    public int Active { get { lock (_sync) return _active; } }
    public int Waiting { get { lock (_sync) return _waiting; } }
    public int Available { get { lock (_sync) return Math.Max(0, _limit - _active); } }
    public void Resize(int value)
    {
        TaskCompletionSource previous;
        lock (_sync) { _limit = Math.Max(1, value); previous = _capacityChanged; _capacityChanged = NewSource(); }
        previous.TrySetResult();
    }
    public IDisposable? TryAcquire()
    {
        lock (_sync)
        {
            if (_active >= _limit) return null;
            _active++;
            return new Permit(this);
        }
    }
    public async Task<IDisposable> AcquireAsync(CancellationToken token)
    {
        var immediate = TryAcquire();
        if (immediate is not null) return immediate;
        lock (_sync)
        {
            if (_waiting >= Math.Min(64, Math.Max(16, _limit * 8))) throw new IOException("Heavy operation queue is full.");
            _waiting++;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    Task changed;
                    lock (_sync)
                    {
                        if (_active < _limit) { _active++; return new Permit(this); }
                        changed = _capacityChanged.Task;
                    }
                    await changed.WaitAsync(timeout.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new IOException("Heavy operation capacity wait timed out."); }
        }
        finally { lock (_sync) _waiting--; }
    }

    private sealed class Permit(AdjustableConcurrencyLimit owner) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            TaskCompletionSource previous;
            lock (owner._sync)
            {
                owner._active--; previous = owner._capacityChanged; owner._capacityChanged = NewSource();
            }
            previous.TrySetResult();
        }
    }
}

public sealed record ResourceSelection(string? ProfileId, ResourceValues? Values = null);
public sealed record ResourceSettingsView(string? ProfileId, IReadOnlyList<ResourceProfile> Profiles,
    ResourceValues? Values = null)
{
    public string ConfigurationId => JsonSerializer.Serialize(new { ProfileId, Values });
}
public sealed record ResourceUsage(string? ProfileId, long CacheBudgetBytes, int DeliveryLimit,
    int SmtpLimit, int ImapLimit, int ActiveDeliveries, int ActiveSmtp, int ActiveImap, bool ApplyFailed,
    string? ConfigurationId = null, string? ProtectionConfigurationId = null, int ProtectedSources = 0,
    IReadOnlyDictionary<string, string>? ServiceStates = null,
    int SmtpMessageLimit = 0, int ActiveSmtpMessages = 0);
public sealed record ResourceProcessView(string ProcessId, string InstanceName, string[] Roles,
    DateTimeOffset LastSeenAt, long EstimatedCacheBytes, ResourceUsage? Usage, string State, string? Message);
public sealed record ResourceStatusView(long TotalCacheBudgetBytes, IReadOnlyList<ResourceProcessView> Processes,
    string? ProfileId, ResourceValues? Values, long TotalEstimatedCacheBytes);

public sealed class ResourceRuntime(string role, IConfiguration configuration)
{
    private readonly object _sync = new();
    private string? _profileId;
    private string? _configurationId;
    private long _cacheBudget;
    private bool _applyFailed;
    private readonly Dictionary<string, string> _serviceStates = new(StringComparer.OrdinalIgnoreCase);
    public AdaptiveResourceOptions AdaptiveOptions { get; } = AdaptiveResourceOptions.Read(configuration);
    public AdaptiveResourceTelemetry Telemetry { get; } = new();
    private bool _experimentInitialized;
    private FairHeavyOperationBudget? _heavyBudget;
    public FairHeavyOperationBudget HeavyBudget => LazyInitializer.EnsureInitialized(ref _heavyBudget, () => new FairHeavyOperationBudget(this));
    public void ResizeExperiment(int inbound, int outbound, int heavy)
    {
        SmtpMessages.Resize(inbound); Delivery.Resize(outbound); LargeMime.Resize(heavy);
    }
    public string Role { get; } = role;
    public bool UsesCache => Role is "smtp" or "imap" or "worker" or "all";
    public AdjustableConcurrencyLimit Smtp { get; } = new(configuration.GetValue("Smtp:MaximumConnections", 200));
    public AdjustableConcurrencyLimit SmtpMessages { get; } = new(configuration.GetValue("Smtp:MaximumActiveMessages", 4));
    public AdjustableConcurrencyLimit Imap { get; } = new(configuration.GetValue("Imap:MaximumConnections", 200));
    public AdjustableConcurrencyLimit Delivery { get; } = new(8);
    public AdjustableConcurrencyLimit LargeMime { get; } = new(1);
    public ResourceUsage Snapshot()
    {
        lock (_sync) return new(_profileId, _cacheBudget, Delivery.Limit, Smtp.Limit, Imap.Limit,
            Delivery.Active, Smtp.Active, Imap.Active, _applyFailed, _configurationId,
            ServiceStates: new Dictionary<string, string>(_serviceStates),
            SmtpMessageLimit: SmtpMessages.Limit, ActiveSmtpMessages: SmtpMessages.Active);
    }
    public void SetServiceState(string role, string state)
    {
        lock (_sync) _serviceStates[role] = state;
    }
    public void MarkFailed() { lock (_sync) _applyFailed = true; }
    public void Apply(string? profileId, ConfigurationSnapshotCache cache) =>
        Apply(new ResourceSettingsView(profileId, ResourceProfiles.All,
            profileId is null ? null : ResourceProfiles.Get(profileId).Values), cache);
    public void Apply(ResourceSettingsView settings, ConfigurationSnapshotCache cache)
    {
        lock (_sync)
        {
            if (settings.Values is { } values)
            {
                values.Validate();
                if (AdaptiveOptions.Enabled)
                {
                    HeavyBudget.Configure(true);
                    cache.Reconfigure(32L * 1024 * 1024, Math.Min(values.EntryBytes, 32L * 1024 * 1024),
                        values.VersionPollSeconds, values.AbsoluteRefreshSeconds, values.MaximumConcurrentLoads, values.RuleFallbackBatchSize);
                    Smtp.Resize(128);
                    if (!_experimentInitialized)
                    {
                        ResizeExperiment(AdaptiveOptions.InitialInbound, AdaptiveOptions.InitialOutbound, AdaptiveOptions.InitialHeavy);
                        _experimentInitialized = true;
                    }
                    _cacheBudget = UsesCache && cache.Options.Enabled ? cache.Options.SizeLimitBytes : 0;
                    _profileId = settings.ProfileId; _configurationId = settings.ConfigurationId; _applyFailed = false;
                    return;
                }
                var budget = Role switch { "all" => values.CacheBytes, "smtp" => values.CacheBytes / 2,
                    "imap" or "worker" => values.CacheBytes / 4, _ => 0 };
                cache.Reconfigure(budget, Math.Min(values.EntryBytes, budget), values.VersionPollSeconds,
                    values.AbsoluteRefreshSeconds, values.MaximumConcurrentLoads, values.RuleFallbackBatchSize);
                var availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                var connectionMemoryBound = (int)Math.Clamp(availableMemory / (512L * 1024), 8, int.MaxValue);
                Smtp.Resize(Math.Min(Math.Min(values.SmtpConnections, connectionMemoryBound),
                    configuration.GetValue<int?>("Smtp:MaximumConnections") ?? int.MaxValue));
                var memoryBound = (int)Math.Clamp(
                    availableMemory / (256L * 1024 * 1024), 1, int.MaxValue);
                SmtpMessages.Resize(Math.Min(Math.Min(values.DeliveryConcurrency, memoryBound),
                    configuration.GetValue("Smtp:MaximumActiveMessages", 16)));
                Imap.Resize(Math.Min(values.ImapConnections, configuration.GetValue<int?>("Imap:MaximumConnections") ?? int.MaxValue));
                Delivery.Resize(values.DeliveryConcurrency);
                LargeMime.Resize(Math.Clamp(configuration.GetValue("Resources:MaximumHeavyOperations",
                    settings.ProfileId is "512mb" or "1gb" ? 1 : 2), 1, 8));
            }
            _cacheBudget = UsesCache && cache.Options.Enabled ? cache.Options.SizeLimitBytes : 0;
            _profileId = settings.ProfileId;
            _configurationId = settings.ConfigurationId;
            _applyFailed = false;
        }
    }
}

public sealed class ResourceSettingsService(IDbContextFactory<MailDbContext> factory,
    ConfigurationSnapshotCache cache, ResourceRuntime? runtime = null)
{
    public const string Key = "resources:profile";
    public async Task<ResourceSettingsView> ReadAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var id = await db.SystemSettings.AsNoTracking().Where(x => x.Key == Key).Select(x => x.Value).SingleOrDefaultAsync(token);
        if (id is null) return new(null, ResourceProfiles.All);
        if (id.StartsWith('{'))
        {
            var saved = JsonSerializer.Deserialize<ResourceSelection>(id, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new FormatException("资源设置无效。");
            if (saved.ProfileId is not null) ResourceProfiles.Get(saved.ProfileId);
            var values = saved.Values ?? (saved.ProfileId is null ? null : ResourceProfiles.Get(saved.ProfileId).Values);
            values?.Validate();
            return new(saved.ProfileId, ResourceProfiles.All, values);
        }
        return new(id, ResourceProfiles.All, ResourceProfiles.Get(id).Values);
    }
    public Task<ResourceSettingsView> SaveAsync(string profileId, string actor, CancellationToken token) =>
        SaveAsync(new ResourceSelection(profileId), actor, token);
    public async Task<ResourceSettingsView> SaveAsync(ResourceSelection selection, string actor, CancellationToken token)
    {
        var profileId = selection.ProfileId ?? throw new ArgumentException("请选择有效的内存档位。");
        var values = selection.Values ?? ResourceProfiles.Get(profileId).Values;
        ResourceProfiles.Get(profileId);
        values.Validate();
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        // Lock the fixed version row before reading settings to serialize concurrent saves.
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Settings, token);
        var setting = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == Key, token);
        var serialized = JsonSerializer.Serialize(new ResourceSelection(profileId, values),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (setting is null) db.SystemSettings.Add(new SystemSetting { Key = Key, Value = serialized });
        else setting.Value = serialized;
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "settings.resources.update",
            Resource = "settings:resources", Detail = profileId });
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        cache.ObserveCommittedVersion(ConfigurationScopes.Settings, version);
        return new(profileId, ResourceProfiles.All, values);
    }
    public async Task InitializeNewInstallationAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (await db.AdminUsers.AnyAsync(token) || await db.Domains.AnyAsync(token)
            || await db.SystemSettings.AnyAsync(x => x.Key == Key, token)) return;
        await SaveAsync("1gb", "installation", token);
    }
    public async Task<ResourceStatusView> StatusAsync(CancellationToken token)
    {
        var desired = await ReadAsync(token);
        if (runtime is null) return new(0, [], desired.ProfileId, desired.Values, 0);
        var usage = runtime.Snapshot();
        var roles = runtime.Role == "all" ? new[] { "api", "smtp", "imap", "worker" } : new[] { runtime.Role };
        var state = usage.ApplyFailed ? "failed"
            : runtime.Role == "api" || usage.ConfigurationId == desired.ConfigurationId ? "applied" : "applying";
        var serviceStarting = roles.Where(role => role is "smtp" or "imap" or "worker")
            .Any(role => usage.ServiceStates?.ContainsKey(role) != true);
        if (state == "applied" && serviceStarting) state = "applying";
        var serviceIssue = roles.Any(role => usage.ServiceStates?.GetValueOrDefault(role) is "stopped" or "error");
        if (state == "applied" && serviceIssue) state = "failed";
        var process = new ResourceProcessView(
            $"{Environment.MachineName}:{Environment.ProcessId}", Environment.MachineName, roles,
            DateTimeOffset.UtcNow, cache.EstimatedBytes, usage, state,
            state == "failed" ? "服务未正常监听或工作，或暂时无法应用设置，请查看服务日志。" : null);
        return new(usage.CacheBudgetBytes, [process], desired.ProfileId, desired.Values, cache.EstimatedBytes);
    }
}
