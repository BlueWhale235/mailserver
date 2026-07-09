using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailServer.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MailServer.Infrastructure;

public sealed record LoadedPlugin(
    PluginManifest Manifest,
    Assembly? Assembly,
    bool IsHostActive,
    string? LoadError = null);

public sealed class PluginCatalog
{
    private readonly List<LoadedPlugin> _plugins = [];
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    internal void Add(LoadedPlugin plugin) => _plugins.Add(plugin);
    internal PluginManifest? FindManifest(Assembly assembly) =>
        _plugins.FirstOrDefault(x => x.Assembly == assembly)?.Manifest;
}

public static class PluginServiceCollectionExtensions
{
    public static IServiceCollection AddMailServerPlugins(
        this IServiceCollection services,
        IConfiguration configuration,
        string hostName)
    {
        var path = configuration[$"{PluginOptions.SectionName}:Path"] ?? "plugins";
        var catalog = new PluginCatalog();
        var roots = new[] { path }
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .ToArray();
        if (roots.Length == 0)
        {
            services.AddSingleton(catalog);
            services.TryAddSingleton<IPluginSettingsProvider, ManifestDefaultPluginSettingsProvider>();
            services.AddSingleton<PluginPipeline>();
            return services;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestPath in roots.SelectMany(root =>
                     Directory.EnumerateFiles(root, "plugin.json", SearchOption.AllDirectories))
                 .Where(IsInstalledPluginManifest))
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() }
                })
                ?? throw new InvalidDataException($"Invalid plugin manifest: {manifestPath}");
            ValidateManifest(manifest, manifestPath);
            if (!seenIds.Add(manifest.Id))
                throw new InvalidDataException($"Duplicate plugin id {manifest.Id}.");
            if (!string.Equals(manifest.ContractVersion, PluginContract.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Plugin {manifest.Id} requires contract {manifest.ContractVersion}; host supports {PluginContract.Version}.");
            }

            var assemblyPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.EntryAssembly));
            if (!hostName.Equals("all", StringComparison.OrdinalIgnoreCase)
                && manifest.Hosts.Length > 0
                && !manifest.Hosts.Contains(hostName, StringComparer.OrdinalIgnoreCase))
            {
                catalog.Add(new LoadedPlugin(
                    manifest,
                    null,
                    false,
                    File.Exists(assemblyPath) ? null : $"Entry assembly was not found: {manifest.EntryAssembly}"));
                continue;
            }

            try
            {
                if (!File.Exists(assemblyPath))
                    throw new FileNotFoundException($"Entry assembly was not found: {manifest.EntryAssembly}", assemblyPath);
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                                   !candidate.IsDynamic
                                   && string.Equals(candidate.Location, assemblyPath, StringComparison.OrdinalIgnoreCase))
                               ?? new PluginLoadContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);
                var pluginType = assembly.GetTypes()
                    .SingleOrDefault(type => !type.IsAbstract && typeof(IMailServerPlugin).IsAssignableFrom(type))
                    ?? throw new InvalidOperationException($"Plugin {manifest.Id} has no IMailServerPlugin implementation.");
                var plugin = (IMailServerPlugin?)Activator.CreateInstance(pluginType)
                    ?? throw new InvalidOperationException($"Plugin {manifest.Id} could not be created.");
                plugin.ConfigureServices(services);
                catalog.Add(new LoadedPlugin(manifest, assembly, true));
            }
            catch (Exception exception)
            {
                catalog.Add(new LoadedPlugin(manifest, null, true, exception.Message));
                if (string.Equals(manifest.FailureMode, "reject", StringComparison.OrdinalIgnoreCase)) throw;
            }
        }

        services.AddSingleton(catalog);
        services.TryAddSingleton<IPluginSettingsProvider, ManifestDefaultPluginSettingsProvider>();
        services.AddSingleton<PluginPipeline>();
        return services;
    }

    private static bool IsInstalledPluginManifest(string manifestPath)
    {
        var directory = Path.GetDirectoryName(manifestPath)!;
        return !Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any();
    }

    private static void ValidateManifest(PluginManifest manifest, string path)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Length > 128)
            throw new InvalidDataException($"Plugin id is invalid: {path}");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in manifest.Settings)
        {
            if (string.IsNullOrWhiteSpace(setting.Key) || !keys.Add(setting.Key))
                throw new InvalidDataException($"Plugin {manifest.Id} contains an invalid or duplicate setting key.");
            if (setting.Type is PluginSettingType.Select or PluginSettingType.Multiselect
                && setting.Options.Length == 0)
                throw new InvalidDataException($"Plugin {manifest.Id} setting {setting.Key} requires options.");
            if (setting.Minimum > setting.Maximum)
                throw new InvalidDataException($"Plugin {manifest.Id} setting {setting.Key} has an invalid range.");
        }
    }
}

internal sealed class ManifestDefaultPluginSettingsProvider(PluginCatalog catalog) : IPluginSettingsProvider
{
    public Task<PluginSettingsSnapshot> GetAsync(string pluginId, CancellationToken cancellationToken)
    {
        var manifest = catalog.Plugins.Single(x => x.Manifest.Id == pluginId).Manifest;
        var values = manifest.Settings
            .Where(x => x.DefaultValue.ValueKind != JsonValueKind.Undefined)
            .ToDictionary(x => x.Key, x => x.DefaultValue.Clone(), StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(new PluginSettingsSnapshot(manifest.DefaultEnabled, values));
    }
}

public sealed class PluginPipeline(
    IServiceProvider services,
    PluginCatalog catalog,
    IPluginSettingsProvider settingsProvider,
    ILogger<PluginPipeline> logger)
{
    public Task<PluginDecision> OnConnectionAsync(ConnectionContext context, CancellationToken cancellationToken) =>
        RunConfiguredAsync(
            services.GetServices<IConnectionMiddleware>(),
            (middleware, settings) => middleware.InvokeAsync(context with { Settings = settings }, cancellationToken),
            cancellationToken);

    public Task OnTlsEstablishedAsync(TlsEstablishedContext context, CancellationToken cancellationToken) =>
        NotifyConfiguredAsync(
            services.GetServices<ITlsEstablishedMiddleware>(),
            (middleware, settings) => middleware.InvokeAsync(context with { Settings = settings }, cancellationToken),
            "TLS established",
            cancellationToken);

    public async Task<IReadOnlyList<string>> GetAuthenticationMechanismsAsync(
        MailProtocol protocol,
        CancellationToken cancellationToken)
    {
        var mechanisms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var middleware in await Ordered(services.GetServices<IAuthenticationMiddleware>(), cancellationToken))
        {
            var manifest = catalog.FindManifest(middleware.GetType().Assembly);
            if (manifest is not null && !(await settingsProvider.GetAsync(manifest.Id, cancellationToken)).Enabled)
                continue;
            foreach (var mechanism in middleware.GetMechanisms(protocol))
                if (!string.IsNullOrWhiteSpace(mechanism)) mechanisms.Add(mechanism.ToUpperInvariant());
        }
        return mechanisms.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        AuthenticationContext context,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in await Ordered(services.GetServices<IAuthenticationMiddleware>(), cancellationToken))
        {
            if (!middleware.GetMechanisms(context.Protocol).Contains(context.Mechanism, StringComparer.OrdinalIgnoreCase)) continue;
            var manifest = catalog.FindManifest(middleware.GetType().Assembly);
            var settings = manifest is null
                ? new PluginSettingsSnapshot(true, new Dictionary<string, JsonElement>())
                : await settingsProvider.GetAsync(manifest.Id, cancellationToken);
            if (!settings.Enabled) continue;
            try
            {
                var invocation = middleware.AuthenticateAsync(context with { Settings = settings }, cancellationToken);
                var result = manifest is null
                    ? await invocation
                    : await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
                if (result.Status != AuthenticationStatus.NotHandled) return result;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Authentication plugin {Plugin} failed", middleware.GetType().FullName);
                if (string.Equals(manifest?.FailureMode, "reject", StringComparison.OrdinalIgnoreCase))
                    return AuthenticationResult.Failure("Authentication provider failed.");
            }
        }
        return AuthenticationResult.NotHandled();
    }

    public Task<PluginDecision> OnSmtpSessionAsync(SmtpSessionContext context, CancellationToken cancellationToken) =>
        RunAsync(Ordered(services.GetServices<ISmtpSessionMiddleware>(), cancellationToken), x => x.InvokeAsync(context, cancellationToken), cancellationToken);

    public Task<PluginDecision> OnSmtpSenderAsync(SmtpSenderContext context, CancellationToken cancellationToken) =>
        RunConfiguredAsync(
            services.GetServices<ISmtpSenderMiddleware>(),
            (middleware, settings) => middleware.InvokeAsync(context with { Settings = settings }, cancellationToken),
            cancellationToken);

    public Task<PluginDecision> OnSmtpRecipientAsync(SmtpRecipientContext context, CancellationToken cancellationToken) =>
        RunConfiguredAsync(
            services.GetServices<ISmtpRecipientMiddleware>(),
            (middleware, settings) => middleware.InvokeAsync(context with { Settings = settings }, cancellationToken),
            cancellationToken);

    public async Task<PluginDecision> OnMessageAsync(MessageAcceptanceContext context, CancellationToken cancellationToken)
    {
        foreach (var middleware in await Ordered(services.GetServices<IMessageAcceptanceMiddleware>(), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = catalog.FindManifest(middleware.GetType().Assembly);
            var settings = manifest is null
                ? new PluginSettingsSnapshot(true, new Dictionary<string, JsonElement>())
                : await settingsProvider.GetAsync(manifest.Id, cancellationToken);
            if (!settings.Enabled) continue;
            try
            {
                var invocation = middleware.InvokeAsync(context with { Settings = settings }, cancellationToken);
                var decision = manifest is null
                    ? await invocation
                    : await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
                if (decision.Kind != PluginDecisionKind.Continue) return decision;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Message acceptance plugin {Plugin} failed", middleware.GetType().FullName);
                if (string.Equals(manifest?.FailureMode, "reject", StringComparison.OrdinalIgnoreCase))
                    return new PluginDecision(PluginDecisionKind.Reject, "Message policy plugin failed.");
            }
        }

        return PluginDecision.Continue();
    }

    public async Task<PluginDecision> OnMessageContentAsync(
        string sender,
        IReadOnlyList<string> recipients,
        string bodyType,
        Stream content,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in await Ordered(services.GetServices<IMessageContentMiddleware>(), cancellationToken))
        {
            var manifest = catalog.FindManifest(middleware.GetType().Assembly);
            var settings = manifest is null
                ? new PluginSettingsSnapshot(true, new Dictionary<string, JsonElement>())
                : await settingsProvider.GetAsync(manifest.Id, cancellationToken);
            if (!settings.Enabled) continue;
            try
            {
                content.Position = 0;
                var invocation = middleware.InvokeAsync(
                    new MessageContentContext(sender, recipients, bodyType, content.Length, content, settings),
                    cancellationToken);
                var decision = manifest is null
                    ? await invocation
                    : await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
                content.Position = 0;
                if (decision.Kind != PluginDecisionKind.Continue) return decision;
            }
            catch (Exception exception)
            {
                content.Position = 0;
                logger.LogError(exception, "Message content plugin {Plugin} failed", middleware.GetType().FullName);
                if (string.Equals(manifest?.FailureMode, "reject", StringComparison.OrdinalIgnoreCase))
                    return new PluginDecision(PluginDecisionKind.Reject, "Message content validation failed.");
            }
        }
        return PluginDecision.Continue();
    }

    public Task<PluginDecision> OnDeliveryAsync(DeliveryContext context, CancellationToken cancellationToken) =>
        RunAsync(Ordered(services.GetServices<IDeliveryMiddleware>(), cancellationToken), x => x.InvokeAsync(context, cancellationToken), cancellationToken);

    public Task OnMessageAcceptedAsync(MessageAcceptedContext context, CancellationToken cancellationToken) =>
        NotifyConfiguredAsync(
            services.GetServices<IMessageAcceptedMiddleware>(),
            (middleware, settings) => middleware.InvokeAsync(context with { Settings = settings }, cancellationToken),
            "message accepted",
            cancellationToken);

    public Task OnDeliveryCompletedAsync(DeliveryCompletedContext context, CancellationToken cancellationToken) =>
        NotifyConfiguredAsync(
            services.GetServices<IDeliveryCompletedMiddleware>(),
            (middleware, settings) => middleware.InvokeAsync(context with { Settings = settings }, cancellationToken),
            "delivery completed",
            cancellationToken);

    public Task<PluginDecision> OnImapSessionAsync(ImapSessionContext context, CancellationToken cancellationToken) =>
        RunAsync(Ordered(services.GetServices<IImapSessionMiddleware>(), cancellationToken), x => x.InvokeAsync(context, cancellationToken), cancellationToken);

    public Task<PluginDecision> OnImapCommandAsync(ImapCommandContext context, CancellationToken cancellationToken) =>
        RunConfiguredAsync(
            services.GetServices<IImapCommandMiddleware>(),
            (middleware, settings) => middleware.InvokeAsync(context with { Settings = settings }, cancellationToken),
            cancellationToken);

    public async Task OnAuditAsync(AuditContext context, CancellationToken cancellationToken)
    {
        foreach (var middleware in await Ordered(services.GetServices<IAuditMiddleware>(), cancellationToken))
        {
            try
            {
                await middleware.InvokeAsync(context, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Audit plugin {Plugin} failed", middleware.GetType().FullName);
            }
        }
    }

    private async Task<PluginDecision> RunAsync<T>(
        Task<IReadOnlyList<T>> middlewares,
        Func<T, Task<PluginDecision>> invoke,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in await middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = middleware is null ? null : catalog.FindManifest(middleware.GetType().Assembly);
            try
            {
                if (manifest is not null
                    && !(await settingsProvider.GetAsync(manifest.Id, cancellationToken)).Enabled)
                    continue;
                var invocation = invoke(middleware);
                var decision = manifest is null
                    ? await invocation
                    : await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
                if (decision.Kind != PluginDecisionKind.Continue)
                {
                    return decision;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Plugin middleware {Plugin} failed", middleware?.GetType().FullName);
                if (string.Equals(manifest?.FailureMode, "reject", StringComparison.OrdinalIgnoreCase))
                {
                    return new PluginDecision(PluginDecisionKind.Reject, "Policy plugin failed.");
                }
            }
        }

        return PluginDecision.Continue();
    }

    private async Task<PluginDecision> RunConfiguredAsync<T>(
        IEnumerable<T> middlewares,
        Func<T, PluginSettingsSnapshot, Task<PluginDecision>> invoke,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in await Ordered(middlewares, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = middleware is null ? null : catalog.FindManifest(middleware.GetType().Assembly);
            var settings = manifest is null
                ? new PluginSettingsSnapshot(true, new Dictionary<string, JsonElement>())
                : await settingsProvider.GetAsync(manifest.Id, cancellationToken);
            if (!settings.Enabled) continue;
            try
            {
                var invocation = invoke(middleware, settings);
                var decision = manifest is null
                    ? await invocation
                    : await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
                if (decision.Kind != PluginDecisionKind.Continue) return decision;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Plugin middleware {Plugin} failed", middleware?.GetType().FullName);
                if (string.Equals(manifest?.FailureMode, "reject", StringComparison.OrdinalIgnoreCase))
                    return new PluginDecision(PluginDecisionKind.Reject, "Policy plugin failed.");
            }
        }
        return PluginDecision.Continue();
    }

    private async Task NotifyConfiguredAsync<T>(
        IEnumerable<T> middlewares,
        Func<T, PluginSettingsSnapshot, Task> invoke,
        string eventName,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<T> ordered;
        try
        {
            ordered = await Ordered(middlewares, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to prepare plugin notification {EventName}", eventName);
            return;
        }
        foreach (var middleware in ordered)
        {
            var manifest = middleware is null ? null : catalog.FindManifest(middleware.GetType().Assembly);
            try
            {
                var settings = manifest is null
                    ? new PluginSettingsSnapshot(true, new Dictionary<string, JsonElement>())
                    : await settingsProvider.GetAsync(manifest.Id, cancellationToken);
                if (!settings.Enabled) continue;
                var invocation = invoke(middleware, settings);
                if (manifest is null) await invocation;
                else await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Plugin notification {EventName} failed in {Plugin}", eventName, middleware?.GetType().FullName);
            }
        }
    }

    private async Task<IReadOnlyList<T>> Ordered<T>(IEnumerable<T> middlewares, CancellationToken cancellationToken)
    {
        var items = middlewares.ToArray();
        var orders = settingsProvider is PluginSettingsService pluginSettings
            ? (await pluginSettings.GetOrderedManifestsAsync(cancellationToken))
                .Select((manifest, index) => new { manifest.Id, Index = index })
                .ToDictionary(x => x.Id, x => x.Index, StringComparer.OrdinalIgnoreCase)
            : catalog.Plugins
                .Select(x => x.Manifest)
                .OrderBy(x => x.Order)
                .ThenBy(x => x.Id)
                .Select((manifest, index) => new { manifest.Id, Index = index })
                .ToDictionary(x => x.Id, x => x.Index, StringComparer.OrdinalIgnoreCase);
        return items
            .OrderBy(item =>
            {
                var manifest = item is null ? null : catalog.FindManifest(item.GetType().Assembly);
                return manifest is not null && orders.TryGetValue(manifest.Id, out var order) ? order : int.MaxValue;
            })
            .ThenBy(item => item?.GetType().FullName)
            .ToArray();
    }
}

internal sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is "MailServer.PluginSdk" or "MailServer.Domain")
        {
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
