using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MailServer.PluginSdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

public sealed record LoadedPlugin(PluginManifest Manifest, Assembly Assembly);

public sealed class PluginCatalog
{
    private readonly List<LoadedPlugin> _plugins = [];
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    internal void Add(LoadedPlugin plugin) => _plugins.Add(plugin);
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
        if (!Directory.Exists(path))
        {
            services.AddSingleton(catalog);
            services.AddSingleton<PluginPipeline>();
            return services;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(path, "plugin.json", SearchOption.AllDirectories))
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"Invalid plugin manifest: {manifestPath}");
            if (!string.Equals(manifest.ContractVersion, PluginContract.Version, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Plugin {manifest.Id} requires contract {manifest.ContractVersion}; host supports {PluginContract.Version}.");
            }

            if (!hostName.Equals("all", StringComparison.OrdinalIgnoreCase)
                && manifest.Hosts.Length > 0
                && !manifest.Hosts.Contains(hostName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var assemblyPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.EntryAssembly));
            var loadContext = new PluginLoadContext(assemblyPath);
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var pluginType = assembly.GetTypes()
                .SingleOrDefault(type => !type.IsAbstract && typeof(IMailServerPlugin).IsAssignableFrom(type))
                ?? throw new InvalidOperationException($"Plugin {manifest.Id} has no IMailServerPlugin implementation.");
            var plugin = (IMailServerPlugin?)Activator.CreateInstance(pluginType)
                ?? throw new InvalidOperationException($"Plugin {manifest.Id} could not be created.");
            plugin.ConfigureServices(services);
            catalog.Add(new LoadedPlugin(manifest, assembly));
        }

        services.AddSingleton(catalog);
        services.AddSingleton<PluginPipeline>();
        return services;
    }
}

public sealed class PluginPipeline(IServiceProvider services, PluginCatalog catalog, ILogger<PluginPipeline> logger)
{
    public Task<PluginDecision> OnSmtpSessionAsync(SmtpSessionContext context, CancellationToken cancellationToken) =>
        RunAsync(services.GetServices<ISmtpSessionMiddleware>(), x => x.InvokeAsync(context, cancellationToken), cancellationToken);

    public Task<PluginDecision> OnMessageAsync(MessageAcceptanceContext context, CancellationToken cancellationToken) =>
        RunAsync(services.GetServices<IMessageAcceptanceMiddleware>(), x => x.InvokeAsync(context, cancellationToken), cancellationToken);

    public Task<PluginDecision> OnDeliveryAsync(DeliveryContext context, CancellationToken cancellationToken) =>
        RunAsync(services.GetServices<IDeliveryMiddleware>(), x => x.InvokeAsync(context, cancellationToken), cancellationToken);

    public Task<PluginDecision> OnImapSessionAsync(ImapSessionContext context, CancellationToken cancellationToken) =>
        RunAsync(services.GetServices<IImapSessionMiddleware>(), x => x.InvokeAsync(context, cancellationToken), cancellationToken);

    public async Task OnAuditAsync(AuditContext context, CancellationToken cancellationToken)
    {
        foreach (var middleware in services.GetServices<IAuditMiddleware>())
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
        IEnumerable<T> middlewares,
        Func<T, Task<PluginDecision>> invoke,
        CancellationToken cancellationToken)
    {
        foreach (var middleware in middlewares)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = catalog.Plugins.FirstOrDefault(x => x.Assembly == middleware?.GetType().Assembly)?.Manifest;
            try
            {
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
