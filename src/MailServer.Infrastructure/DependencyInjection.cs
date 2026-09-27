using DnsClient;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using MailServer.PluginSdk;

namespace MailServer.Infrastructure;

/// <summary>
/// Infrastructure 层的组合根。新增领域服务、后台服务依赖或插件宿主能力时优先在这里注册。
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMailServerInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var connectionString = configuration.GetConnectionString("MailDatabase")
            ?? (database.Provider == DatabaseProvider.Sqlite
                ? "Data Source=data/mailserver.db"
                : throw new InvalidOperationException("ConnectionStrings:MailDatabase is required."));
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<DeliveryOptions>(configuration.GetSection(DeliveryOptions.SectionName));
        services.Configure<MailboxDefaultsOptions>(configuration.GetSection(MailboxDefaultsOptions.SectionName));
        services.Configure<MailRulesOptions>(configuration.GetSection(MailRulesOptions.SectionName));
        services.Configure<ForwardingOptions>(configuration.GetSection(ForwardingOptions.SectionName));
        services.Configure<PluginOptions>(configuration.GetSection(PluginOptions.SectionName));
        services.Configure<SecurityAssetsOptions>(configuration.GetSection(SecurityAssetsOptions.SectionName));
        services.Configure<CertificatesOptions>(configuration.GetSection(CertificatesOptions.SectionName));
        services.Configure<AdminServerOptions>(configuration.GetSection(AdminServerOptions.SectionName));
        services.Configure<TransportSecurityOptions>(configuration.GetSection(TransportSecurityOptions.SectionName));
        services.Configure<MaintenanceOptions>(configuration.GetSection(MaintenanceOptions.SectionName));
        services.AddHttpContextAccessor();
        services.AddSingleton<AuditStampInterceptor>();
        services.AddSingleton<DeliveryNotificationService>();
        services.AddPooledDbContextFactory<MailDbContext>((provider, options) =>
        {
            options.AddInterceptors(provider.GetRequiredService<AuditStampInterceptor>());
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
                case DatabaseProvider.PostgreSql:
                    options.UseNpgsql(connectionString, x => x.MigrationsAssembly(migrationsAssembly));
                    break;
                case DatabaseProvider.MySql:
                    options.UseMySQL(connectionString, x => x.MigrationsAssembly(migrationsAssembly));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(database.Provider), database.Provider, null);
            }
        });
        var securityRoot = Path.GetFullPath(
            configuration[$"{SecurityAssetsOptions.SectionName}:RootPath"] ?? "security-assets");
        var pluginKeyDirectory = new DirectoryInfo(securityRoot);
        Directory.CreateDirectory(pluginKeyDirectory.FullName);
        // DataProtection key 同时服务管理后台 cookie 和插件/安全资产场景，必须稳定持久化。
        services.AddDataProtection()
            .SetApplicationName("MailServer")
            .PersistKeysToFileSystem(pluginKeyDirectory);
        services.AddSingleton<IMessageContentStore, FileMessageContentStore>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IMimeInspectionService, MimeInspectionService>();
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<IAccountAuthenticator, AccountAuthenticator>();
        services.AddSingleton<ISmtpSenderAuthorizer, SmtpSenderAuthorizer>();
        services.AddSingleton<IInboundMessageService, InboundMessageService>();
        services.AddSingleton<IDeliveryQueue, DeliveryQueue>();
        services.AddSingleton<IMailboxService, MailboxService>();
        services.AddSingleton(new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(5), Retries = 2 }));
        services.AddSingleton<DnsDiagnosticService>();
        services.AddSingleton<SystemSettingsService>();
        services.AddSingleton<MailboxFolderManagementService>();
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<PermissionService>();
        services.AddSingleton<DomainCertificateProvider>();
        services.AddSingleton<DomainCertificateService>();
        services.AddSingleton<DomainDnsService>();
        services.AddSingleton<MailRuleService>();
        services.AddSingleton<SrsService>();
        services.AddSingleton<SentCopyService>();
        services.AddSingleton<IpFirewallService>();
        services.AddSingleton<TransportSecurityPolicyService>();
        services.AddSingleton<IQuotaLedgerService, QuotaLedgerService>();
        services.AddSingleton<IQuotaReconciliationService, QuotaReconciliationService>();
        services.AddSingleton<CleanupQueueService>();
        services.AddSingleton<ICleanupQueueService>(provider => provider.GetRequiredService<CleanupQueueService>());
        services.AddSingleton<IRecycleBinService>(provider => provider.GetRequiredService<CleanupQueueService>());
        services.AddSingleton<QuarantineService>();
        services.AddSingleton<IQuarantineService>(provider => provider.GetRequiredService<QuarantineService>());
        services.AddSingleton<IScheduledTaskHandler, CleanupTaskHandler>();
        services.AddSingleton<IScheduledTaskHandler, QuotaTaskHandler>();
        services.AddSingleton<IScheduledTaskHandler, CacheCleanupTaskHandler>();
        services.AddSingleton<IScheduledTaskHandler, OrphanStorageTaskHandler>();
        services.AddSingleton<IScheduledTaskHandler, StorageIntegrityTaskHandler>();
        services.AddSingleton<IScheduledTaskHandler, MailboxFolderDeletionTaskHandler>();
        services.AddSingleton<TaskExecutionService>();
        services.AddSingleton<ITaskScheduler>(provider => provider.GetRequiredService<TaskExecutionService>());
        services.AddSingleton<ITaskExecutionService>(provider => provider.GetRequiredService<TaskExecutionService>());
        services.AddSingleton<PluginSettingsService>();
        services.AddSingleton<IPluginSettingsProvider>(provider => provider.GetRequiredService<PluginSettingsService>());
        services.AddSingleton<IPluginMailSender, PluginMailSender>();
        services.AddSingleton<IPluginDeliveryQueue, PluginDeliveryQueue>();
        services.AddSingleton<IPluginDirectory, PluginDirectory>();
        services.AddSingleton<IPluginMessageStore, PluginMessageStore>();
        services.AddSingleton<IPluginMailboxStore, PluginMailboxStore>();
        services.AddSingleton<IPluginAuditWriter, PluginAuditWriter>();
        services.AddSingleton<IPluginStateStore, PluginStateStore>();
        services.AddSingleton<IPluginQuarantineStore, PluginQuarantineStore>();
        services.AddSingleton<AdminApplicationService>();
        services.AddSingleton<AccountDirectoryService>();
        services.AddSingleton<IOutboundTransport, DirectMxTransport>();
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
        if (!applied.Any(migration => migration.EndsWith("_InitialMailServer2", StringComparison.Ordinal))
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
