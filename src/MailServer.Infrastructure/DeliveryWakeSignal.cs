namespace MailServer.Infrastructure;

/// <summary>A local hint only. Database polling remains the cross-process recovery path.</summary>
public sealed class DeliveryWakeSignal
{
    private readonly object _gate = new();
    private long _version;
    private TaskCompletionSource _changed = NewSource();

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Version { get { lock (_gate) return _version; } }

    public void Pulse()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            _version++;
            old = _changed;
            _changed = NewSource();
        }
        old.TrySetResult();
    }

    public async Task WaitForChangeAsync(long observedVersion, TimeSpan fallback, CancellationToken token)
    {
        Task changed;
        lock (_gate)
        {
            if (_version != observedVersion) return;
            changed = _changed.Task;
        }
        try { await changed.WaitAsync(fallback, token); }
        catch (TimeoutException) { }
    }
}
