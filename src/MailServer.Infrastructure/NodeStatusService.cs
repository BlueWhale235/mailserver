using System.Diagnostics;
using System.Text.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

public sealed class NodeOptions
{
    public bool Enabled { get; set; }
    public string Id { get; set; } = Environment.MachineName;
    public int HeartbeatSeconds { get; set; } = 15;
    public int StaleSeconds { get; set; } = 60;
    public string? ResourceProfile { get; set; }
    public long? CacheBytes { get; set; }
    public int? DeliveryConcurrency { get; set; }
    public int? SmtpConnections { get; set; }
    public int? ImapConnections { get; set; }

    public static NodeOptions Read(IConfiguration config)
    {
        var value = config.GetSection("Node").Get<NodeOptions>() ?? new();
        if (value.Enabled && config.GetValue<bool>("AdaptiveResources:Enabled"))
            throw new InvalidOperationException("Node resource limits cannot be combined with AdaptiveResources experiments.");
        if (value.Id.Length is < 1 or > 64 || value.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            || value.HeartbeatSeconds is < 5 or > 300 || value.StaleSeconds < value.HeartbeatSeconds * 2
            || value.StaleSeconds > 3600 || value.CacheBytes is <= 0 || value.DeliveryConcurrency is <= 0
            || value.SmtpConnections is <= 0 || value.ImapConnections is <= 0)
            throw new InvalidOperationException("Invalid Node identity, heartbeat or resource limits.");
        if (value.ResourceProfile is not null) ResourceProfiles.Get(value.ResourceProfile);
        return value;
    }
}

public sealed record NodeRuntimeSnapshot(ResourceUsage Usage, long WorkingSetBytes, long ManagedHeapBytes,
    int ProcessId, string StorageProvider, string ResourceSource);
public sealed record NodeStatusView(string NodeId, string InstanceId, string[] Roles, DateTimeOffset LastSeenAt,
    string State, NodeRuntimeSnapshot? Runtime, long EstimatedCacheBytes);

public sealed class NodeStatusService(IDbContextFactory<MailDbContext> factory, ResourceRuntime runtime,
    ConfigurationSnapshotCache cache, MessageFetchMetadataCache fetchCache, PerformanceCollector diagnostics,
    IConfiguration configuration, ILogger<NodeStatusService> logger) : BackgroundService
{
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly string _instanceId = $"{runtime.Node.Id}:{runtime.Role}:{Guid.NewGuid():N}";
    public bool Enabled => runtime.Node.Enabled;

    internal async Task PublishAsync(CancellationToken token)
    {
        if (!Enabled) return;
        using var suppression = PerformanceTelemetry.Suppress();
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.ConfigurationProcessStatuses.FindAsync([_instanceId], token);
        if (row is null)
        {
            row = new ConfigurationProcessStatus { ProcessId = _instanceId, InstanceName = runtime.Node.Id,
                Roles = runtime.Role == "all" ? "api,smtp,imap,worker" : runtime.Role, StartedAt = _started };
            db.ConfigurationProcessStatuses.Add(row);
        }
        using var process = Process.GetCurrentProcess();
        row.LastSeenAt = DateTimeOffset.UtcNow;
        row.ResourceStatusJson = JsonSerializer.Serialize(new NodeRuntimeSnapshot(runtime.Snapshot(), process.WorkingSet64,
            GC.GetTotalMemory(false), Environment.ProcessId, configuration["Storage:Provider"] ?? "File", "node"));
        row.EstimatedCacheBytes = cache.EstimatedBytes + fetchCache.EstimatedBytes + diagnostics.EstimatedBufferBytes;
        var versions = cache.Versions;
        row.RulesVersion = versions.Rules; row.SettingsVersion = versions.Settings;
        row.FirewallVersion = versions.Firewall; row.RoutingVersion = versions.Routing;
        row.EntryCount = cache.EntryCount; row.Hits = cache.Hits; row.Misses = cache.Misses;
        row.ReloadFailures = cache.ReloadFailures;
        await db.SaveChangesAsync(token);
    }

    public async Task<IReadOnlyList<NodeStatusView>> ListAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var rows = await db.ConfigurationProcessStatuses.AsNoTracking().OrderByDescending(x => x.LastSeenAt).Take(1000).ToListAsync(token);
        return rows.Select(row =>
        {
            NodeRuntimeSnapshot? snapshot = null;
            try { if (row.ResourceStatusJson is not null) snapshot = JsonSerializer.Deserialize<NodeRuntimeSnapshot>(row.ResourceStatusJson); }
            catch (JsonException) { }
            if (snapshot?.Usage is null) snapshot = null;
            var state = row.LastSeenAt < DateTimeOffset.UtcNow.AddSeconds(-runtime.Node.StaleSeconds) ? "offline"
                : snapshot?.Usage.ApplyFailed == true ? "failed" : snapshot is null ? "unknown" : "online";
            return new NodeStatusView(row.InstanceName, row.ProcessId, row.Roles.Split(','), row.LastSeenAt, state, snapshot, row.EstimatedCacheBytes);
        }).ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await PublishAsync(timeout.Token);
                if (runtime.Role is "api" or "all" && DateTimeOffset.UtcNow >= nextCleanup)
                {
                    await using var db = await factory.CreateDbContextAsync(timeout.Token);
                    var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
                    await db.ConfigurationProcessStatuses.Where(x => x.LastSeenAt < cutoff).ExecuteDeleteAsync(timeout.Token);
                    nextCleanup = DateTimeOffset.UtcNow.AddHours(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning(error, "Node heartbeat unavailable; mail processing continues."); }
            try { await Task.Delay(TimeSpan.FromSeconds(runtime.Node.HeartbeatSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
