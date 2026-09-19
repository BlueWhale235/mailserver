using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

/// <summary>
/// 外发队列状态机。Worker 通过租约领取任务，所有重试、完成和删除都在这里集中维护。
/// </summary>
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
        // PostgreSQL/MySQL 使用 SKIP LOCKED 支持多 Worker 并发抢占；SQLite 没有等价语义，只依赖事务串行化。
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
            // 租约过期后 Processing 任务可被其它 Worker 重新领取，用于进程崩溃后的自动恢复。
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
        var diagnostic = result.Diagnostic;
        var attempt = new DeliveryAttempt
        {
            DeliveryJobId = job.Id,
            AttemptNumber = job.Attempts,
            Succeeded = result.Succeeded,
            PermanentFailure = result.PermanentFailure,
            RemoteHost = result.RemoteHost,
            RemoteAddress = diagnostic?.RemoteAddress,
            Stage = diagnostic?.Stage ?? (result.Succeeded ? "SMTP" : "UNKNOWN"),
            SmtpStatusCode = diagnostic?.SmtpStatusCode,
            EnhancedStatusCode = diagnostic?.EnhancedStatusCode,
            TlsProtocol = diagnostic?.TlsProtocol,
            TlsCipherSuite = diagnostic?.TlsCipherSuite,
            DurationMilliseconds = diagnostic?.DurationMilliseconds ?? 0,
            FailureCategory = diagnostic?.FailureCategory,
            SessionSummary = Truncate(diagnostic?.SessionSummary, 65_536),
            Response = Truncate(result.Response, 4_096)
        };
        db.DeliveryAttempts.Add(attempt);

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
            // 指数退避上限 6 小时，避免持续失败的任务压垮远端或本机 DNS/SMTP 资源。
            var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
            var delayMinutes = Math.Min(Math.Pow(2, Math.Max(0, job.Attempts - 1)) * jitter, 360);
            job.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(delayMinutes);
            attempt.NextRetryAt = job.NextAttemptAt;
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

    private static string? Truncate(string? value, int maximumLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maximumLength ? value : value[..maximumLength];

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
