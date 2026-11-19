using System.Buffers;

namespace MailServer.Domain;

/// <summary>Fixed-size temporary buffer. The rented array is returned exactly once on either disposal path.</summary>
public sealed class PooledMemoryStream : MemoryStream
{
    private byte[]? _rented;
    public PooledMemoryStream(int capacity) : this(ArrayPool<byte>.Shared.Rent(Math.Max(1, capacity)), capacity) { }
    private PooledMemoryStream(byte[] rented, int capacity) : base(rented, 0, capacity, writable: true, publiclyVisible: false)
    {
        _rented = rented;
        SetLength(0);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        var rented = Interlocked.Exchange(ref _rented, null);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
    }
    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
