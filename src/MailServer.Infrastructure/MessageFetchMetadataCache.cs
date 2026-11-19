using MailServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

public sealed class MessageFetchMetadataCache : IDisposable
{
    private sealed record Entry((long Id, string ContentKey) Key, MessageFetchMetadata Value, long Size, DateTimeOffset Expires);
    private readonly IDbContextFactory<MailDbContext> _factory;
    private readonly ResourceCacheBudget? _budget;
    private readonly object _gate = new();
    private readonly Dictionary<(long, string), LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _limit = 16 * 1024 * 1024;
    private long _bytes;
    private bool _disposed;
    private readonly AsyncKeyedLock<long> _loads = new();
    public long EstimatedBytes { get { lock (_gate) return _bytes; } }
    public long SizeLimitBytes { get { lock (_gate) return _limit; } }

    public MessageFetchMetadataCache(IDbContextFactory<MailDbContext> factory, ResourceRuntime? resources = null)
    {
        _factory = factory;
        _budget = resources?.CacheBudgets;
        if (_budget is not null)
        {
            _budget.Changed += Resize;
            Resize(_budget.Allocation);
        }
    }

    private void Resize(CacheBudgetAllocation allocation)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _limit = allocation.FetchMetadataBytes;
            while (_bytes > _limit && _lru.First is { } oldest) Remove(oldest);
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node.Value.Key);
        _lru.Remove(node);
        _bytes -= node.Value.Size;
    }

    private MessageFetchMetadata? Find((long, string) key)
    {
        lock (_gate)
        {
            if (_disposed || !_entries.TryGetValue(key, out var node)) return null;
            if (node.Value.Expires <= DateTimeOffset.UtcNow) { Remove(node); return null; }
            _lru.Remove(node);
            _lru.AddLast(node);
            return node.Value.Value;
        }
    }

    private void Remember((long, string) key, MessageFetchMetadata metadata, long size)
    {
        lock (_gate)
        {
            if (_disposed || size > _limit || size > 512 * 1024) return;
            if (_entries.TryGetValue(key, out var existing)) Remove(existing);
            while (_bytes + size > _limit && _lru.First is { } oldest) Remove(oldest);
            _entries.Add(key, _lru.AddLast(new Entry(key, metadata, size, DateTimeOffset.UtcNow.AddMinutes(10))));
            _bytes += size;
        }
    }
    public async Task<MessageFetchMetadata> GetAsync(long messageId, string contentKey,
        Func<CancellationToken, Task<MessageFetchMetadata>> build, CancellationToken token)
    {
        var key = (messageId, contentKey);
        if (Find(key) is { } hit) return hit;
        using var lease = await _loads.AcquireAsync(messageId, token);
        if (Find(key) is { } cached) return cached;
        await using var db = await _factory.CreateDbContextAsync(token);
        var metadata = await db.MessageFetchMetadata.AsNoTracking()
            .SingleOrDefaultAsync(x => x.MessageId == messageId && x.ContentKey == contentKey && x.ParserVersion == 1, token);
        if (metadata is null)
        {
            metadata = await build(token);
            metadata.MessageId = messageId;
            metadata.ContentKey = contentKey;
            metadata.ParserVersion = 1;
            // A malformed MIME tree must not create an unbounded database/cache entry.
            if (Size(metadata) <= 512 * 1024)
            {
                var existing = await db.MessageFetchMetadata.FindAsync([messageId], token);
                if (existing is null) db.MessageFetchMetadata.Add(metadata);
                else db.Entry(existing).CurrentValues.SetValues(metadata);
                try { await db.SaveChangesAsync(token); }
                catch (DbUpdateException)
                {
                    // Another process may have populated it, or cleanup may have removed
                    // the message after selection. Never fail FETCH just to populate a cache.
                    db.ChangeTracker.Clear();
                    if (await db.Messages.AnyAsync(x => x.Id == messageId, token)
                        && !await db.MessageFetchMetadata.AnyAsync(x => x.MessageId == messageId, token)) throw;
                }
            }
        }
        var size = Size(metadata);
        Remember(key, metadata, size);
        return metadata;
    }
    private static long Size(MessageFetchMetadata value) => 256L + 2L * (value.Envelope.Length + value.Body.Length + value.BodyStructure.Length);
    public void Dispose()
    {
        if (_budget is not null) _budget.Changed -= Resize;
        lock (_gate)
        {
            _disposed = true;
            _entries.Clear();
            _lru.Clear();
            _bytes = 0;
        }
    }
}
