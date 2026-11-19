using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MailServer.Imap;

/// <summary>One mailbox status read per local subscription group and poll interval.</summary>
internal sealed class IdleMailboxCoordinator : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly MailboxChangeSignal? _changes;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<(long Account, string Name), Entry> _entries = [];

    internal IdleMailboxCoordinator(IServiceScopeFactory scopes, MailboxChangeSignal? changes, ILogger logger)
    {
        _scopes = scopes;
        _changes = changes;
        _logger = logger;
        if (changes is not null) changes.Changed += OnChanged;
    }

    internal Subscription Subscribe(long account, string mailbox, TimeSpan interval)
    {
        var key = (account, mailbox.ToUpperInvariant());
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(key, interval);
                _entries.Add(key, entry);
                _ = PollAsync(entry);
            }
            entry.Subscribers++;
            return new Subscription(this, entry);
        }
    }

    private async Task PollAsync(Entry entry)
    {
        try
        {
            while (!entry.Stop.IsCancellationRequested)
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<IMailboxService>();
                    var summary = await service.SelectAsync(entry.Key.Account, entry.Key.Name, entry.Stop.Token);
                    lock (entry.Gate)
                    {
                        entry.MailboxId = summary?.Id;
                        entry.Error = null;
                        entry.Version++;
                        entry.Summary = summary;
                        entry.Updated.TrySetResult();
                        entry.Updated = NewSource();
                    }
                }
                catch (OperationCanceledException) when (entry.Stop.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    _logger.LogWarning(error, "IMAP IDLE mailbox refresh failed.");
                    lock (entry.Gate)
                    {
                        entry.Error = error;
                        entry.Version++;
                        entry.Updated.TrySetResult();
                        entry.Updated = NewSource();
                    }
                }

                Task wake;
                lock (entry.Gate) wake = entry.Wake.Task;
                await Task.WhenAny(wake, Task.Delay(entry.Interval, entry.Stop.Token));
                lock (entry.Gate) if (wake.IsCompleted) entry.Wake = NewSource();
                // Collapse bursts of writes into one refresh.
                if (wake.IsCompleted) await Task.Delay(100, entry.Stop.Token);
            }
        }
        catch (OperationCanceledException) when (entry.Stop.IsCancellationRequested) { }
    }

    private void OnChanged(long mailboxId)
    {
        lock (_gate)
            foreach (var entry in _entries.Values)
                lock (entry.Gate)
                    if (entry.MailboxId == mailboxId) entry.Wake.TrySetResult();
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            if (--entry.Subscribers != 0) return;
            _entries.Remove(entry.Key);
            entry.Stop.Cancel();
        }
    }

    public void Dispose()
    {
        if (_changes is not null) _changes.Changed -= OnChanged;
        lock (_gate)
        {
            foreach (var entry in _entries.Values) entry.Stop.Cancel();
            _entries.Clear();
        }
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class Entry((long Account, string Name) key, TimeSpan interval)
    {
        internal readonly object Gate = new();
        internal readonly (long Account, string Name) Key = key;
        internal readonly TimeSpan Interval = interval;
        internal readonly CancellationTokenSource Stop = new();
        internal int Subscribers;
        internal long Version;
        internal long? MailboxId;
        internal MailboxSummary? Summary;
        internal Exception? Error;
        internal TaskCompletionSource Updated = NewSource();
        internal TaskCompletionSource Wake = NewSource();
    }

    internal sealed class Subscription(IdleMailboxCoordinator owner, Entry entry) : IDisposable
    {
        private int _disposed;

        internal async Task<(long Version, MailboxSummary? Summary)> WaitAsync(long seenVersion, CancellationToken token)
        {
            Task wait;
            lock (entry.Gate)
            {
                if (entry.Error is not null) throw new InvalidOperationException("Mailbox status is unavailable.", entry.Error);
                if (entry.Version != seenVersion) return (entry.Version, entry.Summary);
                wait = entry.Updated.Task;
            }
            await wait.WaitAsync(token);
            lock (entry.Gate)
            {
                if (entry.Error is not null) throw new InvalidOperationException("Mailbox status is unavailable.", entry.Error);
                return (entry.Version, entry.Summary);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(entry);
        }
    }
}
