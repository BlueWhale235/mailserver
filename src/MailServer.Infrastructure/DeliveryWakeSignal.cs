namespace MailServer.Infrastructure;

/// <summary>Commit hint, relayed between local processes by ProcessWakeService; polling recovers lost hints.</summary>
public sealed class DeliveryWakeSignal
{
    private readonly object _gate = new();
    private long _version;
    private TaskCompletionSource _changed = NewSource();

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Version { get { lock (_gate) return _version; } }
    internal event Action? Published;

    public void Pulse()
    {
        PulseLocal();
        Published?.Invoke();
    }

    internal void PulseLocal()
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
