using MailServer.Infrastructure;
using Microsoft.Extensions.Options;
using MailKit.Security;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MailServer.Tests;

public sealed class FairHeavyOperationBudgetTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public async Task ConnectionPoolHonorsConfiguredIdleCapAndOneIdlePerTarget(int cap)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var peers = new List<Task>();
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                    peers.Add(Task.Run(async () =>
                    {
                        using (peer)
                        {
                            using var reader = new StreamReader(peer.GetStream());
                            using var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true, NewLine = "\r\n" };
                            await writer.WriteLineAsync("220 test ready");
                            try
                            {
                                while (await reader.ReadLineAsync(timeout.Token) is not null)
                                    await writer.WriteLineAsync("250 OK");
                            }
                            catch (OperationCanceledException) { }
                        }
                    }));
                }
            }
            catch (OperationCanceledException) { }
        });
        using var pool = new SmtpConnectionPool(Options.Create(new SmtpConnectionReuseOptions
        { Enabled = true, MaximumIdleConnections = cap }));
        for (var index = 0; index <= cap; index++)
        {
            using var lease = pool.Rent("domain-" + index, true);
            await lease.Client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, timeout.Token);
            lease.MarkSuccessful("127.0.0.1");
        }
        for (var index = 0; index < cap; index++)
        {
            using var retained = pool.Rent("domain-" + index, true);
            Assert.True(retained.Reused);
            retained.MarkSuccessful("127.0.0.1");
        }
        using (var overflow = pool.Rent("domain-" + cap, true)) Assert.False(overflow.Reused);
        // A duplicate idle target is closed rather than growing the per-target cap.
        using (var first = pool.Rent("domain-0", true))
        using (var duplicate = pool.Rent("domain-0", true))
        {
            Assert.True(first.Reused);
            Assert.False(duplicate.Reused);
            await duplicate.Client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None, timeout.Token);
            first.MarkSuccessful("127.0.0.1");
            duplicate.MarkSuccessful("127.0.0.1");
        }
        using (var only = pool.Rent("domain-0", true))
        using (var noSecond = pool.Rent("domain-0", true))
        {
            Assert.True(only.Reused);
            Assert.False(noSecond.Reused);
        }
        pool.Dispose();
        timeout.Cancel();
        await server;
        await Task.WhenAll(peers);
    }

    private static (ResourceRuntime Runtime, FairHeavyOperationBudget Budget) Create(int limit = 1)
    {
        var runtime = new ResourceRuntime("all", new ConfigurationBuilder().Build());
        runtime.LargeMime.Resize(limit);
        var budget = new FairHeavyOperationBudget(runtime);
        budget.Configure(true);
        budget.UpdateMemoryHeadroom(4L * 1024 * 1024 * 1024);
        return (runtime, budget);
    }

    [Fact]
    public async Task AlternatesInboundAndOutboundAndLeavesMemoryOwnedDuringNetwork()
    {
        var (runtime, budget) = Create();
        using var blocker = runtime.LargeMime.TryAcquire()!;
        var inbound1 = budget.AcquireAsync(HeavyOperationRole.Inbound, default);
        var inbound2 = budget.AcquireAsync(HeavyOperationRole.Inbound, default);
        var outbound = budget.AcquireAsync(HeavyOperationRole.Outbound, default);
        blocker.Dispose();
        using var first = await inbound1.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(inbound2.IsCompleted);
        Assert.False(outbound.IsCompleted);
        first.ReleaseHeavy();
        using var second = await outbound.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(inbound2.IsCompleted);
        Assert.Equal(512L * 1024 * 1024, budget.ReservedMemoryBytes);
        second.Dispose();
        using var third = await inbound2.WaitAsync(TimeSpan.FromSeconds(2));
        first.Dispose();
        third.Dispose();
        Assert.Equal(0, budget.ReservedMemoryBytes);
        Assert.Equal(0, runtime.LargeMime.Active);
    }

    [Fact]
    public async Task LowMemoryWaitCanCancelAndReservationReleaseUnblocksNext()
    {
        var (runtime, budget) = Create(4);
        budget.UpdateMemoryHeadroom(800L * 1024 * 1024);
        using var first = await budget.AcquireAsync(HeavyOperationRole.Outbound, default);
        var second = budget.AcquireAsync(HeavyOperationRole.Inbound, default);
        await Task.Delay(60);
        Assert.False(second.IsCompleted);
        first.ReleaseHeavy();
        Assert.False(second.IsCompleted);
        first.Dispose();
        using var admitted = await second.WaitAsync(TimeSpan.FromSeconds(2));
        budget.UpdateMemoryHeadroom(null);
        using var cancel = new CancellationTokenSource(70);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            budget.AcquireAsync(HeavyOperationRole.Outbound, cancel.Token));
        Assert.Equal(0, budget.WaitingTotal);
        admitted.Dispose();
        Assert.Equal(0, runtime.LargeMime.Active);
    }

    [Fact]
    public async Task BackgroundWaitsBehindMailAndDisabledBudgetKeepsLegacyBehavior()
    {
        var (runtime, budget) = Create();
        using var blocker = runtime.LargeMime.TryAcquire()!;
        var background = budget.AcquireAsync(HeavyOperationRole.Background, default);
        var inbound = budget.AcquireAsync(HeavyOperationRole.Inbound, default);
        blocker.Dispose();
        using var mail = await inbound.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(background.IsCompleted);
        mail.Dispose();
        using var backfill = await background.WaitAsync(TimeSpan.FromSeconds(2));
        backfill.Dispose();
        budget.Configure(false);
        budget.UpdateMemoryHeadroom(null);
        using var legacy = await budget.AcquireAsync(HeavyOperationRole.Inbound, default);
        Assert.Equal(0, budget.ReservedMemoryBytes);
    }

    [Fact]
    public async Task QueueIsBoundedAndShrinkDoesNotCancelActiveOperations()
    {
        var (runtime, budget) = Create(2);
        using var active1 = await budget.AcquireAsync(HeavyOperationRole.Inbound, default);
        using var active2 = await budget.AcquireAsync(HeavyOperationRole.Outbound, default);
        runtime.LargeMime.Resize(1);
        using var cancel = new CancellationTokenSource();
        var pending = Enumerable.Range(0, 128)
            .Select(_ => budget.AcquireAsync(HeavyOperationRole.Inbound, cancel.Token)).ToArray();
        await Assert.ThrowsAsync<IOException>(() => budget.AcquireAsync(HeavyOperationRole.Inbound, default));
        active1.Dispose();
        Assert.All(pending, task => Assert.False(task.IsCompleted));
        active2.Dispose();
        using var next = await pending[0].WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        foreach (var task in pending.Skip(1))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        next.Dispose();
        Assert.Equal(0, budget.WaitingTotal);
    }
}
