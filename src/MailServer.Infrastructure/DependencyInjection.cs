using DnsClient;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.Configure<PluginOptions>(configuration.GetSection(PluginOptions.SectionName));
        services.Configure<CertificatesOptions>(configuration.GetSection(CertificatesOptions.SectionName));
        services.Configure<AdminServerOptions>(configuration.GetSection(AdminServerOptions.SectionName));
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
        services.AddSingleton<IMessageContentStore, FileMessageContentStore>();
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<IAccountAuthenticator, AccountAuthenticator>();
        services.AddSingleton<IInboundMessageService, InboundMessageService>();
        services.AddSingleton<IDeliveryQueue, DeliveryQueue>();
        services.AddSingleton<IMailboxService, MailboxService>();
        services.AddSingleton(new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(5), Retries = 2 }));
        services.AddSingleton<DnsDiagnosticService>();
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
