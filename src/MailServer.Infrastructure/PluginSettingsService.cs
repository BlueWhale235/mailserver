using System.Text.Json;
using MailServer.Domain;
using MailServer.PluginSdk;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

public sealed record PluginListItem(
    string Id,
    string DisplayName,
    string Description,
    string Version,
    bool System,
    bool Enabled,
    bool IsHostActive,
    string? LoadError,
    int Order,
    int EffectiveOrder);

public sealed record PluginSettingValue(string Key, JsonElement? Value, bool HasSecret);

public sealed record PluginConfigurationView(
    PluginListItem Plugin,
    IReadOnlyList<PluginSettingDefinition> Definitions,
    IReadOnlyList<PluginSettingValue> Values);

public sealed class PluginSettingsService(
    IDbContextFactory<MailDbContext> dbFactory,
    PluginCatalog catalog,
    IDataProtectionProvider protectionProvider) : IPluginSettingsProvider
{
    private const string Prefix = "plugin:";
    private const string OrderKey = Prefix + "order";
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("MailServer.PluginSettings.v1");

    public async Task<PluginSettingsSnapshot> GetAsync(string pluginId, CancellationToken cancellationToken)
    {
        var plugin = Find(pluginId);
        var stored = await ReadStoredAsync(pluginId, cancellationToken);
        var values = Defaults(plugin.Manifest);
        foreach (var pair in stored.Values) values[pair.Key] = pair.Value;
        foreach (var pair in stored.Secrets)
        {
            try { values[pair.Key] = JsonSerializer.SerializeToElement(_protector.Unprotect(pair.Value)); }
            catch { values.Remove(pair.Key); }
        }
        return new PluginSettingsSnapshot(stored.Enabled ?? plugin.Manifest.DefaultEnabled, values);
    }

    public async Task<IReadOnlyList<PluginListItem>> ListAsync(CancellationToken cancellationToken)
    {
        var orders = await ReadOrderOverridesAsync(cancellationToken);
        var result = new List<PluginListItem>();
        foreach (var plugin in catalog.Plugins.OrderBy(x => EffectiveOrder(x.Manifest, orders)).ThenBy(x => x.Manifest.Id))
        {
            var snapshot = await GetAsync(plugin.Manifest.Id, cancellationToken);
            result.Add(ToListItem(plugin, snapshot.Enabled, orders));
        }
        return result;
    }

    public async Task<IReadOnlyList<PluginManifest>> GetOrderedManifestsAsync(CancellationToken cancellationToken)
    {
        var orders = await ReadOrderOverridesAsync(cancellationToken);
        return catalog.Plugins
            .Select(x => x.Manifest)
            .OrderBy(x => EffectiveOrder(x, orders))
            .ThenBy(x => x.Id)
            .ToArray();
    }

    public async Task SaveOrderAsync(IReadOnlyList<string> pluginIds, CancellationToken cancellationToken, string actor = "admin")
    {
        var known = catalog.Plugins.Select(x => x.Manifest.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (pluginIds.Count != pluginIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            || pluginIds.Any(id => !known.Contains(id)))
            throw new FormatException("Plugin order contains an unknown or duplicate plugin.");

        var orders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < pluginIds.Count; index++)
            orders[pluginIds[index]] = (index + 1) * 10;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == OrderKey, cancellationToken);
        var json = JsonSerializer.Serialize(orders);
        if (setting is null) db.SystemSettings.Add(new SystemSetting { Key = OrderKey, Value = json });
        else setting.Value = json;
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor,
            Action = "plugin.order.update",
            Resource = "plugins:order",
            Detail = string.Join(",", pluginIds),
            Category = AuditCategory.Plugin,
            Status = AuditStatus.Succeeded,
            RawJson = AuditLogService.SafeJson(new { pluginIds })
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PluginConfigurationView> GetConfigurationAsync(
        string pluginId,
        CancellationToken cancellationToken)
    {
        var plugin = Find(pluginId);
        var snapshot = await GetAsync(pluginId, cancellationToken);
        var orders = await ReadOrderOverridesAsync(cancellationToken);
        var values = plugin.Manifest.Settings.Select(definition =>
            new PluginSettingValue(
                definition.Key,
                definition.Type == PluginSettingType.Password
                    ? null
                    : snapshot.Values.TryGetValue(definition.Key, out var value) ? value : null,
                definition.Type == PluginSettingType.Password && snapshot.Values.ContainsKey(definition.Key)))
            .ToArray();
        return new PluginConfigurationView(
            ToListItem(plugin, snapshot.Enabled, orders),
            plugin.Manifest.Settings,
            values);
    }

    public async Task<PluginConfigurationView> SaveAsync(
        string pluginId,
        bool enabled,
        IReadOnlyDictionary<string, JsonElement> input,
        IReadOnlySet<string> clearedSecrets,
        CancellationToken cancellationToken,
        string actor = "admin")
    {
        var plugin = Find(pluginId);
        var definitions = plugin.Manifest.Settings.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        if (input.Keys.Any(key => !definitions.ContainsKey(key)))
            throw new FormatException("Plugin configuration contains an unknown setting.");
        var stored = await ReadStoredAsync(pluginId, cancellationToken);
        var previous = await GetAsync(pluginId, cancellationToken);
        var changes = new List<AuditFieldChangeView>();
        if (previous.Enabled != enabled)
            changes.Add(new AuditFieldChangeView("enabled", previous.Enabled.ToString(), enabled.ToString()));
        stored.Enabled = enabled;
        foreach (var definition in definitions.Values)
        {
            if (definition.Type == PluginSettingType.Password)
            {
                var hadSecret = stored.Secrets.ContainsKey(definition.Key);
                if (clearedSecrets.Contains(definition.Key))
                {
                    stored.Secrets.Remove(definition.Key);
                    if (hadSecret) changes.Add(new AuditFieldChangeView(definition.Key, "***", null));
                }
                if (input.TryGetValue(definition.Key, out var secret)
                    && secret.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(secret.GetString()))
                {
                    stored.Secrets[definition.Key] = _protector.Protect(secret.GetString()!);
                    changes.Add(new AuditFieldChangeView(definition.Key, hadSecret ? "***" : null, "***"));
                }
                continue;
            }
            if (!input.TryGetValue(definition.Key, out var value)) continue;
            ValidateValue(definition, value);
            if (!previous.Values.TryGetValue(definition.Key, out var oldValue)
                || oldValue.GetRawText() != value.GetRawText())
            {
                changes.Add(new AuditFieldChangeView(definition.Key, oldValue.GetRawText(), value.GetRawText()));
            }
            stored.Values[definition.Key] = value.Clone();
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var key = Key(pluginId);
        var setting = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        var json = JsonSerializer.Serialize(stored);
        if (setting is null) db.SystemSettings.Add(new SystemSetting { Key = key, Value = json });
        else setting.Value = json;
        db.AuditRecords.Add(new AuditRecord
        {
            Actor = actor,
            Action = "plugin.settings.update",
            Resource = $"plugin:{plugin.Manifest.Id}",
            Detail = plugin.Manifest.DisplayName ?? plugin.Manifest.Id,
            Category = AuditCategory.Plugin,
            Status = AuditStatus.Succeeded,
            PluginId = plugin.Manifest.Id,
            PluginName = plugin.Manifest.DisplayName ?? plugin.Manifest.Id,
            ChangeSummaryJson = JsonSerializer.Serialize(changes),
            RawJson = AuditLogService.SafeJson(new
            {
                pluginId = plugin.Manifest.Id,
                pluginName = plugin.Manifest.DisplayName ?? plugin.Manifest.Id,
                enabled,
                changedFields = changes.Select(x => x.Field).ToArray()
            })
        });
        await db.SaveChangesAsync(cancellationToken);
        return await GetConfigurationAsync(pluginId, cancellationToken);
    }

    private static void ValidateValue(PluginSettingDefinition definition, JsonElement value)
    {
        var valid = definition.Type switch
        {
            PluginSettingType.Text => value.ValueKind == JsonValueKind.String,
            PluginSettingType.Integer => value.TryGetInt32(out _),
            PluginSettingType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            PluginSettingType.Select => value.ValueKind == JsonValueKind.String
                && definition.Options.Any(x => x.Value == value.GetString()),
            PluginSettingType.Multiselect => value.ValueKind == JsonValueKind.Array
                && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String
                    && definition.Options.Any(x => x.Value == item.GetString())),
            _ => false
        };
        if (!valid) throw new FormatException($"Plugin setting {definition.Key} has an invalid value.");
        if (definition.Type == PluginSettingType.Integer)
        {
            var number = value.GetInt32();
            if (definition.Minimum is { } minimum && number < minimum
                || definition.Maximum is { } maximum && number > maximum)
                throw new FormatException($"Plugin setting {definition.Key} is outside its allowed range.");
        }
        if (definition.Required && definition.Type == PluginSettingType.Text
                                && string.IsNullOrWhiteSpace(value.GetString()))
            throw new FormatException($"Plugin setting {definition.Key} is required.");
    }

    private async Task<StoredPluginSettings> ReadStoredAsync(string pluginId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var json = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == Key(pluginId))
            .Select(x => x.Value)
            .SingleOrDefaultAsync(cancellationToken);
        return json is null
            ? new StoredPluginSettings()
            : JsonSerializer.Deserialize<StoredPluginSettings>(json) ?? new StoredPluginSettings();
    }

    private LoadedPlugin Find(string pluginId) =>
        catalog.Plugins.SingleOrDefault(x => x.Manifest.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Plugin {pluginId} was not found.");

    private static Dictionary<string, JsonElement> Defaults(PluginManifest manifest) =>
        manifest.Settings
            .Where(x => x.DefaultValue.ValueKind != JsonValueKind.Undefined)
            .ToDictionary(x => x.Key, x => x.DefaultValue.Clone(), StringComparer.OrdinalIgnoreCase);

    private async Task<Dictionary<string, int>> ReadOrderOverridesAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var json = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == OrderKey)
            .Select(x => x.Value)
            .SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(json)
                   ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static int EffectiveOrder(PluginManifest manifest, IReadOnlyDictionary<string, int> orders) =>
        orders.TryGetValue(manifest.Id, out var order) ? order : manifest.Order;

    private static PluginListItem ToListItem(LoadedPlugin plugin, bool enabled, IReadOnlyDictionary<string, int> orders) =>
        new(
            plugin.Manifest.Id,
            plugin.Manifest.DisplayName ?? plugin.Manifest.Id,
            plugin.Manifest.Description ?? "无可配置描述",
            plugin.Manifest.Version,
            plugin.Manifest.System,
            enabled,
            plugin.IsHostActive,
            plugin.LoadError,
            plugin.Manifest.Order,
            EffectiveOrder(plugin.Manifest, orders));

    private static string Key(string id) => $"{Prefix}{id}:settings";

    private sealed class StoredPluginSettings
    {
        public bool? Enabled { get; set; }
        public Dictionary<string, JsonElement> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
