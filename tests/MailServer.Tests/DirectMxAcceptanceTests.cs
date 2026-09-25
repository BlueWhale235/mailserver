using System.Net;
using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using MailServer.Domain;
using MailServer.Infrastructure;
using Xunit;

namespace MailServer.Tests;

public sealed class DirectMxAcceptanceTests
{
    [Fact]
    public async Task TemporaryMxFailureTriesNextMxAndPermanentFailureStops()
    {
        var tried = new List<string>();
        var delivered = await DirectMxTransport.DeliverAcrossMxAsync(
            ["mx-primary.test", "mx-backup.test", "mx-unused.test"],
            host =>
            {
                tried.Add(host);
                return Task.FromResult(host == "mx-primary.test"
                    ? DeliveryResult.Temporary("connection refused", host)
                    : DeliveryResult.Success("250 queued", host));
            },
            () => DeliveryResult.Temporary("no MX"));
        Assert.True(delivered.Succeeded);
        Assert.Equal("mx-backup.test", delivered.RemoteHost);
        Assert.Equal(["mx-primary.test", "mx-backup.test"], tried);

        tried.Clear();
        var rejected = await DirectMxTransport.DeliverAcrossMxAsync(
            ["mx-primary.test", "mx-backup.test"],
            host =>
            {
                tried.Add(host);
                return Task.FromResult(DeliveryResult.Permanent("550 rejected", host));
            },
            () => DeliveryResult.Temporary("no MX"));
        Assert.True(rejected.PermanentFailure);
        Assert.Equal(["mx-primary.test"], tried);
    }

    [Fact]
    public async Task ConnectFallsBackAcrossMxIpAddressesAndRecordsChosenIp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = peer.GetStream();
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 mx.test ESMTP ready");
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase))
                    await writer.WriteLineAsync("250 mx.test");
                else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 bye");
                    break;
                }
            }
        }, timeout.Token);

        using var client = new SmtpClient();
        var selected = await OutboundDeliveryProfile.ConnectAsync(
            client, "mx.test", port, SecureSocketOptions.None,
            [IPAddress.Parse("192.0.2.1")], timeout.Token,
            [IPAddress.Parse("127.0.0.2"), IPAddress.Loopback]);

        Assert.Equal(IPAddress.Loopback, selected);
        Assert.True(client.IsConnected);
        await client.DisconnectAsync(true, timeout.Token);
        await server;
    }
}
