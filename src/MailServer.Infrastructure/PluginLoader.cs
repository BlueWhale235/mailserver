using System.Reflection;
using System.Runtime.Loader;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailServer.Domain;
using MailServer.PluginSdk;
using MailServer.PluginSdk.Auditing;
using MailServer.PluginSdk.Delivery;
using MailServer.PluginSdk.Imap;
using MailServer.PluginSdk.Security;
using MailServer.PluginSdk.Smtp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

/// <summary>
/// 已发现插件的宿主视图。Assembly 为空表示仅发现清单，或当前 host 不激活该插件。
/// </summary>
public sealed record LoadedPlugin(
    PluginManifest Manifest,
    Assembly? Assembly,
    bool IsHostActive,
    string? LoadError = null);

/// <summary>
/// 插件目录，记录发现结果、搜索路径和被跳过的源码目录，供管理后台和诊断日志使用。
/// </summary>
public sealed class PluginCatalog
{
    private readonly List<LoadedPlugin> _plugins = [];
    private readonly List<string> _skippedSourceDirectories = [];
    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public IReadOnlyList<string> SearchPaths { get; internal set; } = [];
    public IReadOnlyList<string> SkippedSourceDirectories => _skippedSourceDirectories;
    internal void Add(LoadedPlugin plugin) => _plugins.Add(plugin);
    internal void AddSkippedSourceDirectory(string path) => _skippedSourceDirectories.Add(path);
    internal PluginManifest? FindManifest(Assembly assembly) =>
        _plugins.FirstOrDefault(x => x.Assembly == assembly)?.Manifest;
}

/// <summary>
/// 插件发现与注册入口。宿主按 plugin.json 发现已发布插件，再把插件服务注册进 DI。
/// </summary>
public static class PluginServiceCollectionExtensions
{
    public static IServiceCollection AddMailServerPlugins(
        this IServiceCollection services,
        IConfiguration configuration,
        string hostName)
    {
        var path = configuration[$"{PluginOptions.SectionName}:Path"] ?? "plugins";
        var catalog = new PluginCatalog();
        var roots = new[] { ResolvePluginPath(path) }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        catalog.SearchPaths = roots;
        RegisterCatalogServices(services, catalog);
        var existingRoots = roots.Where(Directory.Exists).ToArray();
        if (existingRoots.Length == 0)
        {
            return services;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifestPath in existingRoots.SelectMany(root =>
                     Directory.EnumerateFiles(root, "plugin.json", SearchOption.AllDirectories)))
        {
            if (!IsInstalledPluginManifest(manifestPath))
            {
                // 源码目录中的 plugin.json 不直接加载，避免开发项目引用与已发布插件混在一起。
                catalog.AddSkippedSourceDirectory(Path.GetDirectoryName(manifestPath)!);
                continue;
            }
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
                // 契约版本不匹配时立即失败，避免运行时才出现上下文字段缺失或接口签名不一致。
                throw new InvalidOperationException(
                    $"Plugin {manifest.Id} requires contract {manifest.ContractVersion}; host supports {PluginContract.Version}.");
            }

            var assemblyPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.EntryAssembly));
            if (!hostName.Equals("all", StringComparison.OrdinalIgnoreCase)
                && manifest.Hosts.Length > 0
                && !manifest.Hosts.Contains(hostName, StringComparer.OrdinalIgnoreCase))
            {
                // 插件可以声明只运行在 api/smtp/imap/worker 等宿主，未激活时仍进入目录供后台展示。
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
                var pluginAssemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
                                   !candidate.IsDynamic
                                   && string.Equals(candidate.FullName, pluginAssemblyName.FullName, StringComparison.Ordinal))
                               ?? new PluginLoadContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);
                var pluginType = assembly.GetTypes()
                    .SingleOrDefault(type => !type.IsAbstract && typeof(IMailServerPlugin).IsAssignableFrom(type))
                    ?? throw new InvalidOperationException($"Plugin {manifest.Id} has no IMailServerPlugin implementation.");
                var plugin = (IMailServerPlugin?)Activator.CreateInstance(pluginType)
                    ?? throw new InvalidOperationException($"Plugin {manifest.Id} could not be created.");
                // 插件只通过 ConfigureServices 扩展宿主能力，具体调用顺序由 PluginPipeline 统一控制。
                plugin.ConfigureServices(services);
                catalog.Add(new LoadedPlugin(manifest, assembly, true));
            }
            catch (Exception exception)
            {
                catalog.Add(new LoadedPlugin(manifest, null, true, exception.Message));
                if (string.Equals(manifest.FailureMode, "reject", StringComparison.OrdinalIgnoreCase)) throw;
            }
        }

        return services;
    }

    internal static string ResolvePluginPath(string path) =>
        Path.GetFullPath(path, AppContext.BaseDirectory);

    private static void RegisterCatalogServices(IServiceCollection services, PluginCatalog catalog)
    {
        services.AddSingleton(catalog);
        services.TryAddSingleton<IPluginSettingsProvider, ManifestDefaultPluginSettingsProvider>();
        services.AddSingleton<PluginPipeline>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, PluginCatalogDiagnosticsHostedService>());
    }

    private static bool IsInstalledPluginManifest(string manifestPath)
    {
        var directory = Path.GetDirectoryName(manifestPath)!;
        // 发布后的插件目录不应包含 csproj；这条规则保护开发源码不会被误当成运行时插件加载。
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

/// <summary>
/// 插件发现诊断服务，只输出加载状态，不参与插件执行。
/// </summary>
internal sealed class PluginCatalogDiagnosticsHostedService(
    PluginCatalog catalog,
    IDbContextFactory<MailDbContext> dbFactory,
    ILogger<PluginCatalogDiagnosticsHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        foreach (var path in catalog.SearchPaths)
        {
            if (Directory.Exists(path))
                logger.LogInformation("Plugin discovery scanning {PluginPath}.", path);
            else
                logger.LogWarning("Plugin discovery path does not exist: {PluginPath}.", path);
        }
        foreach (var path in catalog.SkippedSourceDirectories)
            logger.LogWarning(
                "Plugin manifest under {PluginPath} was skipped because the directory contains a project file; publish the plugin before installing it.",
                path);
        foreach (var plugin in catalog.Plugins)
        {
            if (plugin.LoadError is not null)
            {
                logger.LogError(
                    "Plugin {PluginId} could not be loaded for host activation={HostActive}: {LoadError}",
                    plugin.Manifest.Id,
                    plugin.IsHostActive,
                    plugin.LoadError);
                db.AuditRecords.Add(new AuditRecord
                {
                    Actor = "system",
                    Action = "plugin.load.failed",
                    Resource = $"plugin:{plugin.Manifest.Id}",
                    Detail = plugin.LoadError,
                    Category = AuditCategory.Plugin,
                    Status = AuditStatus.Failed,
                    PluginId = plugin.Manifest.Id,
                    PluginName = plugin.Manifest.DisplayName ?? plugin.Manifest.Id,
                    RawJson = AuditLogService.SafeJson(new
                    {
                        pluginId = plugin.Manifest.Id,
                        pluginName = plugin.Manifest.DisplayName ?? plugin.Manifest.Id,
                        plugin.Manifest.Version,
                        plugin.IsHostActive,
                        plugin.LoadError
                    })
                });
            }
            else if (plugin.IsHostActive)
            {
                logger.LogInformation("Plugin {PluginId} loaded successfully.", plugin.Manifest.Id);
                db.AuditRecords.Add(new AuditRecord
                {
                    Actor = "system",
                    Action = "plugin.load.succeeded",
                    Resource = $"plugin:{plugin.Manifest.Id}",
                    Detail = plugin.Manifest.Version,
                    Category = AuditCategory.Plugin,
                    Status = AuditStatus.Succeeded,
                    PluginId = plugin.Manifest.Id,
                    PluginName = plugin.Manifest.DisplayName ?? plugin.Manifest.Id,
                    RawJson = AuditLogService.SafeJson(new
                    {
                        pluginId = plugin.Manifest.Id,
                        pluginName = plugin.Manifest.DisplayName ?? plugin.Manifest.Id,
                        plugin.Manifest.Version,
                        plugin.IsHostActive
                    })
                });
            }
            else
                logger.LogInformation("Plugin {PluginId} was discovered but does not run in this host.", plugin.Manifest.Id);
        }
        logger.LogInformation(
            "Plugin discovery completed: discovered={DiscoveredCount}, active={ActiveCount}.",
            catalog.Plugins.Count,
            catalog.Plugins.Count(x => x.IsHostActive && x.LoadError is null));
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// 当数据库插件设置服务尚未注册时，从清单默认值构造设置快照。
/// </summary>
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

/// <summary>
/// 插件执行流水线。它负责排序、启用状态、超时、失败策略和上下文设置注入。
/// </summary>
public sealed class PluginPipeline(
    IServiceProvider services,
    PluginCatalog catalog,
    IPluginSettingsProvider settingsProvider,
    ILogger<PluginPipeline> logger,
    AuditLogService? audit = null)
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
        // 认证插件按声明机制过滤，未处理时继续尝试后续插件或宿主内置认证。
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
                var stopwatch = Stopwatch.StartNew();
                var invocation = middleware.AuthenticateAsync(context with { Settings = settings }, cancellationToken);
                var result = manifest is null
                    ? await invocation
                    : await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
                stopwatch.Stop();
                if (result.Status != AuthenticationStatus.NotHandled)
                    await RecordAuthenticationAsync(manifest, result, stopwatch.ElapsedMilliseconds, cancellationToken);
                if (result.Status != AuthenticationStatus.NotHandled) return result;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Authentication plugin {Plugin} failed", middleware.GetType().FullName);
                await RecordPluginErrorAsync("authentication", manifest, exception, cancellationToken);
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

    public async Task<PluginDecision> OnMessageAsync(MessageAcceptanceContext context, CancellationToken cancellationToken) =>
        (await OnMessageWithSourceAsync(context, cancellationToken)).Decision;

    public async Task<PluginDecisionResult> OnMessageWithSourceAsync(MessageAcceptanceContext context, CancellationToken cancellationToken)
    {
        // 消息准入需要保留触发插件信息，隔离审核和日志展示会用到插件 ID/名称。
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
                var stopwatch = Stopwatch.StartNew();
                var invocation = middleware.InvokeAsync(context with { Settings = settings }, cancellationToken);
                var decision = manifest is null
                    ? await invocation
                    : await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
                stopwatch.Stop();
                if (decision.Kind != PluginDecisionKind.Continue)
                {
                    await RecordDecisionAsync("message-acceptance", manifest, decision, stopwatch.ElapsedMilliseconds, cancellationToken);
                    return new PluginDecisionResult(decision, manifest?.Id, manifest?.DisplayName ?? manifest?.Id);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Message acceptance plugin {Plugin} failed", middleware.GetType().FullName);
                await RecordPluginErrorAsync("message-acceptance", manifest, exception, cancellationToken);
                if (string.Equals(manifest?.FailureMode, "reject", StringComparison.OrdinalIgnoreCase))
                    return new PluginDecisionResult(
                        new PluginDecision(PluginDecisionKind.Reject, "Message policy plugin failed."),
                        manifest?.Id,
                        manifest?.DisplayName ?? manifest?.Id);
            }
        }

        return new PluginDecisionResult(PluginDecision.Continue(), null, null);
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
                // 内容检查会反复读取同一个内存/文件流，调用前后复位 Position 是插件兼容性的关键。
                content.Position = 0;
                var stopwatch = Stopwatch.StartNew();
                var invocation = middleware.InvokeAsync(
                    new MessageContentContext(sender, recipients, bodyType, content.Length, content, settings),
                    cancellationToken);
                var decision = manifest is null
                    ? await invocation
                    : await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
                stopwatch.Stop();
                content.Position = 0;
                if (decision.Kind != PluginDecisionKind.Continue)
                {
                    await RecordDecisionAsync("message-content", manifest, decision, stopwatch.ElapsedMilliseconds, cancellationToken);
                    return decision;
                }
            }
            catch (Exception exception)
            {
                content.Position = 0;
                logger.LogError(exception, "Message content plugin {Plugin} failed", middleware.GetType().FullName);
                await RecordPluginErrorAsync("message-content", manifest, exception, cancellationToken);
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
                // 带 manifest 的插件受超时保护；宿主内部中间件没有清单，按普通服务调用。
                var stopwatch = Stopwatch.StartNew();
                var invocation = invoke(middleware);
                var decision = manifest is null
                    ? await invocation
                    : await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
                stopwatch.Stop();
                if (decision.Kind != PluginDecisionKind.Continue)
                {
                    await RecordDecisionAsync(typeof(T).Name, manifest, decision, stopwatch.ElapsedMilliseconds, cancellationToken);
                    return decision;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Plugin middleware {Plugin} failed", middleware?.GetType().FullName);
                await RecordPluginErrorAsync(typeof(T).Name, manifest, exception, cancellationToken);
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
                var stopwatch = Stopwatch.StartNew();
                var invocation = invoke(middleware, settings);
                var decision = manifest is null
                    ? await invocation
                    : await invocation.WaitAsync(TimeSpan.FromMilliseconds(manifest.TimeoutMilliseconds), cancellationToken);
                stopwatch.Stop();
                if (decision.Kind != PluginDecisionKind.Continue)
                {
                    await RecordDecisionAsync(typeof(T).Name, manifest, decision, stopwatch.ElapsedMilliseconds, cancellationToken);
                    return decision;
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Plugin middleware {Plugin} failed", middleware?.GetType().FullName);
                await RecordPluginErrorAsync(typeof(T).Name, manifest, exception, cancellationToken);
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
                // 通知型插件不应影响主业务结果，异常只进入日志。
                logger.LogError(exception, "Plugin notification {EventName} failed in {Plugin}", eventName, middleware?.GetType().FullName);
            }
        }
    }

    private async Task RecordDecisionAsync(
        string stage,
        PluginManifest? manifest,
        PluginDecision decision,
        long elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        if (audit is null || manifest is null || decision.Kind == PluginDecisionKind.Continue) return;
        await audit.RecordPluginDecisionAsync(
            new PluginAuditEvent(
                stage,
                MapDecision(decision.Kind),
                decision.Message,
                manifest.Id,
                manifest.DisplayName ?? manifest.Id,
                $"plugin:{manifest.Id}",
                null,
                null,
                null,
                elapsedMilliseconds),
            cancellationToken);
    }

    private async Task RecordAuthenticationAsync(
        PluginManifest? manifest,
        AuthenticationResult result,
        long elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        if (audit is null || manifest is null || result.Status is AuthenticationStatus.NotHandled or AuthenticationStatus.Success) return;
        await audit.RecordPluginDecisionAsync(
            new PluginAuditEvent(
                "authentication",
                PluginDecisionKindName.Reject,
                result.Message,
                manifest.Id,
                manifest.DisplayName ?? manifest.Id,
                $"plugin:{manifest.Id}",
                null,
                null,
                null,
                elapsedMilliseconds),
            cancellationToken);
    }

    private async Task RecordPluginErrorAsync(
        string stage,
        PluginManifest? manifest,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (audit is null || manifest is null) return;
        await audit.RecordPluginDecisionAsync(
            new PluginAuditEvent(
                stage,
                PluginDecisionKindName.Error,
                exception is TimeoutException ? "Plugin execution timed out." : exception.Message,
                manifest.Id,
                manifest.DisplayName ?? manifest.Id,
                $"plugin:{manifest.Id}",
                null,
                null,
                null,
                null,
                Failed: true),
            cancellationToken);
    }

    private static PluginDecisionKindName MapDecision(PluginDecisionKind kind) => kind switch
    {
        PluginDecisionKind.Reject => PluginDecisionKindName.Reject,
        PluginDecisionKind.Defer => PluginDecisionKindName.Defer,
        PluginDecisionKind.Quarantine => PluginDecisionKindName.Quarantine,
        _ => PluginDecisionKindName.Modify
    };

    private async Task<IReadOnlyList<T>> Ordered<T>(IEnumerable<T> middlewares, CancellationToken cancellationToken)
    {
        var items = middlewares.ToArray();
        // 管理后台保存的排序优先于清单 Order；未保存时回退到清单顺序和插件 ID。
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
