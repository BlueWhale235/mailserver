using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed class DeliveryQueue(
    IDbContextFactory<MailDbContext> dbFactory,
    SystemSettingsService settings,
    IOptions<DatabaseOptions> databaseOptions,
    ILogger<DeliveryQueue> logger) : IDeliveryQueue
{
    private readonly DatabaseProvider _provider = databaseOptions.Value.Provider;

    public async Task<IReadOnlyList<DeliveryJob>> LeaseAsync(
        string owner,
        int count,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var nowTicks = now.UtcDateTime.Ticks;
        var jobs = _provider switch
        {
            DatabaseProvider.PostgreSql => await db.DeliveryJobs
                .FromSqlInterpolated($"""
                    SELECT * FROM delivery_jobs
                    WHERE
                        ("Status" IN ('Pending', 'Deferred')
                         OR ("Status" = 'Processing' AND "LeaseUntil" < {now}))
                        AND "NextAttemptAt" <= {now}
                    ORDER BY "NextAttemptAt", "CreatedAt"
                    LIMIT {count}
                    FOR UPDATE SKIP LOCKED
                    """)
                .Include(x => x.Message)
                .ToListAsync(cancellationToken),
            DatabaseProvider.MySql => await db.DeliveryJobs
                .FromSqlInterpolated($"""
                    SELECT * FROM delivery_jobs
                    WHERE
                        (`Status` IN ('Pending', 'Deferred')
                         OR (`Status` = 'Processing' AND `LeaseUntil` < {now}))
                        AND `NextAttemptAt` <= {now}
                    ORDER BY `NextAttemptAt`, `CreatedAt`
                    LIMIT {count}
                    FOR UPDATE SKIP LOCKED
                    """)
                .Include(x => x.Message)
                .ToListAsync(cancellationToken),
            DatabaseProvider.Sqlite => await db.DeliveryJobs
                .FromSqlInterpolated($"""
                    SELECT * FROM delivery_jobs
                    WHERE
                        ("Status" IN ('Pending', 'Deferred')
                         OR ("Status" = 'Processing' AND "LeaseUntil" < {nowTicks}))
                        AND "NextAttemptAt" <= {nowTicks}
                    ORDER BY "NextAttemptAt", "CreatedAt"
                    LIMIT {count}
                    """)
                .Include(x => x.Message)
                .ToListAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException()
        };

        foreach (var job in jobs)
        {
            job.Status = DeliveryStatus.Processing;
            job.LeaseOwner = owner;
            job.LeaseUntil = now.Add(duration);
            job.Attempts++;
            job.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (jobs.Count > 0)
        {
            logger.LogInformation(
                "Delivery queue leased {Count} job(s) for owner={Owner}, leaseUntil={LeaseUntil}.",
                jobs.Count,
                owner,
                now.Add(duration));
        }

        return jobs;
    }

    public async Task CompleteAsync(Guid jobId, DeliveryResult result, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DeliveryJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            DeliveryJobId = job.Id,
            AttemptNumber = job.Attempts,
            Succeeded = result.Succeeded,
            PermanentFailure = result.PermanentFailure,
            RemoteHost = result.RemoteHost,
            Response = result.Response
        });

        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.LastError = result.Succeeded ? null : result.Response;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        var options = await settings.GetDeliveryOptionsAsync(cancellationToken);
        if (result.Succeeded)
        {
            job.Status = DeliveryStatus.Delivered;
        }
        else if (result.PermanentFailure || job.Attempts >= options.MaximumAttempts)
        {
            job.Status = DeliveryStatus.Failed;
        }
        else
        {
            job.Status = DeliveryStatus.Deferred;
            var delayMinutes = Math.Min(Math.Pow(2, Math.Max(0, job.Attempts - 1)), 360);
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(delayMinutes);
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Delivery job {JobId} completed: status={Status}, succeeded={Succeeded}, permanentFailure={PermanentFailure}, attempts={Attempts}, nextAttemptAt={NextAttemptAt}, remoteHost={RemoteHost}, response={Response}.",
            job.Id,
            job.Status,
            result.Succeeded,
            result.PermanentFailure,
            job.Attempts,
            job.NextAttemptAt,
            result.RemoteHost ?? string.Empty,
            result.Response);
    }

    public async Task RetryNowAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DeliveryJobs.SingleAsync(x => x.Id == jobId, cancellationToken);
        job.Status = DeliveryStatus.Pending;
        job.NextAttemptAt = DateTimeOffset.UtcNow;
        job.LeaseOwner = null;
        job.LeaseUntil = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Delivery job {JobId} was reset for immediate retry.", job.Id);
    }

    public async Task DeleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.DeliveryJobs.SingleOrDefaultAsync(x => x.Id == jobId, cancellationToken)
            ?? throw new KeyNotFoundException("Delivery job not found.");
        db.DeliveryJobs.Remove(job);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Delivery job {JobId} was deleted.", jobId);
    }
}
