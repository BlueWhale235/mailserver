using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.PluginSdk;
using Microsoft.Extensions.Options;

namespace MailServer.Smtp;

public sealed class SmtpServerOptions
{
    public const string SectionName = "Smtp";
    public string HostName { get; set; } = "mail.localhost";
    public int CommandTimeoutSeconds { get; set; } = 300;
    public int MaximumConnections { get; set; } = 200;
    public int MaximumRecipients { get; set; } = 100;
    public long MaximumMessageBytes { get; set; } = 52_428_800;
    public List<SmtpEndpointOptions> Endpoints { get; set; } = [];
}

public sealed class SmtpEndpointOptions
{
    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; }
    public bool ImplicitTls { get; set; }
    public bool RequireAuthentication { get; set; }
}

public sealed class SmtpServerService(
    IServiceScopeFactory scopeFactory,
    IOptions<SmtpServerOptions> options,
    IOptions<CertificatesOptions> certificates,
    ILogger<SmtpServerService> logger) : BackgroundService
{
    private readonly SmtpServerOptions _options = options.Value;
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _acceptLoops = [];
    private SemaphoreSlim _connectionLimit = null!;
    private readonly CertificatesOptions _certificates = certificates.Value;
    private LoadedPemCertificate? _certificate;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _connectionLimit = new SemaphoreSlim(_options.MaximumConnections);
        _certificate = LoadCertificate();
        foreach (var endpoint in _options.Endpoints)
        {
            if (endpoint.ImplicitTls && _certificate is null)
            {
                logger.LogWarning("SMTP endpoint {Port} is disabled because no TLS certificate is configured.", endpoint.Port);
                continue;
            }

            if (!IPAddress.TryParse(endpoint.Address, out var bindAddress))
            {
                throw new InvalidOperationException($"Invalid SMTP bind address: {endpoint.Address}");
            }

            var listener = new TcpListener(bindAddress, endpoint.Port);
            listener.Start();
            _listeners.Add(listener);
            _acceptLoops.Add(AcceptLoopAsync(listener, endpoint, stoppingToken));
            logger.LogInformation(
                "SMTP listening on {Port}; implicit TLS={ImplicitTls}, authentication required={RequireAuthentication}",
                endpoint.Port,
                endpoint.ImplicitTls,
                endpoint.RequireAuthentication);
        }

        return Task.WhenAll(_acceptLoops);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        await base.StopAsync(cancellationToken);
        _certificate?.Dispose();
    }

    private async Task AcceptLoopAsync(
        TcpListener listener,
        SmtpEndpointOptions endpoint,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await _connectionLimit.WaitAsync(cancellationToken);
            _ = HandleClientAsync(client, endpoint, cancellationToken).ContinueWith(
                _ => _connectionLimit.Release(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        SmtpEndpointOptions endpoint,
        CancellationToken serverCancellationToken)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        using (client)
        {
            try
            {
                client.NoDelay = true;
                Stream stream = client.GetStream();
                var isTls = false;
                if (endpoint.ImplicitTls)
                {
                    stream = await UpgradeTlsAsync(stream, serverCancellationToken);
                    isTls = true;
                }

                await using var connection = new SmtpConnection(
                    stream,
                    TimeSpan.FromSeconds(_options.CommandTimeoutSeconds),
                    _options.MaximumMessageBytes);
                await connection.ReplyAsync($"220 {_options.HostName} ESMTP MailServer ready", serverCancellationToken);
                var state = new SmtpSessionState();

                while (!serverCancellationToken.IsCancellationRequested)
                {
                    var line = await connection.ReadLineAsync(serverCancellationToken);
                    if (line is null) break;
                    if (line.Length == 0)
                    {
                        await connection.ReplyAsync("500 5.5.2 Empty command", serverCancellationToken);
                        continue;
                    }

                    var separator = line.IndexOf(' ');
                    var command = (separator < 0 ? line : line[..separator]).ToUpperInvariant();
                    var arguments = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
                    switch (command)
                    {
                        case "EHLO":
                            state.ResetTransaction();
                            state.HeloName = ValidateHelo(arguments);
                            state.Extended = true;
                            await ReplyCapabilitiesAsync(connection, isTls, serverCancellationToken);
                            break;
                        case "HELO":
                            state.ResetTransaction();
                            state.HeloName = ValidateHelo(arguments);
                            state.Extended = false;
                            await connection.ReplyAsync($"250 {_options.HostName}", serverCancellationToken);
                            break;
                        case "NOOP":
                            await connection.ReplyAsync("250 2.0.0 OK", serverCancellationToken);
                            break;
                        case "RSET":
                            state.ResetTransaction();
                            await connection.ReplyAsync("250 2.0.0 Reset", serverCancellationToken);
                            break;
                        case "QUIT":
                            await connection.ReplyAsync("221 2.0.0 Bye", serverCancellationToken);
                            return;
                        case "STARTTLS":
                            if (isTls)
                            {
                                await connection.ReplyAsync("503 5.5.1 TLS is already active", serverCancellationToken);
                                break;
                            }

                            if (_certificate is null)
                            {
                                await connection.ReplyAsync("454 4.7.0 TLS not available", serverCancellationToken);
                                break;
                            }

                            await connection.ReplyAsync("220 2.0.0 Ready to start TLS", serverCancellationToken);
                            stream = await UpgradeTlsAsync(connection.Detach(), serverCancellationToken);
                            connection.Attach(stream);
                            isTls = true;
                            state = new SmtpSessionState();
                            break;
                        case "AUTH":
                            if (!isTls)
                            {
                                await connection.ReplyAsync("538 5.7.11 Encryption required for requested authentication mechanism", serverCancellationToken);
                                break;
                            }

                            if (state.HeloName is null)
                            {
                                await connection.ReplyAsync("503 5.5.1 Send EHLO first", serverCancellationToken);
                                break;
                            }

                            await AuthenticateAsync(connection, state, arguments, serverCancellationToken);
                            break;
                        case "MAIL":
                            if (state.HeloName is null)
                            {
                                await connection.ReplyAsync("503 5.5.1 Send HELO/EHLO first", serverCancellationToken);
                                break;
                            }

                            if (endpoint.RequireAuthentication && state.AuthenticatedUser is null)
                            {
                                await connection.ReplyAsync("530 5.7.0 Authentication required", serverCancellationToken);
                                break;
                            }

                            if (!TryParsePath(arguments, "FROM:", allowEmpty: true, out var sender))
                            {
                                await connection.ReplyAsync("501 5.5.4 Invalid MAIL FROM", serverCancellationToken);
                                break;
                            }

                            state.ResetTransaction();
                            state.Sender = sender;
                            await connection.ReplyAsync("250 2.1.0 Sender OK", serverCancellationToken);
                            break;
                        case "RCPT":
                            if (state.Sender is null)
                            {
                                await connection.ReplyAsync("503 5.5.1 Need MAIL FROM first", serverCancellationToken);
                                break;
                            }

                            if (state.Recipients.Count >= _options.MaximumRecipients)
                            {
                                await connection.ReplyAsync("452 4.5.3 Too many recipients", serverCancellationToken);
                                break;
                            }

                            if (!TryParsePath(arguments, "TO:", allowEmpty: false, out var recipient))
                            {
                                await connection.ReplyAsync("501 5.5.4 Invalid RCPT TO", serverCancellationToken);
                                break;
                            }

                            using (var scope = scopeFactory.CreateScope())
                            {
                                var inbound = scope.ServiceProvider.GetRequiredService<IInboundMessageService>();
                                if (state.AuthenticatedUser is null
                                    && !await inbound.IsLocalRecipientAsync(recipient, serverCancellationToken))
                                {
                                    await connection.ReplyAsync("550 5.7.1 Relaying denied", serverCancellationToken);
                                    break;
                                }
                            }

                            state.Recipients.Add(recipient);
                            await connection.ReplyAsync("250 2.1.5 Recipient OK", serverCancellationToken);
                            break;
                        case "DATA":
                            if (state.Sender is null || state.Recipients.Count == 0)
                            {
                                await connection.ReplyAsync("503 5.5.1 Need MAIL FROM and RCPT TO first", serverCancellationToken);
                                break;
                            }

                            await connection.ReplyAsync("354 End data with <CR><LF>.<CR><LF>", serverCancellationToken);
                            Stream data;
                            try
                            {
                                data = await connection.ReadDataAsync(serverCancellationToken);
                            }
                            catch (InvalidDataException)
                            {
                                state.ResetTransaction();
                                await connection.ReplyAsync("552 5.3.4 Message size exceeds fixed maximum", serverCancellationToken);
                                break;
                            }

                            await using (data)
                            using (var scope = scopeFactory.CreateScope())
                            {
                                var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
                                if (pipeline is not null)
                                {
                                    var decision = await pipeline.OnSmtpSessionAsync(
                                        new SmtpSessionContext(remote, state.HeloName, state.AuthenticatedUser, isTls),
                                        serverCancellationToken);
                                    if (decision.Kind != PluginDecisionKind.Continue)
                                    {
                                        await connection.ReplyAsync(
                                            decision.Kind == PluginDecisionKind.Defer
                                                ? $"451 4.7.1 {decision.Message ?? "Deferred by policy"}"
                                                : $"550 5.7.1 {decision.Message ?? "Rejected by policy"}",
                                            serverCancellationToken);
                                        state.ResetTransaction();
                                        break;
                                    }

                                    var messageDecision = await pipeline.OnMessageAsync(
                                        new MessageAcceptanceContext(
                                            state.Sender,
                                            state.Recipients,
                                            "pending",
                                            data.Length),
                                        serverCancellationToken);
                                    if (messageDecision.Kind != PluginDecisionKind.Continue)
                                    {
                                        await connection.ReplyAsync(
                                            messageDecision.Kind == PluginDecisionKind.Defer
                                                ? $"451 4.7.1 {messageDecision.Message ?? "Deferred by policy"}"
                                                : $"550 5.7.1 {messageDecision.Message ?? "Rejected by policy"}",
                                            serverCancellationToken);
                                        state.ResetTransaction();
                                        break;
                                    }
                                }

                                var inbound = scope.ServiceProvider.GetRequiredService<IInboundMessageService>();
                                var accepted = await inbound.AcceptAsync(
                                    new InboundEnvelope(
                                        state.Sender,
                                        state.Recipients,
                                        state.AuthenticatedUser,
                                        remote,
                                        state.HeloName ?? "unknown"),
                                    data,
                                    serverCancellationToken);
                                await connection.ReplyAsync($"250 2.0.0 Queued as {accepted.MessageId:N}", serverCancellationToken);
                            }

                            state.ResetTransaction();
                            break;
                        default:
                            await connection.ReplyAsync("502 5.5.1 Command not implemented", serverCancellationToken);
                            break;
                    }
                }
            }
            catch (TimeoutException)
            {
                logger.LogInformation("SMTP client {Remote} timed out.", remote);
            }
            catch (InvalidDataException exception)
            {
                logger.LogInformation(exception, "SMTP client {Remote} sent invalid or oversized data.", remote);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "SMTP session {Remote} failed.", remote);
            }
        }
    }

    private async Task AuthenticateAsync(
        SmtpConnection connection,
        SmtpSessionState state,
        string arguments,
        CancellationToken cancellationToken)
    {
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            await connection.ReplyAsync("504 5.5.4 Only AUTH PLAIN is supported", cancellationToken);
            return;
        }

        string encoded;
        if (parts.Length == 1)
        {
            await connection.ReplyAsync("334 ", cancellationToken);
            encoded = await connection.ReadLineAsync(cancellationToken) ?? string.Empty;
        }
        else
        {
            encoded = parts[1];
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var fields = decoded.Split('\0');
            if (fields.Length != 3)
            {
                throw new FormatException();
            }

            using var scope = scopeFactory.CreateScope();
            var authenticator = scope.ServiceProvider.GetRequiredService<IAccountAuthenticator>();
            var accountId = await authenticator.AuthenticateMailboxAsync(fields[1], fields[2], cancellationToken);
            if (accountId is null)
            {
                await connection.ReplyAsync("535 5.7.8 Authentication credentials invalid", cancellationToken);
                return;
            }

            state.AuthenticatedUser = fields[1].ToLowerInvariant();
            await connection.ReplyAsync("235 2.7.0 Authentication successful", cancellationToken);
        }
        catch (FormatException)
        {
            await connection.ReplyAsync("501 5.5.2 Invalid AUTH payload", cancellationToken);
        }
    }

    private async Task ReplyCapabilitiesAsync(
        SmtpConnection connection,
        bool isTls,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            $"250-{_options.HostName}",
            "250-PIPELINING",
            $"250-SIZE {_options.MaximumMessageBytes}",
            "250-8BITMIME",
            "250-SMTPUTF8",
            "250-DSN",
            "250-ENHANCEDSTATUSCODES"
        };
        if (!isTls && _certificate is not null)
        {
            lines.Add("250-STARTTLS");
        }

        if (isTls)
        {
            lines.Add("250-AUTH PLAIN");
        }

        lines.Add("250 HELP");
        foreach (var line in lines)
        {
            await connection.ReplyAsync(line, cancellationToken);
        }
    }

    private async Task<Stream> UpgradeTlsAsync(Stream stream, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificateContext = _certificate?.SslContext,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false
            },
            cancellationToken);
        return ssl;
    }

    private LoadedPemCertificate? LoadCertificate() =>
        PemCertificateLoader.LoadOptional(_certificates.Mail, "Mail");

    private static string ValidateHelo(string value)
    {
        var result = value.Trim();
        if (result.Length is 0 or > 255 || result.Any(char.IsControl))
        {
            throw new InvalidDataException("Invalid HELO name.");
        }

        return result;
    }

    private static bool TryParsePath(string arguments, string prefix, bool allowEmpty, out string path)
    {
        path = string.Empty;
        if (!arguments.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = arguments[prefix.Length..].TrimStart();
        var end = remainder.IndexOf('>');
        if (!remainder.StartsWith('<') || end < 1)
        {
            return false;
        }

        path = remainder[1..end].Trim().ToLowerInvariant();
        return allowEmpty && path.Length == 0 || AddressParserForSmtp.IsValid(path);
    }
}

internal sealed class SmtpSessionState
{
    public string? HeloName { get; set; }
    public bool Extended { get; set; }
    public string? AuthenticatedUser { get; set; }
    public string? Sender { get; set; }
    public List<string> Recipients { get; } = [];

    public void ResetTransaction()
    {
        Sender = null;
        Recipients.Clear();
    }
}

internal static class AddressParserForSmtp
{
    public static bool IsValid(string value)
    {
        var at = value.LastIndexOf('@');
        return at > 0 && at < value.Length - 1 && value.Length <= 320 && !value.Any(char.IsWhiteSpace);
    }
}

internal sealed class SmtpConnection(Stream stream, TimeSpan timeout, long maximumMessageBytes) : IAsyncDisposable
{
    private Stream _stream = stream;
    private readonly byte[] _singleByte = new byte[1];

    public void Attach(Stream stream) => _stream = stream;
    public Stream Detach() => _stream;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(128);
        while (bytes.Count <= 8192)
        {
            var read = await _stream.ReadAsync(_singleByte, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            }

            if (_singleByte[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            bytes.Add(_singleByte[0]);
        }

        throw new InvalidDataException("SMTP line exceeds 8192 bytes.");
    }

    public async Task<Stream> ReadDataAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mailserver-{Guid.NewGuid():N}.eml");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                long size = 0;
                while (true)
                {
                    var line = await ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException();
                    if (line == ".") break;
                    if (line.StartsWith("..", StringComparison.Ordinal)) line = line[1..];
                    var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
                    size += bytes.Length;
                    if (size > maximumMessageBytes)
                    {
                        throw new InvalidDataException($"Message exceeds the {maximumMessageBytes} byte limit.");
                    }
                    await output.WriteAsync(bytes, cancellationToken);
                }
            }

            return new DeleteOnDisposeFileStream(path);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public async Task ReplyAsync(string response, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(response + "\r\n");
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

internal sealed class DeleteOnDisposeFileStream(string path)
    : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
