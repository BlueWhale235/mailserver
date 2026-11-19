using System.Buffers.Binary;

namespace MailServer.Domain;

/// <summary>41 timestamp bits, 10 worker bits, 12 sequence bits; epoch 2020-01-01 UTC.</summary>
public sealed class SnowflakeGenerator : IDisposable
{
    public const long EpochMilliseconds = 1577836800000L;
    private readonly object _gate = new();
    private readonly int _workerId;
    private readonly TimeProvider _clock;
    private readonly FileStream? _fence;
    private long _reservedThrough = -1;
    private long _lastMilliseconds = -1;
    private int _sequence;
    private const long ReservationMilliseconds = 10_000;
    private Task? _reservation;
    private long _observedClock = -1;
    private long _restartFloor = -1;
    private bool _disposed;

    public SnowflakeGenerator(int workerId, TimeProvider? clock = null, string? statePath = null)
    {
        if (workerId is < 0 or > 1023) throw new ArgumentOutOfRangeException(nameof(workerId));
        _workerId = workerId;
        _clock = clock ?? TimeProvider.System;
        if (statePath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        // An exclusive file lease prevents two local processes from sharing a worker ID.
        _fence = new FileStream(statePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (_fence.Length is not (0 or 8)) throw new InvalidDataException("Invalid Snowflake state file.");
            if (_fence.Length == 8)
            {
                Span<byte> bytes = stackalloc byte[8];
                _fence.ReadExactly(bytes);
                _reservedThrough = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                var now = Milliseconds();
                if (_reservedThrough < 0 || _reservedThrough >= 1L << 41 || _reservedThrough - now > ReservationMilliseconds + 5000)
                    throw new InvalidOperationException("Snowflake clock is behind its durable state. Correct the clock before starting.");
                _lastMilliseconds = Math.Max(now, _reservedThrough + 1);
                _restartFloor = _lastMilliseconds;
                _observedClock = now;
            }
        }
        catch { _fence.Dispose(); throw; }
    }

    private long Milliseconds() => _clock.GetUtcNow().ToUnixTimeMilliseconds() - EpochMilliseconds;

    public long NextId()
    {
        while (true)
        {
            Task? pending;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = Milliseconds();
                if (now < 0 || now >= 1L << 41) throw new InvalidOperationException("Snowflake timestamp is outside its supported range.");
                if (_observedClock - now > 5000) throw new InvalidOperationException("Snowflake clock moved backwards by more than five seconds.");
                _observedClock = Math.Max(now, _observedClock);
                var timestamp = Math.Max(now, _lastMilliseconds);
                var sequence = timestamp == _lastMilliseconds ? _sequence + 1 : 0;
                if (sequence > 4095) { timestamp++; sequence = 0; }
                if (timestamp > Math.Max(now + 5000, _restartFloor + 5000) || timestamp >= 1L << 41)
                    throw new InvalidOperationException("Snowflake logical clock exceeded its safety limit.");
                if (_fence is null || timestamp <= _reservedThrough)
                {
                    _lastMilliseconds = timestamp;
                    _sequence = sequence;
                    if (_fence is not null && _reservedThrough - timestamp <= ReservationMilliseconds * 3 / 10)
                        ReserveLocked(timestamp);
                    return timestamp << 22 | (long)_workerId << 12 | (uint)sequence;
                }
                pending = ReserveLocked(timestamp);
            }
            // A cold start or exhausted durable window must wait, but fsync never runs
            // while holding the ID allocation lock. No ID can escape an unflushed fence.
            pending.GetAwaiter().GetResult();
        }
    }

    private Task ReserveLocked(long timestamp)
    {
        if (_reservation is { IsCompleted: false }) return _reservation;
        if (_reservation?.IsFaulted == true) _ = _reservation.Exception;
        var reserved = Math.Min(timestamp + ReservationMilliseconds, (1L << 41) - 1);
        _reservation = Task.Run(() =>
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, reserved);
            _fence!.Position = 0;
            _fence.Write(bytes);
            _fence.Flush(flushToDisk: true);
            lock (_gate) _reservedThrough = reserved;
        });
        return _reservation;
    }

    public void Dispose()
    {
        Task? pending;
        lock (_gate) { if (_disposed) return; _disposed = true; pending = _reservation; }
        try { pending?.GetAwaiter().GetResult(); }
        finally { _fence?.Dispose(); }
    }

}

public static class SnowflakeIds
{
    // Tests and model tooling use the process-local generator; hosts replace it before doing work.
    private static SnowflakeGenerator _generator = new(0);
    public static long NewId() => Volatile.Read(ref _generator).NextId();
    public static void Configure(SnowflakeGenerator generator) => Interlocked.Exchange(ref _generator, generator);
}
