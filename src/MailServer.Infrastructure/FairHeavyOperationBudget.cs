namespace MailServer.Infrastructure;

public enum HeavyOperationRole { Inbound, Outbound, Background }

/// <summary>Role-fair admission sharing the existing heavy limiter. Memory lives until content disposal.</summary>
public sealed class FairHeavyOperationBudget(ResourceRuntime runtime)
{
    private const long Reservation = 256L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly LinkedList<Waiter>[] _queues = [new(), new(), new()];
    private bool _enabled;
    private long? _headroom;
    private long _reserved;
    private HeavyOperationRole _last = HeavyOperationRole.Outbound;
    public int WaitingInbound { get { lock (_gate) return _queues[0].Count; } }
    public int WaitingOutbound { get { lock (_gate) return _queues[1].Count; } }
    public int WaitingTotal { get { lock (_gate) return _queues.Sum(x => x.Count); } }
    public long ReservedMemoryBytes { get { lock (_gate) return _reserved; } }
    public void Configure(bool enabled) { lock (_gate) _enabled = enabled; }
    public void UpdateMemoryHeadroom(long? bytes) { lock (_gate) _headroom = bytes; }

    public async Task<FairHeavyOperationLease> AcquireAsync(HeavyOperationRole role, CancellationToken cancellationToken)
    {
        bool enabled;
        lock (_gate) enabled = _enabled;
        if (!enabled)
            return new(await runtime.LargeMime.AcquireAsync(cancellationToken), null, 0);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var waiter = new Waiter(role);
        LinkedListNode<Waiter> node;
        lock (_gate)
        {
            var queue = _queues[(int)role];
            if (queue.Count >= 128) throw new IOException("Heavy operation waiting queue is full.");
            node = queue.AddLast(waiter);
        }
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    // Unknown/stale sampling grants no experimental memory admission.
                    var enoughMemory = _headroom.HasValue && _headroom.Value - _reserved >= Reservation + 512L * 1024 * 1024;
                    if (ReferenceEquals(Selected(), waiter) && enoughMemory)
                    {
                        var permit = runtime.LargeMime.TryAcquire();
                        if (permit is not null)
                        {
                            _queues[(int)role].Remove(node);
                            _reserved += Reservation;
                            if (role != HeavyOperationRole.Background) _last = role;
                            return new(permit, this, Reservation);
                        }
                    }
                }
                await Task.Delay(25, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Heavy operation admission timed out.");
        }
        finally
        {
            lock (_gate) if (node.List is not null) node.List.Remove(node);
        }
    }

    private Waiter? Selected()
    {
        var preferred = _last == HeavyOperationRole.Inbound ? 1 : 0;
        return _queues[preferred].First?.Value ?? _queues[1 - preferred].First?.Value ?? _queues[2].First?.Value;
    }
    internal void ReleaseMemory(long bytes) { lock (_gate) _reserved -= bytes; }
    private sealed record Waiter(HeavyOperationRole Role);
}

public sealed class FairHeavyOperationLease(IDisposable heavy, FairHeavyOperationBudget? owner, long reservation) : IDisposable
{
    private IDisposable? _heavy = heavy;
    private FairHeavyOperationBudget? _owner = owner;
    /// <summary>Release expensive preparation capacity, retaining content memory ownership until Dispose.</summary>
    public void ReleaseHeavy() => Interlocked.Exchange(ref _heavy, null)?.Dispose();
    public void Dispose()
    {
        ReleaseHeavy();
        Interlocked.Exchange(ref _owner, null)?.ReleaseMemory(reservation);
    }
}
