using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MailServer.Domain;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

internal static class ClusterConnectionErrors
{
    public static bool IsExpectedDisconnect(Exception exception) => exception switch
    {
        EndOfStreamException => true,
        SocketException socket when socket.SocketErrorCode is SocketError.ConnectionReset
            or SocketError.ConnectionAborted
            or SocketError.NetworkReset
            or SocketError.Shutdown => true,
        SocketException socket when socket.NativeErrorCode is 32 or 103 or 104 => true,
        IOException io when io.InnerException is not null => IsExpectedDisconnect(io.InnerException),
        _ => false
    };
}

public enum ClusterMessageType : ushort
{
    Hello = 1,
    HelloAck = 2,
    JoinRequest = 3,
    JoinResponse = 4,
    Heartbeat = 5,
    ReplicationPull = 6,
    ReplicationBatch = 7,
    ReplicationAck = 8,
    ContentRequest = 9,
    ContentChunk = 10,
    ContentComplete = 11,
    WitnessLeaseRequest = 12,
    WitnessLeaseResponse = 13,
    PluginManifestRequest = 14,
    PluginManifestResponse = 15,
    Error = 255
}

[MessagePackObject]
public sealed record ClusterFrameEnvelope(
    [property: Key(0)] int ProtocolVersion,
    [property: Key(1)] ClusterMessageType Type,
    [property: Key(2)] Guid RequestId,
    [property: Key(3)] byte[] Payload);

[MessagePackObject]
public sealed record ClusterHello(
    [property: Key(0)] Guid ClusterId,
    [property: Key(1)] string NodeId,
    [property: Key(2)] ClusterNodeRole Role,
    [property: Key(3)] int ProtocolVersion,
    [property: Key(4)] string AppVersion,
    [property: Key(5)] long LastAppliedSequence,
    [property: Key(6)] long SafeSequence,
    [property: Key(7)] string? PluginPackageSetHash,
    [property: Key(8)] string ReplicationSchemaFingerprint);

[MessagePackObject]
public sealed record ClusterJoinWireRequest(
    [property: Key(0)] string Token,
    [property: Key(1)] string NodeId,
    [property: Key(2)] string Name,
    [property: Key(3)] string Endpoint,
    [property: Key(4)] string CertificateSigningRequestPem,
    [property: Key(5)] string ReplicationSchemaFingerprint);

[MessagePackObject]
public sealed record ClusterJoinWireResponse(
    [property: Key(0)] Guid ClusterId,
    [property: Key(1)] string ClusterName,
    [property: Key(2)] ClusterNodeRole Role,
    [property: Key(3)] string NodeId,
    [property: Key(4)] string PrimaryEndpoint,
    [property: Key(5)] string? WitnessEndpoint,
    [property: Key(6)] long BaseSequence,
    [property: Key(7)] string CaCertificatePem,
    [property: Key(8)] string NodeCertificatePem);

[MessagePackObject]
public sealed record ClusterReplicationPull([property: Key(0)] string NodeId, [property: Key(1)] long AfterSequence, [property: Key(2)] int Take);

[MessagePackObject]
public sealed record ClusterReplicationBatch([property: Key(0)] IReadOnlyList<ReplicationLog> Logs);

[MessagePackObject]
public sealed record ClusterReplicationAck([property: Key(0)] string NodeId, [property: Key(1)] long LastAppliedSequence);

[MessagePackObject]
public sealed record ClusterContentRequest([property: Key(0)] string ContentKey, [property: Key(1)] int FromChunk);

[MessagePackObject]
public sealed record ClusterContentChunk([property: Key(0)] string ContentKey, [property: Key(1)] int ChunkIndex, [property: Key(2)] byte[] Bytes, [property: Key(3)] bool IsLast);

[MessagePackObject]
public sealed record ClusterHeartbeatWire(
    [property: Key(0)] string NodeId,
    [property: Key(1)] ClusterNodeStatus Status,
    [property: Key(2)] long AppliedSequence,
    [property: Key(3)] long SafeSequence,
    [property: Key(4)] int InitializationProgress,
    [property: Key(5)] string? LastError,
    [property: Key(6)] string Version);

[MessagePackObject]
public sealed record ClusterError([property: Key(0)] string Code, [property: Key(1)] string Message);

public static class ClusterBinaryProtocol
{
    public const int ProtocolVersion = 2;
    private const uint Magic = 0x4D53434C; // MSCL
    private const int HeaderLength = 28;
    private const int MaxPayloadLength = 16 * 1024 * 1024;
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);

    public static async Task WriteAsync<T>(
        Stream stream,
        ClusterMessageType type,
        Guid requestId,
        T payload,
        CancellationToken cancellationToken)
    {
        var payloadBytes = MessagePackSerializer.Serialize(payload, Options, cancellationToken);
        var envelope = new ClusterFrameEnvelope(ProtocolVersion, type, requestId, payloadBytes);
        var body = MessagePackSerializer.Serialize(envelope, Options, cancellationToken);
        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), Magic);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), ProtocolVersion);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)type);
        requestId.TryWriteBytes(header.AsSpan(8, 16));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(24, 4), body.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32Value(body));
        await stream.WriteAsync(crc, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<(ClusterMessageType Type, Guid RequestId, T Payload)> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var (type, requestId, payload) = await ReadEnvelopeAsync(stream, cancellationToken);
        return (type, requestId, MessagePackSerializer.Deserialize<T>(payload, Options, cancellationToken));
    }

    public static async Task<(ClusterMessageType Type, Guid RequestId, byte[] Payload)> ReadEnvelopeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, HeaderLength, cancellationToken);
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)) != Magic)
            throw new InvalidDataException("Invalid cluster frame magic.");
        var version = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        if (version != ProtocolVersion) throw new InvalidDataException($"Unsupported cluster protocol version {version}.");
        var type = (ClusterMessageType)BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2));
        var requestId = new Guid(header.AsSpan(8, 16));
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24, 4));
        if (length < 0 || length > MaxPayloadLength) throw new InvalidDataException("Cluster frame payload is too large.");
        var body = await ReadExactAsync(stream, length, cancellationToken);
        var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian((await ReadExactAsync(stream, 4, cancellationToken)).AsSpan());
        var actualCrc = Crc32Value(body);
        if (expectedCrc != actualCrc) throw new InvalidDataException("Cluster frame CRC mismatch.");
        var envelope = MessagePackSerializer.Deserialize<ClusterFrameEnvelope>(body, Options, cancellationToken);
        if (envelope.ProtocolVersion != ProtocolVersion || envelope.Type != type || envelope.RequestId != requestId)
            throw new InvalidDataException("Cluster frame envelope does not match its header.");
        return (type, requestId, envelope.Payload);
    }

    public static T DeserializePayload<T>(byte[] payload, CancellationToken cancellationToken) =>
        MessagePackSerializer.Deserialize<T>(payload, Options, cancellationToken);

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return buffer;
    }

    private static uint Crc32Value(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xffffffff;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        return ~crc;
    }
}

public sealed class ClusterCertificateService(
    IDbContextFactory<MailDbContext> factory,
    IDataProtectionProvider dataProtection,
    IClock clock,
    IOptionsMonitor<ClusterOptions>? clusterOptions = null)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("MailServer.Cluster.Certificates.v1");

    public async Task<ClusterCertificateAuthority> EnsureAuthorityAsync(string actor, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ClusterCertificateAuthorities.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing;
        using var key = RSA.Create(4096);
        var request = new CertificateRequest("CN=MailServer Cluster CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var notBefore = clock.UtcNow.AddMinutes(-5);
        var notAfter = clock.UtcNow.AddYears(10);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        var ca = new ClusterCertificateAuthority
        {
            Thumbprint = certificate.Thumbprint ?? string.Empty,
            CertificatePem = PemEncoding.WriteString("CERTIFICATE", certificate.RawData),
            ProtectedPrivateKeyPem = _protector.Protect(key.ExportPkcs8PrivateKeyPem()),
            NotBefore = notBefore,
            NotAfter = notAfter,
            CreatedBy = actor
        };
        db.ClusterCertificateAuthorities.Add(ca);
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "cluster.ca.create", Resource = $"cluster-ca:{ca.Thumbprint}", Category = AuditCategory.Security, Status = AuditStatus.Succeeded });
        await db.SaveChangesAsync(cancellationToken);
        return ca;
    }

    public async Task<ClusterNodeCertificate> SignNodeCertificateAsync(
        string nodeId,
        string nodeName,
        string endpoint,
        string csrPem,
        string actor,
        CancellationToken cancellationToken)
    {
        var ca = await EnsureAuthorityAsync(actor, cancellationToken);
        using var caCertificate = X509Certificate2.CreateFromPem(ca.CertificatePem, _protector.Unprotect(ca.ProtectedPrivateKeyPem));
        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256);
        // Validate the CSR signature, but never trust its subject or extensions.  The
        // registered node identity and endpoint are the certificate authority's input.
        var signingRequest = new CertificateRequest(
            new X500DistinguishedName($"CN={EscapeDistinguishedNameValue(nodeId)}"),
            csr.PublicKey,
            HashAlgorithmName.SHA256);
        ApplyNodeCertificateExtensions(signingRequest, endpoint);
        var notBefore = clock.UtcNow.AddMinutes(-5);
        var notAfter = clock.UtcNow.AddYears(1);
        var serial = RandomNumberGenerator.GetBytes(16);
        using var certificate = signingRequest.Create(
            caCertificate.SubjectName,
            X509SignatureGenerator.CreateForRSA(caCertificate.GetRSAPrivateKey() ?? throw new InvalidOperationException("Cluster CA certificate does not contain an RSA private key."), RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            serial);
        var pem = PemEncoding.WriteString("CERTIFICATE", certificate.RawData);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var nodeCertificate = new ClusterNodeCertificate
        {
            NodeId = nodeId,
            Thumbprint = certificate.Thumbprint ?? Convert.ToHexString(SHA256.HashData(certificate.RawData)),
            CertificatePem = pem,
            NotBefore = notBefore,
            NotAfter = notAfter
        };
        db.ClusterNodeCertificates.Add(nodeCertificate);
        var node = await db.ClusterNodes.SingleOrDefaultAsync(x => x.Id == nodeId, cancellationToken);
        if (node is not null) node.CertificateThumbprint = nodeCertificate.Thumbprint;
        db.AuditRecords.Add(new AuditRecord { Actor = actor, Action = "cluster.certificate.issue", Resource = $"cluster-node:{nodeId}", Detail = nodeName, Category = AuditCategory.Security, Status = AuditStatus.Succeeded });
        await db.SaveChangesAsync(cancellationToken);
        return nodeCertificate;
    }

    private static void ApplyNodeCertificateExtensions(CertificateRequest request, string endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("tls", StringComparison.OrdinalIgnoreCase)
            || uri.Port <= 0)
            throw new ArgumentException("Cluster endpoint must be an absolute tls:// URL.", nameof(endpoint));

        // The signer owns the certificate profile. In particular, never copy a SAN or
        // CA capability supplied by an invitation holder in the CSR.
        request.CertificateExtensions.Clear();
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"), // TLS server authentication
                new("1.3.6.1.5.5.7.3.2")  // TLS client authentication
            },
            true));

        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(uri.Host, out var address))
            subjectAlternativeName.AddIpAddress(address);
        else
            subjectAlternativeName.AddDnsName(uri.IdnHost);
        request.CertificateExtensions.Add(subjectAlternativeName.Build(critical: false));
    }

    private static string EscapeDistinguishedNameValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("+", "\\+", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    public async Task<bool> ValidatePeerAsync(X509Certificate2? certificate, string expectedNodeId, CancellationToken cancellationToken)
    {
        if (certificate is null) return false;
        var thumbprint = certificate.Thumbprint ?? string.Empty;
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.ClusterCertificateRevocations.AnyAsync(x => x.Thumbprint == thumbprint, cancellationToken)) return false;
        if (await db.ClusterNodeCertificates.AnyAsync(x =>
            x.NodeId == expectedNodeId
            && x.Thumbprint == thumbprint
            && (x.Status == ClusterCertificateStatus.Active || x.Status == ClusterCertificateStatus.Grace)
            && x.NotBefore <= clock.UtcNow
            && x.NotAfter >= clock.UtcNow
            && x.RevokedAt == null, cancellationToken)) return true;

        // A witness has no CA private key and may not yet know a newly rotated primary
        // thumbprint.  It may trust only the configured primary identity under the
        // locally stored cluster CA; all other roles retain exact-thumbprint trust.
        if (clusterOptions?.CurrentValue.Role != ClusterNodeRole.Witness) return false;
        var configuration = await db.ClusterConfigurations.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (configuration is null
            || !string.Equals(configuration.PrimaryNodeId, expectedNodeId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(configuration.CaCertificatePem)
            || !string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), expectedNodeId, StringComparison.Ordinal)
            || certificate.NotBefore.ToUniversalTime() > clock.UtcNow.UtcDateTime
            || certificate.NotAfter.ToUniversalTime() < clock.UtcNow.UtcDateTime)
            return false;
        using var ca = X509Certificate2.CreateFromPem(configuration.CaCertificatePem);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = clock.UtcNow.UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        return chain.Build(certificate);
    }
}

public sealed class ClusterPluginPackageService(IOptions<PluginOptions> options)
{
    public async Task<(string SetHash, IReadOnlyList<ClusterPluginPackage> Packages, IReadOnlyList<ClusterPluginPackageFile> Files)> ScanAsync(CancellationToken cancellationToken)
    {
        var root = PluginServiceCollectionExtensions.ResolvePluginPath(options.Value.Path);
        if (!Directory.Exists(root)) return (HashBytes([]), [], []);
        var packageRows = new List<ClusterPluginPackage>();
        var fileRows = new List<ClusterPluginPackageFile>();
        foreach (var manifestPath in Directory.EnumerateFiles(root, "plugin.json", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pluginDirectory = Path.GetDirectoryName(manifestPath)!;
            var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            using var manifestDocument = JsonDocument.Parse(manifestBytes);
            var pluginId = manifestDocument.RootElement.TryGetProperty("id", out var idValue) ? idValue.GetString() ?? Path.GetFileName(pluginDirectory) : Path.GetFileName(pluginDirectory);
            var version = manifestDocument.RootElement.TryGetProperty("version", out var versionValue) ? versionValue.GetString() ?? "unknown" : "unknown";
            var packageHashInput = new List<byte>();
            foreach (var file in Directory.EnumerateFiles(pluginDirectory, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var relative = Path.GetRelativePath(pluginDirectory, file).Replace('\\', '/');
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                var contentHash = HashBytes(bytes);
                packageHashInput.AddRange(Convert.FromHexString(contentHash));
                fileRows.Add(new ClusterPluginPackageFile { PluginId = pluginId, RelativePath = relative, ContentHash = contentHash, Length = bytes.Length });
            }
            packageRows.Add(new ClusterPluginPackage
            {
                PluginId = pluginId,
                Version = version,
                ManifestHash = HashBytes(manifestBytes),
                PackageHash = HashBytes(packageHashInput.ToArray())
            });
        }
        var setHash = HashBytes(packageRows.OrderBy(x => x.PluginId).SelectMany(x => Convert.FromHexString(x.PackageHash)).ToArray());
        return (setHash, packageRows, fileRows);
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class ClusterTransportService(
    IOptionsMonitor<ClusterTransportOptions> transportOptions,
    IOptionsMonitor<ClusterOptions> clusterOptions,
    IDbContextFactory<MailDbContext> factory,
    ClusterManagementService clusterManagement,
    ClusterCertificateService certificates,
    ReplicationSchemaFingerprintProvider schemaFingerprint,
    IMessageContentStore contentStore,
    WitnessLeaseService witness,
    IClock clock,
    ClusterTransportRuntimeState runtime,
    ILogger<ClusterTransportService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var optionSubscription = transportOptions.OnChange(updated =>
        {
            if (updated.Enabled) runtime.RequestStart();
            else runtime.RequestStop();
        });
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = transportOptions.CurrentValue;
            if (!options.Enabled || runtime.StopRequested)
            {
                runtime.IsListening = false;
                runtime.Status = "disabled";
                await runtime.WaitForStartRequestAsync(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            TcpListener? listener = null;
            try
            {
                var address = IPAddress.Parse(options.ListenAddress);
                listener = new TcpListener(address, Math.Clamp(options.Port, 1, 65535));
                listener.Start();
                runtime.IsListening = true;
                runtime.Status = "listening";
                runtime.LastError = null;
                runtime.StartedAt = clock.UtcNow;
                logger.LogInformation("Cluster transport listening on {Address}:{Port}.", address, options.Port);
                using var listenCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, runtime.GetStopToken());
                while (!listenCancellation.IsCancellationRequested && transportOptions.CurrentValue.Enabled)
                {
                    var client = await listener.AcceptTcpClientAsync(listenCancellation.Token);
                    _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (runtime.StopRequested)
            {
                runtime.Status = "disabled";
                runtime.LastError = null;
            }
            catch (Exception exception)
            {
                runtime.IsListening = false;
                runtime.Status = "failed";
                runtime.LastError = exception.Message;
                logger.LogError(exception, "Cluster transport listener failed.");
                await runtime.WaitForStartRequestAsync(TimeSpan.FromSeconds(10), stoppingToken);
            }
            finally
            {
                listener?.Stop();
                runtime.IsListening = false;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        var authenticated = false;
        string? peerNodeId = null;
        try
        {
            var options = transportOptions.CurrentValue;
            using var certificate = X509Certificate2.CreateFromPemFile(options.CertificatePath, options.PrivateKeyPath);
            await using var ssl = new SslStream(stream, false, (_, _, _, _) => true);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, cancellationToken);
            var first = await ClusterBinaryProtocol.ReadEnvelopeAsync(ssl, cancellationToken);
            if (first.Type == ClusterMessageType.JoinRequest)
            {
                var join = ClusterBinaryProtocol.DeserializePayload<ClusterJoinWireRequest>(first.Payload, cancellationToken);
                var localSchemaFingerprint = await schemaFingerprint.GetAsync(cancellationToken);
                if (!string.Equals(join.ReplicationSchemaFingerprint, localSchemaFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteSchemaMismatchAsync(ssl, first.RequestId, cancellationToken);
                    return;
                }
                var joined = await clusterManagement.JoinWithCertificateAsync(join, cancellationToken);
                var response = new ClusterJoinWireResponse(
                    joined.Result.ClusterId,
                    joined.Result.ClusterName,
                    joined.Result.Role,
                    joined.Result.NodeId,
                    joined.Result.PrimaryEndpoint,
                    joined.Result.WitnessEndpoint,
                    joined.Result.BaseSequence,
                    joined.CaCertificatePem,
                    joined.NodeCertificatePem);
                await ClusterBinaryProtocol.WriteAsync(ssl, ClusterMessageType.JoinResponse, first.RequestId, response, cancellationToken);
                return;
            }

            if (first.Type != ClusterMessageType.Hello) throw new InvalidDataException("Cluster peer must start with Hello.");
            var hello = ClusterBinaryProtocol.DeserializePayload<ClusterHello>(first.Payload, cancellationToken);
            if (!await certificates.ValidatePeerAsync(ssl.RemoteCertificate is null ? null : new X509Certificate2(ssl.RemoteCertificate), hello.NodeId, cancellationToken))
                throw new AuthenticationException("Cluster peer certificate is not trusted.");
            authenticated = true;
            peerNodeId = hello.NodeId;
            await using (var identityDb = await factory.CreateDbContextAsync(cancellationToken))
            {
                var localClusterId = await identityDb.ClusterConfigurations.AsNoTracking()
                    .Select(x => (Guid?)x.Id)
                    .SingleOrDefaultAsync(cancellationToken);
                if (localClusterId is null || localClusterId.Value != hello.ClusterId)
                {
                    await ClusterBinaryProtocol.WriteAsync(
                        ssl,
                        ClusterMessageType.Error,
                        first.RequestId,
                        new ClusterError("cluster_mismatch", "The peer belongs to a different or incomplete local cluster identity."),
                        cancellationToken);
                    return;
                }
            }
            var expectedSchemaFingerprint = await schemaFingerprint.GetAsync(cancellationToken);
            if (!string.Equals(hello.ReplicationSchemaFingerprint, expectedSchemaFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Cluster peer {NodeId} was rejected because its replication schema fingerprint does not match.", hello.NodeId);
                await WriteSchemaMismatchAsync(ssl, first.RequestId, cancellationToken);
                return;
            }
            await UpdateHeartbeatAsync(hello, cancellationToken);
            await ClusterBinaryProtocol.WriteAsync(ssl, ClusterMessageType.HelloAck, first.RequestId, await LocalHelloAsync(hello.ClusterId, cancellationToken), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await ClusterBinaryProtocol.ReadEnvelopeAsync(ssl, cancellationToken);
                await DispatchAsync(ssl, frame.Type, frame.RequestId, frame.Payload, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException && ClusterConnectionErrors.IsExpectedDisconnect(exception))
        {
            if (authenticated)
                logger.LogInformation("Authenticated cluster peer {NodeId} disconnected.", peerNodeId);
            else
                logger.LogDebug(exception, "Cluster transport peer disconnected before authentication.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Cluster transport connection failed.");
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task DispatchAsync(SslStream ssl, ClusterMessageType type, Guid requestId, byte[] payload, CancellationToken cancellationToken)
    {
        switch (type)
        {
            case ClusterMessageType.ReplicationPull:
                var pull = ClusterBinaryProtocol.DeserializePayload<ClusterReplicationPull>(payload, cancellationToken);
                await using (var db = await factory.CreateDbContextAsync(cancellationToken))
                {
                    var logs = await db.ReplicationLogs.AsNoTracking()
                        .Where(x => x.Sequence > pull.AfterSequence)
                        .OrderBy(x => x.Sequence)
                        .Take(Math.Clamp(pull.Take, 1, 1000))
                        .ToListAsync(cancellationToken);
                    await ClusterBinaryProtocol.WriteAsync(ssl, ClusterMessageType.ReplicationBatch, requestId, new ClusterReplicationBatch(logs), cancellationToken);
                }
                break;
            case ClusterMessageType.WitnessLeaseRequest:
                var lease = ClusterBinaryProtocol.DeserializePayload<WitnessLeaseRequest>(payload, cancellationToken);
                await ClusterBinaryProtocol.WriteAsync(ssl, ClusterMessageType.WitnessLeaseResponse, requestId, await witness.AcquireAsync(lease, cancellationToken), cancellationToken);
                break;
            case ClusterMessageType.ReplicationAck:
                var ack = ClusterBinaryProtocol.DeserializePayload<ClusterReplicationAck>(payload, cancellationToken);
                await AcknowledgeReplicationAsync(ack, cancellationToken);
                await ClusterBinaryProtocol.WriteAsync(ssl, ClusterMessageType.ReplicationAck, requestId, ack, cancellationToken);
                break;
            case ClusterMessageType.Heartbeat:
                var heartbeat = ClusterBinaryProtocol.DeserializePayload<ClusterHeartbeatWire>(payload, cancellationToken);
                await clusterManagement.HeartbeatAsync(new ClusterHeartbeatRequest(
                    heartbeat.NodeId,
                    heartbeat.Status,
                    heartbeat.AppliedSequence,
                    heartbeat.SafeSequence,
                    heartbeat.InitializationProgress,
                    heartbeat.LastError,
                    heartbeat.Version), cancellationToken);
                await ClusterBinaryProtocol.WriteAsync(ssl, ClusterMessageType.Heartbeat, requestId, heartbeat, cancellationToken);
                break;
            case ClusterMessageType.ContentRequest:
                var content = ClusterBinaryProtocol.DeserializePayload<ClusterContentRequest>(payload, cancellationToken);
                await SendContentAsync(ssl, requestId, content, cancellationToken);
                break;
            default:
                await ClusterBinaryProtocol.WriteAsync(ssl, ClusterMessageType.Error, requestId, new ClusterError("unsupported", $"Unsupported message type {type}."), cancellationToken);
                break;
        }
    }

    private async Task AcknowledgeReplicationAsync(ClusterReplicationAck ack, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var cursor = await db.ReplicationCursors.SingleOrDefaultAsync(x => x.NodeId == ack.NodeId, cancellationToken);
        if (cursor is null)
        {
            cursor = new ReplicationCursor { NodeId = ack.NodeId };
            db.ReplicationCursors.Add(cursor);
        }
        cursor.LastAppliedSequence = Math.Max(cursor.LastAppliedSequence, ack.LastAppliedSequence);
        cursor.UpdatedAt = clock.UtcNow;
        cursor.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SendContentAsync(SslStream ssl, Guid requestId, ClusterContentRequest request, CancellationToken cancellationToken)
    {
        await using var message = await contentStore.OpenReadAsync(request.ContentKey, cancellationToken);
        var buffer = new byte[Math.Clamp(transportOptions.CurrentValue.ContentChunkBytes, 4096, 1024 * 1024)];
        var chunk = 0;
        while (true)
        {
            var read = await message.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (chunk >= request.FromChunk)
                await ClusterBinaryProtocol.WriteAsync(ssl, ClusterMessageType.ContentChunk, requestId,
                    new ClusterContentChunk(request.ContentKey, chunk, buffer[..read], false), cancellationToken);
            chunk++;
        }
        await ClusterBinaryProtocol.WriteAsync(ssl, ClusterMessageType.ContentComplete, requestId,
            new ClusterContentChunk(request.ContentKey, chunk, [], true), cancellationToken);
    }

    private async Task UpdateHeartbeatAsync(ClusterHello hello, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var node = await db.ClusterNodes.SingleOrDefaultAsync(x => x.Id == hello.NodeId, cancellationToken);
        if (node is null) return;
        node.Status = ClusterNodeStatus.Healthy;
        node.ProtocolVersion = hello.ProtocolVersion;
        node.Version = hello.AppVersion;
        node.LastHeartbeatAt = clock.UtcNow;
        node.AppliedSequence = Math.Max(node.AppliedSequence, hello.LastAppliedSequence);
        node.SafeSequence = Math.Max(node.SafeSequence, hello.SafeSequence);
        node.PluginPackageSetHash = hello.PluginPackageSetHash;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ClusterHello> LocalHelloAsync(Guid clusterId, CancellationToken cancellationToken) => new(
        clusterId,
        clusterOptions.CurrentValue.NodeId,
        clusterOptions.CurrentValue.Role,
        ClusterBinaryProtocol.ProtocolVersion,
        typeof(ClusterTransportService).Assembly.GetName().Version?.ToString() ?? "unknown",
        0,
        0,
        null,
        await schemaFingerprint.GetAsync(cancellationToken));

    private static async Task WriteSchemaMismatchAsync(Stream stream, Guid requestId, CancellationToken cancellationToken) =>
        await ClusterBinaryProtocol.WriteAsync(
            stream,
            ClusterMessageType.Error,
            requestId,
            new ClusterError(
                "schema_mismatch",
                "The database/replication schema is incompatible. Install the same MailServer version and run 'mailserver db update' on every node."),
            cancellationToken);
}
