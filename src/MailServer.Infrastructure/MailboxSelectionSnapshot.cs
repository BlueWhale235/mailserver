using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
        var line = JsonSerializer.Serialize(item);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
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
        using var reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            yield return JsonSerializer.Deserialize<MailboxItem>(line)
                ?? throw new InvalidDataException("Mailbox selection contains an invalid item.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stream.DisposeAsync();
        Interlocked.Add(ref _reservedBytes, -_bytes);
    }
}
