using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MailServer.Domain;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed record ClusterTransportJoinFiles(
    string CaCertificatePem,
    string NodeCertificatePem,
    string NodePrivateKeyPem,
    string PrimaryNodeId,
    string PrimaryCertificatePem);

public sealed class ClusterTransportClient(IOptions<ClusterTransportOptions> transportOptions)
{
    public async Task<(ClusterJoinWireResponse Response, ClusterTransportJoinFiles Files)> JoinAsync(
        string endpoint,
        string token,
        string nodeId,
        string nodeName,
        string advertiseEndpoint,
        string replicationSchemaFingerprint,
        CancellationToken cancellationToken)
    {
        var uri = ParseTlsEndpoint(endpoint);
        using var key = RSA.Create(2048);
        using var temporaryCertificate = new CertificateRequest($"CN={nodeId}-bootstrap", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(30));
        var csr = new CertificateRequest($"CN={nodeId}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSigningRequestPem();
        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(transportOptions.Value.ConnectTimeoutSeconds, 5, 120)));
        await tcp.ConnectAsync(uri.Host, uri.Port, timeout.Token);
        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, errors) => IsBootstrapServerCertificateAccepted(errors));
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = uri.Host,
                ClientCertificates = new X509CertificateCollection { temporaryCertificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken);
        }
        catch (AuthenticationException exception)
        {
            throw new AuthenticationException(
                $"The cluster server certificate for '{uri.Host}' was rejected. " +
                "Verify that --endpoint matches the server certificate DNS/IP SAN; for an existing primary node, run the cluster transport repair action before retrying.",
                exception);
        }
        var requestId = Guid.NewGuid();
        await ClusterBinaryProtocol.WriteAsync(
            ssl,
            ClusterMessageType.JoinRequest,
            requestId,
            new ClusterJoinWireRequest(token, nodeId, nodeName, advertiseEndpoint, csr, replicationSchemaFingerprint),
            cancellationToken);
        var response = await ReadResponseAsync<ClusterJoinWireResponse>(
            ssl, ClusterMessageType.JoinResponse, requestId, cancellationToken);
        using var primaryCertificate = ssl.RemoteCertificate is null
            ? throw new AuthenticationException("The primary did not present a TLS certificate.")
            : new X509Certificate2(ssl.RemoteCertificate);
        var primaryNodeId = primaryCertificate.GetNameInfo(X509NameType.SimpleName, false);
        if (string.IsNullOrWhiteSpace(primaryNodeId))
            throw new AuthenticationException("The primary TLS certificate does not contain a node identity.");
        return (response, new ClusterTransportJoinFiles(
            response.CaCertificatePem,
            response.NodeCertificatePem,
            key.ExportPkcs8PrivateKeyPem(),
            primaryNodeId,
            PemEncoding.WriteString("CERTIFICATE", primaryCertificate.RawData)));
    }

    public async Task<IReadOnlyList<ReplicationLog>> PullReplicationAsync(
        string endpoint,
        ClusterHello hello,
        long afterSequence,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(endpoint, hello, cancellationToken);
        var requestId = Guid.NewGuid();
        await ClusterBinaryProtocol.WriteAsync(connection.Stream, ClusterMessageType.ReplicationPull, requestId,
            new ClusterReplicationPull(hello.NodeId, afterSequence, take), cancellationToken);
        var response = await ClusterBinaryProtocol.ReadAsync<ClusterReplicationBatch>(connection.Stream, cancellationToken);
        EnsureResponse(response.Type, response.RequestId, ClusterMessageType.ReplicationBatch, requestId);
        return response.Payload.Logs;
    }

    public async Task AcknowledgeReplicationAsync(
        string endpoint,
        ClusterHello hello,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(endpoint, hello, cancellationToken);
        var requestId = Guid.NewGuid();
        var ack = new ClusterReplicationAck(hello.NodeId, sequence);
        await ClusterBinaryProtocol.WriteAsync(connection.Stream, ClusterMessageType.ReplicationAck, requestId, ack, cancellationToken);
        var response = await ClusterBinaryProtocol.ReadAsync<ClusterReplicationAck>(connection.Stream, cancellationToken);
        EnsureResponse(response.Type, response.RequestId, ClusterMessageType.ReplicationAck, requestId);
    }

    public async Task SendHeartbeatAsync(
        string endpoint,
        ClusterHello hello,
        ClusterHeartbeatWire heartbeat,
        CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(endpoint, hello, cancellationToken);
        var requestId = Guid.NewGuid();
        await ClusterBinaryProtocol.WriteAsync(connection.Stream, ClusterMessageType.Heartbeat, requestId, heartbeat, cancellationToken);
        var response = await ClusterBinaryProtocol.ReadAsync<ClusterHeartbeatWire>(connection.Stream, cancellationToken);
        EnsureResponse(response.Type, response.RequestId, ClusterMessageType.Heartbeat, requestId);
    }

    public async Task<WitnessLeaseResponse> RequestWitnessLeaseAsync(
        string endpoint,
        ClusterHello hello,
        WitnessLeaseRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(endpoint, hello, cancellationToken);
        var requestId = Guid.NewGuid();
        await ClusterBinaryProtocol.WriteAsync(connection.Stream, ClusterMessageType.WitnessLeaseRequest, requestId, request, cancellationToken);
        var response = await ClusterBinaryProtocol.ReadAsync<WitnessLeaseResponse>(connection.Stream, cancellationToken);
        EnsureResponse(response.Type, response.RequestId, ClusterMessageType.WitnessLeaseResponse, requestId);
        return response.Payload;
    }

    public async Task<Stream> DownloadContentAsync(
        string endpoint,
        ClusterHello hello,
        string contentKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await ConnectAsync(endpoint, hello, cancellationToken);
        var requestId = Guid.NewGuid();
        await ClusterBinaryProtocol.WriteAsync(connection.Stream, ClusterMessageType.ContentRequest, requestId,
            new ClusterContentRequest(contentKey, 0), cancellationToken);
        var memory = new MemoryStream();
        while (true)
        {
            var frame = await ClusterBinaryProtocol.ReadAsync<ClusterContentChunk>(connection.Stream, cancellationToken);
            EnsureResponse(frame.Type, frame.RequestId, frame.Payload.IsLast ? ClusterMessageType.ContentComplete : ClusterMessageType.ContentChunk, requestId);
            if (!frame.Payload.ContentKey.Equals(contentKey, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cluster content response key does not match the request.");
            if (frame.Payload.IsLast) break;
            await memory.WriteAsync(frame.Payload.Bytes, cancellationToken);
        }
        memory.Position = 0;
        return memory;
    }

    private async Task<ClusterTransportConnection> ConnectAsync(string endpoint, ClusterHello hello, CancellationToken cancellationToken)
    {
        var uri = ParseTlsEndpoint(endpoint);
        var certificate = X509Certificate2.CreateFromPemFile(transportOptions.Value.CertificatePath, transportOptions.Value.PrivateKeyPath);
        var tcp = new TcpClient();
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(transportOptions.Value.ConnectTimeoutSeconds, 5, 120)));
        await tcp.ConnectAsync(uri.Host, uri.Port, timeout.Token);
        var ssl = new SslStream(tcp.GetStream(), false, ValidateServerCertificate);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = uri.Host,
            ClientCertificates = new X509CertificateCollection { certificate },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, cancellationToken);
        var requestId = Guid.NewGuid();
        await ClusterBinaryProtocol.WriteAsync(ssl, ClusterMessageType.Hello, requestId, hello, cancellationToken);
        await ReadResponseAsync<ClusterHello>(ssl, ClusterMessageType.HelloAck, requestId, cancellationToken);
        return new ClusterTransportConnection(tcp, ssl, certificate, timeout);
    }

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        if (errors == SslPolicyErrors.None) return true;
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors) return false;
        if (string.IsNullOrWhiteSpace(transportOptions.Value.TrustedCaPath) || !File.Exists(transportOptions.Value.TrustedCaPath))
            return false;
        using var ca = LoadCertificateOnly(transportOptions.Value.TrustedCaPath);
        using var peer = new X509Certificate2(certificate);
        using var customChain = new X509Chain
        {
            ChainPolicy =
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck
            }
        };
        customChain.ChainPolicy.CustomTrustStore.Add(ca);
        customChain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return customChain.Build(peer);
    }

    internal static bool IsBootstrapServerCertificateAccepted(SslPolicyErrors errors) =>
        errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors;

    internal static X509Certificate2 LoadCertificateOnly(string path) =>
        X509Certificate2.CreateFromPem(File.ReadAllText(path));

    private static async Task<T> ReadResponseAsync<T>(
        Stream stream,
        ClusterMessageType expectedType,
        Guid expectedRequestId,
        CancellationToken cancellationToken)
    {
        var response = await ClusterBinaryProtocol.ReadEnvelopeAsync(stream, cancellationToken);
        if (response.Type == ClusterMessageType.Error)
        {
            var error = ClusterBinaryProtocol.DeserializePayload<ClusterError>(response.Payload, cancellationToken);
            throw new InvalidOperationException($"Cluster peer rejected the request ({error.Code}): {error.Message}");
        }
        EnsureResponse(response.Type, response.RequestId, expectedType, expectedRequestId);
        return ClusterBinaryProtocol.DeserializePayload<T>(response.Payload, cancellationToken);
    }

    private static void EnsureResponse(ClusterMessageType actualType, Guid actualRequestId, ClusterMessageType expectedType, Guid expectedRequestId)
    {
        if (actualType == ClusterMessageType.Error)
            throw new InvalidOperationException("Cluster peer returned an error response.");
        if (actualType != expectedType || actualRequestId != expectedRequestId)
            throw new InvalidDataException("Cluster response did not match the request.");
    }

    public static Uri ParseTlsEndpoint(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "tls")
            throw new ArgumentException("Cluster endpoint must be an absolute tls:// URL.");
        if (uri.Port <= 0) throw new ArgumentException("Cluster endpoint must include a port.");
        return uri;
    }

    private sealed class ClusterTransportConnection(TcpClient client, SslStream stream, X509Certificate2 certificate, CancellationTokenSource timeout) : IAsyncDisposable
    {
        public SslStream Stream { get; } = stream;

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            certificate.Dispose();
            timeout.Dispose();
            client.Dispose();
        }
    }
}
