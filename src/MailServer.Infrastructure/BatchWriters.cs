using System.Threading.Channels;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

public abstract class BoundedBatchWriter<T>(TimeSpan interval) : BackgroundService
{
    private readonly Channel<T> _queue = Channel.CreateBounded<T>(new BoundedChannelOptions(4096)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });
    private int _running;
    public bool IsRunning => Volatile.Read(ref _running) == 1;
    public ValueTask EnqueueAsync(T value, CancellationToken token) => _queue.Writer.WriteAsync(value, token);
    public override Task StartAsync(CancellationToken token)
    {
        Volatile.Write(ref _running, 1);
        return base.StartAsync(token);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _queue.Reader.WaitToReadAsync(stoppingToken))
        {
            if (_queue.Reader.Count < 256 && IsRunning) await Task.Delay(interval, stoppingToken);
            var batch = new List<T>(256);
            while (batch.Count < 256 && _queue.Reader.TryRead(out var value)) batch.Add(value);
            if (batch.Count != 0) await PersistAsync(batch, stoppingToken);
        }
    }
    protected abstract Task PersistAsync(IReadOnlyList<T> batch, CancellationToken token);
    public override async Task StopAsync(CancellationToken token)
    {
        Volatile.Write(ref _running, 0);
        _queue.Writer.TryComplete();
        try { if (ExecuteTask is not null) await ExecuteTask.WaitAsync(token); }
        finally { await base.StopAsync(token); }
    }
}

public sealed class TlsEventWriter(IDbContextFactory<MailDbContext> factory, ILogger<TlsEventWriter> logger)
    : BoundedBatchWriter<TlsDeliveryEvent>(TimeSpan.FromSeconds(5))
{
    protected override async Task PersistAsync(IReadOnlyList<TlsDeliveryEvent> batch, CancellationToken token)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            db.TlsDeliveryEvents.AddRange(batch);
            await db.SaveChangesAsync(token);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { logger.LogError(error, "Unable to persist {Count} queued TLS delivery events.", batch.Count); }
    }
}

public sealed record LocalLogWrite(object Entity, TaskCompletionSource Completion);

public sealed class LocalLogWriter(LocalLogDatabase database) : BoundedBatchWriter<LocalLogWrite>(TimeSpan.FromMilliseconds(5))
{
    public override async Task StartAsync(CancellationToken token)
    {
        await base.StartAsync(token);
        database.Writer = this;
    }
    protected override async Task PersistAsync(IReadOnlyList<LocalLogWrite> batch, CancellationToken token)
    {
        try
        {
            using var suppression = PerformanceTelemetry.Suppress();
            await using var db = database.CreateDbContext();
            db.AddRange(batch.Select(x => x.Entity));
            await db.SaveChangesAsync(token);
        }
        catch (Exception error) when (error is not OperationCanceledException) { database.ReportFailure(error); }
        finally { foreach (var entry in batch) entry.Completion.TrySetResult(); }
    }
}
