using System.Text.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

// The management-only host reports liveness without starting configuration polling or a cache.
public sealed class ApiProcessHeartbeat(
    IDbContextFactory<MailDbContext> factory,
    ResourceRuntime runtime,
    ILogger<ApiProcessHeartbeat> logger) : BackgroundService
{
    private readonly string _processId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        runtime.SetServiceState("api", "working");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var status = await db.ConfigurationProcessStatuses.SingleOrDefaultAsync(
                    x => x.ProcessId == _processId, stoppingToken);
                if (status is null)
                {
                    status = new ConfigurationProcessStatus
                    {
                        ProcessId = _processId,
                        InstanceName = Environment.MachineName,
                        Roles = "api",
                        StartedAt = _startedAt
                    };
                    db.ConfigurationProcessStatuses.Add(status);
                }
                status.LastSeenAt = DateTimeOffset.UtcNow;
                status.ResourceStatusJson = JsonSerializer.Serialize(runtime.Snapshot());
                var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
                await db.ConfigurationProcessStatuses
                    .Where(x => x.ProcessId != _processId && x.LastSeenAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Unable to report API process status.");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
