using System.Data;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace MailServer.Infrastructure;

/// <summary>Serializes publication and deletion for a content key across MySQL clients.</summary>
internal sealed class ContentMutationLease : IAsyncDisposable
{
    private readonly MailDbContext _db;
    private readonly string _name;
    private ContentMutationLease(MailDbContext db, string name) { _db = db; _name = name; }

    public static async Task<ContentMutationLease?> AcquireAsync(IDbContextFactory<MailDbContext> factory, string key, CancellationToken token, bool allowLocal = false)
    {
        var db = await factory.CreateDbContextAsync(token);
        try
        {
            if (db.Database.ProviderName?.Contains("MySQL", StringComparison.OrdinalIgnoreCase) != true)
            {
                if (!allowLocal) throw new InvalidOperationException("S3 storage requires MySQL for cross-node content coordination.");
                await db.DisposeAsync();
                return null;
            }
            await db.Database.OpenConnectionAsync(token);
            var name = "mail-content:" + key[..48];
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT GET_LOCK(@name, 30)";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = name;
            command.Parameters.Add(parameter);
            var result = await command.ExecuteScalarAsync(token);
            if (Convert.ToInt32(result) != 1) throw new IOException("Content mutation lock timed out.");
            return new(db, name);
        }
        catch
        {
            // An interrupted GET_LOCK may have acquired the server-side lock. Never pool
            // a connection whose advisory-lock ownership is uncertain.
            if (db.Database.IsRelational() && db.Database.GetDbConnection() is MySqlConnection connection) MySqlConnection.ClearPool(connection);
            await db.DisposeAsync(); throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var command = _db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT RELEASE_LOCK(@name)";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = _name;
            command.Parameters.Add(parameter);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await command.ExecuteScalarAsync(timeout.Token);
        }
        catch
        {
            if (_db.Database.GetDbConnection() is MySqlConnection connection) MySqlConnection.ClearPool(connection);
            throw;
        }
        finally { await _db.DisposeAsync(); }
    }
}
