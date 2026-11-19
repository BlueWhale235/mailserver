using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

/// <summary>Only idle connections are retained. A rented client has exactly one owner.</summary>
public sealed class SmtpConnectionPool(IOptions<SmtpConnectionReuseOptions> options) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<Entry>> _idle = new(StringComparer.Ordinal);
    private int _idleCount;
    private Timer? _expiryTimer;
    private bool _disposed;

    public SmtpConnectionPool(IOptions<SmtpConnectionReuseOptions> options, TimeProvider timeProvider)
        : this(options)
    {
        _time = timeProvider;
    }

    private readonly TimeProvider _time = TimeProvider.System;
    internal bool Enabled => options.Value.Enabled;

    // The timer evicts expired sockets even when no further delivery occurs.
    private Timer CreateTimer() => new(_ => Sweep(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    internal Lease Rent(string key, bool allowReuse)
    {
        Sweep();
        lock (_gate)
        {
            if (!_disposed) _expiryTimer ??= CreateTimer();
            if (!_disposed && allowReuse && options.Value.Enabled)
            {
                if (_idle.TryGetValue(key, out var queue) && queue.TryDequeue(out var entry))
                {
                    _idleCount--;
                    if (queue.Count == 0) _idle.Remove(key);
                    return new Lease(this, entry, true);
                }
            }
            return new Lease(allowReuse && options.Value.Enabled && !_disposed ? this : null,
                new Entry(key, new RecipientSmtpClient(), _time.GetUtcNow()), false);
        }
    }

    private void Return(Entry entry)
    {
        Sweep();
        lock (_gate)
        {
            if (!_disposed && options.Value.Enabled && Usable(entry)
                && _idleCount < Math.Clamp(options.Value.MaximumIdleConnections, 0, 4096)
                && (!_idle.TryGetValue(entry.Key, out var existing) || existing.Count < Math.Clamp(options.Value.MaximumIdleConnectionsPerKey, 1, 64)))
            {
                entry.LastUsed = _time.GetUtcNow();
                if (!_idle.TryGetValue(entry.Key, out var queue)) _idle.Add(entry.Key, queue = new());
                queue.Enqueue(entry);
                _idleCount++;
                return;
            }
        }
        entry.Client.Dispose();
    }

    private bool Usable(Entry entry) => entry.Client.IsConnected
        && entry.Messages < Math.Clamp(options.Value.MaximumMessages, 1, 50)
        && _time.GetUtcNow() - entry.Created < TimeSpan.FromSeconds(Math.Clamp(options.Value.MaximumLifetimeSeconds, 1, 300));

    private void Sweep()
    {
        List<Entry> expired;
        lock (_gate) expired = SweepLocked();
        foreach (var entry in expired) entry.Client.Dispose();
    }

    private List<Entry> SweepLocked()
    {
        List<Entry> expired = [];
        foreach (var pair in _idle.ToArray())
        {
            var count = pair.Value.Count;
            for (var index = 0; index < count; index++)
            {
                var entry = pair.Value.Dequeue();
                if (Usable(entry) && _time.GetUtcNow() - entry.LastUsed
                    < TimeSpan.FromSeconds(Math.Clamp(options.Value.IdleSeconds, 1, 30))) pair.Value.Enqueue(entry);
                else { expired.Add(entry); _idleCount--; }
            }
            if (pair.Value.Count == 0) _idle.Remove(pair.Key);
        }
        return expired;
    }

    public void Dispose()
    {
        Entry[] idle;
        lock (_gate)
        {
            _disposed = true;
            _expiryTimer?.Dispose();
            idle = _idle.Values.SelectMany(x => x).ToArray();
            _idle.Clear();
            _idleCount = 0;
        }
        foreach (var entry in idle) entry.Client.Dispose();
    }

    internal sealed class Entry(string key, SmtpClient client, DateTimeOffset created)
    {
        internal readonly string Key = key;
        internal readonly SmtpClient Client = client;
        internal readonly DateTimeOffset Created = created;
        internal DateTimeOffset LastUsed = created;
        internal int Messages;
        internal string? RemoteAddress;
    }

    internal sealed class Lease(SmtpConnectionPool? pool, Entry entry, bool reused) : IDisposable
    {
        internal SmtpClient Client => entry.Client;
        internal bool Reused => reused;
        internal string? RemoteAddress => entry.RemoteAddress;
        private bool _reusable;
        private int _disposed;
        internal void MarkSuccessful(string? remoteAddress)
        {
            entry.Messages++;
            entry.RemoteAddress = remoteAddress;
            _reusable = true;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_reusable && pool is not null) pool.Return(entry);
            else entry.Client.Dispose();
        }
    }
}
