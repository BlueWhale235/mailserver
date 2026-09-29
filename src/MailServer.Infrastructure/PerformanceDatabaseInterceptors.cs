using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MailServer.Infrastructure;

public sealed class PerformanceConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        PerformanceTelemetry.RecordDuration("database.open", eventData.Duration);
    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    { ConnectionOpened(connection, eventData); return Task.CompletedTask; }
    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) =>
        PerformanceTelemetry.RecordDuration("database.open", eventData.Duration, eventData.Exception);
    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    { ConnectionFailed(connection, eventData); return Task.CompletedTask; }
    public override void ConnectionCanceled(DbConnection connection, ConnectionEndEventData eventData) =>
        PerformanceTelemetry.RecordDuration("database.open", eventData.Duration, new OperationCanceledException());
    public override Task ConnectionCanceledAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    { ConnectionCanceled(connection, eventData); return Task.CompletedTask; }
}

public sealed class PerformanceTransactionInterceptor : DbTransactionInterceptor
{
    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    { PerformanceTelemetry.RecordDuration("database.begin-transaction", eventData.Duration); return result; }
    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(TransactionStarted(connection, eventData, result));
    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        PerformanceTelemetry.RecordDuration("database.commit", eventData.Duration);
    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    { TransactionCommitted(transaction, eventData); return Task.CompletedTask; }
    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) =>
        PerformanceTelemetry.RecordDuration("database.rollback", eventData.Duration);
    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    { TransactionRolledBack(transaction, eventData); return Task.CompletedTask; }
    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) =>
        PerformanceTelemetry.RecordDuration("database.transaction-failed", eventData.Duration, eventData.Exception);
    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    { TransactionFailed(transaction, eventData); return Task.CompletedTask; }
}
