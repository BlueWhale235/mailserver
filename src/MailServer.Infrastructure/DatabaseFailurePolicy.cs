using Microsoft.Data.Sqlite;
using MySql.Data.MySqlClient;

namespace MailServer.Infrastructure;

public static class DatabaseFailurePolicy
{
    public static bool IsConcurrencyConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 }
                or MySqlException { Number: 1205 or 1213 }) return true;
        }
        return false;
    }
}
