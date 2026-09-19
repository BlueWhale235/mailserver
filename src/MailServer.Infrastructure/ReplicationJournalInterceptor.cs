using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed class ReplicationJournalInterceptor(
    ClusterRuntimeState runtime,
    IOptionsMonitor<ClusterOptions> clusterOptions) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Append(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Append(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Append(DbContext? context)
    {
        if (!runtime.Enabled
            || clusterOptions.CurrentValue.Role != ClusterNodeRole.Primary
            || context is not MailDbContext db
            || ReplicationApplyScope.IsActive) return;
        var changes = db.ChangeTracker.Entries()
            .Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
                        && ReplicationModelDescriptor.IsReplicated(x.Entity.GetType()))
            .ToArray();
        var batchId = Guid.NewGuid();
        foreach (var entry in changes)
        {
            var primaryKey = entry.Metadata.FindPrimaryKey();
            if (primaryKey is null) continue;
            var key = string.Join('|', primaryKey.Properties.Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? string.Empty));
            var operation = entry.State == EntityState.Deleted ? ReplicationOperation.Delete : ReplicationOperation.Upsert;
            var payload = operation == ReplicationOperation.Delete ? SerializeKey(entry, primaryKey.Properties.Select(x => x.Name)) : SerializeScalars(entry);
            db.ReplicationLogs.Add(new ReplicationLog
            {
                BatchId = batchId,
                EntityType = entry.Metadata.ClrType.FullName ?? entry.Metadata.ClrType.Name,
                EntityKey = key,
                Operation = operation,
                PayloadJson = payload,
                ContentKey = entry.Entity is StoredMessage message ? message.ContentKey : null
            });
        }
    }

    private static string SerializeKey(EntityEntry entry, IEnumerable<string> propertyNames) =>
        JsonSerializer.Serialize(propertyNames.ToDictionary(name => name, name => entry.Property(name).CurrentValue));

    private static string SerializeScalars(EntityEntry entry)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in entry.Properties)
        {
            if (property.Metadata.IsShadowProperty()) continue;
            values[property.Metadata.Name] = property.CurrentValue;
        }
        return JsonSerializer.Serialize(values);
    }
}

public static class ReplicationModelDescriptor
{
    private static readonly HashSet<Type> Excluded =
    [
        typeof(ReplicationLog), typeof(ReplicationCursor), typeof(ClusterLease),
        typeof(ClusterNode), typeof(ClusterConfiguration), typeof(ClusterEvent), typeof(ClusterCertificateAuthority),
        typeof(ClusterNodeCertificate), typeof(ClusterCertificateRevocation),
        typeof(ClusterPluginPackage), typeof(ClusterPluginPackageFile),
        typeof(TaskLease), typeof(TaskExecution)
    ];

    public static bool IsReplicated(Type entityType) => !Excluded.Contains(entityType);

    public static IReadOnlyList<IEntityType> GetReplicatedEntityTypes(IModel model) =>
        model.GetEntityTypes()
            .Where(entity => IsReplicated(entity.ClrType) && entity.FindPrimaryKey() is not null)
            .OrderBy(entity => entity.ClrType.FullName, StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyList<IEntityType> GetSnapshotEntityTypes(IModel model)
    {
        var remaining = GetReplicatedEntityTypes(model).ToList();
        var result = new List<IEntityType>(remaining.Count);
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(entity => entity.GetForeignKeys().All(foreignKey =>
                    foreignKey.PrincipalEntityType == entity
                    || !remaining.Contains(foreignKey.PrincipalEntityType)))
                .OrderBy(entity => entity.ClrType.FullName, StringComparer.Ordinal)
                .ToArray();
            // Self-referencing or mutually dependent optional relationships are added
            // deterministically; their scalar foreign keys can be fixed by later rows.
            if (ready.Length == 0) ready = [remaining.OrderBy(x => x.ClrType.FullName, StringComparer.Ordinal).First()];
            foreach (var entity in ready)
            {
                remaining.Remove(entity);
                result.Add(entity);
            }
        }
        return result;
    }

    public static async Task<IReadOnlyList<string>> FindPopulatedEntityTypesAsync(
        MailDbContext db,
        CancellationToken cancellationToken)
    {
        var populated = new List<string>();
        var probe = typeof(ReplicationModelDescriptor).GetMethod(
            nameof(HasRowsAsync),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(HasRowsAsync));
        foreach (var entity in GetReplicatedEntityTypes(db.Model))
        {
            if (entity.ClrType == typeof(ScheduledTask)) continue;
            var task = (Task<bool>)(probe.MakeGenericMethod(entity.ClrType)
                .Invoke(null, [db, cancellationToken])
                ?? throw new InvalidOperationException($"Unable to inspect {entity.ClrType.Name}."));
            if (await task) populated.Add(entity.GetTableName() ?? entity.ClrType.Name);
        }
        return populated;
    }

    private static Task<bool> HasRowsAsync<TEntity>(MailDbContext db, CancellationToken cancellationToken)
        where TEntity : class =>
        db.Set<TEntity>().IgnoreQueryFilters().AnyAsync(cancellationToken);

    public static string ComputeFingerprint(IModel model)
    {
        var descriptions = GetReplicatedEntityTypes(model).Select(Describe);
        return ComputeFingerprint(descriptions);
    }

    internal static string ComputeFingerprint(IEnumerable<string> descriptions)
    {
        var description = string.Join('\n', descriptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(description))).ToLowerInvariant();
    }

    private static string Describe(IEntityType entity)
    {
        var primaryKey = entity.FindPrimaryKey();
        var keys = primaryKey is null
            ? string.Empty
            : string.Join(',', primaryKey.Properties.Select(property => property.Name));
        var properties = string.Join(',', entity.GetProperties()
            .Where(property => !property.IsShadowProperty())
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => $"{property.Name}:{property.ClrType.FullName}:{property.IsNullable}"));
        return $"{entity.ClrType.FullName}|pk={keys}|properties={properties}";
    }
}

public sealed class ReplicationSchemaFingerprintProvider(IDbContextFactory<MailDbContext> factory)
{
    private readonly Lazy<Task<string>> _fingerprint = new(async () =>
    {
        await using var db = await factory.CreateDbContextAsync();
        return ReplicationModelDescriptor.ComputeFingerprint(db.Model);
    });

    public async Task<string> GetAsync(CancellationToken cancellationToken) =>
        await _fingerprint.Value.WaitAsync(cancellationToken);
}

internal static class ReplicationApplyScope
{
    private static readonly AsyncLocal<int> Depth = new();
    public static bool IsActive => Depth.Value > 0;
    public static IDisposable Begin()
    {
        Depth.Value++;
        return new Scope();
    }
    private sealed class Scope : IDisposable
    {
        public void Dispose() => Depth.Value = Math.Max(0, Depth.Value - 1);
    }
}
