using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MailServer.Tests;

internal static class TestLocalLogs
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbContextOptions<MailDbContext>, LocalLogDatabase> Stores = new();
    internal static LocalLogDatabase Create() => new(new DbContextOptionsBuilder<LocalLogDbContext>()
        .UseInMemoryDatabase("logs-" + System.Guid.NewGuid()).Options);
    internal static LocalLogDatabase For(DbContextOptions<MailDbContext> options) => Stores.GetValue(options, value =>
    {
        var source = value.Extensions.OfType<RelationalOptionsExtension>().FirstOrDefault();
        if (source?.GetType().Name != "SqliteOptionsExtension") return Create();
        SqliteNative.Initialize();
        var original = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(source.ConnectionString ?? source.Connection!.ConnectionString);
        var builder = new DbContextOptionsBuilder<LocalLogDbContext>();
        if (original.DataSource == ":memory:")
        {
            var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
            connection.Open();
            builder.UseSqlite(connection);
        }
        else builder.UseSqlite("Data Source=" + original.DataSource + ".logs.db;Pooling=False");
        builder.AddInterceptors(value.Extensions.OfType<CoreOptionsExtension>().FirstOrDefault()?.Interceptors ?? []);
        var database = new LocalLogDatabase(builder.Options);
        using var initialized = database.CreateDbContext();
        return database;
    });
}

internal sealed class TestMailDbContextFactory(DbContextOptions<MailDbContext> options, LocalLogDatabase? logs = null) : IDbContextFactory<MailDbContext>
{
    private readonly LocalLogDatabase _logs = logs ?? TestLocalLogs.For(options);
    private readonly DbContextOptions<MailDbContext> _options = new DbContextOptionsBuilder<MailDbContext>(options).AddInterceptors(LocalLogCommitInterceptor.Instance).Options;
    public MailDbContext CreateDbContext() => new(_options, _logs);
    public Task<MailDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
}
