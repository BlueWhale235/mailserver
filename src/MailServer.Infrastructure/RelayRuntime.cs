using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MailServer.Infrastructure;

internal sealed record RelayHello(string Role, bool ReceiveEnabled, bool SendEnabled,
    RelayInboundLimits? InboundLimits = null, int Version = 2);
internal sealed record RelayPairRequest(string Id, string Password);
internal sealed record RelayPairResult(bool Accepted);
internal sealed record RelayNodeBinding(string RootFingerprint, string RootHost, int RootPort);
internal sealed record RelayConnectResult(string SourceAddress, string RemoteAddress);
internal sealed class RelayInboundCapacity(int maximum, int maximumPerIp)
{
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, int> _byIp = new();
    private int _maximum = maximum;
    private int _maximumPerIp = maximumPerIp;
    private int _active;

    public void Update(RelayInboundLimits limits)
    {
        limits.Validate();
        lock (_gate)
        {
            _maximum = limits.MaxInboundConnections;
            _maximumPerIp = limits.MaxConnectionsPerIp;
        }
    }

    public bool TryAcquire(IPAddress address, out IDisposable? lease)
    {
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        lock (_gate)
        {
            _byIp.TryGetValue(address, out var count);
            if (_active >= _maximum || count >= _maximumPerIp)
            {
                lease = null;
                return false;
            }
            _active++;
            _byIp[address] = count + 1;
            lease = new CapacityLease(this, address);
            return true;
        }
    }

    private void Release(IPAddress address)
    {
        lock (_gate)
        {
            _active--;
            var remaining = _byIp[address] - 1;
            if (remaining == 0) _byIp.Remove(address);
            else _byIp[address] = remaining;
        }
    }

    private sealed class CapacityLease(RelayInboundCapacity owner, IPAddress address) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(address);
        }
    }
}

public sealed record RelayOutboundConnection(Stream Stream, string SourceAddress, string RemoteAddress) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

internal static class RelayWire
{
    public static TcpListener CreateListener(int port)
    {
        var listener = new TcpListener(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, port);
        if (Socket.OSSupportsIPv6) listener.Server.DualMode = true;
        return listener;
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 4096) throw new InvalidDataException("Relay control message too large.");
        var length = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(bytes.Length));
        await stream.WriteAsync(length, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, token);
        var count = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(length));
        if (count is < 1 or > 4096) throw new InvalidDataException("Invalid relay control length.");
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Invalid relay control message.");
    }
}

/// <summary>API runs the pairing listener; SMTP and Worker maintain their own persistent tunnels.</summary>
public sealed class RelayRootService(
    RelaySettingsService settings,
    RelayIdentity identity,
    ILogger<RelayRootService> logger,
    string role) : BackgroundService
{
    private RelayTunnel? _tunnel;
    private readonly SemaphoreSlim _pairLimit = new(16, 16);
    private DateTimeOffset? _lastConnected;
    private string? _lastError;
    private RelayInboundLimits? _appliedInboundLimits;
    public Func<RelayOpen, Stream, CancellationToken, Task>? InboundHandler { get; set; }
    public bool Connected => _tunnel is not null;
    public int ActiveStreams => _tunnel?.ActiveStreams ?? 0;
    public DateTimeOffset? LastConnected => _lastConnected;
    public string? LastError => _lastError;

    public async Task TestConnectionAsync(CancellationToken token)
    {
        var current = await settings.ReadAsync(token);
        if (current.PeerFingerprint is null) throw new InvalidOperationException("Relay is not paired.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync(current.Host, current.Port, timeout.Token);
        await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && RelayIdentity.FingerprintOf(new X509Certificate2(certificate)) == current.PeerFingerprint);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = current.Host,
            ClientCertificates = new X509CertificateCollection { identity.Certificate },
            EnabledSslProtocols = SslProtocols.Tls13
        }, timeout.Token);
        await RelayWire.WriteAsync(tls, new RelayHello("worker", false, false), timeout.Token);
        if (!(await RelayWire.ReadAsync<RelayPairResult>(tls, timeout.Token)).Accepted)
            throw new AuthenticationException("Relay rejected the protocol handshake.");
    }

    public async Task<bool> RevokeRemoteAsync(CancellationToken token)
    {
        var current = await settings.ReadAsync(token);
        if (current.PeerFingerprint is null) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var client = new TcpClient();
        await client.ConnectAsync(current.Host, current.Port, timeout.Token);
        await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && RelayIdentity.FingerprintOf(new X509Certificate2(certificate)) == current.PeerFingerprint);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = current.Host,
            ClientCertificates = new X509CertificateCollection { identity.Certificate },
            EnabledSslProtocols = SslProtocols.Tls13
        }, timeout.Token);
        await RelayWire.WriteAsync(tls, new RelayHello("revoke", false, false), timeout.Token);
        return (await RelayWire.ReadAsync<RelayPairResult>(tls, timeout.Token)).Accepted;
    }

    public async Task<RelayOutboundConnection> OpenOutboundAsync(string host, CancellationToken token)
    {
        var current = await settings.ReadAsync(token);
        if (!current.SendEnabled || current.PeerFingerprint is null)
            throw new IOException("Outbound relay is disabled or unpaired.");
        var tunnel = _tunnel ?? throw new IOException("Outbound relay is disconnected.");
        var stream = await tunnel.OpenAsync(new RelayOpen("outbound", host, 25), token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var result = await RelayWire.ReadAsync<RelayConnectResult>(stream, timeout.Token);
            return new RelayOutboundConnection(stream, result.SourceAddress, result.RemoteAddress);
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => role switch
    {
        "api" => PairingLoopAsync(stoppingToken),
        "all" => Task.WhenAll(PairingLoopAsync(stoppingToken), ConnectorLoopAsync(stoppingToken)),
        _ => ConnectorLoopAsync(stoppingToken)
    };

    private async Task PairingLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var current = await settings.ReadAsync(token);
            if (current.PendingId is null || current.PendingExpiresAt <= DateTimeOffset.UtcNow)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                continue;
            }
            using var listener = RelayWire.CreateListener(current.PairingPort);
            try
            {
                listener.Start(16);
                while (!token.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(timeout.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        var latest = await settings.ReadAsync(token);
                        if (latest.PairingPort != current.PairingPort || latest.PendingId != current.PendingId ||
                            latest.PendingExpiresAt <= DateTimeOffset.UtcNow) break;
                        continue;
                    }
                    if (!_pairLimit.Wait(0)) { client.Dispose(); continue; }
                    _ = HandlePairAsync(client, token);
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                logger.LogWarning(error, "Relay pairing listener failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
    }

    private async Task HandlePairAsync(TcpClient client, CancellationToken stoppingToken)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = identity.Certificate, ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls13
                    }, timeout.Token);
                    if (tls.RemoteCertificate is null) return;
                    using var peer = new X509Certificate2(tls.RemoteCertificate);
                    var request = await RelayWire.ReadAsync<RelayPairRequest>(tls, timeout.Token);
                    var accepted = await settings.CompletePairingAsync(request.Id, request.Password,
                        RelayIdentity.FingerprintOf(peer), timeout.Token);
                    await RelayWire.WriteAsync(tls, new RelayPairResult(accepted), timeout.Token);
                }
                catch (Exception error) when (error is IOException or AuthenticationException or InvalidDataException or OperationCanceledException)
                {
                    logger.LogWarning(error, "Relay pairing attempt failed.");
                }
            }
        }
        finally { _pairLimit.Release(); }
    }

    private async Task ConnectorLoopAsync(CancellationToken token)
    {
        var delay = 1;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var current = await settings.ReadAsync(token);
                var enabled = role == "smtp" ? current.ReceiveEnabled
                    : role == "worker" ? current.SendEnabled
                    : current.ReceiveEnabled || current.SendEnabled;
                if (!enabled || current.PeerFingerprint is null || string.IsNullOrWhiteSpace(current.Host))
                {
                    _tunnel = null;
                    _appliedInboundLimits = null;
                    await ReportAsync(token);
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                    continue;
                }
                using var client = new TcpClient();
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await client.ConnectAsync(current.Host, current.Port, connectTimeout.Token);
                await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                    certificate is not null && RelayIdentity.FingerprintOf(new X509Certificate2(certificate)) == current.PeerFingerprint);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = current.Host,
                    ClientCertificates = new X509CertificateCollection { identity.Certificate },
                    EnabledSslProtocols = SslProtocols.Tls13
                }, connectTimeout.Token);
                var inboundLimits = role is "smtp" or "all" && current.ReceiveEnabled
                    ? LimitsOf(current) : null;
                await RelayWire.WriteAsync(tls, new RelayHello(role, current.ReceiveEnabled,
                    current.SendEnabled, inboundLimits), connectTimeout.Token);
                if (!(await RelayWire.ReadAsync<RelayPairResult>(tls, connectTimeout.Token)).Accepted)
                    throw new AuthenticationException("Relay rejected the protocol handshake.");
                await using var tunnel = new RelayTunnel(tls, true);
                tunnel.OnOpen = async (request, stream, innerToken) =>
                {
                    var latest = await settings.ReadAsync(innerToken);
                    if (request.Kind != "inbound" || !latest.ReceiveEnabled ||
                        latest.PeerFingerprint != current.PeerFingerprint || InboundHandler is null ||
                        request.RemoteAddress is null || !IPAddress.TryParse(request.RemoteAddress, out _))
                        return;
                    await InboundHandler(request, stream, innerToken);
                };
                _tunnel = tunnel;
                _appliedInboundLimits = inboundLimits;
                _lastConnected = DateTimeOffset.UtcNow;
                _lastError = null;
                await ReportAsync(token);
                delay = 1;
                using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
                var run = tunnel.RunAsync(active.Token);
                var monitor = MonitorConfigurationAsync(current, tunnel, active.Token);
                await Task.WhenAny(run, monitor);
                active.Cancel();
                await tunnel.DisposeAsync();
                try { await Task.WhenAll(run, monitor); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                _tunnel = null;
                _appliedInboundLimits = null;
                await ReportAsync(token);
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                _tunnel = null;
                _appliedInboundLimits = null;
                _lastError = error.Message;
                await ReportAsync(token);
                logger.LogWarning(error, "Relay tunnel disconnected.");
            }
            await Task.Delay(TimeSpan.FromSeconds(delay), token);
            delay = Math.Min(delay * 2, 30);
        }
    }

    private async Task MonitorConfigurationAsync(RelaySettings original, RelayTunnel tunnel, CancellationToken token)
    {
        var applied = _appliedInboundLimits;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            var current = await settings.ReadAsync(token);
            if (current.PeerFingerprint != original.PeerFingerprint || current.Host != original.Host ||
                current.Port != original.Port || current.ReceiveEnabled != original.ReceiveEnabled ||
                current.SendEnabled != original.SendEnabled)
            {
                await tunnel.DisposeAsync();
                return;
            }
            if (role is "smtp" or "all" && current.ReceiveEnabled && LimitsOf(current) != applied)
            {
                var next = LimitsOf(current);
                await using var stream = await tunnel.OpenAsync(new RelayOpen("configure", Limits: next), token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                if (!(await RelayWire.ReadAsync<RelayPairResult>(stream, timeout.Token)).Accepted)
                    throw new InvalidDataException("Relay rejected inbound capacity settings.");
                applied = next;
                _appliedInboundLimits = next;
            }
            await ReportAsync(token);
        }
    }

    private static RelayInboundLimits LimitsOf(RelaySettings value) => new(
        value.MaxInboundConnections, value.MaxConnectionsPerIp, value.IdleTimeoutSeconds);

    private Task ReportAsync(CancellationToken token) => settings.ReportStatusAsync(
        new RelayProcessStatus(role, Connected, ActiveStreams, LastConnected, LastError,
            DateTimeOffset.UtcNow, _appliedInboundLimits), token);
}

public sealed class RelayNodeService
{
    private static readonly byte[] BusyResponse = "421 4.3.2 Relay busy\r\n"u8.ToArray();
    private static readonly byte[] UnavailableResponse = "421 4.3.2 Mail server temporarily unavailable\r\n"u8.ToArray();
    private readonly RelayIdentity identity;
    private readonly ILogger<RelayNodeService> logger;
    private readonly string _bindingPath;
    private RelayInboundLimits _limits = new();
    private readonly RelayInboundCapacity _inboundCapacity;
    private RelayTunnel? _inboundTunnel;
    private readonly ConcurrentDictionary<RelayTunnel, byte> _activeTunnels = new();
    private readonly SemaphoreSlim _rootLimit = new(8, 8);

    public RelayNodeService(RelayIdentity identity, IConfiguration configuration, ILogger<RelayNodeService> logger)
    {
        this.identity = identity;
        this.logger = logger;
        _bindingPath = Path.Combine(Path.GetFullPath(configuration["SecurityAssets:RootPath"] ?? "security-assets"),
            "relay", "binding.json");
        _inboundCapacity = new RelayInboundCapacity(_limits.MaxInboundConnections, _limits.MaxConnectionsPerIp);
    }

    public async Task PairAsync(string host, int port, string id, string fingerprint, string password, CancellationToken token)
    {
        if (File.Exists(_bindingPath))
        {
            var previous = JsonSerializer.Deserialize<RelayNodeBinding>(await File.ReadAllTextAsync(_bindingPath, token));
            if (previous?.RootFingerprint != fingerprint)
                throw new InvalidOperationException("This relay is paired to a different root. Reset its binding explicitly.");
        }
        using var client = await ConnectPairingAsync(host, port, token);
        await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && RelayIdentity.FingerprintOf(new X509Certificate2(certificate)) == fingerprint);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ClientCertificates = new X509CertificateCollection { identity.Certificate },
            EnabledSslProtocols = SslProtocols.Tls13
        }, token);
        await RelayWire.WriteAsync(tls, new RelayPairRequest(id, password), token);
        var result = await RelayWire.ReadAsync<RelayPairResult>(tls, token);
        if (!result.Accepted) throw new AuthenticationException("Pairing password was rejected or expired.");
        Directory.CreateDirectory(Path.GetDirectoryName(_bindingPath)!);
        var temp = _bindingPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new RelayNodeBinding(fingerprint, host, port)), token);
        SecurityAssetFiles.RestrictFile(temp);
        File.Move(temp, _bindingPath, overwrite: true);
    }

    private static async Task<TcpClient> ConnectPairingAsync(string host, int port, CancellationToken token)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(host, port, token);
                return client;
            }
            catch (SocketException) when (attempt < 9)
            {
                client.Dispose();
                await Task.Delay(500, token);
            }
            catch { client.Dispose(); throw; }
        }
        throw new IOException("Pairing endpoint is unavailable.");
    }

    public async Task RunAsync(int tunnelPort, int smtpPort, CancellationToken token)
    {
        var binding = JsonSerializer.Deserialize<RelayNodeBinding>(await File.ReadAllTextAsync(_bindingPath, token))
            ?? throw new InvalidDataException("Invalid relay binding.");
        using var listener = RelayWire.CreateListener(tunnelPort);
        listener.Start(16);
        using var smtpListener = RelayWire.CreateListener(smtpPort);
        smtpListener.Start(200);
        logger.LogInformation(
            "Relay listening for paired root on {TunnelPort} and SMTP on {SmtpPort}; inbound limits are supplied by the root.",
            tunnelPort, smtpPort);
        var control = AcceptRootsAsync(listener, token);
        var inbound = AcceptMailAsync(smtpListener, token);
        await Task.WhenAll(control, inbound);
    }

    private async Task AcceptRootsAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(token);
            if (!File.Exists(_bindingPath)) { client.Dispose(); continue; }
            var binding = JsonSerializer.Deserialize<RelayNodeBinding>(await File.ReadAllTextAsync(_bindingPath, token));
            if (binding is null) { client.Dispose(); continue; }
            if (!_rootLimit.Wait(0)) { client.Dispose(); continue; }
            _ = HandleRootAsync(client, binding, token);
        }
    }

    private async Task HandleRootAsync(TcpClient client, RelayNodeBinding binding, CancellationToken token)
    {
        try
        {
            using (client)
            {
                try
                {
                    await using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                        certificate is not null && RelayIdentity.FingerprintOf(new X509Certificate2(certificate)) == binding.RootFingerprint);
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = identity.Certificate, ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls13
                    }, handshake.Token);
                    if (tls.RemoteCertificate is null ||
                        RelayIdentity.FingerprintOf(new X509Certificate2(tls.RemoteCertificate)) != binding.RootFingerprint)
                        throw new AuthenticationException("Unknown root identity.");
                    var hello = await RelayWire.ReadAsync<RelayHello>(tls, handshake.Token);
                    if (hello.Version != 2) throw new InvalidDataException("Unsupported relay protocol.");
                    if (hello.Role == "revoke")
                    {
                        File.Delete(_bindingPath);
                        _inboundTunnel = null;
                        foreach (var active in _activeTunnels.Keys) await active.DisposeAsync();
                        await RelayWire.WriteAsync(tls, new RelayPairResult(true), token);
                        return;
                    }
                    if (hello.Role is not ("smtp" or "worker" or "all"))
                        throw new InvalidDataException("Unsupported relay protocol or root role.");
                    if (hello.ReceiveEnabled && hello.Role is "smtp" or "all")
                    {
                        if (hello.InboundLimits is null)
                            throw new InvalidDataException("Missing inbound capacity settings.");
                        ApplyInboundLimits(hello.InboundLimits);
                    }
                    await RelayWire.WriteAsync(tls, new RelayPairResult(true), handshake.Token);
                    await using var tunnel = new RelayTunnel(tls, false);
                    _activeTunnels.TryAdd(tunnel, 0);
                    tunnel.OnOpen = async (request, stream, innerToken) =>
                    {
                        if (request.Kind == "configure" && hello.ReceiveEnabled && hello.Role is "smtp" or "all")
                        {
                            if (request.Limits is null) return;
                            ApplyInboundLimits(request.Limits);
                            await RelayWire.WriteAsync(stream, new RelayPairResult(true), innerToken);
                            return;
                        }
                        if (request.Kind != "outbound" || !hello.SendEnabled || hello.Role == "smtp" ||
                            request.Port != 25 || string.IsNullOrWhiteSpace(request.Host) ||
                            Uri.CheckHostName(request.Host) == UriHostNameType.Unknown)
                            return;
                        using var destination = new TcpClient();
                        await destination.ConnectAsync(request.Host, 25, innerToken);
                        await RelayWire.WriteAsync(stream, new RelayConnectResult(
                            ((IPEndPoint)destination.Client.LocalEndPoint!).Address.ToString(),
                            ((IPEndPoint)destination.Client.RemoteEndPoint!).Address.ToString()), innerToken);
                        await RelayStreamBridge.CopyBothWaysAsync(stream, destination.GetStream(), innerToken);
                    };
                    if (hello.ReceiveEnabled && hello.Role is "smtp" or "all") _inboundTunnel = tunnel;
                    try { await tunnel.RunAsync(token); }
                    finally
                    {
                        _activeTunnels.TryRemove(tunnel, out _);
                        if (ReferenceEquals(_inboundTunnel, tunnel)) _inboundTunnel = null;
                    }
                }
                catch (Exception error) when (error is IOException or AuthenticationException or InvalidDataException
                    or SocketException or OperationCanceledException)
                {
                    logger.LogInformation("Relay root connection ended: {Reason}", error.GetType().Name);
                }
            }
        }
        finally { _rootLimit.Release(); }
    }

    private void ApplyInboundLimits(RelayInboundLimits limits)
    {
        _inboundCapacity.Update(limits);
        Volatile.Write(ref _limits, limits);
        logger.LogInformation("Relay inbound limits applied: total {Maximum}, per IP {PerIp}, idle {IdleSeconds}s.",
            limits.MaxInboundConnections, limits.MaxConnectionsPerIp, limits.IdleTimeoutSeconds);
    }

    private async Task AcceptMailAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(token);
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            if (remote is null)
            {
                client.Dispose();
                continue;
            }
            if (_inboundTunnel is null)
            {
                RejectMail(client, UnavailableResponse);
                continue;
            }
            if (!_inboundCapacity.TryAcquire(remote.Address, out var lease))
            {
                RejectMail(client, BusyResponse);
                continue;
            }
            _ = HandleMailAsync(client, remote, lease!, token);
        }
    }

    private static void RejectMail(TcpClient client, byte[] response)
    {
        using (client)
        {
            try
            {
                client.SendTimeout = 1000;
                client.GetStream().Write(response);
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
        }
    }

    private async Task HandleMailAsync(TcpClient client, IPEndPoint remote, IDisposable lease, CancellationToken token)
    {
        try
        {
            using (lease) using (client)
            {
                var tunnel = _inboundTunnel;
                if (tunnel is null)
                {
                    RejectMail(client, UnavailableResponse);
                    return;
                }
                Stream stream;
                try
                {
                    stream = await tunnel.OpenAsync(new RelayOpen("inbound",
                        RemoteAddress: remote.Address.ToString(), RemotePort: remote.Port), token);
                }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException ||
                    error is OperationCanceledException && !token.IsCancellationRequested)
                {
                    RejectMail(client, UnavailableResponse);
                    return;
                }
                await using (stream)
                {
                    try
                    {
                        await RelayStreamBridge.CopyBothWaysAsync(client.GetStream(), stream, token,
                            TimeSpan.FromSeconds(Volatile.Read(ref _limits).IdleTimeoutSeconds));
                    }
                    catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
                    {
                        // An interrupted SMTP session is retried by the sending MTA.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
