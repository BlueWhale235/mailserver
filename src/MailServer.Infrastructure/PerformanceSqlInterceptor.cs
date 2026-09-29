using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MailServer.Infrastructure;

public sealed class PerformanceSqlInterceptor(PerformanceCollector collector) : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<Guid, Pending> _commands = new();
    private static readonly ConditionalWeakTable<IModel, HashSet<string>> Identifiers = new();
    private static readonly HashSet<string> Keywords = new(
        "SELECT FROM WHERE JOIN LEFT RIGHT INNER OUTER CROSS ON AS AND OR NOT NULL IS IN EXISTS BETWEEN LIKE ESCAPE CASE WHEN THEN ELSE END ORDER BY GROUP HAVING LIMIT OFFSET DISTINCT ASC DESC INSERT INTO VALUES UPDATE SET DELETE RETURNING WITH RECURSIVE UNION ALL ANY COUNT SUM AVG MIN MAX COALESCE CAST COLLATE GLOB ISNULL NOTNULL TRUE FALSE BEGIN COMMIT ROLLBACK FOR SKIP LOCKED CONFLICT DO DUPLICATE KEY TABLE CREATE DROP INDEX PRAGMA DEFAULT AUTOINCREMENT PRIMARY UNIQUE IF CURRENT_TIMESTAMP".Split(' '), StringComparer.OrdinalIgnoreCase);
    private sealed record Pending(PerformanceScope Scope, string Fingerprint)
    {
        public double ExecutionMilliseconds;
    }

    internal static (string Fingerprint, string Template) Normalize(string provider, string command, IReadOnlySet<string>? identifiers = null)
    {
        // Do not attempt to retain arbitrary SQL literals: templates are reconstructed from a
        // small lexical allowlist. Quoted identifiers are only retained when identifier-shaped.
        var output = new StringBuilder();
        var safe = command.Length <= 32_768;
        for (var i = 0; safe && i < command.Length; i++)
        {
            var c = command[i];
            if (c == '#' || c == '-' && i + 1 < command.Length && command[i + 1] == '-')
            { while (i < command.Length && command[i] != '\n') i++; output.Append(' '); continue; }
            if (c == '/' && i + 1 < command.Length && command[i + 1] == '*')
            {
                var end = command.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) { safe = false; break; }
                i = end + 1; output.Append(' '); continue;
            }
            if (c == '\'')
            {
                var closed = false;
                while (++i < command.Length)
                {
                    if (command[i] == '\\') { i++; continue; }
                    if (command[i] != '\'') continue;
                    if (i + 1 < command.Length && command[i + 1] == '\'') { i++; continue; }
                    closed = true; break;
                }
                safe = closed; output.Append('?'); continue;
            }
            if (c is '"' or '`' or '[')
            {
                var endChar = c == '[' ? ']' : c;
                var end = command.IndexOf(endChar, i + 1);
                if (end < 0) { safe = false; break; }
                var identifier = command.AsSpan(i + 1, end - i - 1);
                // SQLite also accepts double-quoted string literals. Retain only names
                // present in our EF schema, never arbitrary quoted values or aliases.
                if (identifier.Length is > 0 and <= 128 && identifiers?.Contains(identifier.ToString()) == true
                    && !(c == '"' && provider == "mysql"))
                    output.Append(c).Append(identifier).Append(endChar);
                else output.Append('?');
                i = end; continue;
            }
            if (c == '@' || c == ':' || c == '$')
            {
                while (i + 1 < command.Length && (char.IsLetterOrDigit(command[i + 1]) || command[i + 1] == '_')) i++;
                output.Append('?'); continue;
            }
            if (char.IsDigit(c))
            {
                while (i + 1 < command.Length && (char.IsLetterOrDigit(command[i + 1]) || command[i + 1] is '.' or '_')) i++;
                output.Append('?'); continue;
            }
            if (char.IsWhiteSpace(c)) { if (output.Length > 0 && output[^1] != ' ') output.Append(' '); }
            else if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = i;
                while (i + 1 < command.Length && (char.IsAsciiLetterOrDigit(command[i + 1]) || command[i + 1] == '_')) i++;
                var word = command[start..(i + 1)];
                output.Append(Keywords.Contains(word) || identifiers?.Contains(word) == true ? word : "?");
            }
            else if ("(),.=<>!+-*/%;?".Contains(c)) output.Append(c);
            else safe = false;
        }
        var normalized = safe ? output.ToString().Trim() : "[SQL template unavailable]";
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provider + ":" + normalized)));
        return (fingerprint, normalized.Length <= 2048 ? normalized : normalized[..2048]);
    }

    private void Begin(DbCommand command, CommandEventData data)
    {
        if (PerformanceTelemetry.IsSuppressed || PerformanceTelemetry.CurrentScope.Value is not { } parent) return;
        var scope = parent.CreateChild("database.command", "sql", ambient: false);
        if (scope is null) return;
        var provider = command.Connection?.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true ? "sqlite" : "mysql";
        var identifiers = data.Context is null ? null : Identifiers.GetValue(data.Context.Model, model =>
            model.GetEntityTypes().SelectMany(entity => entity.GetProperties().Select(property => property.GetColumnName())
                .Append(entity.GetTableName() ?? "")).ToHashSet(StringComparer.Ordinal));
        var (fingerprint, template) = Normalize(provider, command.CommandText, identifiers);
        collector.SqlTemplate(fingerprint, provider, template);
        _commands[data.CommandId] = new Pending(scope, fingerprint);
    }
    private void End(Guid id, double duration, Exception? error = null, long? rows = null)
    {
        if (!_commands.TryRemove(id, out var pending)) return;
        if (error is not null) pending.Scope.Fail(error);
        pending.Scope.CompleteSql(pending.Fingerprint, duration, rows);
    }
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Begin(command, eventData); return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Begin(command, eventData); return ValueTask.FromResult(result); }
    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    { if (_commands.TryGetValue(eventData.CommandId, out var pending)) pending.ExecutionMilliseconds = eventData.Duration.TotalMilliseconds; return result; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default) => ValueTask.FromResult(ReaderExecuted(command, eventData, result));
    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        if (_commands.TryGetValue(eventData.CommandId, out var pending))
            End(eventData.CommandId, pending.ExecutionMilliseconds);
        return result;
    }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Begin(command, eventData); return result; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Begin(command, eventData); return ValueTask.FromResult(result); }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    { End(eventData.CommandId, eventData.Duration.TotalMilliseconds, rows: result); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(NonQueryExecuted(command, eventData, result));
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    { Begin(command, eventData); return result; }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { Begin(command, eventData); return ValueTask.FromResult(result); }
    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    { End(eventData.CommandId, eventData.Duration.TotalMilliseconds); return result; }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default) => ValueTask.FromResult(ScalarExecuted(command, eventData, result));
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => End(eventData.CommandId, eventData.Duration.TotalMilliseconds, eventData.Exception);
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    { CommandFailed(command, eventData); return Task.CompletedTask; }
    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) => End(eventData.CommandId, eventData.Duration.TotalMilliseconds, new OperationCanceledException());
    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    { CommandCanceled(command, eventData); return Task.CompletedTask; }
}
