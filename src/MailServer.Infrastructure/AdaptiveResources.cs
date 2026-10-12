using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

public sealed class AdaptiveResourceOptions
{
    public const string SectionName = "AdaptiveResources";
    public bool Enabled { get; set; }
    public bool AutomaticEnabled { get; set; }
    public bool PauseBackfill { get; set; } = true;
    public int InitialInbound { get; set; } = 16;
    public int InitialOutbound { get; set; } = 32;
    public int InitialHeavy { get; set; } = 4;
    public int MaximumInbound { get; set; } = 32;
    public int MaximumOutbound { get; set; } = 64;
    public int MaximumHeavy { get; set; } = 12;
    public int DatabaseBaselineWindowSeconds { get; set; } = 10;
    public static AdaptiveResourceOptions Read(IConfiguration configuration)
    {
        var o = new AdaptiveResourceOptions(); configuration.GetSection(SectionName).Bind(o);
        if (o.InitialInbound < 1 || o.InitialOutbound < 1 || o.InitialHeavy < 1 || o.MaximumInbound < o.InitialInbound
            || o.MaximumOutbound < o.InitialOutbound || o.MaximumHeavy < o.InitialHeavy || o.MaximumInbound > 32
            || o.MaximumOutbound > 64 || o.MaximumHeavy > 12 || o.DatabaseBaselineWindowSeconds < 0 || o.DatabaseBaselineWindowSeconds > 60) throw new ArgumentException("Invalid adaptive experiment limits.");
        return o;
    }
}
public sealed record AdaptiveTelemetrySnapshot(long InboundCompleted, long OutboundCompleted, int InboundDemand,
    int OutboundDemand, double OperationP95Milliseconds, double DatabaseP95Milliseconds, int DatabaseSamples, long HeavyEstimateBytes,
    double InboundP95Milliseconds = 0, double OutboundP95Milliseconds = 0);
public sealed class AdaptiveResourceTelemetry(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Queue<(DateTimeOffset At, double Value)> _operations = new(), _database = new(), _inboundOperations = new(), _outboundOperations = new();
    private long _inbound, _outbound, _heavy = 256L * 1024 * 1024;
    private int _inboundDemand, _outboundDemand;
    public void RecordInboundDemand() => Interlocked.Increment(ref _inboundDemand);
    public void RecordOutboundDemand() => Interlocked.Increment(ref _outboundDemand);
    public void RecordCompletion(bool inbound, TimeSpan duration)
    {
        if (inbound) Interlocked.Increment(ref _inbound); else Interlocked.Increment(ref _outbound);
        lock (_sync) { Add(_operations, duration.TotalMilliseconds); Add(inbound ? _inboundOperations : _outboundOperations, duration.TotalMilliseconds); }
    }
    public void RecordDatabase(TimeSpan duration) { lock (_sync) Add(_database, duration.TotalMilliseconds); }
    public void RecordHeavyCost(long bytes) { if (bytes > 0) lock (_sync) _heavy = Math.Max(_heavy, bytes); }
    private void Add(Queue<(DateTimeOffset At, double Value)> q, double value)
    { q.Enqueue((_clock.GetUtcNow(), value)); while (q.Count > 512) q.Dequeue(); }
    private void Expire(Queue<(DateTimeOffset At, double Value)> q)
    { var cutoff = _clock.GetUtcNow().AddSeconds(-6); while (q.TryPeek(out var item) && item.At < cutoff) q.Dequeue(); }
    private static double P95(Queue<(DateTimeOffset At, double Value)> q) => q.Count == 0 ? 0 : q.Select(x => x.Value).Order().ElementAt((int)Math.Ceiling(q.Count * .95) - 1);
    public AdaptiveTelemetrySnapshot Snapshot(bool resetDemand = true)
    {
        lock (_sync)
        {
        Expire(_operations); Expire(_database); Expire(_inboundOperations); Expire(_outboundOperations);
        return new(Interlocked.Read(ref _inbound), Interlocked.Read(ref _outbound),
            resetDemand ? Interlocked.Exchange(ref _inboundDemand, 0) : Volatile.Read(ref _inboundDemand),
            resetDemand ? Interlocked.Exchange(ref _outboundDemand, 0) : Volatile.Read(ref _outboundDemand),
            P95(_operations), P95(_database), _database.Count, _heavy, P95(_inboundOperations), P95(_outboundOperations));
        }
    }
}
public sealed class AdaptiveDatabaseLatencyInterceptor(ResourceRuntime runtime) : DbCommandInterceptor
{
    private void Record(CommandExecutedEventData e) { if (runtime.AdaptiveOptions.Enabled) runtime.Telemetry.RecordDatabase(e.Duration); }
    public override DbDataReader ReaderExecuted(DbCommand c, CommandExecutedEventData e, DbDataReader r) { Record(e); return r; }
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData e, DbDataReader r, CancellationToken t = default) { Record(e); return ValueTask.FromResult(r); }
    public override int NonQueryExecuted(DbCommand c, CommandExecutedEventData e, int r) { Record(e); return r; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData e, int r, CancellationToken t = default) { Record(e); return ValueTask.FromResult(r); }
    public override object? ScalarExecuted(DbCommand c, CommandExecutedEventData e, object? r) { Record(e); return r; }
    public override ValueTask<object?> ScalarExecutedAsync(DbCommand c, CommandExecutedEventData e, object? r, CancellationToken t = default) { Record(e); return ValueTask.FromResult(r); }
}
public sealed record AdaptiveResourceSample(DateTimeOffset At, bool Valid, long MemoryHeadroomBytes,
    long HostAvailableBytes, double CpuPercent, double GcPausePercent, AdaptiveTelemetrySnapshot Telemetry,
    int InboundWaiting, int OutboundWaiting, int HeavyWaiting,
    double HostCpuPercent = 0, double HostCpuPressurePercent = 0, bool HostMetricsValid = true);
public sealed record AdaptiveResourceDecision(int Inbound, int Outbound, int Heavy, string Reason, bool StopRequested = false);

// Pure state machine: sampling, experiment orchestration, and limits can be tested independently.
public sealed class AdaptiveResourcePolicy(AdaptiveResourceOptions options)
{
    private const long MiB = 1024 * 1024;
    private double _databaseBaseline;
    private DateTimeOffset _baselineStart;
    private bool _baselineFrozen;
    private DateTimeOffset _lastDecision, _lastHeavy, _cooldown, _criticalSince, _gcSince, _databaseSince;
    private int _nextDimension;
    private (DateTimeOffset At, AdaptiveResourceDecision Before, long Completed, double P95, double Rate, int Dimension)? _pending;
    private (DateTimeOffset At, long Inbound, long Outbound)? _previous;
    public AdaptiveResourceDecision Evaluate(AdaptiveResourceSample s, int inbound, int outbound, int heavy)
    {
        var current = new AdaptiveResourceDecision(inbound, outbound, heavy, "hold");
        var previous = _previous;
        double Rate(int dimension) => previous is { } p && s.At > p.At
            ? (Completed(s.Telemetry, dimension) - (dimension == 0 ? p.Inbound : dimension == 1 ? p.Outbound : p.Inbound + p.Outbound)) / (s.At - p.At).TotalSeconds : 0;
        _previous = (s.At, s.Telemetry.InboundCompleted, s.Telemetry.OutboundCompleted);
        if (!s.Valid) { _pending = null; return new(options.InitialInbound, options.InitialOutbound, options.InitialHeavy, "sample-unavailable"); }
        if (_baselineStart == default) _baselineStart = s.At;
        if (!_baselineFrozen && s.Telemetry.DatabaseSamples >= 16)
        {
            _databaseBaseline = Math.Max(_databaseBaseline, Math.Max(.1, s.Telemetry.DatabaseP95Milliseconds));
            if ((s.At - _baselineStart).TotalSeconds >= options.DatabaseBaselineWindowSeconds) _baselineFrozen = true;
        }
        _criticalSince = Since(s.MemoryHeadroomBytes < 128 * MiB, _criticalSince, s.At);
        _gcSince = Since(s.GcPausePercent > 25, _gcSince, s.At);
        _databaseSince = Since(_baselineFrozen && s.Telemetry.DatabaseP95Milliseconds > _databaseBaseline * 3, _databaseSince, s.At);
        if (Elapsed(_criticalSince, s.At, 5)) return current with { Reason = "stop-memory", StopRequested = true };
        if (!options.AutomaticEnabled) return current with { Reason = "fixed" };
        if (s.MemoryHeadroomBytes < 384 * MiB || Elapsed(_gcSince, s.At, 5) || Elapsed(_databaseSince, s.At, 10))
        {
            if (s.At < _cooldown) return current with { Reason = "downscale-cooldown" };
            _cooldown = s.At.AddSeconds(10); _pending = null;
            return new(Math.Max(1, (int)Math.Floor(inbound * .75)), Math.Max(1, (int)Math.Floor(outbound * .75)),
                Math.Max(1, (int)Math.Floor(heavy * .75)), "downscale-pressure");
        }
        if (!s.HostMetricsValid)
        { _pending = null; return new(options.InitialInbound, options.InitialOutbound, options.InitialHeavy, "sample-unavailable"); }
        if (_pending is { } increase)
        {
            if ((s.At - increase.At).TotalSeconds < 6) return current with { Reason = "observe-increase" };
            var observed = (Completed(s.Telemetry, increase.Dimension) - increase.Completed) / (s.At - increase.At).TotalSeconds;
            _pending = null;
            if (observed < increase.Rate * 1.05 && Latency(s.Telemetry, increase.Dimension) > increase.P95 * 1.2)
            { _cooldown = s.At.AddSeconds(10); return increase.Before with { Reason = "revert-no-benefit" }; }
        }
        if (s.At < _cooldown || (s.At - _lastDecision).TotalSeconds < 2) return current;
        _lastDecision = s.At;
        if (!_baselineFrozen || s.MemoryHeadroomBytes <= 768 * MiB || s.CpuPercent >= 90
            || s.GcPausePercent >= 15 || s.HostCpuPercent >= 95 || s.HostCpuPressurePercent >= 20
            || s.Telemetry.DatabaseP95Milliseconds >= _databaseBaseline * 2) return current with { Reason = "capacity-guard" };
        var inDemand = s.InboundWaiting > 0 || s.Telemetry.InboundDemand > 0;
        var outDemand = s.OutboundWaiting > 0 || s.Telemetry.OutboundDemand > 0;
        for (var offset = 0; offset < 3; offset++)
        {
            var dimension = (_nextDimension + offset) % 3;
            AdaptiveResourceDecision? next = dimension switch
            {
                0 when inDemand && inbound < options.MaximumInbound => current with { Inbound = Math.Min(options.MaximumInbound, inbound + 2), Reason = "increase-inbound" },
                1 when outDemand && outbound < options.MaximumOutbound => current with { Outbound = Math.Min(options.MaximumOutbound, outbound + 4), Reason = "increase-outbound" },
                2 when s.HeavyWaiting > 0 && s.MemoryHeadroomBytes > 1024 * MiB && heavy < options.MaximumHeavy
                    && (s.At - _lastHeavy).TotalSeconds >= 6 && s.MemoryHeadroomBytes - 512 * MiB >= s.Telemetry.HeavyEstimateBytes
                    => current with { Heavy = heavy + 1, Reason = "increase-heavy" },
                _ => null
            };
            if (next is null) continue;
            if (dimension == 2) _lastHeavy = s.At;
            _nextDimension = (dimension + 1) % 3;
            _pending = (s.At, current, Completed(s.Telemetry, dimension), Latency(s.Telemetry, dimension), Rate(dimension), dimension);
            return next;
        }
        return current;
    }
    private static long Completed(AdaptiveTelemetrySnapshot t, int dimension) => dimension == 0 ? t.InboundCompleted : dimension == 1 ? t.OutboundCompleted : t.InboundCompleted + t.OutboundCompleted;
    private static double Latency(AdaptiveTelemetrySnapshot t, int dimension) => dimension == 0 ? t.InboundP95Milliseconds : dimension == 1 ? t.OutboundP95Milliseconds : Math.Max(t.InboundP95Milliseconds, t.OutboundP95Milliseconds);

    private static DateTimeOffset Since(bool condition, DateTimeOffset previous, DateTimeOffset now) => condition ? previous == default ? now : previous : default;
    private static bool Elapsed(DateTimeOffset since, DateTimeOffset now, int seconds) => since != default && (now - since).TotalSeconds >= seconds;
}

public sealed class AdaptiveResourceSampler
{
    private readonly Process _process = Process.GetCurrentProcess();
    private long _lastTicks = Stopwatch.GetTimestamp();
    private TimeSpan _lastCpu, _lastPause;
    private long _hostTotal, _hostIdle;
    private readonly string? _cgroup;
    public AdaptiveResourceSampler()
    {
        _lastCpu = _process.TotalProcessorTime; _lastPause = GC.GetTotalPauseDuration();
        if (OperatingSystem.IsLinux())
        {
            try { var line = File.ReadLines("/proc/self/cgroup").FirstOrDefault(x => x.StartsWith("0::"));
                if (line is not null) _cgroup = Path.Combine("/sys/fs/cgroup", line[3..].TrimStart('/')); } catch (IOException) { }
        }
    }
    public AdaptiveResourceSample Sample(ResourceRuntime runtime)
    {
        try
        {
            var now = Stopwatch.GetTimestamp(); var elapsed = Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds; _lastTicks = now;
            var cpu = _process.TotalProcessorTime; var gc = GC.GetTotalPauseDuration();
            var cores = (double)Environment.ProcessorCount;
            long headroom = 0, host = 0; bool bounded = false;
            if (_cgroup is not null)
            {
                var memory = File.ReadAllText(Path.Combine(_cgroup, "memory.max")).Trim();
                var used = long.Parse(File.ReadAllText(Path.Combine(_cgroup, "memory.current")).Trim(), CultureInfo.InvariantCulture);
                if (long.TryParse(memory, out var max)) { bounded = true; headroom = max - used; }
                var quota = File.ReadAllText(Path.Combine(_cgroup, "cpu.max")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (quota[0] != "max") cores = Math.Min(cores, double.Parse(quota[0], CultureInfo.InvariantCulture) / double.Parse(quota[1], CultureInfo.InvariantCulture));
            }
            if (OperatingSystem.IsLinux())
            {
                var line = File.ReadLines("/proc/meminfo").First(x => x.StartsWith("MemAvailable:"));
                host = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
                if (!bounded) headroom = host;
            }
            var hostCpu = 0d; var hostPressure = 0d; var hostValid = false;
            if (OperatingSystem.IsLinux())
            {
                try
                {
                var counters = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                var total = counters.Sum(); var idle = counters[3] + counters[4];
                if (_hostTotal > 0 && total > _hostTotal) hostCpu = 100d * (1 - (double)(idle - _hostIdle) / (total - _hostTotal));
                _hostTotal = total; _hostIdle = idle;
                var pressure = File.ReadLines("/proc/pressure/cpu").First(x => x.StartsWith("some ")).Split(' ', StringSplitOptions.RemoveEmptyEntries).First(x => x.StartsWith("avg10="));
                hostPressure = double.Parse(pressure[6..], CultureInfo.InvariantCulture); hostValid = true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or IndexOutOfRangeException)
                { hostValid = false; }
            }
            var percent = elapsed > 0 ? (cpu - _lastCpu).TotalSeconds / elapsed / Math.Max(.01, cores) * 100 : 0;
            var pause = elapsed > 0 ? (gc - _lastPause).TotalSeconds / elapsed * 100 : 0;
            _lastCpu = cpu; _lastPause = gc;
            return new(DateTimeOffset.UtcNow, (bounded || headroom > 0) && host > 0, Math.Min(headroom, host), host, percent, pause,
                runtime.Telemetry.Snapshot(), runtime.SmtpMessages.Waiting, runtime.Delivery.Waiting, runtime.HeavyBudget.WaitingTotal, hostCpu, hostPressure, hostValid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        { return new(DateTimeOffset.UtcNow, false, 0, 0, 0, 0, runtime.Telemetry.Snapshot(), 0, 0, 0); }
    }
}
public sealed class AdaptiveResourceHostedService(ResourceRuntime runtime, ILogger<AdaptiveResourceHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!runtime.AdaptiveOptions.Enabled) return;
        var sampler = new AdaptiveResourceSampler(); var policy = new AdaptiveResourcePolicy(runtime.AdaptiveOptions);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var sample = sampler.Sample(runtime);
            runtime.HeavyBudget.UpdateMemoryHeadroom(sample.Valid ? sample.MemoryHeadroomBytes : null);
            var decision = policy.Evaluate(sample, runtime.SmtpMessages.Limit, runtime.Delivery.Limit, runtime.LargeMime.Limit);
            runtime.ResizeExperiment(decision.Inbound, decision.Outbound, decision.Heavy);
            logger.LogInformation("adaptive-resource-sample {Sample}", JsonSerializer.Serialize(new { Sample = sample, Decision = decision,
                InboundActive = runtime.SmtpMessages.Active, OutboundActive = runtime.Delivery.Active, HeavyActive = runtime.LargeMime.Active,
                ReservedMemoryBytes = runtime.HeavyBudget.ReservedMemoryBytes }));
            if (decision.StopRequested) logger.LogError("adaptive-experiment-stop-requested {Reason}", decision.Reason);
        }
    }
}
