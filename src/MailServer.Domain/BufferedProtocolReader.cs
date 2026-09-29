using System.Buffers;

namespace MailServer.Domain;

/// <summary>One read cursor shared by protocol lines and fixed-length literals.</summary>
internal sealed class BufferedProtocolReader : IDisposable
{
    private const int BufferSize = 16 * 1024;
    private Stream _stream;
    private byte[]? _buffer;
    private byte[]? _line;
    private int _position;
    private int _end;
    private bool _failed;

    public BufferedProtocolReader(Stream stream) => _stream = stream;

    public void Attach(Stream stream)
    {
        _stream = stream;
        _position = _end = 0;
        _failed = false;
    }

    public void DiscardBufferedPlaintext() => _position = _end = 0;

    public async ValueTask<ReadOnlyMemory<byte>?> ReadLineAsync(
        int maximumLength, bool requireCrlf, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var length = 0;
        while (true)
        {
            if (_position == _end)
            {
                if (await FillAsync(timeout, cancellationToken) == 0)
                {
                    if (length == 0) return null;
                    if (requireCrlf) throw new EndOfStreamException("Line ended before CRLF.");
                    if (length > maximumLength) throw new InvalidDataException("Protocol line exceeds its limit.");
                    return _line!.AsMemory(0, length);
                }
            }

            var available = _buffer!.AsSpan(_position, _end - _position);
            var newline = available.IndexOf((byte)'\n');
            var partLength = newline < 0 ? available.Length : newline;
            if (length + partLength > maximumLength + 1)
                throw new InvalidDataException("Protocol line exceeds its limit.");
            if (length == 0 && newline >= 0)
            {
                var start = _position;
                _position += newline + 1;
                var hasCr = newline > 0 && _buffer![_position - 2] == (byte)'\r';
                if (requireCrlf && (!hasCr || _buffer!.AsSpan(start, newline - 1).Contains((byte)'\r')))
                    throw new InvalidDataException("Protocol line contains an invalid line ending.");
                var contiguousLength = newline - (hasCr ? 1 : 0);
                if (contiguousLength > maximumLength) throw new InvalidDataException("Protocol line exceeds its limit.");
                return _buffer.AsMemory(start, contiguousLength);
            }

            EnsureLineCapacity(length + partLength + 1);
            available[..partLength].CopyTo(_line.AsSpan(length));
            length += partLength;
            _position += partLength + (newline >= 0 ? 1 : 0);
            if (newline < 0) continue;
            var endsInCr = length > 0 && _line![length - 1] == (byte)'\r';
            if (requireCrlf && (!endsInCr || _line.AsSpan(0, length - 1).Contains((byte)'\r')))
                throw new InvalidDataException("Protocol line contains an invalid line ending.");
            var finalLength = length - (endsInCr ? 1 : 0);
            if (finalLength > maximumLength) throw new InvalidDataException("Protocol line exceeds its limit.");
            return _line.AsMemory(0, finalLength);
        }
    }

    public async Task CopyExactlyAsync(Stream destination, long length, TimeSpan timeout, CancellationToken cancellationToken)
    {
        while (length > 0)
        {
            if (_position == _end && await FillAsync(timeout, cancellationToken) == 0)
                throw new EndOfStreamException();
            var count = (int)Math.Min(length, _end - _position);
            await destination.WriteAsync(_buffer!.AsMemory(_position, count), cancellationToken);
            _position += count;
            length -= count;
        }
    }

    private async ValueTask<int> FillAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_failed) throw new InvalidOperationException("The protocol stream can no longer be read.");
        _buffer ??= ArrayPool<byte>.Shared.Rent(BufferSize);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            _end = await _stream.ReadAsync(_buffer.AsMemory(0, BufferSize), deadline.Token);
            _position = 0;
            return _end;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _failed = true;
            throw new TimeoutException("Protocol read timed out.");
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    private void EnsureLineCapacity(int required)
    {
        if (_line is { Length: var size } && size >= required) return;
        var replacement = ArrayPool<byte>.Shared.Rent(Math.Max(256, required));
        if (_line is not null)
        {
            _line.AsSpan().CopyTo(replacement);
            ArrayPool<byte>.Shared.Return(_line);
        }
        _line = replacement;
    }

    public void Dispose()
    {
        if (_buffer is not null) ArrayPool<byte>.Shared.Return(_buffer);
        if (_line is not null) ArrayPool<byte>.Shared.Return(_line);
        _buffer = _line = null;
    }
}
