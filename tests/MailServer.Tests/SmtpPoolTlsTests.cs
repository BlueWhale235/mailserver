using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailKit;
using MailKit.Security;
using MailServer.Infrastructure;
using Microsoft.Extensions.Options;
using MimeKit;
using Xunit;

namespace MailServer.Tests;

public sealed class SmtpPoolTlsTests
{
    [Theory]
    [InlineData(421)]
    [InlineData(451)]
    [InlineData(550)]
    public async Task TlsReuseThenRemoteFailureIsEvictedWithoutAnotherSend(int failure)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = 0;
        var connections = 0;
        var mailCommands = 0;
        var server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            Interlocked.Increment(ref connections);
            using var ssl = new SslStream(peer.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
            using var reader = new StreamReader(ssl);
            using var writer = new StreamWriter(ssl) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 localhost ready");
            while (await reader.ReadLineAsync(deadline.Token) is { } command)
            {
                if (command.StartsWith("MAIL FROM", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref mailCommands);
                    if (received == 2 && failure != 421)
                    {
                        await writer.WriteLineAsync(failure + " rejected");
                        continue;
                    }
                }
                if (command == "NOOP" && received == 2 && failure == 421)
                {
                    await writer.WriteLineAsync("421 closing");
                    break;
                }
                if (command == "DATA")
                {
                    await writer.WriteLineAsync("354 send");
                    while (await reader.ReadLineAsync(deadline.Token) is { } line && line != ".") { }
                    Interlocked.Increment(ref received);
                }
                await writer.WriteLineAsync("250 OK");
            }
        });
        using var pool = new SmtpConnectionPool(Options.Create(new SmtpConnectionReuseOptions { Enabled = true, MaximumIdleConnections = 16 }));
        var message = new MimeMessage { Subject = "tls pool", Body = new TextPart("plain") { Text = "personalized" } };
        for (var index = 0; index < 2; index++)
        {
            using var lease = pool.Rent("tls-policy-v1", true);
            Assert.Equal(index == 1, lease.Reused);
            if (!lease.Reused)
            {
                lease.Client.ServerCertificateValidationCallback = (_, _, _, _) => true;
                await lease.Client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.SslOnConnect, deadline.Token);
            }
            Assert.True(lease.Client.IsSecure);
            await lease.Client.SendAsync(message, new MailboxAddress("", "a@test.example"),
                [new MailboxAddress("", "b@test.example")], deadline.Token);
            lease.MarkSuccessful("127.0.0.1");
        }
        using (var changedPolicy = pool.Rent("tls-policy-v2", true)) Assert.False(changedPolicy.Reused);
        using (var failed = pool.Rent("tls-policy-v1", true))
        {
            Assert.True(failed.Reused);
            if (failure == 421)
                await Assert.ThrowsAnyAsync<Exception>(() => failed.Client.NoOpAsync(deadline.Token));
            else
            {
                var error = await Assert.ThrowsAsync<MailKit.Net.Smtp.SmtpCommandException>(() =>
                    failed.Client.SendAsync(message, new MailboxAddress("", "a@test.example"),
                        [new MailboxAddress("", "b@test.example")], deadline.Token));
                Assert.Equal(failure, (int)error.StatusCode);
            }
        }
        using (var next = pool.Rent("tls-policy-v1", true)) Assert.False(next.Reused);
        await server.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, connections);
        Assert.Equal(2, received);
        Assert.Equal(failure == 421 ? 2 : 3, mailCommands);
        message.Dispose();
    }
}
