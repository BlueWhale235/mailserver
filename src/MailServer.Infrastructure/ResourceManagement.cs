using System.Text.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MailServer.Infrastructure;

public sealed record ResourceProfile(string Id, string Label, long CacheBytes, long EntryBytes,
    int DeliveryConcurrency, int SmtpConnections, int ImapConnections);

public static class ResourceProfiles
{
    private const long MiB = 1024 * 1024;
    public static readonly IReadOnlyList<ResourceProfile> All = Array.AsReadOnly(new[]
    {
        new ResourceProfile("512mb", "极小 · 512 MB", 4 * MiB, MiB / 2, 1, 8, 16),
        new ResourceProfile("1gb", "小型 · 1 GB", 8 * MiB, MiB, 2, 16, 32),
        new ResourceProfile("2gb", "轻量 · 2 GB", 16 * MiB, 2 * MiB, 4, 32, 64),
        new ResourceProfile("4gb", "标准 · 4 GB", 32 * MiB, 4 * MiB, 8, 64, 128),
        new ResourceProfile("8gb", "增强 · 8 GB", 64 * MiB, 8 * MiB, 16, 128, 256),
        new ResourceProfile("16gb", "大型 · 16 GB 以上", 128 * MiB, 16 * MiB, 32, 256, 512)
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
    public int Limit { get { lock (_sync) return _limit; } }
    public int Active { get { lock (_sync) return _active; } }
    public int Available { get { lock (_sync) return Math.Max(0, _limit - _active); } }
    public void Resize(int value) { lock (_sync) _limit = Math.Max(1, value); }
    public IDisposable? TryAcquire()
    {
        lock (_sync)
        {
            if (_active >= _limit) return null;
            _active++;
            return new Permit(this);
        }
    }
    private sealed class Permit(AdjustableConcurrencyLimit owner) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._sync) owner._active--;
        }
    }
}

public sealed record ResourceSelection(string? ProfileId);
public sealed record ResourceSettingsView(string? ProfileId, IReadOnlyList<ResourceProfile> Profiles);
public sealed record ResourceUsage(string? ProfileId, long CacheBudgetBytes, int DeliveryLimit,
    int SmtpLimit, int ImapLimit, int ActiveDeliveries, int ActiveSmtp, int ActiveImap, bool ApplyFailed);
public sealed record ResourceProcessView(string ProcessId, string InstanceName, string[] Roles,
    DateTimeOffset LastSeenAt, long EstimatedCacheBytes, ResourceUsage? Usage, string State, string? Message);
public sealed record ResourceStatusView(long TotalCacheBudgetBytes, IReadOnlyList<ResourceProcessView> Processes);

public sealed class ResourceRuntime(string role, IConfiguration configuration)
{
    private readonly object _sync = new();
    private string? _profileId;
    private long _cacheBudget;
    private bool _applyFailed;
    public string Role { get; } = role;
    public bool UsesCache => Role is "smtp" or "imap" or "worker" or "all";
    public AdjustableConcurrencyLimit Smtp { get; } = new(configuration.GetValue("Smtp:MaximumConnections", 200));
    public AdjustableConcurrencyLimit Imap { get; } = new(configuration.GetValue("Imap:MaximumConnections", 200));
    public AdjustableConcurrencyLimit Delivery { get; } = new(8);
    public ResourceUsage Snapshot()
    {
        lock (_sync) return new(_profileId, _cacheBudget, Delivery.Limit, Smtp.Limit, Imap.Limit,
            Delivery.Active, Smtp.Active, Imap.Active, _applyFailed);
    }
    public void MarkFailed() { lock (_sync) _applyFailed = true; }
    public void Apply(string? profileId, ConfigurationSnapshotCache cache)
    {
        lock (_sync)
        {
            if (profileId is not null)
            {
                var profile = ResourceProfiles.Get(profileId);
                var budget = Role switch { "all" => profile.CacheBytes, "smtp" => profile.CacheBytes / 2,
                    "imap" or "worker" => profile.CacheBytes / 4, _ => 0 };
                cache.Resize(budget, Math.Min(profile.EntryBytes, budget));
                Smtp.Resize(Math.Min(profile.SmtpConnections, configuration.GetValue<int?>("Smtp:MaximumConnections") ?? int.MaxValue));
                Imap.Resize(Math.Min(profile.ImapConnections, configuration.GetValue<int?>("Imap:MaximumConnections") ?? int.MaxValue));
                Delivery.Resize(profile.DeliveryConcurrency);
            }
            _cacheBudget = UsesCache && cache.Options.Enabled ? cache.Options.SizeLimitBytes : 0;
            _profileId = profileId;
            _applyFailed = false;
        }
    }
}

public sealed class ResourceSettingsService(IDbContextFactory<MailDbContext> factory,
    ConfigurationSnapshotCache cache)
{
    public const string Key = "resources:profile";
    public async Task<ResourceSettingsView> ReadAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var id = await db.SystemSettings.AsNoTracking().Where(x => x.Key == Key).Select(x => x.Value).SingleOrDefaultAsync(token);
        if (id is not null) ResourceProfiles.Get(id);
        return new(id, ResourceProfiles.All);
    }
    public async Task<ResourceSettingsView> SaveAsync(string profileId, string actor, CancellationToken token)
    {
        ResourceProfiles.Get(profileId);
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        // Lock the fixed version row before reading settings to serialize concurrent saves.
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Settings, token);
        var setting = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == Key, token);
        if (setting is null) db.SystemSettings.Add(new SystemSetting { Key = Key, Value = profileId });
        else setting.Value = profileId;
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "settings.resources.update",
            Resource = "settings:resources", Detail = profileId });
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        cache.ObserveCommittedVersion(ConfigurationScopes.Settings, version);
        return new(profileId, ResourceProfiles.All);
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
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await db.ConfigurationProcessStatuses.AsNoTracking().OrderBy(x => x.InstanceName).ToListAsync(token);
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(-90);
        var processes = rows.Where(x => x.Roles.Split(',').Any(r => r is "smtp" or "imap" or "worker" or "all"))
            .Select(x =>
            {
                ResourceUsage? usage = null;
                try { if (x.ResourceStatusJson is not null) usage = JsonSerializer.Deserialize<ResourceUsage>(x.ResourceStatusJson); }
                catch (JsonException) { }
                var state = x.LastSeenAt < cutoff || usage is null ? "unreported"
                    : usage.ApplyFailed ? "failed" : usage.ProfileId != desired.ProfileId ? "applying" : "applied";
                return new ResourceProcessView(x.ProcessId, x.InstanceName, x.Roles.Split(','), x.LastSeenAt,
                    x.EstimatedCacheBytes, usage, state, state == "failed" ? "暂时无法读取或应用设置，服务继续使用此前的配置。" : null);
            }).ToArray();
        return new(processes.Where(x => x.State != "unreported").Sum(x => x.Usage?.CacheBudgetBytes ?? 0), processes);
    }
}
