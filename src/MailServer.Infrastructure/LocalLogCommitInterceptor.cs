using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MailServer.Infrastructure;

internal sealed class LocalLogCommitInterceptor : DbTransactionInterceptor
{
    internal static readonly LocalLogCommitInterceptor Instance = new();
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is MailDbContext db) db.FlushLocalLogsAsync().GetAwaiter().GetResult();
    }
    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is MailDbContext db) await db.FlushLocalLogsAsync();
    }
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is MailDbContext db) db.PendingLogs?.ChangeTracker.Clear();
    }
    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        TransactionRolledBack(transaction, eventData);
        return Task.CompletedTask;
    }
}
