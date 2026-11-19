using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MailServer.Infrastructure;

/// <summary>Business instrumentation never waits for diagnostic storage.</summary>
public static class PerformanceTelemetry
{
    internal static readonly AsyncLocal<PerformanceScope?> CurrentScope = new();
    private static readonly AsyncLocal<int> Suppressed = new();
    public static readonly ActivitySource Source = new("MailServer.Performance");
    internal static PerformanceCollector? Collector;
    public static string? TraceId => CurrentScope.Value?.Root.Record.TraceId;
    public static bool IsSuppressed => Suppressed.Value > 0;
    public static IDisposable Suppress()
    {
        Suppressed.Value++;
        return new Callback(() => Suppressed.Value--);
    }
    public static PerformanceScope? Operation(string module, string name, string? sourceTraceId = null)
    {
        if (IsSuppressed) return null;
        if (CurrentScope.Value is not null) return Stage(name);
        return Collector?.Start(module, name, sourceTraceId);
    }
    public static PerformanceScope? Stage(string name, string category = "processing") =>
        IsSuppressed || CurrentScope.Value is not { } parent ? null : parent.CreateChild(name, category);
    public static void Resource(long? messageId = null, long? deliveryJobId = null, int? attempt = null)
    {
        if (CurrentScope.Value is not { } scope) return;
        lock (scope.Root.Gate)
        {
            if (messageId is not null) scope.Root.Record.MessageId = messageId;
            if (deliveryJobId is not null) scope.Root.Record.DeliveryJobId = deliveryJobId;
            if (attempt is not null) scope.Root.Record.Attempt = attempt;
        }
    }
    public static void Fail(Exception error) => CurrentScope.Value?.Fail(error);
    public static void Status(string status) => CurrentScope.Value?.SetStatus(status);
    internal static void RecordDuration(string name, TimeSpan duration, Exception? error = null)
    {
        if (IsSuppressed || CurrentScope.Value is not { } parent) return;
        parent.CreateChild(name, "processing", ambient: false)?.CompleteMeasured(duration, error);
    }
    public static async Task<T> MeasureAsync<T>(string name, Func<Task<T>> action, string category = "processing")
    {
        using var scope = Stage(name, category);
        try { return await action(); }
        catch (Exception error) { scope?.Fail(error); throw; }
    }
    private sealed class Callback(Action action) : IDisposable { public void Dispose() => action(); }
}

public sealed class PerformanceCollector : IDisposable
{
    internal const int Reservation = 64 * 1024;
    private readonly ConcurrentQueue<PerformanceOperation> _queue = new();
    private readonly object _aggregateGate = new();
    private readonly Dictionary<string, Aggregate> _aggregates = new();
    private readonly Dictionary<string, PerformanceSql> _sql = new();
    private readonly ActivityListener _listener;
    private readonly ResourceCacheBudget? _cacheBudget;
    private long _reserved;
    private long _dropped;
    private long _droppedAggregates;
    private long _truncated;
    private PerformanceSettings _settings = new();
    public PerformanceSettings Settings { get => Volatile.Read(ref _settings); set => Volatile.Write(ref _settings, value); }
    internal void ApplyStoredSettings(PerformanceSettings observed, PerformanceSettings loaded) =>
        Interlocked.CompareExchange(ref _settings, loaded, observed);
    public string ProcessId { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{System.Guid.NewGuid()}";
    private long _budgetBytes = 8 * 1024 * 1024;
    public long BudgetBytes { get => Interlocked.Read(ref _budgetBytes); set => Interlocked.Exchange(ref _budgetBytes, Math.Max(0, value)); }
    public bool StorageUnavailable { get; set; }
    private int AggregateLimit => (int)Math.Clamp(BudgetBytes / 4 / 4096, 0, 512);
    public long EstimatedBufferBytes
    {
        get
        {
            lock (_aggregateGate) return Interlocked.Read(ref _reserved) + _aggregates.Count * 2048L + _sql.Count * 8192L;
        }
    }

    public PerformanceCollector(ResourceRuntime? resources = null)
    {
        _cacheBudget = resources?.CacheBudgets;
        if (_cacheBudget is not null)
        {
            _cacheBudget.Changed += ApplyBudget;
            ApplyBudget(_cacheBudget.Allocation);
        }
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "MailServer.Performance",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_listener);
    }

    private void ApplyBudget(CacheBudgetAllocation allocation) => BudgetBytes = allocation.DiagnosticsBytes;

    public PerformanceScope? Start(string module, string name, string? sourceTraceId = null)
    {
        if (!Settings.Enabled) return null;
        // Reserve the entire bounded operation before collecting any children. Aggregate storage
        // has its own fixed quarter of the process budget.
        if (Interlocked.Add(ref _reserved, Reservation) > BudgetBytes * 3 / 4)
        {
            Interlocked.Add(ref _reserved, -Reservation);
            Interlocked.Increment(ref _dropped);
            return null;
        }
        return new PerformanceScope(this, module, name, sourceTraceId);
    }

    internal void Observe(string kind, string module, string name, double milliseconds, string status, bool slow)
    {
        var minute = DateTimeOffset.UtcNow.UtcTicks / TimeSpan.TicksPerMinute;
        var key = $"{minute}:{kind}:{module}:{name}";
        lock (_aggregateGate)
        {
            if (!_aggregates.TryGetValue(key, out var aggregate))
            {
                if (_aggregates.Count >= AggregateLimit)
                {
                    Interlocked.Increment(ref _droppedAggregates);
                    return;
                }
                aggregate = new Aggregate(new PerformanceMinute { ProcessId = ProcessId,
                    Minute = new DateTimeOffset(minute * TimeSpan.TicksPerMinute, TimeSpan.Zero),
                    Kind = kind, Module = module, Name = name });
                _aggregates.Add(key, aggregate);
            }
            aggregate.Row.Count++;
            if (status is "error" or "timeout") aggregate.Row.Errors++;
            if (slow) aggregate.Row.Slow++;
            aggregate.Row.TotalMilliseconds += milliseconds;
            aggregate.Row.MaximumMilliseconds = Math.Max(aggregate.Row.MaximumMilliseconds, milliseconds);
            aggregate.Buckets[HistogramBucket(milliseconds)]++;
        }
    }

    internal void SqlTemplate(string fingerprint, string provider, string template)
    {
        lock (_aggregateGate)
        {
            if (!_sql.ContainsKey(fingerprint) && _sql.Count >= AggregateLimit / 4) return;
            _sql[fingerprint] = new PerformanceSql { Fingerprint = fingerprint, Provider = provider,
                Template = template, LastSeenAt = DateTimeOffset.UtcNow };
        }
    }

    internal void Complete(PerformanceScope root)
    {
        var record = root.Record;
        var options = Settings;
        Observe("operation", record.Module, record.Name, record.ProcessingMilliseconds, record.Status, record.Slow);
        if (record.Truncated) Interlocked.Increment(ref _truncated);
        if (record.Slow || record.Status is "error" or "timeout"
            || options.DiagnosticUntil > DateTimeOffset.UtcNow || Random.Shared.NextDouble() < options.SampleRate)
        {
            record.DetailsJson = JsonSerializer.Serialize(root.SnapshotStages());
            record.DetailBytes = Encoding.UTF8.GetByteCount(record.DetailsJson) + 1024;
            if (record.DetailBytes > Reservation)
            {
                record.Truncated = true;
                record.DetailsJson = "[]";
                record.DetailBytes = 1024;
            }
            _queue.Enqueue(record);
        }
        else Interlocked.Add(ref _reserved, -Reservation);
    }

    public List<PerformanceOperation> DrainOperations()
    {
        var rows = new List<PerformanceOperation>();
        while (rows.Count < 100 && _queue.TryDequeue(out var row)) rows.Add(row);
        return rows;
    }
    public void ReleaseOperations(int count, bool lost)
    {
        Interlocked.Add(ref _reserved, -Reservation * count);
        if (lost) Interlocked.Add(ref _dropped, count);
    }
    public (List<PerformanceMinute> Minutes, List<PerformanceSql> Sql) DrainAggregates()
    {
        lock (_aggregateGate)
        {
            var rows = _aggregates.Values.Select(x =>
            {
                x.Row.HistogramJson = JsonSerializer.Serialize(x.Buckets);
                return x.Row;
            }).ToList();
            var sql = _sql.Values.ToList();
            _aggregates.Clear();
            _sql.Clear();
            return (rows, sql);
        }
    }
    public void LostAggregates(long count) => Interlocked.Add(ref _droppedAggregates, count);
    public PerformanceProcess ProcessStatus() => new() { ProcessId = ProcessId, LastSeenAt = DateTimeOffset.UtcNow,
        BufferBytes = Interlocked.Read(ref _reserved), BudgetBytes = BudgetBytes,
        DroppedOperations = Interlocked.Read(ref _dropped), DroppedAggregates = Interlocked.Read(ref _droppedAggregates),
        TruncatedOperations = Interlocked.Read(ref _truncated), StorageUnavailable = StorageUnavailable };
    public static int HistogramBucket(double milliseconds) => Math.Clamp((int)Math.Ceiling(Math.Log2(Math.Max(1, milliseconds))), 0, 24);
    public static double? Percentile(long[] histogram, double fraction)
    {
        var count = histogram.Sum();
        if (count == 0) return null;
        var threshold = (long)Math.Ceiling(count * fraction);
        long total = 0;
        for (var i = 0; i < histogram.Length; i++) { total += histogram[i]; if (total >= threshold) return Math.Pow(2, i); }
        return null;
    }
    public void Dispose()
    {
        if (_cacheBudget is not null) _cacheBudget.Changed -= ApplyBudget;
        _listener.Dispose();
    }
    private sealed record Aggregate(PerformanceMinute Row) { public long[] Buckets { get; } = new long[25]; }
}

public sealed class PerformanceScope : IDisposable
{
    internal readonly object Gate = new();
    private readonly PerformanceCollector _collector;
    private readonly PerformanceScope? _previous;
    private readonly Activity? _activity;
    private long _start = Stopwatch.GetTimestamp();
    private readonly List<PerformanceStage> _stages = new();
    private readonly Dictionary<string, int> _sqlCounts = new();
    private int _stageCount;
    private int _disposed;
    private bool _retainStage = true;
    private string _status = "ok";
    private string? _error;
    internal PerformanceScope Root { get; }
    internal PerformanceOperation Record { get; }
    internal string Id { get; } = ActivitySpanId.CreateRandom().ToString();
    internal string Name { get; }
    internal string Category { get; }

    internal PerformanceScope(PerformanceCollector collector, string module, string name, string? sourceTraceId)
    {
        _collector = collector;
        Root = this;
        Name = name.Length > 128 ? name[..128] : name;
        Category = "processing";
        _previous = PerformanceTelemetry.CurrentScope.Value;
        _activity = PerformanceTelemetry.Source.StartActivity(Name, ActivityKind.Internal);
        Record = new PerformanceOperation { TraceId = _activity?.TraceId.ToString() ?? ActivityTraceId.CreateRandom().ToString(),
            SourceTraceId = sourceTraceId, ProcessId = collector.ProcessId, Module = module,
            Name = Name, StartedAt = DateTimeOffset.UtcNow };
        PerformanceTelemetry.CurrentScope.Value = this;
    }
    private PerformanceScope(PerformanceScope parent, string name, string category, bool ambient)
    {
        Root = parent.Root;
        _collector = parent._collector;
        Record = Root.Record;
        _previous = parent;
        Name = name.Length > 128 ? name[..128] : name;
        Category = category;
        if (ambient)
        {
            _activity = PerformanceTelemetry.Source.StartActivity(Name);
            PerformanceTelemetry.CurrentScope.Value = this;
        }
    }
    internal PerformanceScope? CreateChild(string name, string category, bool ambient = true)
    {
        lock (Root.Gate)
        {
            if (Root._disposed != 0) return null;
            if (Root._stageCount >= 256)
            {
                Record.Truncated = true;
                // Keep exact SQL totals even when the detail timeline is full.
                return category == "sql" ? new PerformanceScope(this, name, category, ambient) { _retainStage = false } : null;
            }
            Root._stageCount++;
        }
        return new PerformanceScope(this, name, category, ambient);
    }
    public void SetStatus(string status)
    {
        if (status is not ("ok" or "error" or "timeout" or "cancelled" or "rejected")) return;
        _status = status;
        if (status != "ok" && Root._status is not ("error" or "timeout")) Root._status = status;
    }
    public void SetQueueTimes(double wait, double backoff)
    {
        Record.ClockAnomaly = wait < 0 || backoff < 0;
        Record.QueueWaitMilliseconds = Math.Max(0, wait);
        Record.RetryBackoffMilliseconds = Math.Max(0, backoff);
    }
    public void Fail(Exception error)
    {
        _error = error.GetType().Name;
        SetStatus(error is TimeoutException ? "timeout" : error is OperationCanceledException ? "cancelled" : "error");
    }
    internal void CompleteSql(string fingerprint, double executionMilliseconds, long? rows = null)
    {
        lock (Root.Gate)
        {
            if (Root._disposed != 0) return;
            Record.SqlCount++;
            Record.SqlMilliseconds += executionMilliseconds;
            var count = Root._sqlCounts.GetValueOrDefault(fingerprint) + 1;
            if (Root._sqlCounts.Count < 256 || Root._sqlCounts.ContainsKey(fingerprint)) Root._sqlCounts[fingerprint] = count;
            else Record.Truncated = true;
            Record.Slow |= executionMilliseconds >= _collector.Settings.SqlMilliseconds
                || count >= _collector.Settings.RepeatedSqlCount;
        }
        _collector.Observe("sql", Record.Module, fingerprint, executionMilliseconds, _status,
            executionMilliseconds >= _collector.Settings.SqlMilliseconds);
        Finish(fingerprint, executionMilliseconds, rows);
    }
    public void Dispose() => Finish(null, null, null);
    internal void CompleteMeasured(TimeSpan duration, Exception? error)
    {
        // EF supplies elapsed client time for connection/transaction callbacks.
        _start = Stopwatch.GetTimestamp() - (long)(Math.Max(0, duration.TotalSeconds) * Stopwatch.Frequency);
        if (error is not null) Fail(error);
        Dispose();
    }
    private void Finish(string? fingerprint, double? execution, long? rows)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var duration = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        if (PerformanceTelemetry.CurrentScope.Value == this) PerformanceTelemetry.CurrentScope.Value = _previous;
        _activity?.Dispose();
        if (Root != this)
        {
            var offset = Stopwatch.GetElapsedTime(Root._start, _start).TotalMilliseconds;
            var slow = false;
            lock (Root.Gate)
            {
                if (Root._disposed != 0) { Record.Truncated = true; return; }
                if (_retainStage) Root._stages.Add(new PerformanceStage(Id, _previous == Root ? null : _previous?.Id,
                    Name, Category, offset, duration, duration, _status, _error, fingerprint, execution, rows));
                var processing = duration - Union(Root._stages.Where(x => x.Category == "wait")
                    .Select(x => (Math.Max(offset, x.OffsetMilliseconds), Math.Min(offset + duration, x.OffsetMilliseconds + x.DurationMilliseconds)))
                    .Where(x => x.Item2 > x.Item1));
                slow = Category == "processing" && processing >= _collector.Settings.StageMilliseconds;
                Record.Slow |= slow;
            }
            if (fingerprint is null) _collector.Observe("stage", Record.Module, Name, duration, _status, slow);
            return;
        }
        lock (Gate)
        {
            Record.DurationMilliseconds = duration;
            Record.ProcessingMilliseconds = Math.Max(0, duration - Union(_stages.Where(x => x.Category == "wait")
                .Select(x => (x.OffsetMilliseconds, Math.Min(duration, x.OffsetMilliseconds + x.DurationMilliseconds)))));
            Record.Status = _status;
            Record.Slow |= Record.ProcessingMilliseconds >= _collector.Settings.Threshold(Record.Module)
                || Record.SqlMilliseconds >= _collector.Settings.SqlTotalMilliseconds;
            if (_stages.Count < Math.Min(_stageCount, 256)) Record.Truncated = true;
            _collector.Complete(this);
        }
    }
    internal PerformanceStage[] SnapshotStages() => _stages.OrderBy(x => x.OffsetMilliseconds).Select(stage => stage with
    {
        SelfMilliseconds = Math.Max(0, stage.DurationMilliseconds - Union(_stages.Where(child => child.ParentId == stage.Id)
            .Select(child => (Math.Max(stage.OffsetMilliseconds, child.OffsetMilliseconds),
                Math.Min(stage.OffsetMilliseconds + stage.DurationMilliseconds, child.OffsetMilliseconds + child.DurationMilliseconds)))))
    }).ToArray();
    internal static double Union(IEnumerable<(double Start, double End)> ranges)
    {
        double total = 0, end = 0;
        foreach (var range in ranges.OrderBy(x => x.Start))
        {
            total += Math.Max(0, range.End - Math.Max(range.Start, end));
            end = Math.Max(end, range.End);
        }
        return total;
    }
}
