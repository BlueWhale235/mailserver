using System.Net;
using System.Text.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

public sealed record LoginProtection(int WindowMinutes = 15, int MaximumFailures = 5, int BlockMinutes = 30)
{
    public void Validate()
    {
        if (WindowMinutes is < 1 or > 1440 || MaximumFailures is < 1 or > 1000 || BlockMinutes is < 1 or > 10080)
            throw new ArgumentException("登录防护参数超出允许范围。");
    }
}

public sealed record ConnectionQuota(int PerMinute, int Burst, int MaximumConcurrent)
{
    public void Validate()
    {
        if (PerMinute is < 1 or > 100000 || Burst is < 1 or > 100000 || MaximumConcurrent is < 1 or > 10000)
            throw new ArgumentException("连接限制参数超出允许范围。");
    }
}

public sealed record ConnectionProtectionSettings(
    bool Enabled,
    LoginProtection SmtpLogin,
    LoginProtection ImapLogin,
    ConnectionQuota SmtpInbound,
    ConnectionQuota SmtpSubmission,
    ConnectionQuota Imap,
    int MaximumTrackedSources,
    int TlsHandshakeSeconds,
    int PreAuthenticationSeconds)
{
    public static ConnectionProtectionSettings Default { get; } = new(true,
        new(), new(), new(120, 30, 16), new(60, 20, 10), new(60, 20, 20), 4096, 10, 120);

    public void Validate()
    {
        if (SmtpLogin is null || ImapLogin is null || SmtpInbound is null || SmtpSubmission is null || Imap is null)
            throw new ArgumentException("连接防护设置不完整。");
        SmtpLogin.Validate(); ImapLogin.Validate();
        SmtpInbound.Validate(); SmtpSubmission.Validate(); Imap.Validate();
        if (MaximumTrackedSources is < 128 or > 65536 || TlsHandshakeSeconds is < 1 or > 120
            || PreAuthenticationSeconds is < 1 or > 3600)
            throw new ArgumentException("记录容量或连接超时超出允许范围。");
    }
}

public enum ProtectedConnectionGroup { SmtpInbound, SmtpSubmission, Imap }
public enum ProtectedLoginProtocol { Smtp, Imap }

// One lock covers capacity, admission and release: no concurrent caller can bypass an IP quota.
public sealed class ConnectionProtection(IConfiguration configuration, ILogger<ConnectionProtection>? logger = null)
{
    private sealed class SourceState
    {
        public DateTimeOffset LastSeen;
        public readonly QuotaState[] Quotas = [new(), new(), new()];
        public readonly LoginState[] Logins = [new(), new()];
        public bool Active => Quotas.Any(x => x.Active > 0);
        public bool Protected(DateTimeOffset now) => Logins.Any(x => x.BlockedUntil > now || x.WindowUntil > now);
    }
    private sealed class QuotaState { public double Tokens = -1; public DateTimeOffset RefilledAt; public int Active; }
    private sealed class LoginState { public int Failures; public DateTimeOffset WindowUntil; public DateTimeOffset BlockedUntil; }
    private readonly object _sync = new();
    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.Ordinal);
    private ConnectionProtectionSettings _settings = Initial(configuration);
    private DateTimeOffset _nextCleanup;
    private DateTimeOffset _nextPressureCleanup;
    private DateTimeOffset _nextRejectionReport;
    private long _rejectionsSinceReport;
    public ConnectionProtectionSettings Settings { get { lock (_sync) return _settings; } }
    public int TrackedSources { get { lock (_sync) return _sources.Count; } }

    public static string Normalize(IPAddress address) =>
        (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    public bool IsAuthBlocked(ProtectedLoginProtocol protocol, IPAddress address, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_settings.Enabled || !_sources.TryGetValue(Normalize(address), out var state)) return false;
            var blocked = state.Logins[(int)protocol].BlockedUntil > now;
            if (blocked) NoteRejection(now);
            return blocked;
        }
    }

    public bool RecordAuthFailure(ProtectedLoginProtocol protocol, IPAddress address, DateTimeOffset now)
    {
        lock (_sync)
        {
            if (!_settings.Enabled) return false;
            var state = GetOrCreate(Normalize(address), now);
            if (state is null) { NoteRejection(now); return true; } // At capacity, fail closed.
            var login = state.Logins[(int)protocol];
            if (login.BlockedUntil > now) return true;
            var policy = protocol == ProtectedLoginProtocol.Smtp ? _settings.SmtpLogin : _settings.ImapLogin;
            if (login.WindowUntil <= now)
            {
                login.Failures = 0;
                login.WindowUntil = now.AddMinutes(policy.WindowMinutes);
            }
            login.Failures++;
            state.LastSeen = now;
            if (login.Failures < policy.MaximumFailures) return false;
            login.BlockedUntil = now.AddMinutes(policy.BlockMinutes);
            return true;
        }
    }

    public bool TryAcquire(ProtectedConnectionGroup group, IPAddress address, DateTimeOffset now, out IDisposable? permit)
    {
        permit = null;
        lock (_sync)
        {
            if (!_settings.Enabled) { permit = NoopPermit.Instance; return true; }
            var state = GetOrCreate(Normalize(address), now);
            if (state is null) { NoteRejection(now); return false; }
            var policy = group switch
            {
                ProtectedConnectionGroup.SmtpInbound => _settings.SmtpInbound,
                ProtectedConnectionGroup.SmtpSubmission => _settings.SmtpSubmission,
                _ => _settings.Imap
            };
            var quota = state.Quotas[(int)group];
            if (quota.Tokens < 0) quota.Tokens = policy.Burst;
            else quota.Tokens = Math.Min(policy.Burst, quota.Tokens +
                Math.Max(0, (now - quota.RefilledAt).TotalMinutes) * policy.PerMinute);
            quota.RefilledAt = now;
            state.LastSeen = now;
            if (quota.Tokens < 1 || quota.Active >= policy.MaximumConcurrent)
            {
                NoteRejection(now);
                return false;
            }
            quota.Tokens -= 1;
            quota.Active++;
            permit = new Permit(this, state, group);
            return true;
        }
    }

    public void Apply(ConnectionProtectionSettings settings)
    {
        settings.Validate();
        lock (_sync)
        {
            if (!settings.Enabled) _sources.Clear();
            _settings = settings;
            Cleanup(DateTimeOffset.UtcNow, force: true, forAdmission: false);
        }
    }

    private SourceState? GetOrCreate(string key, DateTimeOffset now)
    {
        if (_sources.TryGetValue(key, out var existing)) return existing;
        if (_sources.Count >= _settings.MaximumTrackedSources)
        {
            // Source churn must not turn every rejected connection into a full dictionary scan.
            if (now < _nextPressureCleanup) return null;
            _nextPressureCleanup = now.AddSeconds(1);
            Cleanup(now, force: true, forAdmission: true);
        }
        else Cleanup(now, force: false, forAdmission: true);
        if (_sources.Count >= _settings.MaximumTrackedSources) return null;
        var created = new SourceState { LastSeen = now };
        _sources.Add(key, created);
        return created;
    }

    private void Cleanup(DateTimeOffset now, bool force, bool forAdmission)
    {
        if (!force && now < _nextCleanup) return;
        _nextCleanup = now.AddSeconds(30);
        foreach (var key in _sources.Where(x => !x.Value.Active && !x.Value.Protected(now)
                     && now - x.Value.LastSeen > TimeSpan.FromMinutes(2)).Select(x => x.Key).ToArray())
            _sources.Remove(key);
        var excess = _sources.Count - _settings.MaximumTrackedSources + (forAdmission ? 1 : 0);
        if (excess <= 0) return;
        foreach (var item in _sources.Where(x => !x.Value.Active && !x.Value.Protected(now))
                     .OrderBy(x => x.Value.LastSeen).Take(excess).ToArray())
            _sources.Remove(item.Key);
    }

    // The log volume is independent of the number of refused connections.
    private void NoteRejection(DateTimeOffset now)
    {
        _rejectionsSinceReport++;
        if (now < _nextRejectionReport) return;
        logger?.LogWarning("Connection protection rejected {Count} attempts since the previous report.",
            _rejectionsSinceReport);
        _rejectionsSinceReport = 0;
        _nextRejectionReport = now.AddSeconds(30);
    }

    private sealed class Permit(ConnectionProtection owner, SourceState state, ProtectedConnectionGroup group) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync) state.Quotas[(int)group].Active--;
        }
    }
    private sealed class NoopPermit : IDisposable
    {
        public static readonly NoopPermit Instance = new();
        public void Dispose() { }
    }

    internal static ConnectionProtectionSettings Initial(IConfiguration configuration)
    {
        var legacy = new LoginProtection(
            configuration.GetValue("SmtpAbuse:AuthenticationWindowMinutes", 15),
            configuration.GetValue("SmtpAbuse:MaximumAuthenticationFailures", 5),
            configuration.GetValue("SmtpAbuse:AuthenticationBlockMinutes", 30));
        return ConnectionProtectionSettings.Default with { SmtpLogin = legacy };
    }
}

public sealed class ConnectionProtectionSettingsService(IDbContextFactory<MailDbContext> factory,
    ConfigurationSnapshotCache cache, IConfiguration configuration,
    ResourceRuntime? runtime = null, ConnectionProtection? protection = null)
{
    public const string Key = "security:connection-protection";
    public async Task<ConnectionProtectionSettings> ReadAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var json = await db.SystemSettings.AsNoTracking().Where(x => x.Key == Key)
            .Select(x => x.Value).SingleOrDefaultAsync(token);
        if (json is null) return ConnectionProtection.Initial(configuration);
        var result = JsonSerializer.Deserialize<ConnectionProtectionSettings>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new FormatException("连接防护设置无效。");
        result.Validate();
        return result;
    }

    public async Task<ConnectionProtectionSettings> SaveAsync(ConnectionProtectionSettings settings, string actor, CancellationToken token)
    {
        settings.Validate();
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
        var version = await ConfigurationVersionService.BumpAsync(db, ConfigurationScopes.Settings, token);
        var row = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == Key, token);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (row is null) db.SystemSettings.Add(new SystemSetting { Key = Key, Value = json });
        else row.Value = json;
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "security.connection-protection.update",
            Resource = "security:connection-protection", Detail = "updated" });
        await db.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        cache.ObserveCommittedVersion(ConfigurationScopes.Settings, version);
        return settings;
    }

    public async Task<ConnectionProtectionStatus> StatusAsync(CancellationToken token)
    {
        var desired = await ReadAsync(token);
        if (runtime is null || protection is null || runtime.Role is not ("smtp" or "imap" or "all"))
            return new ConnectionProtectionStatus([]);
        var state = runtime.Snapshot().ApplyFailed ? "error"
            : protection.Settings == desired ? "applied" : "applying";
        var roles = runtime.Role == "all" ? "smtp,imap" : runtime.Role;
        return new ConnectionProtectionStatus([
            new ConnectionProtectionProcessStatus(Environment.MachineName, roles, DateTimeOffset.UtcNow,
                state, protection.TrackedSources)
        ]);
    }
}

public sealed record ConnectionProtectionProcessStatus(string InstanceName, string Roles,
    DateTimeOffset LastSeenAt, string State, int TrackedSources);
public sealed record ConnectionProtectionStatus(IReadOnlyList<ConnectionProtectionProcessStatus> Processes);
