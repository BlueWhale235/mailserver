using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MailServer.Infrastructure;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MailServer.Tests;

public sealed class RelayTests
{
    [Fact]
    public void InboundCapacityAppliesGlobalAndPerIpLimitsAtomically()
    {
        var capacity = new RelayInboundCapacity(2, 1);
        Assert.True(capacity.TryAcquire(IPAddress.Loopback, out var first));
        Assert.False(capacity.TryAcquire(IPAddress.Parse("::ffff:127.0.0.1"), out _));
        Assert.True(capacity.TryAcquire(IPAddress.Parse("127.0.0.2"), out var second));
        Assert.False(capacity.TryAcquire(IPAddress.Parse("127.0.0.3"), out _));
        first!.Dispose();
        first.Dispose();
        Assert.True(capacity.TryAcquire(IPAddress.Parse("127.0.0.3"), out var third));
        second!.Dispose();
        third!.Dispose();
    }

    [Fact]
    public void RelayedCapacitySettingsValidateRanges()
    {
        Assert.Equal(new RelayInboundLimits(32, 4, 120), new RelayInboundLimits());
        var previous = JsonSerializer.Deserialize<RelaySettings>("""{"host":"relay.example.test","port":18443}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(32, previous!.MaxInboundConnections);
        Assert.Equal(4, previous.MaxConnectionsPerIp);
        Assert.Equal(120, previous.IdleTimeoutSeconds);
        foreach (var limits in new[]
        {
            new RelayInboundLimits(0, 1, 120), new RelayInboundLimits(201, 1, 120),
            new RelayInboundLimits(32, 0, 120), new RelayInboundLimits(32, 33, 120),
            new RelayInboundLimits(32, 4, 0), new RelayInboundLimits(32, 4, 3601)
        })
            Assert.Throws<ArgumentOutOfRangeException>(limits.Validate);
    }

    [Fact]
    public async Task InboundLimitReturns421AndIdleTimeoutReleasesSlotWithoutCuttingActiveMail()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mailserver-relay-capacity-" + MailServer.Domain.SnowflakeIds.NewId().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var pairingPort = ReservePort();
            var tunnelPort = ReservePort();
            var smtpPort = ReservePort();
            var rootConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["SecurityAssets:RootPath"] = Path.Combine(directory, "root") }).Build();
            var nodeConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SecurityAssets:RootPath"] = Path.Combine(directory, "node"),
                ["RelayNode:MaxInboundConnections"] = "5" // Local values cannot override the root.
            }).Build();
            var services = new ServiceCollection();
            services.AddDbContextFactory<MailDbContext>(options => options.UseInMemoryDatabase(MailServer.Domain.SnowflakeIds.NewId().ToString()));
            await using var provider = services.BuildServiceProvider();
            var rootIdentity = new RelayIdentity(rootConfig);
            var settings = new RelaySettingsService(provider.GetRequiredService<IDbContextFactory<MailDbContext>>(), rootIdentity);
            await settings.SaveAsync(new RelaySettingsUpdate("localhost", tunnelPort, "localhost", pairingPort,
                false, false, 1, 1, 2), CancellationToken.None);
            Assert.Equal(1, (await settings.ViewAsync(CancellationToken.None)).MaxInboundConnections);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => settings.SaveAsync(
                new RelaySettingsUpdate("localhost", tunnelPort, "localhost", pairingPort,
                    false, false, 1, 2, 2), CancellationToken.None));
            var invitation = await settings.CreateInvitationAsync(CancellationToken.None);
            var pairing = new RelayRootService(settings, rootIdentity, NullLogger<RelayRootService>.Instance, "api");
            var node = new RelayNodeService(new RelayIdentity(nodeConfig), nodeConfig, NullLogger<RelayNodeService>.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await pairing.StartAsync(timeout.Token);
            try
            {
                var id = invitation.Command.Split("--id ")[1].Split(' ')[0];
                await node.PairAsync("localhost", pairingPort, id, rootIdentity.Fingerprint, invitation.Password, timeout.Token);
                await settings.SaveAsync(new RelaySettingsUpdate("localhost", tunnelPort, "localhost", pairingPort,
                    true, false, 1, 1, 2), timeout.Token);
                using var nodeShutdown = new CancellationTokenSource();
                var nodeRun = node.RunAsync(tunnelPort, smtpPort, nodeShutdown.Token);
                Func<RelayOpen, Stream, CancellationToken, Task> inboundHandler = async (_, stream, token) =>
                {
                    await stream.WriteAsync("220 ready\r\n"u8.ToArray(), token);
                    await stream.CopyToAsync(Stream.Null, token);
                };
                var smtpRoot = new RelayRootService(settings, rootIdentity, NullLogger<RelayRootService>.Instance, "smtp")
                    { InboundHandler = inboundHandler };
                RelayRootService? reconnectedRoot = null;
                await smtpRoot.StartAsync(timeout.Token);
                try
                {
                    while (!smtpRoot.Connected) await Task.Delay(50, timeout.Token);
                    using var first = new TcpClient();
                    await first.ConnectAsync(IPAddress.Loopback, smtpPort, timeout.Token);
                    Assert.Equal("220 ready", await ReadLineAsync(first, timeout.Token));
                    using var busy = new TcpClient();
                    await busy.ConnectAsync(IPAddress.Loopback, smtpPort, timeout.Token);
                    Assert.StartsWith("421", await ReadLineAsync(busy, timeout.Token));

                    // Keep the first SMTP connection active beyond its two-second idle limit.
                    for (var i = 0; i < 6; i++)
                    {
                        await Task.Delay(400, timeout.Token);
                        await first.GetStream().WriteAsync("x"u8.ToArray(), timeout.Token);
                    }
                    using var stillBusy = new TcpClient();
                    await stillBusy.ConnectAsync(IPAddress.Loopback, smtpPort, timeout.Token);
                    Assert.StartsWith("421", await ReadLineAsync(stillBusy, timeout.Token));

                    var eof = new byte[1];
                    Assert.Equal(0, await first.GetStream().ReadAsync(eof, timeout.Token));
                    using var afterIdle = new TcpClient();
                    await afterIdle.ConnectAsync(IPAddress.Loopback, smtpPort, timeout.Token);
                    Assert.Equal("220 ready", await ReadLineAsync(afterIdle, timeout.Token));
                    afterIdle.Dispose();

                    var connectedAt = smtpRoot.LastConnected;
                    await settings.SaveAsync(new RelaySettingsUpdate("localhost", tunnelPort, "localhost", pairingPort,
                        true, false, 2, 2, 10), timeout.Token);
                    while ((await settings.ReadStatusAsync(timeout.Token)).AppliedInboundLimits != new RelayInboundLimits(2, 2, 10))
                        await Task.Delay(50, timeout.Token);
                    Assert.Equal(connectedAt, smtpRoot.LastConnected);
                    using var expandedOne = new TcpClient();
                    using var expandedTwo = new TcpClient();
                    await expandedOne.ConnectAsync(IPAddress.Loopback, smtpPort, timeout.Token);
                    await expandedTwo.ConnectAsync(IPAddress.Loopback, smtpPort, timeout.Token);
                    Assert.Equal("220 ready", await ReadLineAsync(expandedOne, timeout.Token));
                    Assert.Equal("220 ready", await ReadLineAsync(expandedTwo, timeout.Token));
                    Assert.Equal(new[] { "binding.json", "identity.pem" },
                        Directory.GetFiles(Path.Combine(directory, "node", "relay"))
                            .Select(path => Path.GetFileName(path)!).OrderBy(x => x).ToArray());

                    expandedOne.Dispose();
                    expandedTwo.Dispose();
                    await smtpRoot.StopAsync(CancellationToken.None);
                    await settings.SaveAsync(new RelaySettingsUpdate("localhost", tunnelPort, "localhost", pairingPort,
                        true, false, 1, 1, 2), timeout.Token);
                    reconnectedRoot = new RelayRootService(settings, rootIdentity, NullLogger<RelayRootService>.Instance,
                        "smtp") { InboundHandler = inboundHandler };
                    await reconnectedRoot.StartAsync(timeout.Token);
                    while (!reconnectedRoot.Connected ||
                        (await settings.ReadStatusAsync(timeout.Token)).AppliedInboundLimits != new RelayInboundLimits(1, 1, 2))
                        await Task.Delay(50, timeout.Token);
                    using var afterReconnect = new TcpClient();
                    await afterReconnect.ConnectAsync(IPAddress.Loopback, smtpPort, timeout.Token);
                    Assert.Equal("220 ready", await ReadLineAsync(afterReconnect, timeout.Token));
                    using var afterReconnectBusy = new TcpClient();
                    await afterReconnectBusy.ConnectAsync(IPAddress.Loopback, smtpPort, timeout.Token);
                    Assert.StartsWith("421", await ReadLineAsync(afterReconnectBusy, timeout.Token));
                }
                finally
                {
                    if (reconnectedRoot is not null) await reconnectedRoot.StopAsync(CancellationToken.None);
                    await smtpRoot.StopAsync(CancellationToken.None);
                    nodeShutdown.Cancel();
                    try { await nodeRun; } catch (OperationCanceledException) { }
                }
            }
            finally { await pairing.StopAsync(CancellationToken.None); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<string> ReadLineAsync(TcpClient client, CancellationToken token)
    {
        var bytes = new List<byte>();
        var current = new byte[1];
        while (await client.GetStream().ReadAsync(current, token) == 1)
        {
            if (current[0] == '\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(current[0]);
        }
        throw new EndOfStreamException();
    }

    [Fact]
    public async Task MailKitCanDeliverOverMultiplexedRelayStream()
    {
        using var mxListener = new TcpListener(IPAddress.Loopback, 0);
        mxListener.Start();
        var mxPort = ((IPEndPoint)mxListener.LocalEndpoint).Port;
        using var wireListener = new TcpListener(IPAddress.Loopback, 0);
        wireListener.Start();
        using var rootSocket = new TcpClient();
        var connecting = rootSocket.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)wireListener.LocalEndpoint).Port);
        using var relaySocket = await wireListener.AcceptTcpClientAsync();
        await connecting;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var mxTask = ServeFakeMxAsync(mxListener, timeout.Token);
        await using var root = new RelayTunnel(rootSocket.GetStream(), true);
        await using var relay = new RelayTunnel(relaySocket.GetStream(), false);
        relay.OnOpen = async (_, stream, token) =>
        {
            using var destination = new TcpClient();
            await destination.ConnectAsync(IPAddress.Loopback, mxPort, token);
            await RelayStreamBridge.CopyBothWaysAsync(stream, destination.GetStream(), token);
        };
        var rootLoop = root.RunAsync(timeout.Token);
        var relayLoop = relay.RunAsync(timeout.Token);
        await using var relayedStream = await root.OpenAsync(new RelayOpen("outbound", "localhost", 25), timeout.Token);
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(relayedStream, "localhost", mxPort, SecureSocketOptions.None, timeout.Token);
        var from = new MailboxAddress("sender", "sender@example.test");
        var to = new MailboxAddress("recipient", "recipient@outside.test");
        var message = new MimeMessage { Subject = "relay smoke", Body = new TextPart("plain") { Text = "hello" } };
        message.From.Add(from);
        message.To.Add(to);
        Assert.Contains("queued", await smtp.SendAsync(message, from, [to], timeout.Token));
        await smtp.DisconnectAsync(true, timeout.Token);
        await mxTask;
        timeout.Cancel();
        await Task.WhenAll(rootLoop, relayLoop);
    }

    private static async Task ServeFakeMxAsync(TcpListener listener, CancellationToken token)
    {
        using var peer = await listener.AcceptTcpClientAsync(token);
        using var reader = new StreamReader(peer.GetStream(), Encoding.ASCII, false, 1024, true);
        using var writer = new StreamWriter(peer.GetStream(), Encoding.ASCII, 1024, true)
            { NewLine = "\r\n", AutoFlush = true };
        await writer.WriteLineAsync("220 mx.example.test ESMTP");
        while (true)
        {
            var line = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException();
            if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync("250 mx.example.test");
            else if (line.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync("250 sender accepted");
            else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase)) await writer.WriteLineAsync("250 recipient accepted");
            else if (line == "DATA")
            {
                await writer.WriteLineAsync("354 send data");
                while (await reader.ReadLineAsync(token) is { } data && data != ".") { }
                await writer.WriteLineAsync("250 queued");
            }
            else if (line == "QUIT") { await writer.WriteLineAsync("221 bye"); return; }
            else throw new InvalidDataException("Unexpected SMTP command: " + line);
        }
    }

    [Fact]
    public async Task PairingPinsBothIdentitiesAndConsumesOneTimePassword()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mailserver-relay-test-" + MailServer.Domain.SnowflakeIds.NewId().ToString());
        Directory.CreateDirectory(directory);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var rootConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["SecurityAssets:RootPath"] = Path.Combine(directory, "root") }).Build();
        var nodeConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["SecurityAssets:RootPath"] = Path.Combine(directory, "node") }).Build();
        var services = new ServiceCollection();
        services.AddDbContextFactory<MailDbContext>(options => options.UseInMemoryDatabase(MailServer.Domain.SnowflakeIds.NewId().ToString()));
        await using var provider = services.BuildServiceProvider();
        var rootIdentity = new RelayIdentity(rootConfig);
        var settings = new RelaySettingsService(provider.GetRequiredService<IDbContextFactory<MailDbContext>>(), rootIdentity);
        await settings.SaveAsync(new RelaySettingsUpdate("localhost", 18443, "localhost", port, false, false), CancellationToken.None);
        var invitation = await settings.CreateInvitationAsync(CancellationToken.None);
        var root = new RelayRootService(settings, rootIdentity, NullLogger<RelayRootService>.Instance, "api");
        var node = new RelayNodeService(new RelayIdentity(nodeConfig), nodeConfig, NullLogger<RelayNodeService>.Instance);
        await root.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            await Assert.ThrowsAnyAsync<Exception>(() => node.PairAsync("localhost", port,
                invitation.Command.Split("--id ")[1].Split(' ')[0], "00", invitation.Password, CancellationToken.None));
            Assert.False((await settings.ViewAsync(CancellationToken.None)).Paired);
            var id = invitation.Command.Split("--id ")[1].Split(' ')[0];
            await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() =>
                node.PairAsync("localhost", port, id, rootIdentity.Fingerprint, "wrong", CancellationToken.None));
            await node.PairAsync("localhost", port, id, rootIdentity.Fingerprint, invitation.Password, CancellationToken.None);
            var view = await settings.ViewAsync(CancellationToken.None);
            Assert.True(view.Paired);
            Assert.False(view.PairingPending);
            Assert.False(await settings.CompletePairingAsync(id, invitation.Password, view.PeerFingerprint!, CancellationToken.None));

            await settings.SaveAsync(new RelaySettingsUpdate("localhost", 18443, "localhost", port, true, false), CancellationToken.None);
            using var tunnelPortListener = new TcpListener(IPAddress.Loopback, 0);
            tunnelPortListener.Start();
            var tunnelPort = ((IPEndPoint)tunnelPortListener.LocalEndpoint).Port;
            tunnelPortListener.Stop();
            using var smtpPortListener = new TcpListener(IPAddress.Loopback, 0);
            smtpPortListener.Start();
            var smtpPort = ((IPEndPoint)smtpPortListener.LocalEndpoint).Port;
            smtpPortListener.Stop();
            await settings.SaveAsync(new RelaySettingsUpdate("localhost", tunnelPort, "localhost", port, true, false), CancellationToken.None);
            using var nodeShutdown = new CancellationTokenSource();
            var nodeRun = node.RunAsync(tunnelPort, smtpPort, nodeShutdown.Token);
            var smtpRoot = new RelayRootService(settings, rootIdentity, NullLogger<RelayRootService>.Instance, "smtp");
            smtpRoot.InboundHandler = async (request, stream, token) =>
            {
                Assert.Equal("inbound", request.Kind);
                Assert.NotNull(request.RemoteAddress);
                var received = new byte[4];
                await stream.ReadExactlyAsync(received, token);
                Assert.Equal("ping", Encoding.ASCII.GetString(received));
                await stream.WriteAsync("pong"u8.ToArray(), token);
            };
            await smtpRoot.StartAsync(CancellationToken.None);
            try
            {
                using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!smtpRoot.Connected)
                    await Task.Delay(50, connectionTimeout.Token);
                using var sender = new TcpClient();
                await sender.ConnectAsync(IPAddress.Loopback, smtpPort, connectionTimeout.Token);
                await sender.GetStream().WriteAsync("ping"u8.ToArray(), connectionTimeout.Token);
                var reply = new byte[4];
                await sender.GetStream().ReadExactlyAsync(reply, connectionTimeout.Token);
                Assert.Equal("pong", Encoding.ASCII.GetString(reply));
                Assert.True(await smtpRoot.RevokeRemoteAsync(connectionTimeout.Token));
                Assert.False(File.Exists(Path.Combine(directory, "node", "relay", "binding.json")));
                using var afterRevoke = new TcpClient();
                await afterRevoke.ConnectAsync(IPAddress.Loopback, smtpPort, connectionTimeout.Token);
                var temporary = new byte[3];
                await afterRevoke.GetStream().ReadExactlyAsync(temporary, connectionTimeout.Token);
                Assert.Equal("421", Encoding.ASCII.GetString(temporary));
            }
            finally
            {
                await smtpRoot.StopAsync(CancellationToken.None);
                nodeShutdown.Cancel();
                try { await nodeRun; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            await root.StopAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MultiplexedTunnelCarriesIndependentStreamsAndClosesThemOnDisconnect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var leftClient = new TcpClient();
        var connecting = leftClient.ConnectAsync(IPAddress.Loopback, port);
        using var rightClient = await listener.AcceptTcpClientAsync();
        await connecting;
        await using var left = new RelayTunnel(leftClient.GetStream(), true);
        await using var right = new RelayTunnel(rightClient.GetStream(), false);
        right.OnOpen = async (request, stream, token) =>
        {
            var bytes = new byte[request.Kind == "bulk" ? 256 * 1024 : 5];
            await stream.ReadExactlyAsync(bytes, token);
            await stream.WriteAsync(bytes, token);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var leftLoop = left.RunAsync(timeout.Token);
        var rightLoop = right.RunAsync(timeout.Token);
        var requests = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var stream = await left.OpenAsync(new RelayOpen("outbound", "example.com", 25), timeout.Token);
            await stream.WriteAsync("hello"u8.ToArray(), timeout.Token);
            var reply = new byte[5];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            Assert.Equal("hello", Encoding.ASCII.GetString(reply));
        });
        await Task.WhenAll(requests);
        await using (var bulk = await left.OpenAsync(new RelayOpen("bulk"), timeout.Token))
        {
            var payload = Enumerable.Range(0, 256 * 1024).Select(x => (byte)x).ToArray();
            await bulk.WriteAsync(payload, timeout.Token);
            var received = new byte[payload.Length];
            await bulk.ReadExactlyAsync(received, timeout.Token);
            Assert.Equal(payload, received);
        }
        timeout.Cancel();
        await Task.WhenAll(leftLoop, rightLoop);
        Assert.Equal(0, left.ActiveStreams);
        Assert.Equal(0, right.ActiveStreams);
    }
}
