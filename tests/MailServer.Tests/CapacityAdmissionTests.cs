using MailServer.Infrastructure;
using Xunit;

namespace MailServer.Tests;

public sealed class CapacityAdmissionTests
{
    [Fact]
    public async Task CanceledAdmissionDoesNotConsumeAnAvailablePermit()
    {
        var limit = new AdjustableConcurrencyLimit(1);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limit.AcquireAsync(canceled.Token));
        Assert.Equal(0, limit.Active);
        Assert.Equal(0, limit.Waiting);
    }

    [Fact]
    public async Task WaitingAdmissionIsBoundedAndCancellationReleasesQueueSpace()
    {
        var limit = new AdjustableConcurrencyLimit(1);
        using var held = limit.TryAcquire();
        using var cancel = new CancellationTokenSource();
        var waiting = limit.AcquireAsync(cancel.Token, TimeSpan.FromSeconds(5), maximumWaiting: 1);
        Assert.Equal(1, limit.Waiting);
        await Assert.ThrowsAsync<IOException>(() => limit.AcquireAsync(CancellationToken.None,
            TimeSpan.FromSeconds(5), maximumWaiting: 1));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, limit.Waiting);
        Assert.Equal(1, limit.Active);
    }

    [Fact]
    public async Task ResizeWakesWaitersWithoutRevokingExistingWork()
    {
        var limit = new AdjustableConcurrencyLimit(1);
        using var held = limit.TryAcquire();
        var waiting = limit.AcquireAsync(CancellationToken.None, TimeSpan.FromSeconds(2), 1);
        limit.Resize(2);
        using var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, limit.Active);
        limit.Resize(1);
        Assert.Null(limit.TryAcquire());
        admitted.Dispose();
        Assert.Null(limit.TryAcquire());
        held!.Dispose();
        using var next = limit.TryAcquire();
        Assert.NotNull(next);
        Assert.Equal(0, limit.Waiting);
    }

    [Fact]
    public async Task TimedOutWaiterDoesNotLeakCapacity()
    {
        var limit = new AdjustableConcurrencyLimit(1);
        using var held = limit.TryAcquire();
        await Assert.ThrowsAsync<IOException>(() => limit.AcquireAsync(CancellationToken.None,
            TimeSpan.FromMilliseconds(50), 1));
        Assert.Equal(0, limit.Waiting);
        held!.Dispose();
        using var next = await limit.AcquireAsync(CancellationToken.None, TimeSpan.FromSeconds(1), 1);
        Assert.Equal(1, limit.Active);
    }
}
