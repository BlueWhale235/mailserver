using System.Runtime.CompilerServices;
using System.Text;
using MailServer.Domain;

namespace MailServer.Infrastructure;

internal sealed class MailboxSelectionSnapshot : IMailboxSelectionSnapshot
{
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const long MaximumProcessBytes = 256L * 1024 * 1024;
    private static long _reservedBytes;
    private readonly FileStream _stream;
    private readonly long _maximumFileBytes;
    private long _bytes;
    private bool _sealed;
    private readonly MemoryStream _record = new(512);
    private int _disposed;
    internal string FilePath { get; }
    public long Count { get; private set; }

    public MailboxSelectionSnapshot(long maximumFileBytes = MaximumFileBytes)
    {
        _maximumFileBytes = maximumFileBytes;
        var path = Path.Combine(Path.GetTempPath(), $"mailbox-selection-{Guid.NewGuid():N}.tmp");
        FilePath = path;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    }

    public async Task AddAsync(MailboxItem item, CancellationToken cancellationToken)
    {
        if (_sealed) throw new InvalidOperationException("Mailbox selection is already sealed.");
        _record.SetLength(0);
        using (var writer = new BinaryWriter(_record, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(item.Id.ToByteArray()); writer.Write(item.MessageId.ToByteArray());
            writer.Write(item.Sequence); writer.Write(item.Uid); writer.Write(item.ModSeq);
            writer.Write(item.InternalDate.Ticks); writer.Write((short)item.InternalDate.Offset.TotalMinutes);
            writer.Write(item.Size); writer.Write(item.Flags); writer.Write(item.ContentKey);
        }
        var bytes = _record.GetBuffer().AsMemory(0, checked((int)_record.Length));
        if (_bytes + bytes.Length > _maximumFileBytes)
            throw new InvalidOperationException("Mailbox selection exceeds the temporary storage limit.");
        var reserved = Interlocked.Add(ref _reservedBytes, bytes.Length);
        if (reserved > MaximumProcessBytes)
        {
            Interlocked.Add(ref _reservedBytes, -bytes.Length);
            throw new InvalidOperationException("Mailbox selections exceed the process temporary storage limit.");
        }
        _bytes += bytes.Length;
        await _stream.WriteAsync(bytes, cancellationToken);
        Count++;
    }

    public async Task SealAsync(CancellationToken cancellationToken)
    {
        await _stream.FlushAsync(cancellationToken);
        _stream.Position = 0;
        _sealed = true;
    }

    public async IAsyncEnumerable<MailboxItem> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_sealed) throw new InvalidOperationException("Mailbox selection must be sealed before reading.");
        _stream.Position = 0;
        using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        while (_stream.Position < _stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.ReadByte() != 1) throw new InvalidDataException("Unsupported mailbox snapshot version.");
            var id = new Guid(reader.ReadBytes(16)); var messageId = new Guid(reader.ReadBytes(16));
            var sequence = reader.ReadInt64(); var uid = reader.ReadInt64(); var modSeq = reader.ReadInt64();
            var date = new DateTimeOffset(reader.ReadInt64(), TimeSpan.FromMinutes(reader.ReadInt16()));
            var size = reader.ReadInt64(); var flags = reader.ReadString(); var key = reader.ReadString();
            yield return new MailboxItem(id, sequence, uid, flags, modSeq, date, size, key, messageId);
        }
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _record.Dispose();
        await _stream.DisposeAsync();
        Interlocked.Add(ref _reservedBytes, -_bytes);
    }
}
