using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

/// <summary>Only idle connections are retained. A rented client has exactly one owner.</summary>
public sealed class SmtpConnectionPool(IOptions<SmtpConnectionReuseOptions> options) : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Entry> _idle = [];
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
        lock (_gate)
        {
            _expiryTimer ??= CreateTimer();
            SweepLocked();
            if (!_disposed && allowReuse && options.Value.Enabled)
            {
                var index = _idle.FindIndex(entry => entry.Key == key);
                if (index >= 0)
                {
                    var entry = _idle[index];
                    _idle.RemoveAt(index);
                    return new Lease(this, entry, true);
                }
            }
            return new Lease(allowReuse && options.Value.Enabled && !_disposed ? this : null,
                new Entry(key, new SmtpClient(), _time.GetUtcNow()), false);
        }
    }

    private void Return(Entry entry)
    {
        lock (_gate)
        {
            SweepLocked();
            if (_disposed || !options.Value.Enabled || !Usable(entry)
                || _idle.Count >= Math.Clamp(options.Value.MaximumIdleConnections, 0, 16)
                || _idle.Any(x => x.Key == entry.Key))
            {
                entry.Client.Dispose();
                return;
            }
            entry.LastUsed = _time.GetUtcNow();
            _idle.Add(entry);
        }
    }

    private bool Usable(Entry entry) => entry.Client.IsConnected
        && entry.Messages < Math.Clamp(options.Value.MaximumMessages, 1, 50)
        && _time.GetUtcNow() - entry.Created < TimeSpan.FromSeconds(Math.Clamp(options.Value.MaximumLifetimeSeconds, 1, 300));

    private void Sweep()
    {
        lock (_gate) SweepLocked();
    }

    private void SweepLocked()
    {
        for (var index = _idle.Count - 1; index >= 0; index--)
        {
            var entry = _idle[index];
            if (Usable(entry) && _time.GetUtcNow() - entry.LastUsed
                < TimeSpan.FromSeconds(Math.Clamp(options.Value.IdleSeconds, 1, 30))) continue;
            _idle.RemoveAt(index);
            entry.Client.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _expiryTimer?.Dispose();
            foreach (var entry in _idle) entry.Client.Dispose();
            _idle.Clear();
        }
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
