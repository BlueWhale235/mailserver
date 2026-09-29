using System.Text;
using MailServer.Domain;
using Xunit;

namespace MailServer.Tests;

public sealed class BufferedProtocolReaderTests
{
    [Fact]
    public async Task LinesAndLiteralShareBufferedCursorWithoutExtraReads()
    {
        var payload = new byte[1024 * 1024];
        Random.Shared.NextBytes(payload);
        var wire = "A APPEND INBOX {1048576}\r\n"u8.ToArray()
            .Concat(payload).Concat("\r\nB NOOP\r\n"u8.ToArray()).ToArray();
        using var input = new ChunkedReadStream(wire, 4096);
        using var reader = new BufferedProtocolReader(input);
        Assert.Equal("A APPEND INBOX {1048576}",
            Encoding.ASCII.GetString((await reader.ReadLineAsync(128, false, TimeSpan.FromSeconds(1), CancellationToken.None))!.Value.Span));
        using var output = new MemoryStream();
        await reader.CopyExactlyAsync(output, payload.Length, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(payload, output.ToArray());
        Assert.Equal(string.Empty,
            Encoding.ASCII.GetString((await reader.ReadLineAsync(128, false, TimeSpan.FromSeconds(1), CancellationToken.None))!.Value.Span));
        Assert.Equal("B NOOP",
            Encoding.ASCII.GetString((await reader.ReadLineAsync(128, false, TimeSpan.FromSeconds(1), CancellationToken.None))!.Value.Span));
        Assert.InRange(input.ReadCount, 256, 260);
    }

    [Fact]
    public async Task CrossChunkCrlfAndTlsResetKeepProtocolsSeparated()
    {
        using var plain = new ChunkedReadStream("A STARTTLS\r\nUNTRUSTED\r\n"u8.ToArray(), 11);
        using var reader = new BufferedProtocolReader(plain);
        Assert.Equal("A STARTTLS", Encoding.ASCII.GetString(
            (await reader.ReadLineAsync(128, false, TimeSpan.FromSeconds(1), CancellationToken.None))!.Value.Span));
        reader.DiscardBufferedPlaintext();
        using var tls = new MemoryStream("B NOOP\r\n"u8.ToArray());
        reader.Attach(tls);
        Assert.Equal("B NOOP", Encoding.ASCII.GetString(
            (await reader.ReadLineAsync(128, false, TimeSpan.FromSeconds(1), CancellationToken.None))!.Value.Span));
    }

    [Fact]
    public async Task StrictCrlfCrossesReadBoundaryAndRejectsBareCarriageReturn()
    {
        using var valid = new ChunkedReadStream("abc\r\n"u8.ToArray(), 4);
        using var reader = new BufferedProtocolReader(valid);
        Assert.Equal("abc", Encoding.ASCII.GetString(
            (await reader.ReadLineAsync(64, true, TimeSpan.FromSeconds(1), CancellationToken.None))!.Value.Span));

        using var invalid = new MemoryStream("ab\rc\r\n"u8.ToArray());
        using var invalidReader = new BufferedProtocolReader(invalid);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await invalidReader.ReadLineAsync(64, true, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task TimeoutCancelsReadAndDisallowsReusingCursor()
    {
        using var input = new StallingStream();
        using var reader = new BufferedProtocolReader(input);
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await reader.ReadLineAsync(64, false, TimeSpan.FromMilliseconds(10), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await reader.ReadLineAsync(64, false, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    private sealed class ChunkedReadStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public int ReadCount { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
        }
    }

    private sealed class StallingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
