using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.DataProtection;

namespace MailServer.Infrastructure;

/// <summary>
/// Infrastructure 层的组合根。新增领域服务、后台服务依赖或插件宿主能力时优先在这里注册。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMailServerInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        string role = "api")
    {
        services.TryAddSingleton(configuration);
        var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        if (database.Provider != DatabaseProvider.MySql && (configuration.GetValue("Node:Enabled", false)
            || string.Equals(configuration["Storage:Provider"], "S3", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Node mode and S3 storage require a shared MySQL database.");
        if (database.ContextPoolSize is < 1 or > 4096)
            throw new ArgumentOutOfRangeException("Database:ContextPoolSize", "Context pool size must be between 1 and 4096.");
        var connectionString = configuration.GetConnectionString("MailDatabase")
            ?? (database.Provider == DatabaseProvider.Sqlite
                ? "Data Source=data/mailserver.db"
                : throw new InvalidOperationException("ConnectionStrings:MailDatabase is required."));
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<DeliveryOptions>(configuration.GetSection(DeliveryOptions.SectionName));
        services.Configure<MessageSearchOptions>(configuration.GetSection(MessageSearchOptions.SectionName));
        services.Configure<SmtpConnectionReuseOptions>(configuration.GetSection(SmtpConnectionReuseOptions.SectionName));
        services.Configure<MailboxDefaultsOptions>(configuration.GetSection(MailboxDefaultsOptions.SectionName));
        services.Configure<MailRulesOptions>(configuration.GetSection(MailRulesOptions.SectionName));
        services.Configure<ConfigurationCacheOptions>(configuration.GetSection(ConfigurationCacheOptions.SectionName));
        var runtime = new ResourceRuntime(configuration.GetValue("RunAll:Enabled", false) ? "all" : role, configuration);
        services.AddSingleton(runtime);
        services.AddSingleton<NodeStatusService>();
        services.AddSingleton<AdaptiveDatabaseLatencyInterceptor>();
        if (!runtime.UsesCache) services.PostConfigure<ConfigurationCacheOptions>(value => value.Enabled = false);
        services.PostConfigure<ConfigurationCacheOptions>(value =>
        {
            value.SizeLimitBytes = runtime.CacheBudgets.Allocation.ConfigurationBytes;
            value.MaximumEntryBytes = Math.Min(value.MaximumEntryBytes, value.SizeLimitBytes);
        });
        services.AddSingleton<ResourceSettingsService>();
        services.AddSingleton<ConnectionProtectionSettingsService>();
        services.AddSingleton<ConnectionProtection>();
        services.Configure<ForwardingOptions>(configuration.GetSection(ForwardingOptions.SectionName));
        services.Configure<PluginOptions>(configuration.GetSection(PluginOptions.SectionName));
        services.Configure<SecurityAssetsOptions>(configuration.GetSection(SecurityAssetsOptions.SectionName));
        services.Configure<CertificatesOptions>(configuration.GetSection(CertificatesOptions.SectionName));
        services.Configure<AdminServerOptions>(configuration.GetSection(AdminServerOptions.SectionName));
        services.Configure<TransportSecurityOptions>(configuration.GetSection(TransportSecurityOptions.SectionName));
        services.Configure<MaintenanceOptions>(configuration.GetSection(MaintenanceOptions.SectionName));
        services.AddHttpContextAccessor();
        services.AddSingleton<DeliveryNotificationService>();
        services.AddSingleton<DeliveryWakeSignal>();
        services.AddSingleton<SearchBackfillWakeSignal>();
        services.AddSingleton<MailboxChangeSignal>();
        services.AddSingleton<SmtpConnectionPool>();
        services.AddSingleton(provider => new LocalLogDatabase(configuration, provider.GetService<Microsoft.Extensions.Logging.ILogger<LocalLogDatabase>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalLogDatabase>.Instance));
        services.AddSingleton<LocalLogWriter>();
        services.AddSingleton<AuditStampInterceptor>();
        services.AddSingleton<PerformanceCollector>();
        services.AddSingleton<PerformanceSqlInterceptor>();
        services.AddSingleton<PerformanceConnectionInterceptor>();
        services.AddSingleton<PerformanceTransactionInterceptor>();
        services.AddPooledDbContextFactory<MailDbContext>((provider, options) =>
        {
            options.AddInterceptors(LocalLogCommitInterceptor.Instance, provider.GetRequiredService<AuditStampInterceptor>());
            options.AddInterceptors(provider.GetRequiredService<AdaptiveDatabaseLatencyInterceptor>());
            options.AddInterceptors(provider.GetRequiredService<PerformanceSqlInterceptor>());
            options.AddInterceptors(provider.GetRequiredService<PerformanceConnectionInterceptor>(), provider.GetRequiredService<PerformanceTransactionInterceptor>());
            var migrationsAssembly = database.Provider.MigrationsAssemblyName();
            switch (database.Provider)
            {
                case DatabaseProvider.Sqlite:
                    SqliteNative.Initialize();
                    var sqlitePath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
                    if (!string.IsNullOrWhiteSpace(sqlitePath) && sqlitePath != ":memory:")
                    {
                        // 本地单机默认使用 SQLite，启动时创建目录可减少首次运行配置成本。
                        var fullPath = Path.GetFullPath(sqlitePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    }
                    options.UseSqlite(connectionString, x => x.MigrationsAssembly(migrationsAssembly));
                    options.ConfigureWarnings(warnings =>
                        warnings.Ignore(RelationalEventId.NonTransactionalMigrationOperationWarning));
                    break;
                case DatabaseProvider.MySql:
                    options.UseMySQL(connectionString, x => x.MigrationsAssembly(migrationsAssembly));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(database.Provider), database.Provider, null);
            }
        }, poolSize: database.ContextPoolSize);
        var securityRoot = Path.GetFullPath(
            configuration[$"{SecurityAssetsOptions.SectionName}:RootPath"] ?? "security-assets");
        var pluginKeyDirectory = new DirectoryInfo(securityRoot);
        Directory.CreateDirectory(pluginKeyDirectory.FullName);
        // DataProtection key 同时服务管理后台 cookie 和插件/安全资产场景，必须稳定持久化。
        services.AddDataProtection()
            .SetApplicationName("MailServer")
            .PersistKeysToFileSystem(pluginKeyDirectory);
        services.AddMailMessageServices();
        services.AddMailManagementServices();
        services.AddMailMaintenanceServices();
        services.AddMailPluginAdapters();
        services.AddMailServerBackgroundServices(configuration, runtime);
        return services;
    }

    /// <summary>
    /// 启动前校验数据库可连接且迁移已应用，避免协议服务在半初始化状态下接收邮件。
    /// </summary>
    public static async Task VerifyMailDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            throw new InvalidOperationException("The configured mail database is unavailable. Run 'mailserver doctor'.");
        }

        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        if (!applied.Any(migration => migration.EndsWith("_InitialSnowflakeMailServer", StringComparison.Ordinal))
            || applied.Any(migration => !known.Contains(migration)))
        {
            throw new InvalidOperationException(
                "This database belongs to a pre-2.0 migration chain and cannot be upgraded in place. Back it up, create an empty database, then run 'mailserver db update'.");
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        if (pending.Length > 0)
        {
            throw new InvalidOperationException(
                $"The mail database schema is out of date ({pending.Length} pending migration(s)). Run 'mailserver db update'.");
        }
    }
}

/// <summary>
/// SQLite 原生 provider 初始化器，保证单文件发布或便携运行时能正确加载 SQLitePCL。
/// </summary>
internal static class SqliteNative
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        lock (Sync)
        {
            if (_initialized) return;
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            _initialized = true;
        }
    }
}
