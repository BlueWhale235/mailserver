namespace MailServer.Infrastructure;

public enum DatabaseProvider
{
    Sqlite,
    MySql
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;
}

public static class DatabaseProviderExtensions
{
    public static string MigrationsAssemblyName(this DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Sqlite => "MailServer.Migrations.Sqlite",
        DatabaseProvider.MySql => "MailServer.Migrations.MySql",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };
}
