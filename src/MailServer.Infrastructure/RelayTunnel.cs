using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MailServer.Infrastructure;

public sealed record RelayInboundLimits(int MaxInboundConnections = 32, int MaxConnectionsPerIp = 4,
    int IdleTimeoutSeconds = 120)
{
    public void Validate()
    {
        if (MaxInboundConnections is < 1 or > 200 || MaxConnectionsPerIp < 1 ||
            MaxConnectionsPerIp > MaxInboundConnections || IdleTimeoutSeconds is < 1 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(MaxInboundConnections),
                "Relay limits require 1-200 total connections, 1-total per IP, and a 1-3600 second idle timeout.");
    }
}

public sealed record RelayOpen(string Kind, string? Host = null, int Port = 0,
    string? RemoteAddress = null, int RemotePort = 0, RelayInboundLimits? Limits = null);

/// <summary>Bounded, multiplexed streams over one authenticated TLS connection.</summary>
public sealed class RelayTunnel(Stream wire, bool initiator) : IAsyncDisposable
{
    private const int MaximumFrame = 16 * 1024;
    private const int MaximumStreams = 200;
    private const byte OpenFrame = 1, DataFrame = 2, CloseFrame = 3, WindowFrame = 4,
        PingFrame = 5, PongFrame = 6;
    private readonly ConcurrentDictionary<int, RelayVirtualStream> _streams = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private int _nextId = initiator ? -1 : 0;
    private int _closed;
    private long _lastPong = DateTimeOffset.UtcNow.UtcTicks;
    public int ActiveStreams => _streams.Count;
    public Func<RelayOpen, Stream, CancellationToken, Task>? OnOpen { get; set; }

    public async Task<Stream> OpenAsync(RelayOpen request, CancellationToken token)
    {
        if (Volatile.Read(ref _closed) != 0 || _streams.Count >= MaximumStreams)
            throw new IOException("Relay tunnel is unavailable or at capacity.");
        var id = Interlocked.Add(ref _nextId, 2);
        var stream = new RelayVirtualStream(this, id);
        if (!_streams.TryAdd(id, stream)) throw new IOException("Relay stream collision.");
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(request);
            await SendAsync(id, OpenFrame, payload, token);
            return stream;
        }
        catch
        {
            _streams.TryRemove(id, out _);
            stream.Abort();
            throw;
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var heartbeat = initiator ? HeartbeatAsync(heartbeatCancellation.Token) : Task.CompletedTask;
        try
        {
            var header = new byte[9];
            while (!token.IsCancellationRequested)
            {
                await wire.ReadExactlyAsync(header, token);
                var id = BinaryPrimitives.ReadInt32BigEndian(header);
                var kind = header[4];
                var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5));
                if (id == 0)
                {
                    if (length != 0 || kind is not (PingFrame or PongFrame))
                        throw new InvalidDataException("Invalid relay control frame.");
                    if (kind == PingFrame) await SendAsync(0, PongFrame, ReadOnlyMemory<byte>.Empty, token);
                    else Interlocked.Exchange(ref _lastPong, DateTimeOffset.UtcNow.UtcTicks);
                    continue;
                }
                if (id < 0 || length < 0 || length > MaximumFrame ||
                    kind is not (OpenFrame or DataFrame or CloseFrame or WindowFrame))
                    throw new InvalidDataException("Invalid relay frame.");
                var data = new byte[length];
                if (length > 0) await wire.ReadExactlyAsync(data, token);
                if (kind == OpenFrame)
                {
                    if (_streams.Count >= MaximumStreams || _streams.ContainsKey(id))
                        throw new InvalidDataException("Relay stream limit or duplicate id.");
                    var request = JsonSerializer.Deserialize<RelayOpen>(data)
                        ?? throw new InvalidDataException("Invalid relay open request.");
                    var stream = new RelayVirtualStream(this, id);
                    if (!_streams.TryAdd(id, stream)) throw new InvalidDataException("Duplicate stream id.");
                    _ = HandleOpenAsync(request, stream, token);
                }
                else if (_streams.TryGetValue(id, out var stream))
                {
                    if (kind == DataFrame) stream.Receive(data);
                    else if (kind == WindowFrame)
                    {
                        if (length != 0) throw new InvalidDataException("Invalid relay window frame.");
                        stream.GrantCredit();
                    }
                    else
                    {
                        _streams.TryRemove(id, out _);
                        stream.Abort();
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or EndOfStreamException or OperationCanceledException
            or ObjectDisposedException or InvalidDataException)
        {
            // Loss of the authenticated tunnel terminates every SMTP session.
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try { await heartbeat; }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            Interlocked.Exchange(ref _closed, 1);
            foreach (var stream in _streams.Values) stream.Abort();
            _streams.Clear();
            await wire.DisposeAsync();
        }
    }

    private async Task HeartbeatAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            if (DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _lastPong) > TimeSpan.FromSeconds(15).Ticks)
            {
                await wire.DisposeAsync();
                return;
            }
            await SendAsync(0, PingFrame, ReadOnlyMemory<byte>.Empty, token);
        }
    }

    private async Task HandleOpenAsync(RelayOpen request, RelayVirtualStream stream, CancellationToken token)
    {
        try
        {
            if (OnOpen is null) throw new IOException("No handler for relay stream.");
            await OnOpen(request, stream, token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The remote side observes EOF and can retry; message bodies are never logged.
        }
        finally { await stream.DisposeAsync(); }
    }

    internal async Task SendAsync(int id, byte kind, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        if (data.Length > MaximumFrame) throw new ArgumentOutOfRangeException(nameof(data));
        var header = new byte[9];
        BinaryPrimitives.WriteInt32BigEndian(header, id);
        header[4] = kind;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), data.Length);
        await _writer.WaitAsync(token);
        try
        {
            await wire.WriteAsync(header, token);
            if (!data.IsEmpty) await wire.WriteAsync(data, token);
            await wire.FlushAsync(token);
        }
        finally { _writer.Release(); }
    }

    internal void Remove(int id) => _streams.TryRemove(id, out _);

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        foreach (var stream in _streams.Values) stream.Abort();
        _streams.Clear();
        await wire.DisposeAsync();
    }

    private sealed class RelayVirtualStream(RelayTunnel owner, int id) : Stream
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true
        });
        private byte[]? _current;
        private int _offset;
        private int _disposed;
        private readonly SemaphoreSlim _sendWindow = new(4, 4);

        public void Receive(byte[] bytes)
        {
            if (bytes.Length == 0 || !_inbound.Writer.TryWrite(bytes))
                throw new InvalidDataException("Relay peer exceeded its stream window.");
        }
        public void GrantCredit() => _sendWindow.Release();
        public void Abort()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _inbound.Writer.TryComplete();
            // Wake blocked writers on a lost connection.
            for (var i = 0; i < 4; i++)
            {
                try { _sendWindow.Release(); } catch (SemaphoreFullException) { break; }
            }
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            while (_current is null || _offset == _current.Length)
            {
                if (!await _inbound.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (_inbound.Reader.TryRead(out var bytes)) { _current = bytes; _offset = 0; }
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == _current.Length)
                await owner.SendAsync(id, WindowFrame, ReadOnlyMemory<byte>.Empty, cancellationToken);
            return count;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            await ReadAsync(buffer.AsMemory(offset, count), token);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            if (_disposed != 0) throw new ObjectDisposedException(nameof(RelayVirtualStream));
            while (!buffer.IsEmpty)
            {
                var count = Math.Min(MaximumFrame, buffer.Length);
                await _sendWindow.WaitAsync(token);
                if (_disposed != 0) throw new IOException("Relay stream is closed.");
                await owner.SendAsync(id, DataFrame, buffer[..count], token);
                buffer = buffer[count..];
            }
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Remove(id);
            Abort();
            try { await owner.SendAsync(id, CloseFrame, ReadOnlyMemory<byte>.Empty, CancellationToken.None); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            await base.DisposeAsync();
        }
        protected override void Dispose(bool disposing) => DisposeAsync().AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }
}

public static class RelayStreamBridge
{
    public static async Task CopyBothWaysAsync(Stream left, Stream right, CancellationToken token,
        TimeSpan? idleTimeout = null)
    {
        using var idle = idleTimeout is null ? null : new CancellationTokenSource();
        if (idleTimeout is { } timeout)
        {
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleTimeout));
            idle!.CancelAfter(timeout);
        }
        using var linked = idle is null
            ? CancellationTokenSource.CreateLinkedTokenSource(token)
            : CancellationTokenSource.CreateLinkedTokenSource(token, idle.Token);
        var first = CopyAsync(left, right, idle, idleTimeout, linked.Token);
        var second = CopyAsync(right, left, idle, idleTimeout, linked.Token);
        await Task.WhenAny(first, second);
        linked.Cancel();
        try { await Task.WhenAll(first, second); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationTokenSource? idle,
        TimeSpan? idleTimeout, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await source.ReadAsync(buffer, token);
            if (count == 0) return;
            idle?.CancelAfter(idleTimeout!.Value);
            await destination.WriteAsync(buffer.AsMemory(0, count), token);
            idle?.CancelAfter(idleTimeout!.Value);
        }
    }
}
