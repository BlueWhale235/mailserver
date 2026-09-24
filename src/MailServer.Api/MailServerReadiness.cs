using System.Data.Common;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Api;

public sealed record ReadinessSnapshot(
    string Status,
    string Database,
    string MessageStorage,
    string SecurityAssets);

public static class MailServerReadiness
{
    public static async Task<ReadinessSnapshot> CheckAsync(
        IDbContextFactory<MailDbContext> dbFactory,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var database = await CheckDatabaseAsync(dbFactory, timeout.Token);
        var storage = await CheckDirectoryAsync(
            configuration["Storage:RootPath"] ?? "data/messages", timeout.Token);
        var security = await CheckDirectoryAsync(
            configuration["SecurityAssets:RootPath"] ?? "security-assets", timeout.Token);
        return new ReadinessSnapshot(
            database == "ready" && storage == "ready" && security == "ready" ? "ready" : "unavailable",
            database, storage, security);
    }

    private static async Task<string> CheckDatabaseAsync(
        IDbContextFactory<MailDbContext> dbFactory, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            if (!await db.Database.CanConnectAsync(cancellationToken)) return "unavailable";
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any()) return "migration-pending";
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return "ready";
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException
            or IOException or TimeoutException or OperationCanceledException)
        {
            return "unavailable";
        }
    }

    private static async Task<string> CheckDirectoryAsync(string configuredPath, CancellationToken cancellationToken)
    {
        string? probe = null;
        try
        {
            var root = Path.GetFullPath(configuredPath);
            Directory.CreateDirectory(root);
            _ = Directory.EnumerateFileSystemEntries(root).Take(1).ToArray();
            probe = Path.Combine(root, $".mailserver-ready-{Guid.NewGuid():N}");
            await using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose))
            {
                await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            return "ready";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or OperationCanceledException)
        {
            return "unavailable";
        }
        finally
        {
            if (probe is not null)
            {
                try { File.Delete(probe); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
