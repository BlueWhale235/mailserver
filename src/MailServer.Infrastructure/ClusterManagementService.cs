using System.Data;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed record CreateClusterRequest(string Name, string Endpoint);
public sealed record CreateClusterResult(Guid ClusterId, string Name, string PrimaryNodeId);
public sealed record CreateClusterInvitationRequest(ClusterNodeRole Role);
public sealed record RecoverPrimaryRequest(string ConfirmName, bool ConfirmNodesFenced);
public sealed record ClusterInvitationView(Guid Id, ClusterNodeRole Role, DateTimeOffset ExpiresAt, bool Used, bool Revoked, string? Token);
public sealed record ClusterJoinRequest(string Token, string NodeId, string Name, string Endpoint);
public sealed record ClusterJoinResult(Guid ClusterId, string ClusterName, ClusterNodeRole Role, string NodeId, string PrimaryEndpoint, string? WitnessEndpoint, long BaseSequence);
public sealed record ClusterCertificateJoinResult(ClusterJoinResult Result, string CaCertificatePem, string NodeCertificatePem);
public sealed record ClusterHeartbeatRequest(string NodeId, ClusterNodeStatus Status, long AppliedSequence, long SafeSequence, int InitializationProgress, string? LastError, string Version);
public sealed record ClusterTransportStatusView(bool Enabled, bool IsListening, string Status, int Port, string AdvertiseEndpoint, string? LastError, DateTimeOffset? StartedAt);
public sealed record ClusterConfigurationView(Guid Id, string Name, string PrimaryNodeId, DateTimeOffset CreatedAt);
public sealed record ClusterOverview(ClusterConfigurationView? Configuration, ClusterHealth Health, bool RecoveryRequired, ClusterTransportStatusView Transport, IReadOnlyList<ClusterNode> Nodes, IReadOnlyList<ReplicationCursor> Cursors, IReadOnlyList<ClusterEvent> Events, IReadOnlyList<ClusterSnapshot> Snapshots);

public sealed class ClusterRuntimeState(bool enabled, bool recoveryRequired = false)
{
    private volatile bool _enabled = enabled;
    private volatile bool _recoveryRequired = recoveryRequired;
    public bool Enabled { get => _enabled; set => _enabled = value; }
    public bool RecoveryRequired { get => _recoveryRequired; set => _recoveryRequired = value; }
}

public sealed class ClusterRuntimeBootstrap(
    ClusterRuntimeState state,
    IDbContextFactory<MailDbContext> factory,
    IOptionsMonitor<ClusterOptions> options,
    IClock clock,
    ILogger<ClusterRuntimeBootstrap> logger) : IHostedService
{
    private IDisposable? _subscription;
    private readonly CancellationTokenSource _stopping = new();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        state.Enabled |= await db.ClusterConfigurations.AnyAsync(cancellationToken);
        state.RecoveryRequired |= options.CurrentValue.RecoveryRequired;
        _subscription = options.OnChange(updated =>
        {
            if (!updated.Enabled)
                _ = DisableAfterClusterRowsAreRemovedAsync(_stopping.Token);
            else if (updated.RecoveryRequired)
                state.RecoveryRequired = true;
            else
                _ = ClearRecoveryAfterLeaseIsReadyAsync(updated.NodeId, _stopping.Token);
        });
    }

    private async Task ClearRecoveryAfterLeaseIsReadyAsync(string nodeId, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < 40 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                var configuration = await db.ClusterConfigurations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
                if (configuration is not null
                    && string.Equals(configuration.PrimaryNodeId, nodeId, StringComparison.Ordinal)
                    && !await db.ClusterNodes.AnyAsync(x => x.Id != nodeId && x.RevokedAt == null, cancellationToken)
                    && await db.ClusterLeases.AnyAsync(
                        x => x.Name == "primary" && x.OwnerNodeId == nodeId && x.ExpiresAt > clock.UtcNow,
                        cancellationToken)
                    && await db.AuditRecords.AnyAsync(
                        x => x.Action == "cluster.primary.auto-recover" || x.Action == "cluster.primary.force-recover",
                        cancellationToken))
                {
                    state.Enabled = true;
                    state.RecoveryRequired = false;
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to confirm an externally completed primary recovery; runtime remains read-only.");
        }
    }

    private async Task DisableAfterClusterRowsAreRemovedAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < 40 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                if (!await db.ClusterConfigurations.AnyAsync(cancellationToken))
                {
                    state.RecoveryRequired = false;
                    state.Enabled = false;
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to confirm that local cluster rows were removed after configuration reload; runtime remains in cluster mode.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        _subscription?.Dispose();
        _stopping.Dispose();
        return Task.CompletedTask;
    }
}

public sealed class ClusterManagementService(
    IDbContextFactory<MailDbContext> factory,
    IOptions<ClusterOptions> options,
    IClock clock,
    ClusterRuntimeState runtime,
    ClusterTransportRuntimeState transportRuntime,
    IOptionsMonitor<ClusterTransportOptions> transportOptions,
    ClusterCertificateService certificates,
    ReplicationSchemaFingerprintProvider schemaFingerprint,
    ClusterPluginPackageService pluginPackages,
    IClusterHealthService health)
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new() { WriteIndented = true };

    public async Task<CreateClusterResult> CreateAsync(CreateClusterRequest request, string actor, CancellationToken cancellationToken)
    {
        var name = RequireName(request.Name, "Cluster name");
        var endpoint = RequireClusterEndpoint(request.Endpoint);
        var ca = await certificates.EnsureAuthorityAsync(actor, cancellationToken);
        var packageSet = await pluginPackages.ScanAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (await db.ClusterConfigurations.AnyAsync(cancellationToken)) throw new InvalidOperationException("Cluster already exists.");
        var nodeId = string.IsNullOrWhiteSpace(options.Value.NodeId) ? Environment.MachineName : options.Value.NodeId;
        var configuration = new ClusterConfiguration
        {
            Name = name, PrimaryNodeId = nodeId, CreatedBy = actor,
            ClusterCaThumbprint = ca.Thumbprint,
            CaCertificatePem = ca.CertificatePem,
            ProtectedCaPrivateKeyPem = ca.ProtectedPrivateKeyPem
        };
        db.ClusterConfigurations.Add(configuration);
        db.ClusterNodes.Add(new ClusterNode
        {
            Id = nodeId, Name = options.Value.NodeName, Endpoint = endpoint, Role = ClusterNodeRole.Primary,
            Status = ClusterNodeStatus.Healthy, Version = typeof(ClusterManagementService).Assembly.GetName().Version?.ToString() ?? "unknown",
            LastHeartbeatAt = clock.UtcNow, InitializationProgress = 100,
            ProtocolVersion = ClusterBinaryProtocol.ProtocolVersion,
            PluginPackageSetHash = packageSet.SetHash
        });
        db.ClusterLeases.Add(new ClusterLease { Name = "primary", OwnerNodeId = nodeId, Term = 1, ExpiresAt = clock.UtcNow.AddSeconds(Math.Clamp(options.Value.LeaseSeconds, 5, 300)) });
        db.ClusterEvents.Add(new ClusterEvent { Type = "cluster-created", NodeId = nodeId, Detail = $"{name}; ca={ca.Thumbprint}" });
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "cluster.create", Resource = $"cluster:{configuration.Id}", Category = AuditCategory.Node, Status = AuditStatus.Succeeded });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await EnsureLocalPrimaryTransportAsync(nodeId, options.Value.NodeName, endpoint, ca.CertificatePem, actor, cancellationToken);
        runtime.Enabled = true;
        runtime.RecoveryRequired = false;
        transportRuntime.RequestStart();
        return new CreateClusterResult(configuration.Id, configuration.Name, nodeId);
    }

    public async Task RepairLocalPrimaryTransportAsync(string actor, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var configuration = await db.ClusterConfigurations.AsNoTracking().SingleAsync(cancellationToken);
        var primary = await db.ClusterNodes.AsNoTracking().SingleAsync(x => x.Id == configuration.PrimaryNodeId, cancellationToken);
        var ca = await certificates.EnsureAuthorityAsync(actor, cancellationToken);
        await EnsureLocalPrimaryTransportAsync(primary.Id, primary.Name, primary.Endpoint, configuration.CaCertificatePem ?? ca.CertificatePem, actor, cancellationToken);
        runtime.Enabled = true;
        transportRuntime.RequestStart();
    }

    public async Task<ClusterInvitationView> CreateInvitationAsync(ClusterNodeRole role, string actor, CancellationToken cancellationToken)
    {
        if (role is not (ClusterNodeRole.Standby or ClusterNodeRole.Witness)) throw new ArgumentException("Invitation role must be Standby or Witness.");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.ClusterConfigurations.AnyAsync(cancellationToken)) throw new InvalidOperationException("Create the cluster first.");
        if (await db.ClusterNodes.AnyAsync(x => x.Role == role && x.RevokedAt == null, cancellationToken))
            throw new InvalidOperationException($"An active {role} node already exists.");
        var invitation = new ClusterJoinInvitation
        {
            TokenHash = Hash(token), Role = role,
            ExpiresAt = clock.UtcNow.AddMinutes(15), CreatedBy = actor
        };
        db.ClusterJoinInvitations.Add(invitation);
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "cluster.invitation.create", Resource = $"cluster-invitation:{invitation.Id}", Detail = role.ToString(), Category = AuditCategory.Node, Status = AuditStatus.Succeeded });
        await db.SaveChangesAsync(cancellationToken);
        return new ClusterInvitationView(invitation.Id, role, invitation.ExpiresAt, false, false, token);
    }

    public async Task<IReadOnlyList<ClusterInvitationView>> ListInvitationsAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.ClusterJoinInvitations.AsNoTracking().OrderByDescending(x => x.CreatedAt)
            .Select(x => new ClusterInvitationView(x.Id, x.Role, x.ExpiresAt, x.UsedAt != null, x.RevokedAt != null, null))
            .ToListAsync(cancellationToken);
    }

    public async Task RevokeInvitationAsync(Guid id, string actor, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var invitation = await db.ClusterJoinInvitations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new KeyNotFoundException("Invitation not found.");
        invitation.RevokedAt = clock.UtcNow;
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "cluster.invitation.revoke", Resource = $"cluster-invitation:{id}", Category = AuditCategory.Node, Status = AuditStatus.Succeeded });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ClusterJoinResult> JoinAsync(ClusterJoinRequest request, CancellationToken cancellationToken)
    {
        using var key = RSA.Create(2048);
        var csr = new CertificateRequest($"CN={request.NodeId}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSigningRequestPem();
        var joined = await JoinWithCertificateAsync(new ClusterJoinWireRequest(
            request.Token,
            request.NodeId,
            request.Name,
            request.Endpoint,
            csr,
            await schemaFingerprint.GetAsync(cancellationToken)), cancellationToken);
        return joined.Result;
    }

    public async Task<ClusterCertificateJoinResult> JoinWithCertificateAsync(ClusterJoinWireRequest request, CancellationToken cancellationToken)
    {
        var localSchemaFingerprint = await schemaFingerprint.GetAsync(cancellationToken);
        if (!string.Equals(request.ReplicationSchemaFingerprint, localSchemaFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The joining node database/replication schema is incompatible. Install the same MailServer version and run 'mailserver db update' on every node before joining.");
        var nodeId = RequireName(request.NodeId, "Node ID");
        var nodeName = RequireName(request.Name, "Node name");
        var endpoint = RequireClusterEndpoint(request.Endpoint);
        if (string.IsNullOrWhiteSpace(request.CertificateSigningRequestPem))
            throw new ArgumentException("Certificate signing request is required.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var tokenHash = Hash(request.Token);
        var invitation = await db.ClusterJoinInvitations.SingleOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken)
            ?? throw new UnauthorizedAccessException("Invalid invitation token.");
        if (invitation.UsedAt is not null || invitation.RevokedAt is not null || invitation.ExpiresAt <= clock.UtcNow)
            throw new UnauthorizedAccessException("Invitation token is expired or already used.");
        if (await db.ClusterNodes.AnyAsync(x => x.Id == nodeId && x.RevokedAt == null, cancellationToken)) throw new InvalidOperationException("Node ID already exists.");
        if (await db.ClusterNodes.AnyAsync(x => x.Role == invitation.Role && x.RevokedAt == null, cancellationToken)) throw new InvalidOperationException($"An active {invitation.Role} node already exists.");
        var configuration = await db.ClusterConfigurations.SingleAsync(cancellationToken);
        var primary = await db.ClusterNodes.AsNoTracking().SingleAsync(x => x.Id == configuration.PrimaryNodeId, cancellationToken);
        var witnessEndpoint = await db.ClusterNodes.AsNoTracking()
            .Where(x => x.Role == ClusterNodeRole.Witness && x.RevokedAt == null)
            .Select(x => x.Endpoint)
            .SingleOrDefaultAsync(cancellationToken);
        var baseSequence = await db.ReplicationLogs.MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? 0;
        db.ClusterNodes.Add(new ClusterNode
        {
            Id = nodeId, Name = nodeName, Endpoint = endpoint, Role = invitation.Role,
            Status = ClusterNodeStatus.Synchronizing,
            Version = "joining", LastHeartbeatAt = clock.UtcNow,
            InitializationProgress = 0,
            ProtocolVersion = ClusterBinaryProtocol.ProtocolVersion
        });
        invitation.UsedAt = clock.UtcNow;
        if (invitation.Role == ClusterNodeRole.Standby)
            db.ClusterSnapshots.Add(new ClusterSnapshot { NodeId = nodeId, BaseSequence = baseSequence, Status = ClusterSnapshotStatus.Pending });
        db.ClusterEvents.Add(new ClusterEvent { Type = "node-joined", NodeId = nodeId, Detail = invitation.Role.ToString() });
        db.AuditRecords.Add(new AuditRecord { Actor = $"node:{nodeId}", Action = "cluster.node.join", Resource = $"cluster-node:{nodeId}", Category = AuditCategory.Node, Status = AuditStatus.Succeeded });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var certificate = await certificates.SignNodeCertificateAsync(
            nodeId,
            nodeName,
            endpoint,
            request.CertificateSigningRequestPem,
            $"node:{nodeId}",
            cancellationToken);
        var result = new ClusterJoinResult(
            configuration.Id,
            configuration.Name,
            invitation.Role,
            nodeId,
            primary.Endpoint,
            witnessEndpoint,
            baseSequence);
        return new ClusterCertificateJoinResult(result, configuration.CaCertificatePem ?? string.Empty, certificate.CertificatePem);
    }

    public async Task HeartbeatAsync(ClusterHeartbeatRequest request, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await db.ClusterNodes.SingleOrDefaultAsync(x => x.Id == request.NodeId && x.RevokedAt == null, cancellationToken) ?? throw new KeyNotFoundException("Node not found.");
        node.Status = request.Status;
        node.AppliedSequence = Math.Max(node.AppliedSequence, request.AppliedSequence);
        node.SafeSequence = Math.Max(node.SafeSequence, request.SafeSequence);
        node.InitializationProgress = Math.Clamp(request.InitializationProgress, 0, 100);
        node.LastError = request.LastError;
        node.Version = request.Version;
        node.LastHeartbeatAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> ResolveNodeIdAsync(string idOrName, CancellationToken cancellationToken)
    {
        var value = idOrName?.Trim();
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Node ID or name is required.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var nodes = await db.ClusterNodes.AsNoTracking()
            .Where(x => x.RevokedAt == null)
            .OrderBy(x => x.Role)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var byId = nodes.SingleOrDefault(x => string.Equals(x.Id, value, StringComparison.Ordinal));
        if (byId is not null) return byId.Id;
        var byName = nodes.Where(x => string.Equals(x.Name, value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byName.Length == 1) return byName[0].Id;
        var candidates = nodes.Count == 0
            ? "No active nodes are registered."
            : "Active nodes: " + string.Join(", ", nodes.Select(x => $"{x.Name} ({x.Id}, {x.Role})"));
        if (byName.Length > 1)
            throw new InvalidOperationException($"Multiple active nodes are named '{value}'. Use a full node ID. {candidates}");
        throw new KeyNotFoundException($"Node '{value}' was not found. {candidates}");
    }

    public async Task RemoveNodeAsync(string nodeId, string actor, CancellationToken cancellationToken, bool forceRequested = false)
    {
        var writable = (await health.GetAsync(cancellationToken)).Writable;
        var force = forceRequested || !writable;
        string? configPath = null;
        string? originalConfig = null;
        JsonObject? root = null;
        if (force)
        {
            configPath = ResolveConfigurationPath();
            originalConfig = await File.ReadAllTextAsync(configPath, cancellationToken);
            root = JsonNode.Parse(originalConfig) as JsonObject
                ?? throw new InvalidDataException("Configuration root must be a JSON object.");
        }

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var configuration = await db.ClusterConfigurations.SingleAsync(cancellationToken);
        if (!string.Equals(configuration.PrimaryNodeId, options.Value.NodeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Nodes can only be removed from the configured primary node.");
        if (configuration.PrimaryNodeId == nodeId) throw new InvalidOperationException("Transfer primary ownership before removing this node.");
        var node = await db.ClusterNodes.SingleOrDefaultAsync(x => x.Id == nodeId && x.RevokedAt == null, cancellationToken) ?? throw new KeyNotFoundException("Node not found.");
        foreach (var certificate in await db.ClusterNodeCertificates.Where(x => x.NodeId == nodeId && x.RevokedAt == null).ToListAsync(cancellationToken))
        {
            certificate.Status = ClusterCertificateStatus.Revoked;
            certificate.RevokedAt = clock.UtcNow;
            db.ClusterCertificateRevocations.Add(new ClusterCertificateRevocation { NodeId = nodeId, Thumbprint = certificate.Thumbprint, Reason = "node removed", RevokedAt = clock.UtcNow });
        }
        db.ReplicationCursors.RemoveRange(await db.ReplicationCursors.Where(x => x.NodeId == nodeId).ToListAsync(cancellationToken));
        db.ClusterSnapshots.RemoveRange(await db.ClusterSnapshots.Where(x => x.NodeId == nodeId).ToListAsync(cancellationToken));
        db.ClusterNodes.Remove(node);
        db.ClusterEvents.Add(new ClusterEvent { Type = force ? "node-force-removed" : "node-removed", NodeId = nodeId, Detail = actor });
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor,
            Action = force ? "cluster.node.force-remove" : "cluster.node.remove",
            Resource = $"cluster-node:{nodeId}",
            Detail = force ? "Remote node must be fenced before primary recovery." : null,
            Category = AuditCategory.Node,
            Status = force ? AuditStatus.Warning : AuditStatus.Succeeded
        });
        await db.SaveChangesAsync(cancellationToken);

        var autoRecovered = force && !await db.ClusterNodes.AnyAsync(
            x => x.Id != configuration.PrimaryNodeId && x.RevokedAt == null,
            cancellationToken);
        if (autoRecovered)
        {
            await RenewLocalPrimaryLeaseAsync(db, configuration, cancellationToken);
            db.ClusterEvents.Add(new ClusterEvent { Type = "primary-auto-recovered", NodeId = options.Value.NodeId, Detail = actor });
            db.AuditRecords.Add(new AuditRecord
            {
                Actor = actor,
                Action = "cluster.primary.auto-recover",
                Resource = $"cluster:{configuration.Id}",
                Detail = "The final child node was force-removed; local primary writes were restored.",
                Category = AuditCategory.Node,
                Status = AuditStatus.Warning
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        var configUpdated = false;
        try
        {
            if (force)
            {
                SetRecoveryConfiguration(root!, required: !autoRecovered, clearWitness: autoRecovered);
                await WriteConfigurationAtomicallyAsync(configPath!, root!.ToJsonString(ConfigJsonOptions), cancellationToken);
                configUpdated = true;
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (configUpdated)
                await WriteConfigurationAtomicallyAsync(configPath!, originalConfig!, CancellationToken.None);
            throw;
        }
        if (force) runtime.RecoveryRequired = !autoRecovered;
    }

    public async Task RecoverPrimaryAsync(RecoverPrimaryRequest request, string actor, CancellationToken cancellationToken)
    {
        if (!request.ConfirmNodesFenced)
            throw new InvalidOperationException("Confirm that every remote cluster node has been powered off, disconnected, or otherwise fenced.");

        var configPath = ResolveConfigurationPath();
        var originalConfig = await File.ReadAllTextAsync(configPath, cancellationToken);
        var root = JsonNode.Parse(originalConfig) as JsonObject
            ?? throw new InvalidDataException("Configuration root must be a JSON object.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var configuration = await db.ClusterConfigurations.SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No cluster exists.");
        if (!string.Equals(configuration.Name, request.ConfirmName?.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("The confirmation name does not match the cluster name.");
        if (!string.Equals(configuration.PrimaryNodeId, options.Value.NodeId, StringComparison.Ordinal))
            throw new InvalidOperationException("Primary recovery can only be run on the configured primary node.");
        if (await db.ClusterNodes.AnyAsync(x => x.Id != configuration.PrimaryNodeId && x.RevokedAt == null, cancellationToken))
            throw new InvalidOperationException("Force-remove every standby and witness node before recovering primary writes.");

        await RenewLocalPrimaryLeaseAsync(db, configuration, cancellationToken);
        db.ClusterEvents.Add(new ClusterEvent { Type = "primary-force-recovered", NodeId = options.Value.NodeId, Detail = actor });
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor,
            Action = "cluster.primary.force-recover",
            Resource = $"cluster:{configuration.Id}",
            Detail = "All remote nodes confirmed fenced.",
            Category = AuditCategory.Node,
            Status = AuditStatus.Warning
        });
        await db.SaveChangesAsync(cancellationToken);

        SetRecoveryConfiguration(root, required: false, clearWitness: true);
        var configUpdated = false;
        try
        {
            await WriteConfigurationAtomicallyAsync(configPath, root.ToJsonString(ConfigJsonOptions), cancellationToken);
            configUpdated = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (configUpdated)
                await WriteConfigurationAtomicallyAsync(configPath, originalConfig, CancellationToken.None);
            throw;
        }
        runtime.RecoveryRequired = false;
    }

    private async Task RenewLocalPrimaryLeaseAsync(
        MailDbContext db,
        ClusterConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var lease = await db.ClusterLeases.SingleOrDefaultAsync(x => x.Name == "primary", cancellationToken);
        if (lease is null)
        {
            lease = new ClusterLease { Name = "primary", OwnerNodeId = options.Value.NodeId, Term = 1 };
            db.ClusterLeases.Add(lease);
        }
        else
        {
            lease.Term++;
        }
        lease.OwnerNodeId = options.Value.NodeId;
        lease.ExpiresAt = clock.UtcNow.AddSeconds(Math.Clamp(options.Value.LeaseSeconds, 5, 300));
        configuration.PrimaryNodeId = options.Value.NodeId;
    }

    public async Task DeleteClusterAsync(string confirmName, string actor, CancellationToken cancellationToken)
        => await LeaveClusterAsync(confirmName, actor, "cluster.delete", cancellationToken);

    public async Task ForceLeaveAsync(string confirmName, string actor, CancellationToken cancellationToken)
        => await LeaveClusterAsync(confirmName, actor, "cluster.force-leave", cancellationToken);

    private async Task LeaveClusterAsync(
        string confirmName,
        string actor,
        string auditAction,
        CancellationToken cancellationToken)
    {
        var configPath = ResolveConfigurationPath();
        var originalConfig = await File.ReadAllTextAsync(configPath, cancellationToken);
        var root = JsonNode.Parse(originalConfig) as JsonObject
            ?? throw new InvalidDataException("Configuration root must be a JSON object.");
        var securityFiles = ResolveClusterSecurityFiles(root, configPath);
        var securityBackups = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in securityFiles)
            if (File.Exists(path)) securityBackups[path] = await File.ReadAllBytesAsync(path, cancellationToken);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var configuration = await db.ClusterConfigurations.SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No cluster exists.");
        if (!string.Equals(configuration.Name, confirmName?.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("The confirmation name does not match the cluster name.");

        var clusterId = configuration.Id;
        using (ReplicationApplyScope.Begin())
        {
            await DeleteClusterRowsAsync(db, cancellationToken);
            db.AuditRecords.Add(new AuditRecord
            {
                Actor = actor,
                Action = auditAction,
                Resource = $"cluster:{clusterId}",
                Detail = $"{confirmName}; local node left the cluster without contacting remote nodes",
                Category = AuditCategory.Node,
                Status = AuditStatus.Warning
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        DisableClusterConfiguration(root);
        var configUpdated = false;
        try
        {
            await WriteConfigurationAtomicallyAsync(configPath, root.ToJsonString(ConfigJsonOptions), cancellationToken);
            configUpdated = true;
            foreach (var path in securityBackups.Keys) File.Delete(path);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (configUpdated)
                await WriteConfigurationAtomicallyAsync(configPath, originalConfig, CancellationToken.None);
            foreach (var (path, bytes) in securityBackups)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);
            }
            throw;
        }

        runtime.Enabled = false;
        runtime.RecoveryRequired = false;
        transportRuntime.RequestStop();
    }

    public async Task RequestResynchronizationAsync(string nodeId, string actor, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await db.ClusterNodes.SingleOrDefaultAsync(x => x.Id == nodeId && x.Role == ClusterNodeRole.Standby && x.RevokedAt == null, cancellationToken) ?? throw new KeyNotFoundException("Standby node not found.");
        node.Status = ClusterNodeStatus.Synchronizing;
        node.InitializationProgress = 0;
        node.LastError = null;
        var cursor = await db.ReplicationCursors.SingleOrDefaultAsync(x => x.NodeId == nodeId, cancellationToken);
        if (cursor is not null) { cursor.LastAppliedSequence = 0; cursor.LastError = "Full resynchronization requested."; }
        var baseSequence = await db.ReplicationLogs.MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? 0;
        db.ClusterSnapshots.Add(new ClusterSnapshot { NodeId = nodeId, BaseSequence = baseSequence, Status = ClusterSnapshotStatus.Pending });
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "cluster.resync.request", Resource = $"cluster-node:{nodeId}", Category = AuditCategory.Node, Status = AuditStatus.Warning });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ClusterOverview> GetOverviewAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return new ClusterOverview(
            await db.ClusterConfigurations.AsNoTracking()
                .Select(x => new ClusterConfigurationView(x.Id, x.Name, x.PrimaryNodeId, x.CreatedAt))
                .SingleOrDefaultAsync(cancellationToken),
            await health.GetAsync(cancellationToken),
            runtime.RecoveryRequired,
            new ClusterTransportStatusView(
                transportOptions.CurrentValue.Enabled,
                transportRuntime.IsListening,
                transportRuntime.Status,
                transportOptions.CurrentValue.Port,
                transportOptions.CurrentValue.AdvertiseEndpoint,
                transportRuntime.LastError,
                transportRuntime.StartedAt),
            await db.ClusterNodes.AsNoTracking().Where(x => x.RevokedAt == null).OrderBy(x => x.Role).ToListAsync(cancellationToken),
            await db.ReplicationCursors.AsNoTracking().OrderBy(x => x.NodeId).ToListAsync(cancellationToken),
            await db.ClusterEvents.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(cancellationToken),
            await db.ClusterSnapshots.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(cancellationToken));
    }

    private async Task EnsureLocalPrimaryTransportAsync(
        string nodeId,
        string nodeName,
        string endpoint,
        string caCertificatePem,
        string actor,
        CancellationToken cancellationToken)
    {
        var configPath = ResolveConfigurationPath();
        var configDirectory = Path.GetDirectoryName(configPath)!;
        var clusterDirectory = Path.Combine(configDirectory, "security-assets", "cluster");
        Directory.CreateDirectory(clusterDirectory);
        var caPath = Path.Combine(clusterDirectory, "ca.crt");
        var certificatePath = Path.Combine(clusterDirectory, "node.crt");
        var privateKeyPath = Path.Combine(clusterDirectory, "node.key");

        using var key = RSA.Create(2048);
        var csr = new CertificateRequest($"CN={nodeId}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSigningRequestPem();
        var certificate = await certificates.SignNodeCertificateAsync(nodeId, nodeName, endpoint, csr, actor, cancellationToken);
        await File.WriteAllTextAsync(caPath, caCertificatePem, cancellationToken);
        await File.WriteAllTextAsync(certificatePath, certificate.CertificatePem, cancellationToken);
        await File.WriteAllTextAsync(privateKeyPath, key.ExportPkcs8PrivateKeyPem(), cancellationToken);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken)) as JsonObject
            ?? throw new InvalidDataException("Configuration root must be a JSON object.");
        var cluster = root[ClusterOptions.SectionName] as JsonObject ?? new JsonObject();
        root[ClusterOptions.SectionName] = cluster;
        cluster["Enabled"] = true;
        cluster["RecoveryRequired"] = false;
        cluster["NodeId"] = nodeId;
        cluster["NodeName"] = nodeName;
        cluster["Role"] = ClusterNodeRole.Primary.ToString();
        cluster["Endpoint"] = endpoint;
        cluster["PrimaryEndpoint"] = endpoint;

        var transport = root[ClusterTransportOptions.SectionName] as JsonObject ?? new JsonObject();
        root[ClusterTransportOptions.SectionName] = transport;
        transport["Enabled"] = true;
        transport["AdvertiseEndpoint"] = endpoint;
        transport["CertificatePath"] = certificatePath;
        transport["PrivateKeyPath"] = privateKeyPath;
        transport["TrustedCaPath"] = caPath;
        if (transport["Port"] is null)
        {
            transport["Port"] = transportOptions.CurrentValue.Port;
        }
        if (transport["ListenAddress"] is null)
        {
            transport["ListenAddress"] = transportOptions.CurrentValue.ListenAddress;
        }
        await File.WriteAllTextAsync(configPath, root.ToJsonString(ConfigJsonOptions), cancellationToken);
    }

    private static string ResolveConfigurationPath()
    {
        var configured = Environment.GetEnvironmentVariable(PortableConfiguration.EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var fallback = Path.Combine(Environment.CurrentDirectory, "mailserver.json");
        if (File.Exists(fallback)) return Path.GetFullPath(fallback);
        throw new InvalidOperationException("Cannot update cluster transport settings because mailserver.json was not found. Start MailServer with MAILSERVER_CONFIG or from the configuration directory.");
    }

    private static IReadOnlyList<string> ResolveClusterSecurityFiles(JsonObject root, string configPath)
    {
        var directory = Path.GetDirectoryName(configPath)!;
        var transport = root[ClusterTransportOptions.SectionName] as JsonObject;
        var configured = new[]
        {
            transport?["TrustedCaPath"]?.GetValue<string>(),
            transport?["CertificatePath"]?.GetValue<string>(),
            transport?["PrivateKeyPath"]?.GetValue<string>()
        };
        return configured.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.IsPathRooted(path!) ? Path.GetFullPath(path!) : Path.GetFullPath(path!, directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void DisableClusterConfiguration(JsonObject root)
    {
        var cluster = root[ClusterOptions.SectionName] as JsonObject ?? new JsonObject();
        root[ClusterOptions.SectionName] = cluster;
        cluster["Enabled"] = false;
        cluster["RecoveryRequired"] = false;
        cluster["NodeId"] = string.Empty;
        cluster["NodeName"] = string.Empty;
        cluster["Role"] = ClusterNodeRole.Primary.ToString();
        cluster["Endpoint"] = string.Empty;
        cluster["PrimaryEndpoint"] = string.Empty;
        cluster["WitnessEndpoint"] = string.Empty;

        var transport = root[ClusterTransportOptions.SectionName] as JsonObject ?? new JsonObject();
        root[ClusterTransportOptions.SectionName] = transport;
        transport["Enabled"] = false;
        transport["AdvertiseEndpoint"] = string.Empty;
    }

    private static void SetRecoveryConfiguration(JsonObject root, bool required, bool clearWitness)
    {
        var cluster = root[ClusterOptions.SectionName] as JsonObject ?? new JsonObject();
        root[ClusterOptions.SectionName] = cluster;
        cluster["RecoveryRequired"] = required;
        if (clearWitness) cluster["WitnessEndpoint"] = string.Empty;
    }

    private static async Task WriteConfigurationAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task DeleteClusterRowsAsync(MailDbContext db, CancellationToken cancellationToken)
    {
        await DeleteAllAsync(db, db.ClusterJoinInvitations, cancellationToken);
        await DeleteAllAsync(db, db.ClusterNodeCertificates, cancellationToken);
        await DeleteAllAsync(db, db.ClusterCertificateRevocations, cancellationToken);
        await DeleteAllAsync(db, db.ClusterPluginPackageFiles, cancellationToken);
        await DeleteAllAsync(db, db.ClusterPluginPackages, cancellationToken);
        await DeleteAllAsync(db, db.ClusterSnapshots, cancellationToken);
        await DeleteAllAsync(db, db.ReplicationCursors, cancellationToken);
        await DeleteAllAsync(db, db.ClusterLeases, cancellationToken);
        await DeleteAllAsync(db, db.ClusterEvents, cancellationToken);
        await DeleteAllAsync(db, db.ReplicationLogs, cancellationToken);
        await DeleteAllAsync(db, db.ClusterNodes, cancellationToken);
        await DeleteAllAsync(db, db.ClusterCertificateAuthorities, cancellationToken);
        await DeleteAllAsync(db, db.ClusterConfigurations, cancellationToken);
    }

    private static async Task DeleteAllAsync<TEntity>(MailDbContext db, DbSet<TEntity> set, CancellationToken cancellationToken)
        where TEntity : class
    {
        if (db.Database.IsRelational())
            await set.ExecuteDeleteAsync(cancellationToken);
        else
            set.RemoveRange(await set.ToListAsync(cancellationToken));
    }

    private static string RequireName(string value, string field)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 128) throw new ArgumentException($"{field} is required and must not exceed 128 characters.");
        return normalized;
    }

    private static string RequireClusterEndpoint(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "tls") throw new ArgumentException("Cluster endpoint must be an absolute tls:// URL.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
