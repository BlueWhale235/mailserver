using DnsClient;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using MailServer.PluginSdk;

namespace MailServer.Infrastructure;

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
        services.Configure<RelayOptions>(configuration.GetSection(RelayOptions.SectionName));
        services.Configure<DeliveryOptions>(configuration.GetSection(DeliveryOptions.SectionName));
        services.Configure<MailboxDefaultsOptions>(configuration.GetSection(MailboxDefaultsOptions.SectionName));
        services.Configure<MailRulesOptions>(configuration.GetSection(MailRulesOptions.SectionName));
        services.Configure<ForwardingOptions>(configuration.GetSection(ForwardingOptions.SectionName));
        services.Configure<PluginOptions>(configuration.GetSection(PluginOptions.SectionName));
        services.Configure<SecurityAssetsOptions>(configuration.GetSection(SecurityAssetsOptions.SectionName));
        services.Configure<CertificatesOptions>(configuration.GetSection(CertificatesOptions.SectionName));
        services.Configure<AdminServerOptions>(configuration.GetSection(AdminServerOptions.SectionName));
        services.Configure<TransportSecurityOptions>(configuration.GetSection(TransportSecurityOptions.SectionName));
        services.AddPooledDbContextFactory<MailDbContext>(options =>
        {
            var migrationsAssembly = database.Provider.MigrationsAssemblyName();
            switch (database.Provider)
            {
                case DatabaseProvider.Sqlite:
                    SqliteNative.Initialize();
                    var sqlitePath = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
                    if (!string.IsNullOrWhiteSpace(sqlitePath) && sqlitePath != ":memory:")
                    {
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
        services.AddDataProtection()
            .SetApplicationName("MailServer")
            .PersistKeysToFileSystem(pluginKeyDirectory);
        services.AddSingleton<IMessageContentStore, FileMessageContentStore>();
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<IAccountAuthenticator, AccountAuthenticator>();
        services.AddSingleton<ISmtpSenderAuthorizer, SmtpSenderAuthorizer>();
        services.AddSingleton<IInboundMessageService, InboundMessageService>();
        services.AddSingleton<IDeliveryQueue, DeliveryQueue>();
        services.AddSingleton<IMailboxService, MailboxService>();
        services.AddSingleton(new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(5), Retries = 2 }));
        services.AddSingleton<DnsDiagnosticService>();
        services.AddSingleton<SystemSettingsService>();
        services.AddSingleton<PermissionService>();
        services.AddSingleton<DomainCertificateProvider>();
        services.AddSingleton<DomainCertificateService>();
        services.AddSingleton<DomainDnsService>();
        services.AddSingleton<MailRuleService>();
        services.AddSingleton<SrsService>();
        services.AddSingleton<SentCopyService>();
        services.AddSingleton<IpFirewallService>();
        services.AddSingleton<TransportSecurityPolicyService>();
        services.AddSingleton<PluginSettingsService>();
        services.AddSingleton<IPluginSettingsProvider>(provider => provider.GetRequiredService<PluginSettingsService>());
        services.AddSingleton<AdminApplicationService>();
        services.AddSingleton<IOutboundTransport, RelaySmtpTransport>();
        services.AddSingleton<IOutboundTransport, DirectMxTransport>();
        return services;
    }

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

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
        if (pending.Length > 0)
        {
            throw new InvalidOperationException(
                $"The mail database schema is out of date ({pending.Length} pending migration(s)). Run 'mailserver db update'.");
        }
    }
}

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
