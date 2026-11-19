namespace MailServer.Infrastructure;

/// <summary>Waiters retain their entry until cancellation or release; idle keys are reclaimed.</summary>
internal sealed class AsyncKeyedLock<TKey> where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries = [];
    private sealed class Entry
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int References;
    }
    internal int Count { get { lock (_gate) return _entries.Count; } }

    public async ValueTask<IDisposable> AcquireAsync(TKey key, CancellationToken token)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries.Add(key, entry = new());
            entry.References++;
        }
        try { await entry.Semaphore.WaitAsync(token); }
        catch { Release(key, entry, acquired: false); throw; }
        return new Lease(this, key, entry);
    }

    private void Release(TKey key, Entry entry, bool acquired)
    {
        lock (_gate)
        {
            if (acquired) entry.Semaphore.Release();
            if (--entry.References != 0) return;
            _entries.Remove(key);
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Lease(AsyncKeyedLock<TKey> owner, TKey key, Entry entry) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(key, entry, acquired: true);
        }
    }
}
