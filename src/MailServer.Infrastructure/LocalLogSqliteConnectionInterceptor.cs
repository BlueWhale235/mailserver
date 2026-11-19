using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MailServer.Infrastructure;

internal sealed class LocalLogSqliteConnectionInterceptor : DbConnectionInterceptor
{
    internal static readonly LocalLogSqliteConnectionInterceptor Instance = new();
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
    }
    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
