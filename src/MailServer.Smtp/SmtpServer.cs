using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Collections.Concurrent;
using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.PluginSdk;
using MailServer.PluginSdk.Security;
using MailServer.PluginSdk.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailServer.Smtp;

/// <summary>
/// SMTP 服务的运行配置。这里的限制值直接影响协议层拒绝响应，修改时要同步考虑客户端兼容性。
/// </summary>
public sealed class SmtpServerOptions
{
    public const string SectionName = "Smtp";
    public const int MaximumHeaderLineBytes = 998;
    public const int MaximumHeaderBytes = 256 * 1024;
    public const int MaximumReceivedHeaders = 100;
    public string HostName { get; set; } = "mail.localhost";
    public int CommandTimeoutSeconds { get; set; } = 300;
    public int MaximumConnections { get; set; } = 200;
    public int MaximumActiveMessages { get; set; } = 4;
    public int ActiveMessageWaitMilliseconds { get; set; } = 2000;
    public int MaximumWaitingMessages { get; set; } = 128;
    public int MaximumRecipients { get; set; } = 100;
    public long MaximumMessageBytes { get; set; } = 52_428_800;
    public List<SmtpEndpointOptions> Endpoints { get; set; } = [];
}

/// <summary>
/// 单个 SMTP 监听端点配置。Security 是新配置项，ImplicitTls 保留给旧配置文件平滑升级。
/// </summary>
public sealed class SmtpEndpointOptions
{
    private ConnectionSecurity? _security;
    private bool? _legacyImplicitTls;
    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; }
    public ConnectionSecurity Security
    {
        get => _security
               ?? (_legacyImplicitTls == true
                   ? ConnectionSecurity.ImplicitTls
                   : ConnectionSecurity.StartTls);
        set => _security = value;
    }
    public bool ImplicitTls
    {
        get => Security == ConnectionSecurity.ImplicitTls;
        set => _legacyImplicitTls = value;
    }
    public bool RequireAuthentication { get; set; }
    public string[] AuthenticationMechanisms { get; set; } = ["PLAIN", "LOGIN", "XOAUTH2", "OAUTHBEARER"];
    internal ConnectionSecurity EffectiveSecurity => Security;
}

/// <summary>
/// SMTP 认证失败限流配置，用于在协议层阻断连续爆破。
/// </summary>
public sealed class SmtpAbuseOptions
{
    public const string SectionName = "SmtpAbuse";
    public int AuthenticationWindowMinutes { get; set; } = 15;
    public int MaximumAuthenticationFailures { get; set; } = 5;
    public int AuthenticationBlockMinutes { get; set; } = 30;
}

/// <summary>
/// SMTP 协议宿主。它只负责会话、认证、信封和 DATA 接收，真正的落库与投递交给 Infrastructure 服务。
/// </summary>
public sealed class SmtpServerService(
    IServiceScopeFactory scopeFactory,
    IOptions<SmtpServerOptions> options,
    DomainCertificateProvider certificateProvider,
    ILogger<SmtpServerService> logger,
    IpFirewallService? firewall = null,
    IOptions<SmtpAbuseOptions>? abuseOptions = null,
    SystemSettingsService? systemSettings = null,
    ResourceRuntime? resources = null,
    ConnectionProtection? protection = null,
    RelayRootService? relay = null,
    IOptions<StorageOptions>? storageOptions = null) : BackgroundService
{
    private readonly SmtpServerOptions _options = options.Value;
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _acceptLoops = [];
    private AdjustableConcurrencyLimit _connectionLimit = null!;
    private readonly AdjustableConcurrencyLimit _messageLimit = resources?.SmtpMessages
        ?? new AdjustableConcurrencyLimit(options.Value.MaximumActiveMessages);
    private readonly SmtpAuthFailureLimiter _authFailures = new(abuseOptions?.Value ?? new SmtpAbuseOptions());
    private volatile bool _stopping;

    private async Task<IDisposable?> AcquireMessagePermitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var permit = _messageLimit.TryAcquire();
        if (permit is not null) return permit;
        resources?.Telemetry.RecordInboundDemand();
        if (_options.ActiveMessageWaitMilliseconds <= 0) return null;
        try
        {
            return await _messageLimit.AcquireAsync(cancellationToken,
                TimeSpan.FromMilliseconds(Math.Clamp(_options.ActiveMessageWaitMilliseconds, 1, 30_000)),
                Math.Clamp(_options.MaximumWaitingMessages, 1, 128));
        }
        catch (IOException)
        {
            return null;
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _connectionLimit = resources?.Smtp ?? new AdjustableConcurrencyLimit(_options.MaximumConnections);
        if (relay is not null) relay.InboundHandler = HandleTunneledAsync;
        foreach (var endpoint in _options.Endpoints)
        {
            if (!IPAddress.TryParse(endpoint.Address, out var bindAddress))
            {
                throw new InvalidOperationException($"Invalid SMTP bind address: {endpoint.Address}");
            }

            var listener = new TcpListener(bindAddress, endpoint.Port);
            listener.Start();
            _listeners.Add(listener);
            _acceptLoops.Add(AcceptLoopAsync(listener, endpoint, stoppingToken));
            logger.LogInformation(
                "SMTP listening on {Port}; security={Security}, authentication required={RequireAuthentication}",
                endpoint.Port,
                endpoint.EffectiveSecurity,
                endpoint.RequireAuthentication);
        }

        resources?.SetServiceState("smtp", _listeners.Count > 0 ? "listening" : "stopped");

        return Task.WhenAll(_acceptLoops);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        resources?.SetServiceState("smtp", "stopped");
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        await base.StopAsync(cancellationToken);
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
            catch (SocketException) when (_stopping || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            IDisposable? sourcePermit = null;
            if (protection is not null && peer is not null)
            {
                var group = endpoint.RequireAuthentication
                    ? ProtectedConnectionGroup.SmtpSubmission : ProtectedConnectionGroup.SmtpInbound;
                var banned = endpoint.RequireAuthentication
                    && protection.IsAuthBlocked(ProtectedLoginProtocol.Smtp, peer, DateTimeOffset.UtcNow);
                if (banned || !protection.TryAcquire(group, peer, DateTimeOffset.UtcNow, out sourcePermit))
                {
                    await RejectBusyAsync(client, endpoint, cancellationToken, banned);
                    continue;
                }
            }
            // 连接级防火墙在欢迎语之前执行，避免被拒绝来源确认服务器能力。
            try
            {
                if (firewall is not null
                    && client.Client.RemoteEndPoint is IPEndPoint remoteEndPoint
                    && await firewall.IsBlockedAsync(remoteEndPoint.Address, cancellationToken))
                {
                    logger.LogWarning(
                        "SMTP connection from {RemoteAddress} was closed by firewall before banner.",
                        remoteEndPoint.Address);
                    client.Dispose();
                    sourcePermit?.Dispose();
                    continue;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "SMTP firewall evaluation failed; closing only the current connection.");
                client.Dispose();
                sourcePermit?.Dispose();
                continue;
            }

            // 这里用信号量做进程级连接上限；每个会话独立运行，结束时释放名额。
            var permit = _connectionLimit.TryAcquire();
            if (permit is null)
            {
                sourcePermit?.Dispose();
                await RejectBusyAsync(client, endpoint, cancellationToken);
                continue;
            }
            _ = HandleLimitedAsync(client, endpoint, permit, sourcePermit, cancellationToken);
        }
    }

    private static async Task RejectBusyAsync(TcpClient client, SmtpEndpointOptions endpoint, CancellationToken token, bool banned = false)
    {
        using (client)
        {
            if (endpoint.EffectiveSecurity == ConnectionSecurity.ImplicitTls) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var reply = banned ? "421 4.7.0 Too many authentication failures\r\n"
                    : "421 4.3.2 Server busy; retry later.\r\n";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(reply), timeout.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private async Task HandleLimitedAsync(TcpClient client, SmtpEndpointOptions endpoint, IDisposable permit, IDisposable? sourcePermit, CancellationToken token)
    {
        using (permit) using (sourcePermit) await HandleClientAsync(client, endpoint, token);
    }

    private async Task HandleTunneledAsync(RelayOpen request, Stream stream, CancellationToken token)
    {
        if (request.Kind != "inbound" || request.RemoteAddress is null ||
            !IPAddress.TryParse(request.RemoteAddress, out var peer)) return;
        if (firewall is not null && await firewall.IsBlockedAsync(peer, token)) return;
        IDisposable? sourcePermit = null;
        if (protection is not null && !protection.TryAcquire(
                ProtectedConnectionGroup.SmtpInbound, peer, DateTimeOffset.UtcNow, out sourcePermit)) return;
        var permit = _connectionLimit.TryAcquire();
        if (permit is null)
        {
            sourcePermit?.Dispose();
            await stream.WriteAsync("421 4.3.2 Server busy; retry later\r\n"u8.ToArray(), token);
            return;
        }
        var endpoint = _options.Endpoints.FirstOrDefault(x => x.Port == 25 && !x.RequireAuthentication)
            ?? new SmtpEndpointOptions { Port = 25, Security = ConnectionSecurity.StartTls };
        using (permit) using (sourcePermit)
            await HandleStreamClientAsync(null, endpoint, token, stream, $"{peer}:{request.RemotePort}");
    }

    private async Task HandleClientAsync(
        TcpClient client,
        SmtpEndpointOptions endpoint,
        CancellationToken serverCancellationToken) =>
        await HandleStreamClientAsync(client, endpoint, serverCancellationToken);

    private async Task HandleStreamClientAsync(
        TcpClient? client,
        SmtpEndpointOptions endpoint,
        CancellationToken serverCancellationToken,
        Stream? tunneledStream = null,
        string? tunneledRemote = null)
    {
        var remote = tunneledRemote ?? client?.Client.RemoteEndPoint?.ToString() ?? "unknown";
        var endpointName = $"{endpoint.Address}:{endpoint.Port}";
        using (client)
        {
            using var preAuthentication = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
            if (endpoint.RequireAuthentication)
                preAuthentication.CancelAfter(TimeSpan.FromSeconds(protection?.Settings.PreAuthenticationSeconds ?? 120));
            try
            {
                if (client is not null) client.NoDelay = true;
                logger.LogInformation(
                    "SMTP connection accepted from {Remote} on {Endpoint}.",
                    remote,
                    endpointName);
                Stream stream = tunneledStream ?? client!.GetStream();
                var isTls = false;
                if (endpoint.EffectiveSecurity == ConnectionSecurity.ImplicitTls)
                {
                    stream = await UpgradeTlsBoundedAsync(stream, preAuthentication.Token);
                    isTls = true;
                }

                var smtpSettings = systemSettings is null
                    ? new SmtpSettingsView(_options.CommandTimeoutSeconds)
                    : await systemSettings.GetSmtpSettingsAsync(serverCancellationToken, useCache: true);
                await using var connection = new SmtpConnection(
                    stream,
                    TimeSpan.FromSeconds(smtpSettings.CommandTimeoutSeconds),
                    _options.MaximumMessageBytes,
                    storageOptions?.Value.PreparedInboundEnabled == true
                        ? Path.GetFullPath(storageOptions.Value.InboundTemporaryPath
                            ?? Path.Combine(storageOptions.Value.RootPath, ".incoming")) : null);
                using (var scope = scopeFactory.CreateScope())
                {
                    var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
                    if (pipeline is not null)
                    {
                        var decision = await pipeline.OnConnectionAsync(
                            new ConnectionContext(MailProtocol.Smtp, remote, isTls),
                            serverCancellationToken);
                        if (decision.Kind != PluginDecisionKind.Continue)
                        {
                            // 插件可以在 banner 前拒绝连接，适合灰名单、来源信誉和维护窗口。
                            await connection.ReplyAsync(
                                decision.Kind == PluginDecisionKind.Defer
                                    ? $"421 4.7.0 {decision.Message ?? "Connection deferred by policy"}"
                                    : $"554 5.7.1 {decision.Message ?? "Connection rejected by policy"}",
                                serverCancellationToken);
                            return;
                        }
                        if (isTls)
                            await pipeline.OnTlsEstablishedAsync(
                                new TlsEstablishedContext(MailProtocol.Smtp, remote),
                                serverCancellationToken);
                    }
                }
                await connection.ReplyAsync($"220 {_options.HostName} ESMTP MailServer ready", serverCancellationToken);
                var state = new SmtpSessionState();

                while (!serverCancellationToken.IsCancellationRequested)
                {
                    var readToken = endpoint.RequireAuthentication && state.AuthenticatedAccountId is null
                        ? preAuthentication.Token : serverCancellationToken;
                    var line = await connection.ReadLineAsync(readToken);
                    if (line is null) break;
                    if (line.Length == 0)
                    {
                        await connection.ReplyAsync("500 5.5.2 Empty command", serverCancellationToken);
                        continue;
                    }

                    var separator = line.IndexOf(' ');
                    var command = (separator < 0 ? line : line[..separator]).ToUpperInvariant();
                    var arguments = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
                    using var operation = PerformanceTelemetry.Operation("smtp", command is "EHLO" or "HELO" or "MAIL" or "RCPT" or "DATA" or "AUTH" or "STARTTLS" or "QUIT" or "RSET" or "NOOP" ? command : "UNKNOWN");
                    try
                    {
                    switch (command)
                    {
                        case "EHLO":
                            state.ResetTransaction();
                            if (!TryValidateHelo(arguments, out var ehloName))
                            {
                                logger.LogWarning("SMTP EHLO from {Remote} used an invalid name.", remote);
                                await connection.ReplyAsync("501 5.5.4 Invalid EHLO name", serverCancellationToken);
                                break;
                            }
                            state.HeloName = ehloName;
                            state.Extended = true;
                            logger.LogInformation(
                                "SMTP EHLO from {Remote}: {HeloName}.",
                                remote,
                                state.HeloName);
                            await ReplyCapabilitiesAsync(connection, endpoint, isTls, serverCancellationToken);
                            break;
                        case "HELO":
                            state.ResetTransaction();
                            if (!TryValidateHelo(arguments, out var heloName))
                            {
                                logger.LogWarning("SMTP HELO from {Remote} used an invalid name.", remote);
                                await connection.ReplyAsync("501 5.5.4 Invalid HELO name", serverCancellationToken);
                                break;
                            }
                            state.HeloName = heloName;
                            state.Extended = false;
                            logger.LogInformation(
                                "SMTP HELO from {Remote}: {HeloName}.",
                                remote,
                                state.HeloName);
                            await connection.ReplyAsync($"250 {_options.HostName}", serverCancellationToken);
                            break;
                        case "NOOP":
                            await connection.ReplyAsync("250 2.0.0 OK", serverCancellationToken);
                            break;
                        case "VRFY":
                            await connection.ReplyAsync("252 2.5.2 Cannot VRFY user", serverCancellationToken);
                            break;
                        case "EXPN":
                            await connection.ReplyAsync("502 5.5.1 Command not implemented", serverCancellationToken);
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

                            if (endpoint.EffectiveSecurity != ConnectionSecurity.StartTls)
                            {
                                logger.LogInformation(
                                    "SMTP STARTTLS from {Remote} was rejected because TLS is unavailable on {Endpoint}.",
                                    remote,
                                    endpointName);
                                await connection.ReplyAsync("454 4.7.0 TLS not available", serverCancellationToken);
                                break;
                            }

                            if (certificateProvider is not null
                                && !await certificateProvider.HasAnyCertificateAsync(serverCancellationToken))
                            {
                                logger.LogInformation(
                                    "SMTP STARTTLS from {Remote} was rejected because no mail TLS certificate is configured.",
                                    remote);
                                await connection.ReplyAsync("454 4.7.0 TLS not available", serverCancellationToken);
                                break;
                            }

                            await connection.ReplyAsync("220 2.0.0 Ready to start TLS", serverCancellationToken);
                            stream = await UpgradeTlsBoundedAsync(connection.Detach(), preAuthentication.Token);
                            connection.Attach(stream);
                            isTls = true;
                            // RFC 要求 STARTTLS 后丢弃之前的 EHLO/MAIL 状态，客户端必须重新 EHLO。
                            state = new SmtpSessionState();
                            using (var scope = scopeFactory.CreateScope())
                            {
                                var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
                                if (pipeline is not null)
                                    await pipeline.OnTlsEstablishedAsync(
                                        new TlsEstablishedContext(MailProtocol.Smtp, remote),
                                        serverCancellationToken);
                            }
                            logger.LogInformation("SMTP STARTTLS completed for {Remote}.", remote);
                            break;
                        case "AUTH":
                            if (!endpoint.RequireAuthentication)
                            {
                                logger.LogInformation(
                                    "SMTP AUTH from {Remote} was rejected because authentication is disabled on this endpoint.",
                                    remote);
                                await connection.ReplyAsync("502 5.5.1 Authentication is not available on this endpoint", serverCancellationToken);
                                break;
                            }

                            if (!isTls)
                            {
                                logger.LogInformation(
                                    "SMTP AUTH from {Remote} was rejected because TLS is required.",
                                    remote);
                                await connection.ReplyAsync("538 5.7.11 Encryption required for requested authentication mechanism", serverCancellationToken);
                                break;
                            }

                            if (state.HeloName is null || !state.Extended)
                            {
                                logger.LogInformation(
                                    "SMTP AUTH from {Remote} was rejected because EHLO was not sent.",
                                    remote);
                                await connection.ReplyAsync("503 5.5.1 Send EHLO first", serverCancellationToken);
                                break;
                            }

                            if (state.AuthenticatedAccountId is not null)
                            {
                                await connection.ReplyAsync("503 5.5.1 Already authenticated", serverCancellationToken);
                                break;
                            }
                            if (state.Sender is not null || state.Recipients.Count != 0)
                            {
                                await connection.ReplyAsync("503 5.5.1 AUTH not permitted during a mail transaction", serverCancellationToken);
                                break;
                            }
                            var authPeer = IpFirewallRules.TryParseRemoteAddress(remote);
                            if ((protection is not null && authPeer is not null
                                    && protection.IsAuthBlocked(ProtectedLoginProtocol.Smtp, authPeer, DateTimeOffset.UtcNow))
                                || (protection is null && _authFailures.IsBlocked(remote, DateTimeOffset.UtcNow)))
                            {
                                // 连续认证失败直接关闭会话，减少密码爆破时的资源占用。
                                await connection.ReplyAsync("421 4.7.0 Too many authentication failures", serverCancellationToken);
                                return;
                            }

                            state.CredentialRejected = false;
                            var blockedAfterAttempt = false;
                            bool authenticated;
                            try
                            {
                                authenticated = await AuthenticateAsync(
                                    connection, state, arguments, remote, endpoint, isTls, preAuthentication.Token);
                            }
                            finally
                            {
                                if (state.CredentialRejected)
                                    blockedAfterAttempt = protection is not null && authPeer is not null
                                        ? protection.RecordAuthFailure(ProtectedLoginProtocol.Smtp, authPeer, DateTimeOffset.UtcNow)
                                        : _authFailures.RecordFailure(remote, DateTimeOffset.UtcNow);
                            }
                            if (!authenticated && blockedAfterAttempt)
                            {
                                await connection.ReplyAsync("421 4.7.0 Too many authentication failures", serverCancellationToken);
                                return;
                            }
                            if (authenticated && protection is null) _authFailures.Clear(remote);
                            break;
                        case "MAIL":
                            await HandleMailFromAsync(
                                connection,
                                state,
                                arguments,
                                remote,
                                endpoint,
                                serverCancellationToken);
                            break;
                        case "RCPT":
                            if (state.Sender is null)
                            {
                                logger.LogInformation(
                                    "SMTP RCPT TO from {Remote} was rejected because MAIL FROM was not sent.",
                                    remote);
                                await connection.ReplyAsync("503 5.5.1 Need MAIL FROM first", serverCancellationToken);
                                break;
                            }

                            if (!TryParsePathAndParameters(arguments, "TO:", allowEmpty: false, out var recipient, out var rcptParameters))
                            {
                                logger.LogInformation(
                                    "SMTP RCPT TO from {Remote} was rejected because the path was invalid.",
                                    remote);
                                await connection.ReplyAsync("501 5.5.4 Invalid RCPT TO", serverCancellationToken);
                                break;
                            }
                            if (rcptParameters.Length != 0)
                            {
                                await connection.ReplyAsync("555 5.5.4 Unsupported RCPT TO parameter", serverCancellationToken);
                                break;
                            }
                            using (var scope = scopeFactory.CreateScope())
                            {
                                // RCPT 阶段先给插件机会拦截，再做本地收件人/中继判断，便于实现黑名单或灰名单。
                                var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
                                if (pipeline is not null)
                                {
                                    var decision = await pipeline.OnSmtpRecipientAsync(
                                        new SmtpRecipientContext(
                                            state.Sender,
                                            recipient,
                                            state.Recipients,
                                            remote,
                                            state.AuthenticatedUser,
                                            state.AuthenticatedAccountId),
                                        serverCancellationToken);
                                    if (decision.Kind != PluginDecisionKind.Continue)
                                    {
                                        await connection.ReplyAsync(
                                            decision.Kind == PluginDecisionKind.Defer
                                                ? $"451 4.7.1 {decision.Message ?? "Recipient deferred by policy"}"
                                                : $"550 5.7.1 {decision.Message ?? "Recipient rejected by policy"}",
                                            serverCancellationToken);
                                        break;
                                    }
                                }
                            }
                            using (var scope = scopeFactory.CreateScope())
                            {
                                var inbound = scope.ServiceProvider.GetRequiredService<IInboundMessageService>();
                                // 未认证客户端只能投递到本地域名，防止服务器变成开放中继。
                                if (state.AuthenticatedUser is null
                                    && !await inbound.IsLocalRecipientAsync(recipient, serverCancellationToken))
                                {
                                    logger.LogInformation(
                                        "SMTP RCPT TO from {Remote} rejected recipient={Recipient}, sender={Sender}: unauthenticated relay denied or local recipient not found.",
                                        remote,
                                        recipient,
                                        state.Sender);
                                    await connection.ReplyAsync("550 5.7.1 Relaying denied", serverCancellationToken);
                                    break;
                                }
                            }

                            var recipientResult = state.TryAddRecipient(recipient, _options.MaximumRecipients);
                            if (recipientResult == RecipientAddResult.Duplicate)
                            {
                                await connection.ReplyAsync("250 2.1.5 Recipient OK", serverCancellationToken);
                                break;
                            }
                            if (recipientResult == RecipientAddResult.LimitExceeded)
                            {
                                logger.LogInformation(
                                    "SMTP RCPT TO from {Remote} was rejected because recipient limit {Limit} was reached.",
                                    remote,
                                    _options.MaximumRecipients);
                                await connection.ReplyAsync("452 4.5.3 Too many recipients", serverCancellationToken);
                                break;
                            }

                            logger.LogInformation(
                                "SMTP RCPT TO accepted from {Remote}: recipient={Recipient}, sender={Sender}, recipientCount={RecipientCount}.",
                                remote,
                                recipient,
                                state.Sender,
                                state.Recipients.Count);
                            await connection.ReplyAsync("250 2.1.5 Recipient OK", serverCancellationToken);
                            break;
                        case "DATA":
                        {
                            if (state.Sender is null || state.Recipients.Count == 0)
                            {
                                logger.LogInformation(
                                    "SMTP DATA from {Remote} was rejected because MAIL FROM or RCPT TO was missing.",
                                    remote);
                                await connection.ReplyAsync("503 5.5.1 Need MAIL FROM and RCPT TO first", serverCancellationToken);
                                break;
                            }

                            using var messagePermit = await AcquireMessagePermitAsync(serverCancellationToken);
                            if (messagePermit is null)
                            {
                                logger.LogInformation("SMTP DATA from {Remote} deferred: bounded active message wait exhausted.", remote);
                                state.ResetTransaction();
                                await connection.ReplyAsync("451 4.3.2 Server busy; retry later", serverCancellationToken);
                                break;
                            }

                            logger.LogInformation(
                                "SMTP DATA started from {Remote}: sender={Sender}, recipientCount={RecipientCount}.",
                                remote,
                                state.Sender,
                                state.Recipients.Count);
                            await connection.ReplyAsync("354 End data with <CR><LF>.<CR><LF>", serverCancellationToken);
                            Stream data;
                            try
                            {
                                // ReadDataAsync 会做点号透明处理、大小限制和头部安全检查。
                                data = await connection.ReadDataAsync(serverCancellationToken);
                            }
                            catch (InvalidDataException exception)
                            {
                                state.ResetTransaction();
                                logger.LogInformation(exception, "SMTP DATA from {Remote} was rejected.", remote);
                                await connection.ReplyAsync(
                                    exception.Message.Contains("header", StringComparison.OrdinalIgnoreCase)
                                    || exception.Message.Contains("line ending", StringComparison.OrdinalIgnoreCase)
                                        ? "554 5.6.0 Invalid or oversized message headers"
                                        : "552 5.3.4 Message size exceeds fixed maximum",
                                    serverCancellationToken);
                                break;
                            }

                            await using (data)
                            using (var scope = scopeFactory.CreateScope())
                            {
                                using var ruleEvaluation = MailRuleService.BeginEvaluationScope();
                                if (!await ValidateAuthenticatedMessageHeadersAsync(
                                        connection,
                                        data,
                                        state,
                                        remote,
                                        scope.ServiceProvider,
                                        serverCancellationToken))
                                {
                                    state.ResetTransaction();
                                    break;
                                }

                                var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
                                if (pipeline is not null)
                                {
                                    // 内容插件拿到的是完整 DATA 流，适合 MIME/附件检查；拒绝后不会进入落库事务。
                                    var contentDecision = await pipeline.OnMessageContentAsync(
                                        state.Sender,
                                        state.Recipients,
                                        state.BodyType,
                                        data,
                                        serverCancellationToken);
                                    if (contentDecision.Kind != PluginDecisionKind.Continue)
                                    {
                                        await connection.ReplyAsync(
                                            contentDecision.Kind == PluginDecisionKind.Defer
                                                ? $"451 4.6.0 {contentDecision.Message ?? "Message validation deferred"}"
                                                : $"554 5.6.0 {contentDecision.Message ?? "Message content rejected"}",
                                            serverCancellationToken);
                                        state.ResetTransaction();
                                        break;
                                    }

                                    var decision = await pipeline.OnSmtpSessionAsync(
                                        new SmtpSessionContext(remote, state.HeloName, state.AuthenticatedUser, isTls),
                                        serverCancellationToken);
                                    if (decision.Kind != PluginDecisionKind.Continue)
                                    {
                                        logger.LogInformation(
                                            "SMTP session from {Remote} was rejected by plugin policy: decision={Decision}, message={Message}.",
                                            remote,
                                            decision.Kind,
                                            decision.Message ?? string.Empty);
                                        await connection.ReplyAsync(
                                            decision.Kind == PluginDecisionKind.Defer
                                                ? $"451 4.7.1 {decision.Message ?? "Deferred by policy"}"
                                                : $"550 5.7.1 {decision.Message ?? "Rejected by policy"}",
                                            serverCancellationToken);
                                        state.ResetTransaction();
                                        break;
                                    }

                                    var messageDecision = await pipeline.OnMessageWithSourceAsync(
                                        new MessageAcceptanceContext(
                                            state.Sender,
                                            state.Recipients,
                                            "pending",
                                            data.Length,
                                            remote),
                                        serverCancellationToken);
                                    if (messageDecision.Decision.Kind != PluginDecisionKind.Continue)
                                    {
                                        if (messageDecision.Decision.Kind == PluginDecisionKind.Quarantine)
                                        {
                                            // Quarantine 对客户端返回成功，邮件转入人工审核，不再进入常规收件/外发链路。
                                            data.Position = 0;
                                            var quarantine = scope.ServiceProvider.GetRequiredService<IQuarantineService>();
                                            await quarantine.CreateAsync(
                                                new QuarantineCreateRequest(
                                                    state.Sender,
                                                    state.Recipients,
                                                    remote,
                                                    data,
                                                    messageDecision.PluginId,
                                                    messageDecision.PluginName,
                                                    messageDecision.Decision.Message),
                                                serverCancellationToken);
                                            await connection.ReplyAsync(
                                                $"250 2.6.0 {messageDecision.Decision.Message ?? "Message quarantined for review"}",
                                                serverCancellationToken);
                                            state.ResetTransaction();
                                            break;
                                        }

                                        logger.LogInformation(
                                            "SMTP message from {Remote} sender={Sender} was rejected by plugin policy: decision={Decision}, message={Message}.",
                                            remote,
                                            state.Sender,
                                            messageDecision.Decision.Kind,
                                            messageDecision.Decision.Message ?? string.Empty);
                                        await connection.ReplyAsync(
                                            messageDecision.Decision.Kind == PluginDecisionKind.Defer
                                                ? $"451 4.7.1 {messageDecision.Decision.Message ?? "Deferred by policy"}"
                                                : $"550 5.7.1 {messageDecision.Decision.Message ?? "Rejected by policy"}",
                                            serverCancellationToken);
                                        state.ResetTransaction();
                                        break;
                                    }
                                }

                                var inbound = scope.ServiceProvider.GetRequiredService<IInboundMessageService>();
                                InboundAcceptance accepted;
                                try
                                {
                                    accepted = await inbound.AcceptAsync(
                                        new InboundEnvelope(
                                            state.Sender,
                                            state.Recipients,
                                            state.AuthenticatedUser,
                                            remote,
                                            state.HeloName ?? "unknown",
                                            state.AuthenticatedAccountId),
                                        data,
                                        serverCancellationToken);
                                }
                                catch (Exception exception) when (exception is MailboxQuotaExceededException or DomainQuotaExceededException)
                                {
                                    var sender = state.Sender;
                                    var authenticatedAccountId = state.AuthenticatedAccountId;
                                    var recipients = state.Recipients.ToArray();
                                    var quotaRecipient = exception is MailboxQuotaExceededException mailboxQuota
                                        ? mailboxQuota.Address : ((DomainQuotaExceededException)exception).Address;
                                    var quotaType = exception is MailboxQuotaExceededException ? "mailbox" : "domain";
                                    var response = exception is MailboxQuotaExceededException
                                        ? "552 5.2.2 Mailbox full; please contact the recipient manually"
                                        : "552 5.2.2 Mailbox storage quota exceeded; please contact the recipient manually";
                                    logger.LogInformation("SMTP DATA from {Remote} rejected: {QuotaType} quota exceeded for {Recipient}.",
                                        remote, quotaType, quotaRecipient);
                                    await connection.ReplyAsync(response, serverCancellationToken);
                                    state.ResetTransaction();
                                    if (authenticatedAccountId is not null && !string.IsNullOrWhiteSpace(sender))
                                    {
                                        var notifications = scope.ServiceProvider.GetRequiredService<DeliveryNotificationService>();
                                        await notifications.CreateAsync(sender, string.Join(", ", recipients),
                                            $"{response}. 超限目标：{quotaRecipient}；本次邮件事务的所有收件人均未投递成功。",
                                            "未入队", 1, DeliveryNotificationKind.QuotaExceeded, serverCancellationToken);
                                    }
                                    break;
                                }
                                catch (Exception exception) when (DatabaseFailurePolicy.IsConcurrencyConflict(exception))
                                {
                                    logger.LogInformation(exception, "SMTP DATA from {Remote} deferred because storage has a concurrency conflict.", remote);
                                    await connection.ReplyAsync("451 4.3.0 Temporary storage concurrency conflict", serverCancellationToken);
                                    state.ResetTransaction();
                                    break;
                                }
                                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                                {
                                    logger.LogError(
                                        exception,
                                        "SMTP DATA from {Remote} could not be committed to storage.",
                                        remote);
                                    await connection.ReplyAsync(
                                        "451 4.3.0 Temporary storage failure",
                                        serverCancellationToken);
                                    state.ResetTransaction();
                                    break;
                                }
                                logger.LogInformation(
                                    "SMTP DATA accepted from {Remote}: messageId={MessageId}, sender={Sender}, recipientCount={RecipientCount}, localDeliveries={LocalDeliveries}, queuedDeliveries={QueuedDeliveries}.",
                                    remote,
                                    accepted.MessageId,
                                    state.Sender,
                                    state.Recipients.Count,
                                    accepted.LocalDeliveries,
                                    accepted.QueuedDeliveries);
                                if (pipeline is not null)
                                    await pipeline.OnMessageAcceptedAsync(
                                        new MessageAcceptedContext(
                                            accepted.MessageId,
                                            state.Sender,
                                            state.Recipients,
                                            remote,
                                            state.AuthenticatedUser),
                                        serverCancellationToken);
                                await connection.ReplyAsync($"250 2.0.0 Queued as {accepted.MessageId}", serverCancellationToken);
                            }

                            state.ResetTransaction();
                            break;
                        }
                        default:
                            logger.LogDebug(
                                "SMTP command {Command} from {Remote} is not implemented.",
                                command,
                                remote);
                            await connection.ReplyAsync("502 5.5.1 Command not implemented", serverCancellationToken);
                            break;
                    }
                    }
                    catch (Exception error) { operation?.Fail(error); throw; }
                }
            }
            catch (TimeoutException)
            {
                logger.LogInformation("SMTP client {Remote} timed out.", remote);
            }
            catch (OperationCanceledException) when (!serverCancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("SMTP client {Remote} exceeded the authentication or TLS deadline.", remote);
            }
            catch (InvalidDataException exception)
            {
                logger.LogInformation(exception, "SMTP client {Remote} sent invalid or oversized data.", remote);
            }
            catch (AuthenticationException exception)
            {
                logger.LogDebug(
                    "SMTP TLS handshake with {Remote} was rejected: {Message}",
                    remote,
                    exception.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "SMTP session {Remote} failed.", remote);
            }
            finally
            {
                logger.LogInformation("SMTP connection from {Remote} closed.", remote);
            }
        }
    }

    internal async Task HandleMailFromAsync(
        SmtpConnection connection,
        SmtpSessionState state,
        string arguments,
        string remote,
        SmtpEndpointOptions endpoint,
        CancellationToken cancellationToken)
    {
        if (state.HeloName is null)
        {
            logger.LogInformation(
                "SMTP MAIL FROM from {Remote} was rejected because HELO/EHLO was not sent.",
                remote);
            await connection.ReplyAsync("503 5.5.1 Send HELO/EHLO first", cancellationToken);
            return;
        }

        if (endpoint.RequireAuthentication && state.AuthenticatedUser is null)
        {
            logger.LogInformation(
                "SMTP MAIL FROM from {Remote} was rejected because authentication is required.",
                remote);
            await connection.ReplyAsync("530 5.7.0 Authentication required", cancellationToken);
            return;
        }
        if (state.Sender is not null)
        {
            await connection.ReplyAsync("503 5.5.1 MAIL transaction already in progress", cancellationToken);
            return;
        }

        if (arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Any(x => x.Equals("SMTPUTF8", StringComparison.OrdinalIgnoreCase)))
        {
            // 当前存储和地址解析仍按传统邮箱地址处理，明确拒绝 SMTPUTF8 避免静默存入不可投递地址。
            await connection.ReplyAsync("555 5.5.4 SMTPUTF8 is not supported", cancellationToken);
            return;
        }

        if (!TryParsePathAndParameters(arguments, "FROM:", allowEmpty: true, out var sender, out var parameters))
        {
            logger.LogInformation(
                "SMTP MAIL FROM from {Remote} was rejected because the path was invalid.",
                remote);
            await connection.ReplyAsync("501 5.5.4 Invalid MAIL FROM", cancellationToken);
            return;
        }

        var sizeParameter = parameters.FirstOrDefault(x => x.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase));
        if (parameters.Count(x => x.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase)) > 1
            || parameters.Count(x => x.StartsWith("BODY=", StringComparison.OrdinalIgnoreCase)) > 1)
        {
            await connection.ReplyAsync("501 5.5.4 Duplicate MAIL FROM parameter", cancellationToken);
            return;
        }
        if (sizeParameter is not null
            && (!long.TryParse(sizeParameter.AsSpan(5), out var declaredSize) || declaredSize < 0))
        {
            await connection.ReplyAsync("501 5.5.4 Invalid SIZE parameter", cancellationToken);
            return;
        }
        if (sizeParameter is not null
            && long.Parse(sizeParameter.AsSpan(5)) > _options.MaximumMessageBytes)
        {
            await connection.ReplyAsync("552 5.3.4 Message size exceeds fixed maximum", cancellationToken);
            return;
        }
        if (parameters.Any(x => !x.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase)
                                && !x.Equals("BODY=8BITMIME", StringComparison.OrdinalIgnoreCase)
                                && !x.Equals("BODY=7BIT", StringComparison.OrdinalIgnoreCase)))
        {
            await connection.ReplyAsync("555 5.5.4 Unsupported MAIL FROM parameter", cancellationToken);
            return;
        }

        if (state.AuthenticatedAccountId is { } accountId)
        {
            using var scope = scopeFactory.CreateScope();
            var authorizer = scope.ServiceProvider.GetRequiredService<ISmtpSenderAuthorizer>();
            // 认证用户只能使用自己账号或授权别名作为信封发件人，防止域内冒名。
            if (sender.Length == 0 || !await authorizer.IsAuthorizedSenderAsync(accountId, sender, cancellationToken))
            {
                logger.LogInformation(
                    "SMTP MAIL FROM from {Remote} rejected sender={Sender}, authenticatedUser={AuthenticatedUser}: sender is not authorized.",
                    remote,
                    sender,
                    state.AuthenticatedUser ?? string.Empty);
                await connection.ReplyAsync("553 5.7.1 Sender address is not authorized for authenticated user", cancellationToken);
                return;
            }
        }

        using (var scope = scopeFactory.CreateScope())
        {
            var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
            if (pipeline is not null)
            {
                var decision = await pipeline.OnSmtpSenderAsync(
                    new SmtpSenderContext(
                        sender,
                        remote,
                        state.AuthenticatedUser,
                        state.AuthenticatedAccountId),
                    cancellationToken);
                if (decision.Kind != PluginDecisionKind.Continue)
                {
                    await connection.ReplyAsync(
                        decision.Kind == PluginDecisionKind.Defer
                            ? $"451 4.7.1 {decision.Message ?? "Sender deferred by policy"}"
                            : $"550 5.7.1 {decision.Message ?? "Sender rejected by policy"}",
                        cancellationToken);
                    return;
                }
            }
        }

        state.Sender = sender;
        state.BodyType = parameters.FirstOrDefault(x => x.StartsWith("BODY=", StringComparison.OrdinalIgnoreCase))?[5..]
                         ?? "7BIT";
        logger.LogInformation(
            "SMTP MAIL FROM accepted from {Remote}: sender={Sender}, authenticatedUser={AuthenticatedUser}.",
            remote,
            sender,
            state.AuthenticatedUser ?? string.Empty);
        await connection.ReplyAsync("250 2.1.0 Sender OK", cancellationToken);
    }

    internal async Task<bool> ValidateAuthenticatedMessageHeadersAsync(
        SmtpConnection connection,
        Stream data,
        SmtpSessionState state,
        string remote,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken)
    {
        if (state.AuthenticatedAccountId is not { } accountId)
        {
            return true;
        }

        MimeMessage message;
        try
        {
            Rewind(data);
            message = await MimeMessage.LoadAsync(data, cancellationToken);
            MailRuleService.RememberParsedMessage(data, message);
        }
        catch (Exception exception) when (exception is FormatException or IOException or InvalidOperationException)
        {
            logger.LogInformation(
                "SMTP DATA from {Remote} rejected for authenticatedUser={AuthenticatedUser}: unable to parse message headers.",
                remote,
                state.AuthenticatedUser ?? string.Empty);
            await connection.ReplyAsync("550 5.7.1 Message sender header is not authorized", cancellationToken);
            return false;
        }

        var authorizer = serviceProvider.GetRequiredService<ISmtpSenderAuthorizer>();
        if (message.From.Count == 0)
        {
            await RejectUnauthorizedHeaderAsync(
                connection,
                remote,
                state.AuthenticatedUser,
                string.Empty,
                "missing From header",
                cancellationToken);
            return false;
        }

        foreach (var address in message.From)
        {
            if (address is not MailboxAddress mailbox || string.IsNullOrWhiteSpace(mailbox.Address))
            {
                await RejectUnauthorizedHeaderAsync(
                    connection,
                    remote,
                    state.AuthenticatedUser,
                    address.ToString(),
                    "From header is not a mailbox address",
                    cancellationToken);
                return false;
            }

            if (!await authorizer.IsAuthorizedSenderAsync(accountId, mailbox.Address, cancellationToken))
            {
                await RejectUnauthorizedHeaderAsync(
                    connection,
                    remote,
                    state.AuthenticatedUser,
                    mailbox.Address,
                    "From header address is not authorized",
                    cancellationToken);
                return false;
            }
        }

        if (message.Sender is { } sender)
        {
            if (string.IsNullOrWhiteSpace(sender.Address)
                || !await authorizer.IsAuthorizedSenderAsync(accountId, sender.Address, cancellationToken))
            {
                await RejectUnauthorizedHeaderAsync(
                    connection,
                    remote,
                    state.AuthenticatedUser,
                    sender.Address,
                    "Sender header address is not authorized",
                    cancellationToken);
                return false;
            }
        }

        // 发信规则可以在 DATA 阶段直接拒信（例如命中敏感内容或收件域黑名单）。
        var ruleService = serviceProvider.GetService<MailRuleService>();
        if (ruleService is not null)
        {
            var facts = MailRuleService.BuildFacts(
                message,
                state.Sender ?? message.From.Mailboxes.FirstOrDefault()?.Address ?? string.Empty,
                state.Recipients,
                data.CanSeek ? data.Length : 0,
                DateTimeOffset.UtcNow);
            MailRuleRejection? rejection;
            try
            {
                rejection = await ruleService.EvaluateOutgoingRejectionAsync(accountId, facts, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Outgoing rule evaluation failed for SMTP DATA from {Remote}.", remote);
                await connection.ReplyAsync("451 4.3.0 Mail policy is temporarily unavailable", cancellationToken);
                return false;
            }
            if (rejection is not null)
            {
                logger.LogInformation(
                    "SMTP DATA from {Remote} sender={Sender} was rejected by outgoing rule {RuleId} ({RuleName}).",
                    remote,
                    state.Sender,
                    rejection.RuleId,
                    rejection.RuleName);
                await connection.ReplyAsync(
                    string.IsNullOrWhiteSpace(rejection.Reason)
                        ? "550 5.7.1 Message rejected by outgoing rule"
                        : $"550 5.7.1 {rejection.Reason}",
                    cancellationToken);
                return false;
            }
        }

        Rewind(data);
        return true;
    }

    private async Task RejectUnauthorizedHeaderAsync(
        SmtpConnection connection,
        string remote,
        string? authenticatedUser,
        string rejectedAddress,
        string reason,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "SMTP DATA from {Remote} rejected for authenticatedUser={AuthenticatedUser}, headerAddress={HeaderAddress}: {Reason}.",
            remote,
            authenticatedUser ?? string.Empty,
            rejectedAddress,
            reason);
        await connection.ReplyAsync("550 5.7.1 Message sender header is not authorized", cancellationToken);
    }

    internal async Task<bool> AuthenticateAsync(
        SmtpConnection connection,
        SmtpSessionState state,
        string arguments,
        string remote,
        CancellationToken cancellationToken)
        => await AuthenticateAsync(
            connection,
            state,
            arguments,
            remote,
            new SmtpEndpointOptions
            {
                RequireAuthentication = true,
                AuthenticationMechanisms = ["PLAIN", "LOGIN", "XOAUTH2", "OAUTHBEARER"]
            },
            true,
            cancellationToken);

    private async Task<bool> AuthenticateAsync(
        SmtpConnection connection,
        SmtpSessionState state,
        string arguments,
        string remote,
        SmtpEndpointOptions endpoint,
        bool isTls,
        CancellationToken cancellationToken)
    {
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await connection.ReplyAsync("501 5.5.4 AUTH mechanism is required", cancellationToken);
            return false;
        }

        try
        {
            var mechanism = parts[0].ToUpperInvariant();
            if (!endpoint.AuthenticationMechanisms.Contains(mechanism, StringComparer.OrdinalIgnoreCase))
            {
                await connection.ReplyAsync("504 5.5.4 Authentication mechanism is disabled", cancellationToken);
                return false;
            }
            if (mechanism == "PLAIN")
            {
                return await AuthenticatePlainAsync(
                    connection,
                    state,
                    parts.Length == 2 ? parts[1] : null,
                    remote,
                    cancellationToken);
            }

            if (mechanism == "LOGIN")
            {
                return await AuthenticateLoginAsync(
                    connection,
                    state,
                    parts.Length == 2 ? parts[1] : null,
                    remote,
                    cancellationToken);
            }

            if (mechanism is "XOAUTH2" or "OAUTHBEARER")
                return await AuthenticateBearerAsync(
                    connection,
                    state,
                    mechanism,
                    parts.Length == 2 ? parts[1] : null,
                    remote,
                    isTls,
                    cancellationToken);

            logger.LogInformation(
                "SMTP authentication mechanism {Mechanism} from {Remote} is not supported.",
                parts[0],
                remote);
            await connection.ReplyAsync("504 5.5.4 Only AUTH PLAIN and LOGIN are supported", cancellationToken);
            return false;
        }
        catch (FormatException)
        {
            logger.LogInformation(
                "SMTP authentication payload from {Remote} was invalid for mechanism {Mechanism}.",
                remote,
                parts[0]);
            await connection.ReplyAsync("501 5.5.2 Invalid AUTH payload", cancellationToken);
            return false;
        }
    }

    private async Task<bool> AuthenticateBearerAsync(
        SmtpConnection connection,
        SmtpSessionState state,
        string mechanism,
        string? initialResponse,
        string remote,
        bool isTls,
        CancellationToken cancellationToken)
    {
        var encoded = initialResponse;
        if (encoded is null)
        {
            await connection.ReplyAsync("334 ", cancellationToken);
            encoded = await connection.ReadLineAsync(cancellationToken) ?? string.Empty;
        }
        var (userName, token) = ParseBearerPayload(mechanism, DecodeBase64Utf8(encoded));
        using var scope = scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
        if (pipeline is null)
        {
            await connection.ReplyAsync("504 5.5.4 Authentication mechanism is not available", cancellationToken);
            return false;
        }
        var result = await pipeline.AuthenticateAsync(
            new AuthenticationContext(MailProtocol.Smtp, mechanism, userName, token, remote, isTls),
            cancellationToken);
        if (result.Status != AuthenticationStatus.Success || string.IsNullOrWhiteSpace(result.Identity))
        {
            state.CredentialRejected = result.Status == AuthenticationStatus.Failure;
            await connection.ReplyAsync(
                result.Status == AuthenticationStatus.NotHandled
                    ? "504 5.5.4 Authentication mechanism is not available"
                    : $"535 5.7.8 {result.Message ?? "Authentication credentials invalid"}",
                cancellationToken);
            return false;
        }
        var authenticator = scope.ServiceProvider.GetRequiredService<IAccountAuthenticator>();
        var accountId = await authenticator.ResolveMailboxIdentityAsync(result.Identity, cancellationToken);
        if (accountId is null)
        {
            state.CredentialRejected = true;
            await connection.ReplyAsync("535 5.7.8 Authentication identity is not an active mailbox", cancellationToken);
            return false;
        }
        state.AuthenticatedAccountId = accountId.Value;
        state.AuthenticatedUser = AddressParserForSmtp.Normalize(result.Identity);
        await connection.ReplyAsync("235 2.7.0 Authentication successful", cancellationToken);
        return true;
    }

    internal static (string? UserName, string Token) ParseBearerPayload(string mechanism, string payload)
    {
        string? userName = null;
        string? token = null;
        foreach (var field in payload.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            if (field.StartsWith("user=", StringComparison.OrdinalIgnoreCase))
                userName = field[5..];
            else if (field.StartsWith("n,a=", StringComparison.OrdinalIgnoreCase))
                userName = field[4..].TrimEnd(',');
            else if (field.StartsWith("auth=Bearer ", StringComparison.OrdinalIgnoreCase))
                token = field[12..];
        }
        if (string.IsNullOrWhiteSpace(token)) throw new FormatException();
        return (userName, token);
    }

    private async Task<bool> AuthenticatePlainAsync(
        SmtpConnection connection,
        SmtpSessionState state,
        string? initialResponse,
        string remote,
        CancellationToken cancellationToken)
    {
        var encoded = initialResponse;
        if (encoded is null)
        {
            await connection.ReplyAsync("334 ", cancellationToken);
            encoded = await connection.ReadLineAsync(cancellationToken) ?? string.Empty;
        }

        var decoded = DecodeBase64Utf8(encoded);
        var fields = decoded.Split('\0');
        if (fields.Length != 3)
        {
            throw new FormatException();
        }

        return await AuthenticateCredentialsAsync(connection, state, fields[1], fields[2], remote, cancellationToken);
    }

    private async Task<bool> AuthenticateLoginAsync(
        SmtpConnection connection,
        SmtpSessionState state,
        string? initialUserName,
        string remote,
        CancellationToken cancellationToken)
    {
        var encodedUserName = initialUserName;
        if (encodedUserName is null)
        {
            await connection.ReplyAsync("334 VXNlcm5hbWU6", cancellationToken);
            encodedUserName = await connection.ReadLineAsync(cancellationToken) ?? string.Empty;
        }

        var userName = DecodeBase64Utf8(encodedUserName);
        await connection.ReplyAsync("334 UGFzc3dvcmQ6", cancellationToken);
        var password = DecodeBase64Utf8(await connection.ReadLineAsync(cancellationToken) ?? string.Empty);
        return await AuthenticateCredentialsAsync(connection, state, userName, password, remote, cancellationToken);
    }

    private async Task<bool> AuthenticateCredentialsAsync(
        SmtpConnection connection,
        SmtpSessionState state,
        string userName,
        string password,
        string remote,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<IAccountAuthenticator>();
        var accountId = await authenticator.AuthenticateMailboxAsync(userName, password, cancellationToken);
        if (accountId is null)
        {
            state.CredentialRejected = true;
            logger.LogInformation(
                "SMTP authentication failed for {UserName} from {Remote}.",
                userName,
                remote);
            await connection.ReplyAsync("535 5.7.8 Authentication credentials invalid", cancellationToken);
            return false;
        }

        state.AuthenticatedAccountId = accountId.Value;
        state.AuthenticatedUser = AddressParserForSmtp.Normalize(userName);
        logger.LogInformation(
            "SMTP authentication succeeded for {UserName} from {Remote}.",
            state.AuthenticatedUser,
            remote);
        await connection.ReplyAsync("235 2.7.0 Authentication successful", cancellationToken);
        return true;
    }

    private static string DecodeBase64Utf8(string encoded) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));

    internal async Task ReplyCapabilitiesAsync(
        SmtpConnection connection,
        SmtpEndpointOptions endpoint,
        bool isTls,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            $"250-{_options.HostName}",
            "250-PIPELINING",
            $"250-SIZE {_options.MaximumMessageBytes}",
            "250-8BITMIME",
            "250-ENHANCEDSTATUSCODES"
        };
        if (!isTls
            && endpoint.EffectiveSecurity == ConnectionSecurity.StartTls
            && (certificateProvider is null || await certificateProvider.HasAnyCertificateAsync(cancellationToken)))
        {
            lines.Add("250-STARTTLS");
            logger.LogDebug("SMTP STARTTLS advertised on port {Port}.", endpoint.Port);
        }

        if (isTls && endpoint.RequireAuthentication)
        {
            var mechanisms = new List<string>();
            foreach (var builtIn in new[] { "PLAIN", "LOGIN" })
                if (endpoint.AuthenticationMechanisms.Contains(builtIn, StringComparer.OrdinalIgnoreCase))
                    mechanisms.Add(builtIn);
            using var scope = scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
            if (pipeline is not null)
            {
                foreach (var pluginMechanism in await pipeline.GetAuthenticationMechanismsAsync(
                             MailProtocol.Smtp,
                             cancellationToken))
                {
                    if (endpoint.AuthenticationMechanisms.Contains(pluginMechanism, StringComparer.OrdinalIgnoreCase)
                        && !mechanisms.Contains(pluginMechanism, StringComparer.OrdinalIgnoreCase))
                        mechanisms.Add(pluginMechanism);
                }
            }
            if (mechanisms.Count > 0) lines.Add($"250-AUTH {string.Join(' ', mechanisms)}");
        }

        lines.Add("250 HELP");
        await connection.ReplyAsync(string.Join("\r\n", lines), cancellationToken);
    }

    private async Task<Stream> UpgradeTlsBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(protection?.Settings.TlsHandshakeSeconds ?? 10));
        return await UpgradeTlsAsync(stream, timeout.Token);
    }

    private async Task<Stream> UpgradeTlsAsync(Stream stream, CancellationToken cancellationToken)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(
            async (_, hello, _, token) => await certificateProvider.SelectOptionsAsync(hello, token),
            state: null,
            cancellationToken);
        return ssl;
    }

    internal static bool TryValidateHelo(string value, out string result)
    {
        result = value.Trim();
        if (result.Length is 0 or > 255 || result.Any(char.IsControl) || !result.All(x => x <= 127))
        {
            return false;
        }

        if (result.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (result[0] == '[' && result[^1] == ']')
        {
            var literal = result[1..^1];
            if (literal.StartsWith("IPv6:", StringComparison.OrdinalIgnoreCase)) literal = literal[5..];
            return IPAddress.TryParse(literal, out _);
        }

        return Uri.CheckHostName(result) == UriHostNameType.Dns
               && result.Split('.').All(label =>
                   label.Length is > 0 and <= 63
                   && label[0] != '-'
                   && label[^1] != '-'
                   && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static void Rewind(Stream stream)
    {
        if (!stream.CanSeek)
        {
            throw new InvalidOperationException("Message stream is not seekable.");
        }

        stream.Position = 0;
    }

    private static bool TryParsePath(string arguments, string prefix, bool allowEmpty, out string path)
    {
        return TryParsePathAndParameters(arguments, prefix, allowEmpty, out path, out _);
    }

    internal static bool TryParsePathAndParameters(
        string arguments,
        string prefix,
        bool allowEmpty,
        out string path,
        out string[] parameters)
    {
        path = string.Empty;
        parameters = [];
        if (!arguments.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = arguments[prefix.Length..].TrimStart();
        if (!remainder.StartsWith('<'))
        {
            return false;
        }
        var end = FindPathEnd(remainder);
        if (end < 1) return false;

        path = remainder[1..end].Trim().ToLowerInvariant();
        parameters = remainder[(end + 1)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowEmpty && path.Length == 0 || AddressParserForSmtp.IsValid(path);
    }

    private static int FindPathEnd(string value)
    {
        var quoted = false;
        var escaped = false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped) { escaped = false; continue; }
            if (quoted && character == '\\') { escaped = true; continue; }
            if (character == '"') { quoted = !quoted; continue; }
            if (character == '>' && !quoted) return index;
        }
        return -1;
    }
}

internal sealed class SmtpAuthFailureLimiter(SmtpAbuseOptions options)
{
    private readonly ConcurrentDictionary<string, FailureState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _window = TimeSpan.FromMinutes(Math.Max(1, options.AuthenticationWindowMinutes));
    private readonly TimeSpan _block = TimeSpan.FromMinutes(Math.Max(1, options.AuthenticationBlockMinutes));
    private readonly int _maximumFailures = Math.Max(1, options.MaximumAuthenticationFailures);

    public bool IsBlocked(string remote, DateTimeOffset now)
    {
        var key = NormalizeRemote(remote);
        if (!_states.TryGetValue(key, out var state)) return false;
        if (state.BlockedUntil > now) return true;
        if (state.BlockedUntil is not null || now - state.WindowStarted >= _window)
            _states.TryRemove(key, out _);
        return false;
    }

    public bool RecordFailure(string remote, DateTimeOffset now)
    {
        var key = NormalizeRemote(remote);
        var state = _states.AddOrUpdate(
            key,
            _ => new FailureState(now, 1, null),
            (_, current) => now - current.WindowStarted >= _window
                ? new FailureState(now, 1, null)
                : current with { Failures = current.Failures + 1 });
        if (state.Failures < _maximumFailures) return false;
        _states[key] = state with { BlockedUntil = now + _block };
        return true;
    }

    public void Clear(string remote) => _states.TryRemove(NormalizeRemote(remote), out _);

    private static string NormalizeRemote(string remote) =>
        IpFirewallRules.TryParseRemoteAddress(remote)?.ToString() ?? remote.Trim();

    private sealed record FailureState(DateTimeOffset WindowStarted, int Failures, DateTimeOffset? BlockedUntil);
}

internal sealed class SmtpSessionState
{
    public bool CredentialRejected { get; set; }
    public string? HeloName { get; set; }
    public bool Extended { get; set; }
    public long? AuthenticatedAccountId { get; set; }
    public string? AuthenticatedUser { get; set; }
    public string? Sender { get; set; }
    public string BodyType { get; set; } = "7BIT";
    public List<string> Recipients { get; } = [];

    public void ResetTransaction()
    {
        Sender = null;
        BodyType = "7BIT";
        Recipients.Clear();
    }

    public RecipientAddResult TryAddRecipient(string recipient, int maximumRecipients)
    {
        if (Recipients.Contains(recipient, StringComparer.OrdinalIgnoreCase))
            return RecipientAddResult.Duplicate;
        if (Recipients.Count >= maximumRecipients)
            return RecipientAddResult.LimitExceeded;
        Recipients.Add(recipient);
        return RecipientAddResult.Added;
    }
}

internal enum RecipientAddResult
{
    Added,
    Duplicate,
    LimitExceeded
}

internal static class AddressParserForSmtp
{
    public static bool IsValid(string value)
    {
        var at = value.LastIndexOf('@');
        if (at <= 0 || at >= value.Length - 1 || value.Length > 320
            || value.Any(character => character > 127 || char.IsControl(character)))
            return false;
        return IsValidLocalPart(value[..at]) && IsValidDomain(value[(at + 1)..]);
    }

    private static bool IsValidLocalPart(string value)
    {
        if (value.Length is 0 or > 64) return false;
        if (value[0] == '"' && value[^1] == '"')
        {
            var escaped = false;
            foreach (var character in value[1..^1])
            {
                if (escaped)
                {
                    if (character is < (char)32 or > (char)126) return false;
                    escaped = false;
                }
                else if (character == '\\') escaped = true;
                else if (character is '\r' or '\n' or '"') return false;
            }
            return !escaped;
        }
        const string allowed = "!#$%&'*+-/=?^_`{|}~.";
        return value[0] != '.' && value[^1] != '.' && !value.Contains("..", StringComparison.Ordinal)
               && value.All(x => char.IsAsciiLetterOrDigit(x) || allowed.Contains(x));
    }

    private static bool IsValidDomain(string value)
    {
        if (value[0] == '[' && value[^1] == ']')
        {
            var literal = value[1..^1];
            if (literal.StartsWith("IPv6:", StringComparison.OrdinalIgnoreCase)) literal = literal[5..];
            return IPAddress.TryParse(literal, out _);
        }
        return value.Length <= 255 && value.Split('.').All(label =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-'
            && label.All(x => char.IsAsciiLetterOrDigit(x) || x == '-'));
    }

    public static string Normalize(string value)
    {
        var normalized = value.Trim().Trim('<', '>').ToLowerInvariant();
        if (!IsValid(normalized))
        {
            throw new FormatException($"Invalid email address: {value}");
        }

        var at = normalized.LastIndexOf('@');
        return normalized[..at] + "@" + normalized[(at + 1)..].TrimEnd('.');
    }

    public static string TryNormalizeForAudit(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        return trimmed.Length <= 320 && trimmed.All(x => !char.IsControl(x)) ? trimmed : "invalid";
    }
}

internal sealed class SmtpConnection(Stream stream, TimeSpan timeout, long maximumMessageBytes, string? preparedDirectory = null) : IAsyncDisposable
{
    private static readonly byte[] CrLf = "\r\n"u8.ToArray();
    private Stream _stream = stream;
    private readonly BufferedProtocolReader _reader = new(stream);

    public void Attach(Stream stream)
    {
        _stream = stream;
        _reader.Attach(stream);
    }
    public Stream Detach()
    {
        _reader.DiscardBufferedPlaintext();
        return _stream;
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await _reader.ReadLineAsync(8192, false, timeout, cancellationToken);
        return line is null ? null : Encoding.UTF8.GetString(line.Value.Span);
    }

    public async Task<Stream> ReadDataAsync(CancellationToken cancellationToken)
    {
        using var performance = PerformanceTelemetry.Stage("smtp.upload", "wait");
        var directory = preparedDirectory ?? Path.GetTempPath();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"mailserver-{System.Guid.NewGuid()}.eml");
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        long size = 0;
        var writeBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        var buffered = 0;
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var headerBytes = 0;
                var inHeaders = true;
                var hasHeader = false;
                var receivedHeaders = 0;
                while (true)
                {
                    var line = await ReadDataLineAsync(cancellationToken)
                        ?? throw new EndOfStreamException("SMTP DATA ended before the terminator.");
                    if (line.Span.SequenceEqual("."u8)) break;
                    if (line.Length >= 2 && line.Span[0] == (byte)'.' && line.Span[1] == (byte)'.')
                    {
                        line = line[1..];
                    }

                    if (inHeaders)
                    {
                        if (line.Length == 0)
                        {
                            inHeaders = false;
                        }
                        else
                        {
                            if (!TryValidateHeaderLine(line, hasHeader, out var fieldName))
                            {
                                if (!hasHeader && line.Span[0] is not ((byte)' ' or (byte)'\t'))
                                {
                                    inHeaders = false;
                                }
                                else
                                {
                                    throw new InvalidDataException("SMTP header field is malformed.");
                                }
                            }
                            else
                            {
                                hasHeader = true;
                                if (line.Length > SmtpServerOptions.MaximumHeaderLineBytes)
                                    throw new InvalidDataException("SMTP header line exceeds the configured limit.");
                                if (fieldName.Equals("Received", StringComparison.OrdinalIgnoreCase)
                                    && ++receivedHeaders > SmtpServerOptions.MaximumReceivedHeaders)
                                    throw new InvalidDataException("SMTP Received header count exceeds the configured limit.");
                            }
                        }

                        if (inHeaders)
                        {
                            headerBytes += line.Length + 2;
                            if (headerBytes > SmtpServerOptions.MaximumHeaderBytes)
                                throw new InvalidDataException("SMTP headers exceed the configured total limit.");
                        }
                    }

                    size += line.Length + 2;
                    if (size > maximumMessageBytes)
                    {
                        throw new InvalidDataException($"Message exceeds the {maximumMessageBytes} byte limit.");
                    }
                    if (buffered + line.Length + CrLf.Length > writeBuffer.Length && buffered > 0)
                    {
                        if (preparedDirectory is not null) hash.AppendData(writeBuffer, 0, buffered);
                        await output.WriteAsync(writeBuffer.AsMemory(0, buffered), cancellationToken);
                        buffered = 0;
                    }
                    if (line.Length + CrLf.Length > writeBuffer.Length)
                    {
                        if (preparedDirectory is not null) { hash.AppendData(line.Span); hash.AppendData(CrLf); }
                        await output.WriteAsync(line, cancellationToken);
                        await output.WriteAsync(CrLf, cancellationToken);
                    }
                    else
                    {
                        line.Span.CopyTo(writeBuffer.AsSpan(buffered)); buffered += line.Length;
                        CrLf.CopyTo(writeBuffer, buffered); buffered += CrLf.Length;
                    }
                }
                if (buffered > 0)
                {
                    if (preparedDirectory is not null) hash.AppendData(writeBuffer, 0, buffered);
                    await output.WriteAsync(writeBuffer.AsMemory(0, buffered), cancellationToken);
                }
            }

            return preparedDirectory is null ? new DeleteOnDisposeFileStream(path)
                : new PreparedMessageContent(path, new ContentDescriptor(Convert.ToHexStringLower(hash.GetHashAndReset()), size));
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(writeBuffer); }
    }

    private ValueTask<ReadOnlyMemory<byte>?> ReadDataLineAsync(CancellationToken cancellationToken) =>
        _reader.ReadLineAsync(65_536, true, timeout, cancellationToken);

    private static bool TryValidateHeaderLine(ReadOnlyMemory<byte> line, bool hasHeader, out string fieldName)
    {
        fieldName = string.Empty;
        if (line.Span[0] is (byte)' ' or (byte)'\t')
        {
            if (!hasHeader)
                return false;
            fieldName = string.Empty;
            return true;
        }

        var colon = line.Span.IndexOf((byte)':');
        if (colon <= 0
            || line.Span[..colon].IndexOfAnyExceptInRange((byte)33, (byte)126) >= 0)
        {
            return false;
        }
        fieldName = Encoding.ASCII.GetString(line.Span[..colon]);
        return true;
    }

    public async Task ReplyAsync(string response, CancellationToken cancellationToken)
    {
        using var performance = PerformanceTelemetry.Stage("smtp.response", "wait");
        if (response.StartsWith('4')) PerformanceTelemetry.Status("error");
        else if (response.StartsWith('5')) PerformanceTelemetry.Status("rejected");
        var bytes = Encoding.ASCII.GetBytes(response + "\r\n");
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        return _stream.DisposeAsync();
    }
}

internal sealed class DeleteOnDisposeFileStream(string path)
    : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
