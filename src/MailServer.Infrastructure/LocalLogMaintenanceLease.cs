namespace MailServer.Infrastructure;

/// <summary>One maintenance owner per local log file; OS releases ownership on process exit.</summary>
public sealed class LocalLogMaintenanceLease(LocalLogDatabase database) : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _lease;
    private bool _disposed;

    public bool TryAcquire()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_lease is not null || database.Path == "embedded") return true;
            try
            {
                // Keep the file after releasing the handle: deleting it can split ownership
                // between different inodes on Unix while another process is acquiring it.
                _lease = new FileStream(database.Path + ".maintenance.lock", FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return true;
            }
            catch (IOException) { return false; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _lease?.Dispose();
            _lease = null;
        }
    }
}
