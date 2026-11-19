using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

/// <summary>Best-effort same-user IPC. Only commit hints cross the pipe; database leases remain authoritative.</summary>
public sealed class ProcessWakeService(DeliveryWakeSignal delivery, MailboxChangeSignal mailboxes,
    IOptions<StorageOptions> storage, ILogger<ProcessWakeService> logger) : BackgroundService
{
    private readonly Channel<long> _outgoing = Channel.CreateBounded<long>(new BoundedChannelOptions(4096)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private void OnDelivery() => _outgoing.Writer.TryWrite(0);
    private void OnMailbox(long id) => _outgoing.Writer.TryWrite(id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = Path.GetFullPath(storage.Value.RootPath);
        var directory = Path.Combine(root, ".signals");
        var scope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root)))[..16];
        var name = $"mail-wake-{scope}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var registration = Path.Combine(directory, name);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(registration, string.Empty, stoppingToken);
            delivery.Published += OnDelivery;
            mailboxes.Published += OnMailbox;
            var receive = ReceiveAsync(name, lifetime.Token);
            var publish = PublishAsync(directory, name, lifetime.Token);
            await Task.WhenAny(receive, publish);
            lifetime.Cancel();
            await Task.WhenAll(receive, publish);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error) { logger.LogWarning(error, "Process wake IPC unavailable; database polling remains active."); }
        finally
        {
            delivery.Published -= OnDelivery;
            mailboxes.Published -= OnMailbox;
            try { File.Delete(registration); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task ReceiveAsync(string name, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var bytes = new byte[8];
                while (true)
                {
                    var first = await pipe.ReadAsync(bytes.AsMemory(0, 1), timeout.Token);
                    if (first == 0) break;
                    await pipe.ReadExactlyAsync(bytes.AsMemory(1), timeout.Token);
                    var id = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                    if (id == 0) delivery.PulseLocal();
                    else if (id > 0) mailboxes.PulseLocal(id);
                }
            }
            catch (Exception error) when (error is IOException || error is OperationCanceledException && !token.IsCancellationRequested) { }
        }
    }

    private async Task PublishAsync(string directory, string ownName, CancellationToken token)
    {
        string[] peers = [];
        var nextScan = DateTimeOffset.MinValue;
        while (await _outgoing.Reader.WaitToReadAsync(token))
        {
            await Task.Delay(50, token);
            var ids = new HashSet<long>();
            while (ids.Count < 256 && _outgoing.Reader.TryRead(out var id)) ids.Add(id);
            if (DateTimeOffset.UtcNow >= nextScan)
            {
                peers = Directory.EnumerateFiles(directory, "mail-wake-*").Select(Path.GetFileName)
                    .Where(x => x is not null && x != ownName && x.Length < 128).Take(256).ToArray()!;
                nextScan = DateTimeOffset.UtcNow.AddSeconds(5);
            }
            var bytes = new byte[ids.Count * 8];
            var offset = 0;
            foreach (var id in ids) { BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(offset), id); offset += 8; }
            await Parallel.ForEachAsync(peers, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (peer, ct) =>
            {
                try
                {
                    await using var pipe = new NamedPipeClientStream(".", peer, PipeDirection.Out,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.ConnectAsync(100, ct);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(1));
                    await pipe.WriteAsync(bytes, timeout.Token);
                }
                catch (Exception error) when (error is IOException or TimeoutException || error is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    var path = Path.Combine(directory, peer);
                    if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                        try { File.Delete(path); } catch (IOException) { }
                }
            });
        }
    }
}
