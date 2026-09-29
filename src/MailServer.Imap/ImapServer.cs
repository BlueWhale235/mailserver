using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.PluginSdk;
using MailServer.PluginSdk.Imap;
using MailServer.PluginSdk.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MailServer.Imap;

/// <summary>
/// IMAP 服务配置。命令超时和 APPEND 大小限制会直接影响客户端同步体验。
/// </summary>
public sealed class ImapServerOptions
{
    public const string SectionName = "Imap";
    public string HostName { get; set; } = "mail.localhost";
    public int CommandTimeoutSeconds { get; set; } = 1800;
    public int MaximumConnections { get; set; } = 200;
    public long MaximumAppendBytes { get; set; } = 52_428_800;
    public int IdlePollSeconds { get; set; } = 15;
    public string[] AuthenticationMechanisms { get; set; } = ["LOGIN", "PLAIN", "XOAUTH2", "OAUTHBEARER"];
    public List<ImapEndpointOptions> Endpoints { get; set; } = [];
}

/// <summary>
/// 单个 IMAP 监听端点配置。Security 是当前配置入口，ImplicitTls 用于兼容旧配置。
/// </summary>
public sealed class ImapEndpointOptions
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
}

/// <summary>
/// IMAP 协议宿主，负责连接、TLS、认证和命令调度；邮箱状态修改统一委托给 MailboxService。
/// </summary>
public sealed partial class ImapServerService(
    IServiceScopeFactory scopeFactory,
    IOptions<ImapServerOptions> options,
    DomainCertificateProvider certificateProvider,
    ILogger<ImapServerService> logger,
    IpFirewallService? firewall = null,
    ResourceRuntime? resources = null,
    ConnectionProtection? protection = null) : BackgroundService
{
    private sealed class CredentialRejectedException(string message) : InvalidOperationException(message);
    private const int MaximumBodyStructureDepth = 64;
    private const int MaximumBodyStructureParts = 2048;
    internal const string BaseCapabilities = "IMAP4rev1 UIDPLUS MOVE IDLE NAMESPACE ENABLE UTF8=ACCEPT SPECIAL-USE";
    private readonly ImapServerOptions _options = options.Value;
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _acceptLoops = [];
    private AdjustableConcurrencyLimit _connectionLimit = null!;
    private readonly AdjustableConcurrencyLimit _largeMimeLimit = resources?.LargeMime ?? new AdjustableConcurrencyLimit(1);
    private volatile bool _stopping;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _connectionLimit = resources?.Imap ?? new AdjustableConcurrencyLimit(_options.MaximumConnections);
        foreach (var endpoint in _options.Endpoints)
        {
            if (!IPAddress.TryParse(endpoint.Address, out var bindAddress))
            {
                throw new InvalidOperationException($"Invalid IMAP bind address: {endpoint.Address}");
            }

            var listener = new TcpListener(bindAddress, endpoint.Port);
            listener.Start();
            _listeners.Add(listener);
            _acceptLoops.Add(AcceptLoopAsync(listener, endpoint, stoppingToken));
            logger.LogInformation("IMAP listening on {Port}; security={Security}", endpoint.Port, endpoint.Security);
        }

        resources?.SetServiceState("imap", _listeners.Count > 0 ? "listening" : "stopped");

        return Task.WhenAll(_acceptLoops);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        resources?.SetServiceState("imap", "stopped");
        foreach (var listener in _listeners) listener.Stop();
        await base.StopAsync(cancellationToken);
    }

    private async Task AcceptLoopAsync(
        TcpListener listener,
        ImapEndpointOptions endpoint,
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
            var banned = protection is not null && peer is not null
                && protection.IsAuthBlocked(ProtectedLoginProtocol.Imap, peer, DateTimeOffset.UtcNow);
            if (protection is not null && peer is not null
                && (banned || !protection.TryAcquire(ProtectedConnectionGroup.Imap, peer, DateTimeOffset.UtcNow, out sourcePermit)))
            {
                await RejectBusyAsync(client, endpoint, cancellationToken, banned);
                continue;
            }
            // 在 IMAP greeting 之前执行防火墙判断，避免被阻断来源继续探测能力列表。
            try
            {
                if (firewall is not null
                    && client.Client.RemoteEndPoint is IPEndPoint remoteEndPoint
                    && await firewall.IsBlockedAsync(remoteEndPoint.Address, cancellationToken))
                {
                    logger.LogWarning(
                        "IMAP connection from {RemoteAddress} was closed by firewall before greeting.",
                        remoteEndPoint.Address);
                    client.Dispose();
                    sourcePermit?.Dispose();
                    continue;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "IMAP firewall evaluation failed; closing only the current connection.");
                client.Dispose();
                sourcePermit?.Dispose();
                continue;
            }

            // 与 SMTP 一样使用进程级并发上限，避免长连接客户端耗尽资源。
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

    private static async Task RejectBusyAsync(TcpClient client, ImapEndpointOptions endpoint, CancellationToken token, bool banned = false)
    {
        using (client)
        {
            if (endpoint.Security == ConnectionSecurity.ImplicitTls) return;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var reply = banned ? "* BYE [UNAVAILABLE] Too many authentication failures\r\n"
                    : "* BYE [UNAVAILABLE] Server busy; retry later.\r\n";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(reply), timeout.Token);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException) { }
        }
    }

    private async Task HandleLimitedAsync(TcpClient client, ImapEndpointOptions endpoint, IDisposable permit, IDisposable? sourcePermit, CancellationToken token)
    {
        using (permit) using (sourcePermit) await HandleClientAsync(client, endpoint, token);
    }

    private async Task HandleClientAsync(
        TcpClient client,
        ImapEndpointOptions endpoint,
        CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        using (client)
        {
            using var preAuthentication = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            preAuthentication.CancelAfter(TimeSpan.FromSeconds(protection?.Settings.PreAuthenticationSeconds ?? 120));
            try
            {
                Stream stream = client.GetStream();
                var isTls = false;
                if (endpoint.Security == ConnectionSecurity.ImplicitTls)
                {
                    stream = await UpgradeTlsBoundedAsync(stream, preAuthentication.Token);
                    isTls = true;
                }

                await using var connection = new ImapConnection(
                    stream,
                    TimeSpan.FromSeconds(_options.CommandTimeoutSeconds),
                    _options.MaximumAppendBytes);
                using (var scope = scopeFactory.CreateScope())
                {
                    var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
                    if (pipeline is not null)
                    {
                        var decision = await pipeline.OnConnectionAsync(
                            new ConnectionContext(MailProtocol.Imap, remote, isTls),
                            cancellationToken);
                        if (decision.Kind != PluginDecisionKind.Continue)
                        {
                            // 连接级插件可以在认证前拒绝 IMAP 会话，适合来源策略和维护窗口。
                            await connection.WriteLineAsync(
                                $"* BYE {decision.Message ?? "Connection rejected by policy"}",
                                cancellationToken);
                            return;
                        }
                        if (isTls)
                            await pipeline.OnTlsEstablishedAsync(
                                new TlsEstablishedContext(MailProtocol.Imap, remote),
                                cancellationToken);
                    }
                }
                await connection.WriteLineAsync(
                    $"* OK [CAPABILITY {BaseCapabilities}] MailServer ready",
                    cancellationToken);
                var state = new ImapSessionState();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var commandToken = state.AccountId is null ? preAuthentication.Token : cancellationToken;
                    var line = await connection.ReadLineAsync(commandToken);
                    if (line is null) return;
                    var firstSpace = line.IndexOf(' ');
                    if (firstSpace <= 0)
                    {
                        await connection.WriteLineAsync("* BAD Invalid command", cancellationToken);
                        continue;
                    }

                    var parsed = ParseCommandLine(line);
                    if (parsed is null)
                    {
                        await connection.WriteLineAsync("* BAD Invalid command tag", cancellationToken);
                        continue;
                    }

                    var (tag, command, arguments, useUid) = parsed.Value;
                    var keepRunning = await ExecuteCommandAsync(
                            connection,
                            state,
                            tag,
                            command,
                            arguments,
                            useUid,
                            remote,
                            endpoint.Security,
                            isTls,
                            commandToken);
                    if (!keepRunning) return;
                    if (command == "STARTTLS" && state.TlsUpgradeRequested)
                    {
                        stream = await UpgradeTlsBoundedAsync(connection.Detach(), commandToken);
                        connection.Attach(stream);
                        isTls = true;
                        // STARTTLS 后必须丢弃认证和选择邮箱状态，避免明文阶段状态跨越到 TLS 会话。
                        state = new ImapSessionState();
                        using var scope = scopeFactory.CreateScope();
                        var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
                        if (pipeline is not null)
                            await pipeline.OnTlsEstablishedAsync(
                                new TlsEstablishedContext(MailProtocol.Imap, remote),
                                cancellationToken);
                    }
                }
            }
            catch (TimeoutException)
            {
                logger.LogInformation("IMAP client {Remote} timed out.", remote);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("IMAP client {Remote} exceeded the authentication or TLS deadline.", remote);
            }
            catch (AuthenticationException exception)
            {
                logger.LogDebug(
                    "IMAP TLS handshake with {Remote} was rejected: {Message}",
                    remote,
                    exception.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "IMAP session {Remote} failed.", remote);
            }
        }
    }

    internal async Task<bool> ExecuteLineAsync(
        ImapConnection connection,
        ImapSessionState state,
        string line,
        string remote,
        ConnectionSecurity endpointSecurity,
        bool isTls,
        CancellationToken cancellationToken)
    {
        var parsed = ParseCommandLine(line);
        if (parsed is null)
        {
            await connection.WriteLineAsync("* BAD Invalid command tag", cancellationToken);
            return true;
        }

        var (tag, command, arguments, useUid) = parsed.Value;
        return await ExecuteCommandAsync(
            connection,
            state,
            tag,
            command,
            arguments,
            useUid,
            remote,
            endpointSecurity,
            isTls,
            cancellationToken);
    }

    internal async Task<bool> ExecuteCommandAsync(
        ImapConnection connection,
        ImapSessionState state,
        string tag,
        string command,
        string arguments,
        bool useUid,
        string remote,
        ConnectionSecurity endpointSecurity,
        bool isTls,
        CancellationToken cancellationToken)
    {
        var peer = IpFirewallRules.TryParseRemoteAddress(remote);
        if (protection is not null && peer is not null && state.AccountId is null
            && (command is "LOGIN" or "AUTHENTICATE")
            && protection.IsAuthBlocked(ProtectedLoginProtocol.Imap, peer, DateTimeOffset.UtcNow))
        {
            await connection.WriteLineAsync("* BYE [UNAVAILABLE] Too many authentication failures", cancellationToken);
            return false;
        }
        try
        {
            return await DispatchAsync(
                connection,
                state,
                tag,
                command,
                arguments,
                useUid,
                remote,
                endpointSecurity,
                isTls,
                cancellationToken);
        }
        catch (CredentialRejectedException exception)
        {
            var blocked = protection is not null && peer is not null
                && protection.RecordAuthFailure(ProtectedLoginProtocol.Imap, peer, DateTimeOffset.UtcNow);
            await connection.WriteLineAsync($"{tag} NO {Quote(exception.Message)}", cancellationToken);
            if (blocked)
            {
                await connection.WriteLineAsync("* BYE [UNAVAILABLE] Too many authentication failures", cancellationToken);
                return false;
            }
        }
        catch (KeyNotFoundException exception)
        {
            await connection.WriteLineAsync($"{tag} NO {Quote(exception.Message)}", cancellationToken);
        }
        catch (FormatException exception)
        {
            logger.LogInformation(
                "IMAP command {Command} from {Remote} was rejected as malformed: {Message}; argumentLength={ArgumentLength}, hasLiteralMarker={HasLiteralMarker}, hasUtf8Wrapper={HasUtf8Wrapper}.",
                command,
                remote,
                exception.Message,
                arguments.Length,
                command == "APPEND" && LiteralRegex().IsMatch(arguments),
                command == "APPEND" && arguments.Contains("UTF8", StringComparison.OrdinalIgnoreCase));
            await connection.WriteLineAsync($"{tag} BAD {Quote(exception.Message)}", cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            await connection.WriteLineAsync($"{tag} BAD {Quote(exception.Message)}", cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await connection.WriteLineAsync($"{tag} NO {Quote(exception.Message)}", cancellationToken);
        }

        return true;
    }

    private static (string Tag, string Command, string Arguments, bool UseUid)? ParseCommandLine(string line)
    {
        var firstSpace = line.IndexOf(' ');
        if (firstSpace <= 0)
        {
            return null;
        }

        var tag = line[..firstSpace];
        if (!IsValidTag(tag))
        {
            return null;
        }

        var remainder = line[(firstSpace + 1)..].TrimStart();
        var secondSpace = remainder.IndexOf(' ');
        var command = (secondSpace < 0 ? remainder : remainder[..secondSpace]).ToUpperInvariant();
        var arguments = secondSpace < 0 ? string.Empty : remainder[(secondSpace + 1)..].Trim();
        var useUid = false;
        if (command == "UID")
        {
            var uidSpace = arguments.IndexOf(' ');
            if (uidSpace < 0)
            {
                return (tag, command, arguments, useUid);
            }

            command = arguments[..uidSpace].ToUpperInvariant();
            arguments = arguments[(uidSpace + 1)..].Trim();
            useUid = true;
        }

        return (tag, command, arguments, useUid);
    }

    private static bool IsValidTag(string tag)
    {
        if (tag.Length == 0)
        {
            return false;
        }

        foreach (var character in tag)
        {
            if (character is <= ' ' or >= '\u007f'
                || character is '(' or ')' or '{' or '%' or '*' or '"' or '\\' or ']')
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> DispatchAsync(
        ImapConnection connection,
        ImapSessionState state,
        string tag,
        string command,
        string arguments,
        bool useUid,
        string remote,
        ConnectionSecurity endpointSecurity,
        bool isTls,
        CancellationToken cancellationToken)
    {
        using (var scope = scopeFactory.CreateScope())
        {
            var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
            if (pipeline is not null)
            {
                var decision = await pipeline.OnImapCommandAsync(
                    new ImapCommandContext(
                        command,
                        arguments,
                        state.AccountId,
                        state.UserName,
                        state.MailboxName,
                        state.ReadOnly,
                        remote),
                    cancellationToken);
                if (decision.Kind != PluginDecisionKind.Continue)
                    throw new InvalidOperationException(decision.Message ?? "Command rejected by policy.");
            }
        }

        switch (command)
        {
            case "CAPABILITY":
                var authCapabilities = await GetAuthenticationCapabilitiesAsync(isTls, cancellationToken);
                await connection.WriteLineAsync(
                    $"* CAPABILITY {BaseCapabilities}{(!isTls && endpointSecurity == ConnectionSecurity.StartTls && (certificateProvider is null || await certificateProvider.HasAnyCertificateAsync(cancellationToken)) ? " STARTTLS" : string.Empty)}{(isTls ? authCapabilities : " LOGINDISABLED")}",
                    cancellationToken);
                await OkAsync(connection, tag, "CAPABILITY completed", cancellationToken);
                return true;
            case "NOOP":
                if (state.MailboxId is not null)
                {
                    await RefreshSelectedMailboxAsync(connection, state, remote, cancellationToken);
                }

                await OkAsync(connection, tag, "NOOP completed", cancellationToken);
                return true;
            case "LOGOUT":
                await connection.WriteLineAsync("* BYE Logging out", cancellationToken);
                await OkAsync(connection, tag, "LOGOUT completed", cancellationToken);
                return false;
            case "STARTTLS":
                RequireNotAuthenticated(state);
                if (isTls) throw new InvalidOperationException("TLS is already active.");
                if (endpointSecurity != ConnectionSecurity.StartTls) throw new InvalidOperationException("TLS is unavailable.");
                if (certificateProvider is not null
                    && !await certificateProvider.HasAnyCertificateAsync(cancellationToken))
                    throw new InvalidOperationException("TLS is unavailable.");
                await OkAsync(connection, tag, "Begin TLS negotiation now", cancellationToken);
                state.TlsUpgradeRequested = true;
                return true;
            case "LOGIN":
                RequireNotAuthenticated(state);
                if (!isTls) throw new InvalidOperationException("TLS is required before authentication.");
                if (!_options.AuthenticationMechanisms.Contains("LOGIN", StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("LOGIN authentication is disabled.");
                var loginTokens = ImapTokenizer.Tokenize(arguments);
                if (loginTokens.Count != 2) throw new FormatException("LOGIN requires user name and password.");
                await AuthenticateAsync(state, loginTokens[0], loginTokens[1], remote, isTls, cancellationToken);
                await OkAsync(connection, tag, "LOGIN completed", cancellationToken);
                return true;
            case "AUTHENTICATE":
                RequireNotAuthenticated(state);
                if (!isTls) throw new InvalidOperationException("TLS is required before authentication.");
                var mechanism = arguments.ToUpperInvariant();
                if (!_options.AuthenticationMechanisms.Contains(mechanism, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Authentication mechanism is disabled.");
                await connection.WriteLineAsync("+", cancellationToken);
                var encoded = await connection.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException();
                if (mechanism == "PLAIN")
                {
                    var fields = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\0');
                    if (fields.Length != 3) throw new FormatException("Invalid AUTHENTICATE PLAIN payload.");
                    await AuthenticateAsync(state, fields[1], fields[2], remote, isTls, cancellationToken);
                }
                else if (mechanism is "XOAUTH2" or "OAUTHBEARER")
                {
                    await AuthenticateBearerAsync(
                        state,
                        mechanism,
                        Encoding.UTF8.GetString(Convert.FromBase64String(encoded)),
                        remote,
                        isTls,
                        cancellationToken);
                }
                else
                {
                    throw new InvalidOperationException("Authentication mechanism is not available.");
                }
                await OkAsync(connection, tag, "AUTHENTICATE completed", cancellationToken);
                return true;
            case "NAMESPACE":
                RequireAuthenticated(state);
                await connection.WriteLineAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL", cancellationToken);
                await OkAsync(connection, tag, "NAMESPACE completed", cancellationToken);
                return true;
            case "ID":
                await connection.WriteLineAsync("* ID (\"name\" \"MailServer\" \"version\" \"1.0\")", cancellationToken);
                await OkAsync(connection, tag, "ID completed", cancellationToken);
                return true;
            case "ENABLE":
                RequireAuthenticated(state);
                var enabled = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(x => x.Equals("UTF8=ACCEPT", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                await connection.WriteLineAsync($"* ENABLED {string.Join(' ', enabled)}", cancellationToken);
                await OkAsync(connection, tag, "ENABLE completed", cancellationToken);
                return true;
            case "LIST":
            case "LSUB":
                RequireAuthenticated(state);
                await ListAsync(connection, state, arguments, command == "LSUB", cancellationToken);
                await OkAsync(connection, tag, $"{command} completed", cancellationToken);
                return true;
            case "SUBSCRIBE":
            case "UNSUBSCRIBE":
                RequireAuthenticated(state);
                if (command == "SUBSCRIBE")
                {
                    await WithMailboxServiceAsync(service =>
                        service.SubscribeAsync(state.AccountId!.Value, ImapTokenizer.Unquote(arguments), cancellationToken));
                }
                else
                {
                    await WithMailboxServiceAsync(service =>
                        service.UnsubscribeAsync(state.AccountId!.Value, ImapTokenizer.Unquote(arguments), cancellationToken));
                }

                await OkAsync(connection, tag, $"{command} completed", cancellationToken);
                return true;
            case "CREATE":
                RequireAuthenticated(state);
                await WithMailboxServiceAsync(service => service.CreateAsync(state.AccountId!.Value, ImapTokenizer.Unquote(arguments), cancellationToken));
                await OkAsync(connection, tag, "CREATE completed", cancellationToken);
                return true;
            case "RENAME":
                RequireAuthenticated(state);
                var renameTokens = ImapTokenizer.Tokenize(arguments);
                if (renameTokens.Count != 2) throw new FormatException("RENAME requires source and destination mailbox.");
                await WithMailboxServiceAsync(service =>
                    service.RenameAsync(state.AccountId!.Value, renameTokens[0], renameTokens[1], cancellationToken));
                if (state.MailboxName is not null && MailboxNamesEqual(state.MailboxName, renameTokens[0]))
                {
                    state.Deselect();
                }
                await OkAsync(connection, tag, "RENAME completed", cancellationToken);
                return true;
            case "DELETE":
                RequireAuthenticated(state);
                var deletedMailbox = ImapTokenizer.Unquote(arguments);
                await WithMailboxServiceAsync(service => service.DeleteAsync(state.AccountId!.Value, deletedMailbox, cancellationToken));
                if (state.MailboxName is not null && MailboxNamesEqual(state.MailboxName, deletedMailbox))
                {
                    state.Deselect();
                }
                await OkAsync(connection, tag, "DELETE completed", cancellationToken);
                return true;
            case "SELECT":
            case "EXAMINE":
                RequireAuthenticated(state);
                await SelectAsync(connection, state, ImapTokenizer.Unquote(arguments), command == "EXAMINE", remote, cancellationToken);
                await OkAsync(connection, tag, $"[{(command == "EXAMINE" ? "READ-ONLY" : "READ-WRITE")}] {command} completed", cancellationToken);
                return true;
            case "STATUS":
                RequireAuthenticated(state);
                await StatusAsync(connection, state, arguments, cancellationToken);
                await OkAsync(connection, tag, "STATUS completed", cancellationToken);
                return true;
            case "APPEND":
                RequireAuthenticated(state);
                await AppendAsync(connection, state, tag, arguments, remote, cancellationToken);
                return true;
            case "CHECK":
                RequireSelected(state);
                await RefreshSelectedMailboxAsync(connection, state, remote, cancellationToken);
                await OkAsync(connection, tag, "CHECK completed", cancellationToken);
                return true;
            case "CLOSE":
                RequireSelected(state);
                if (!state.ReadOnly)
                {
                    _ = await WithMailboxServiceAsync(service => service.ExpungeAsync(state.MailboxId!.Value, cancellationToken));
                }
                state.Deselect();
                await OkAsync(connection, tag, "CLOSE completed", cancellationToken);
                return true;
            case "UNSELECT":
                RequireSelected(state);
                state.Deselect();
                await OkAsync(connection, tag, "UNSELECT completed", cancellationToken);
                return true;
            case "EXPUNGE":
                RequireSelected(state);
                RequireWritable(state);
                await ExpungeAsync(connection, state, cancellationToken);
                await OkAsync(connection, tag, "EXPUNGE completed", cancellationToken);
                return true;
            case "SEARCH":
                RequireSelected(state);
                await SearchAsync(connection, state, arguments, useUid, cancellationToken);
                await OkAsync(connection, tag, "SEARCH completed", cancellationToken);
                return true;
            case "FETCH":
                RequireSelected(state);
                await FetchAsync(connection, state, arguments, remote, useUid, cancellationToken);
                await OkAsync(connection, tag, "FETCH completed", cancellationToken);
                return true;
            case "STORE":
                RequireSelected(state);
                RequireWritable(state);
                await StoreAsync(connection, state, arguments, useUid, cancellationToken);
                await OkAsync(connection, tag, "STORE completed", cancellationToken);
                return true;
            case "COPY":
            case "MOVE":
                RequireSelected(state);
                if (command == "MOVE") RequireWritable(state);
                var copyMoveResult = await CopyMoveAsync(connection, state, arguments, useUid, command == "MOVE", cancellationToken);
                await connection.WriteLineAsync(
                    $"{tag} OK [COPYUID {copyMoveResult.TargetUidValidity} {FormatSequenceSet(copyMoveResult.SourceUids)} {FormatSequenceSet(copyMoveResult.TargetUids)}] {command} completed",
                    cancellationToken);
                return true;
            case "IDLE":
                RequireAuthenticated(state);
                await IdleAsync(connection, state, tag, remote, cancellationToken);
                return true;
            default:
                await connection.WriteLineAsync($"{tag} BAD Unsupported command", cancellationToken);
                return true;
        }
    }

    private async Task AuthenticateAsync(
        ImapSessionState state,
        string address,
        string password,
        string remote,
        bool isTls,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<IAccountAuthenticator>();
        var accountId = await authenticator.AuthenticateMailboxAsync(address, password, cancellationToken);
        if (accountId is null)
        {
            logger.LogInformation(
                "IMAP authentication failed for {UserName} from {Remote}.",
                address,
                remote);
            throw new CredentialRejectedException("Authentication failed.");
        }

        await CompleteAuthenticationAsync(scope.ServiceProvider, state, accountId.Value, address, remote, isTls, cancellationToken);
    }

    private async Task AuthenticateBearerAsync(
        ImapSessionState state,
        string mechanism,
        string payload,
        string remote,
        bool isTls,
        CancellationToken cancellationToken)
    {
        var (userName, token) = ParseBearerPayload(payload);
        using var scope = scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetService<PluginPipeline>()
                       ?? throw new InvalidOperationException("Authentication mechanism is not available.");
        var result = await pipeline.AuthenticateAsync(
            new AuthenticationContext(MailProtocol.Imap, mechanism, userName, token, remote, isTls),
            cancellationToken);
        if (result.Status != AuthenticationStatus.Success || string.IsNullOrWhiteSpace(result.Identity))
            throw (result.Status == AuthenticationStatus.Failure
                ? new CredentialRejectedException(result.Message ?? "Authentication failed.")
                : new InvalidOperationException(
                result.Status == AuthenticationStatus.NotHandled
                    ? "Authentication mechanism is not available."
                    : result.Message ?? "Authentication failed."));
        var authenticator = scope.ServiceProvider.GetRequiredService<IAccountAuthenticator>();
        var accountId = await authenticator.ResolveMailboxIdentityAsync(result.Identity, cancellationToken);
        if (accountId is null) throw new CredentialRejectedException("Authentication identity is not an active mailbox.");
        await CompleteAuthenticationAsync(
            scope.ServiceProvider,
            state,
            accountId.Value,
            result.Identity,
            remote,
            isTls,
            cancellationToken);
    }

    private async Task CompleteAuthenticationAsync(
        IServiceProvider serviceProvider,
        ImapSessionState state,
        Guid accountId,
        string address,
        string remote,
        bool isTls,
        CancellationToken cancellationToken)
    {
        var pipeline = serviceProvider.GetService<PluginPipeline>();
        if (pipeline is not null)
        {
            var decision = await pipeline.OnImapSessionAsync(
                new ImapSessionContext(remote, address, isTls),
                cancellationToken);
            if (decision.Kind != PluginDecisionKind.Continue)
                throw new InvalidOperationException(decision.Message ?? "Rejected by policy.");
        }

        var mailboxes = serviceProvider.GetRequiredService<IMailboxService>();
        await mailboxes.EnsureDefaultFoldersAsync(accountId, cancellationToken);
        state.AccountId = accountId;
        state.UserName = address.ToLowerInvariant();
        logger.LogInformation(
            "IMAP authentication succeeded for {UserName} from {Remote}.",
            state.UserName,
            remote);
    }

    private async Task<string> GetAuthenticationCapabilitiesAsync(bool isTls, CancellationToken cancellationToken)
    {
        if (!isTls) return string.Empty;
        var mechanisms = new List<string>();
        if (_options.AuthenticationMechanisms.Contains("PLAIN", StringComparer.OrdinalIgnoreCase))
            mechanisms.Add("PLAIN");
        using var scope = scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
        if (pipeline is not null)
        {
            foreach (var mechanism in await pipeline.GetAuthenticationMechanismsAsync(MailProtocol.Imap, cancellationToken))
            {
                if (_options.AuthenticationMechanisms.Contains(mechanism, StringComparer.OrdinalIgnoreCase)
                    && !mechanisms.Contains(mechanism, StringComparer.OrdinalIgnoreCase))
                    mechanisms.Add(mechanism);
            }
        }
        return mechanisms.Count == 0 ? string.Empty : " " + string.Join(' ', mechanisms.Select(x => $"AUTH={x}"));
    }

    internal static (string? UserName, string Token) ParseBearerPayload(string payload)
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
        if (string.IsNullOrWhiteSpace(token)) throw new FormatException("Invalid bearer authentication payload.");
        return (userName, token);
    }

    internal Task ListAsync(
        ImapConnection connection,
        ImapSessionState state,
        bool subscribedOnly,
        CancellationToken cancellationToken) =>
        ListAsync(connection, state, "\"\" \"*\"", subscribedOnly, cancellationToken);

    internal async Task ListAsync(
        ImapConnection connection,
        ImapSessionState state,
        string arguments,
        bool subscribedOnly,
        CancellationToken cancellationToken)
    {
        var tokens = ImapTokenizer.Tokenize(arguments);
        if (tokens.Count != 2) throw new FormatException("LIST requires reference name and mailbox pattern.");
        var referenceName = tokens[0];
        var mailboxPattern = tokens[1];
        var pattern = BuildListPattern(referenceName, mailboxPattern);
        var mailboxes = await WithMailboxServiceAsync(service => subscribedOnly
            ? service.ListSubscribedAsync(state.AccountId!.Value, cancellationToken)
            : service.ListAsync(state.AccountId!.Value, cancellationToken));
        foreach (var mailbox in mailboxes.Where(mailbox => MailboxMatchesListPattern(mailbox.Name, pattern)))
        {
            var attributes = SpecialUseAttributes(mailbox.Name);
            await connection.WriteLineAsync(
                $"* {(subscribedOnly ? "LSUB" : "LIST")} ({attributes}) \"/\" {Quote(mailbox.Name)}",
                cancellationToken);
        }
    }

    internal async Task SelectAsync(
        ImapConnection connection,
        ImapSessionState state,
        string mailboxName,
        bool readOnly,
        string remote,
        CancellationToken cancellationToken)
    {
        var mailbox = await WithMailboxServiceAsync(service =>
            service.SelectAsync(state.AccountId!.Value, mailboxName, cancellationToken));
        if (mailbox is null) throw new KeyNotFoundException("Mailbox not found.");
        state.MailboxId = mailbox.Id;
        state.MailboxName = mailbox.Name;
        state.ReadOnly = readOnly;
        state.Exists = mailbox.Exists;
        state.UidNext = mailbox.UidNext;
        state.HighestModSeq = mailbox.HighestModSeq;
        logger.LogInformation(
            "IMAP SELECT from {Remote}: user={UserName}, mailbox={Mailbox}, readOnly={ReadOnly}, exists={Exists}, uidNext={UidNext}, highestModSeq={HighestModSeq}.",
            remote,
            state.UserName ?? string.Empty,
            mailbox.Name,
            readOnly,
            mailbox.Exists,
            mailbox.UidNext,
            mailbox.HighestModSeq);
        await connection.WriteLineAsync("* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)", cancellationToken);
        await connection.WriteLineAsync("* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft \\*)] Flags permitted", cancellationToken);
        await connection.WriteLineAsync($"* {mailbox.Exists} EXISTS", cancellationToken);
        await connection.WriteLineAsync($"* OK [UIDVALIDITY {mailbox.UidValidity}] UIDs valid", cancellationToken);
        await connection.WriteLineAsync($"* OK [UIDNEXT {mailbox.UidNext}] Predicted next UID", cancellationToken);
        await connection.WriteLineAsync($"* OK [HIGHESTMODSEQ {mailbox.HighestModSeq}] Highest mod-sequence", cancellationToken);
        if (mailbox.Unseen > 0) await connection.WriteLineAsync("* OK [UNSEEN 1] First unseen message", cancellationToken);
    }

    private async Task StatusAsync(
        ImapConnection connection,
        ImapSessionState state,
        string arguments,
        CancellationToken cancellationToken)
    {
        var tokens = ImapTokenizer.Tokenize(arguments);
        if (tokens.Count < 2) throw new FormatException("STATUS requires mailbox and data items.");
        var mailbox = await WithMailboxServiceAsync(service =>
            service.SelectAsync(state.AccountId!.Value, tokens[0], cancellationToken));
        if (mailbox is null) throw new KeyNotFoundException("Mailbox not found.");
        await connection.WriteLineAsync(
            $"* STATUS {Quote(mailbox.Name)} (MESSAGES {mailbox.Exists} UNSEEN {mailbox.Unseen} UIDNEXT {mailbox.UidNext} UIDVALIDITY {mailbox.UidValidity} HIGHESTMODSEQ {mailbox.HighestModSeq})",
            cancellationToken);
    }

    internal async Task RefreshSelectedMailboxAsync(
        ImapConnection connection,
        ImapSessionState state,
        string remote,
        CancellationToken cancellationToken)
    {
        if (state.AccountId is null || state.MailboxName is null)
        {
            return;
        }

        var mailbox = await WithMailboxServiceAsync(service =>
            service.SelectAsync(state.AccountId.Value, state.MailboxName, cancellationToken));
        if (mailbox is null)
        {
            await connection.WriteLineAsync("* 0 EXISTS", cancellationToken);
            state.Deselect();
            return;
        }

        var changed = false;
        if (mailbox.Exists != state.Exists)
        {
            await connection.WriteLineAsync($"* {mailbox.Exists} EXISTS", cancellationToken);
            changed = true;
        }

        if (mailbox.UidNext != state.UidNext)
        {
            await connection.WriteLineAsync($"* OK [UIDNEXT {mailbox.UidNext}] Predicted next UID", cancellationToken);
            changed = true;
        }

        if (mailbox.HighestModSeq != state.HighestModSeq)
        {
            await connection.WriteLineAsync($"* OK [HIGHESTMODSEQ {mailbox.HighestModSeq}] Highest mod-sequence", cancellationToken);
            changed = true;
        }

        if (changed)
        {
            logger.LogInformation(
                "IMAP mailbox refresh for {Remote}: user={UserName}, mailbox={Mailbox}, exists={Exists}, uidNext={UidNext}, highestModSeq={HighestModSeq}.",
                remote,
                state.UserName ?? string.Empty,
                mailbox.Name,
                mailbox.Exists,
                mailbox.UidNext,
                mailbox.HighestModSeq);
        }

        state.Exists = mailbox.Exists;
        state.UidNext = mailbox.UidNext;
        state.HighestModSeq = mailbox.HighestModSeq;
    }

    private async Task IdleAsync(
        ImapConnection connection,
        ImapSessionState state,
        string tag,
        string remote,
        CancellationToken cancellationToken)
    {
        await connection.WriteLineAsync("+ idling", cancellationToken);
        await RefreshSelectedMailboxAsync(connection, state, remote, cancellationToken);
        var doneTask = connection.ReadLineAsync(cancellationToken);
        var poll = TimeSpan.FromSeconds(Math.Max(1, _options.IdlePollSeconds));
        while (!cancellationToken.IsCancellationRequested)
        {
            var delayTask = Task.Delay(poll, cancellationToken);
            var completed = await Task.WhenAny(doneTask, delayTask);
            if (completed == doneTask)
            {
                var done = await doneTask;
                if (!string.Equals(done, "DONE", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Expected DONE.");
                await OkAsync(connection, tag, "IDLE terminated", cancellationToken);
                return;
            }

            await RefreshSelectedMailboxAsync(connection, state, remote, cancellationToken);
        }
    }

    internal async Task AppendAsync(
        ImapConnection connection,
        ImapSessionState state,
        string tag,
        string arguments,
        string remote,
        CancellationToken cancellationToken)
    {
        var request = await ParseAppendArgumentsAsync(connection, arguments, cancellationToken);
        logger.LogInformation(
            "IMAP APPEND requested from {Remote}: user={UserName}, mailbox={Mailbox}, flags={FlagCount}, hasInternalDate={HasInternalDate}, literalLength={LiteralLength}, literalKind={LiteralKind}, mailboxLiteralKind={MailboxLiteralKind}, utf8Wrapped={Utf8Wrapped}.",
            remote,
            state.UserName ?? string.Empty,
            request.MailboxName,
            request.Flags.Count,
            request.InternalDate.HasValue,
            request.LiteralLength,
            request.BinaryLiteral ? "binary" : "literal",
            request.MailboxLiteralKind,
            request.Utf8Wrapped);

        var mailbox = await WithMailboxServiceAsync(service =>
            service.SelectAsync(state.AccountId!.Value, request.MailboxName, cancellationToken));
        if (mailbox is null)
        {
            if (request.NonSynchronizing)
            {
                await using var ignored = await connection.ReadLiteralAsync(request.LiteralLength, cancellationToken);
                await ReadAppendCommandEndAsync(connection, request, cancellationToken);
            }

            logger.LogInformation(
                "IMAP APPEND from {Remote} was rejected because mailbox {Mailbox} does not exist.",
                remote,
                request.MailboxName);
            await connection.WriteLineAsync($"{tag} NO [TRYCREATE] Mailbox does not exist.", cancellationToken);
            return;
        }

        if (!request.NonSynchronizing)
        {
            await connection.WriteLineAsync("+ Ready for literal data", cancellationToken);
        }

        await using var literal = await connection.ReadLiteralAsync(request.LiteralLength, cancellationToken);
        await ReadAppendCommandEndAsync(connection, request, cancellationToken);
        var item = await WithMailboxServiceAsync(service =>
            service.AppendAsync(
                state.AccountId!.Value,
                request.MailboxName,
                literal,
                request.Flags,
                request.InternalDate,
                cancellationToken));
        logger.LogInformation(
            "IMAP APPEND completed from {Remote}: user={UserName}, mailbox={Mailbox}, uid={Uid}, size={Size}.",
            remote,
            state.UserName ?? string.Empty,
            request.MailboxName,
            item.Item.Uid,
            request.LiteralLength);
        await connection.WriteLineAsync(
            $"{tag} OK [APPENDUID {item.UidValidity} {item.Item.Uid}] APPEND completed",
            cancellationToken);
    }

    internal async Task<ImapAppendRequest> ParseAppendArgumentsAsync(
        ImapConnection connection,
        string arguments,
        CancellationToken cancellationToken)
    {
        var firstLiteral = TryParseSoleLiteralMarker(arguments);
        if (firstLiteral is null)
        {
            return ParseAppendArguments(arguments);
        }

        if (!firstLiteral.NonSynchronizing)
        {
            await connection.WriteLineAsync("+ Ready for mailbox name literal", cancellationToken);
        }

        var mailboxName = await connection.ReadLiteralTextAsync(firstLiteral.Length, cancellationToken);
        var remainder = await connection.ReadLineAsync(cancellationToken) ?? string.Empty;
        return ParseAppendArguments(
            Quote(mailboxName) + remainder,
            firstLiteral.Binary ? "binary-mailbox" : "mailbox");
    }

    private static async Task ReadAppendCommandEndAsync(
        ImapConnection connection,
        ImapAppendRequest request,
        CancellationToken cancellationToken)
    {
        var remainder = await connection.ReadLineAsync(cancellationToken);
        if (request.Utf8Wrapped)
        {
            if (!string.Equals(remainder?.Trim(), ")", StringComparison.Ordinal))
            {
                throw MalformedAppend();
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(remainder))
        {
            throw MalformedAppend();
        }
    }

    internal static ImapAppendRequest ParseAppendArguments(string arguments)
        => ParseAppendArguments(arguments, mailboxLiteralKind: "none");

    private static ImapAppendRequest ParseAppendArguments(string arguments, string mailboxLiteralKind)
    {
        if (TryExpandInlineMailboxLiteral(arguments, out var expandedArguments, out var expandedLiteralKind))
        {
            arguments = expandedArguments;
            mailboxLiteralKind = expandedLiteralKind;
        }

        var match = LiteralRegex().Match(arguments);
        if (!match.Success) throw MalformedAppend();

        if (!long.TryParse(match.Groups["length"].Value, CultureInfo.InvariantCulture, out var literalLength))
        {
            throw MalformedAppend();
        }
        var nonSynchronizing = match.Groups["plus"].Success;
        var binaryLiteral = match.Groups["binary"].Success;
        var reader = new ImapAppendArgumentReader(arguments[..match.Index].Trim());
        var mailboxName = DecodeMailboxName(reader.ReadAString());
        reader.SkipWhitespace();

        IReadOnlyCollection<string> flags = [];
        if (!reader.End && reader.Current == '(')
        {
            flags = ParseFlags(reader.ReadList());
            reader.SkipWhitespace();
        }

        DateTimeOffset? internalDate = null;
        var utf8Wrapped = false;
        if (!reader.End)
        {
            var nextArgument = reader.ReadAString();
            if (nextArgument.Equals("UTF8", StringComparison.OrdinalIgnoreCase))
            {
                ReadUtf8WrapperOpen(reader);
                utf8Wrapped = true;
            }
            else
            {
                if (!nextArgument.Equals("NIL", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryParseInternalDate(nextArgument, out var parsedInternalDate))
                    {
                        throw MalformedAppend();
                    }

                    internalDate = parsedInternalDate;
                }

                reader.SkipWhitespace();
                if (!reader.End)
                {
                    if (!reader.ReadAString().Equals("UTF8", StringComparison.OrdinalIgnoreCase))
                    {
                        throw MalformedAppend();
                    }

                    ReadUtf8WrapperOpen(reader);
                    utf8Wrapped = true;
                }
            }
        }

        if (!reader.End) throw MalformedAppend();
        return new ImapAppendRequest(
            mailboxName,
            flags,
            internalDate,
            literalLength,
            nonSynchronizing,
            binaryLiteral,
            mailboxLiteralKind,
            utf8Wrapped);
    }

    private static void ReadUtf8WrapperOpen(ImapAppendArgumentReader reader)
    {
        reader.SkipWhitespace();
        reader.ReadExpected('(');
        reader.SkipWhitespace();
    }

    private async Task SearchAsync(
        ImapConnection connection,
        ImapSessionState state,
        string criteria,
        bool useUid,
        CancellationToken cancellationToken)
    {
        var results = await WithMailboxServiceAsync(service =>
            service.SearchAsync(state.MailboxId!.Value, criteria, useUid, cancellationToken));
        await connection.WriteLineAsync(
            results.Count == 0 ? "* SEARCH" : $"* SEARCH {string.Join(' ', results)}",
            cancellationToken);
    }

    private static string BuildListPattern(string referenceName, string mailboxPattern)
    {
        if (string.IsNullOrEmpty(referenceName)) return mailboxPattern;
        if (string.IsNullOrEmpty(mailboxPattern)) return referenceName.TrimEnd('/');
        return referenceName.EndsWith("/", StringComparison.Ordinal)
            ? referenceName + mailboxPattern
            : referenceName + "/" + mailboxPattern;
    }

    private static bool MailboxMatchesListPattern(string mailboxName, string pattern)
    {
        if (pattern.Length == 0) return mailboxName.Length == 0;
        var regex = new StringBuilder("^");
        foreach (var character in pattern)
        {
            regex.Append(character switch
            {
                '*' => ".*",
                '%' => "[^/]*",
                _ => Regex.Escape(character.ToString())
            });
        }

        regex.Append('$');
        return Regex.IsMatch(mailboxName, regex.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private static bool MailboxNamesEqual(string left, string right) =>
        string.Equals(NormalizeMailboxNameForComparison(left), NormalizeMailboxNameForComparison(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMailboxNameForComparison(string mailboxName)
    {
        var value = mailboxName.Trim().Trim('"');
        return value.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? "INBOX" : value;
    }

    internal async Task FetchAsync(
        ImapConnection connection,
        ImapSessionState state,
        string arguments,
        string remote,
        bool useUid,
        CancellationToken cancellationToken)
    {
        var separator = arguments.IndexOf(' ');
        if (separator < 0) throw new FormatException("FETCH requires sequence set and data items.");
        var sequenceSet = arguments[..separator];
        var dataItems = arguments[(separator + 1)..].Trim();
        var request = ImapFetchRequest.Parse(dataItems);
        await using var items = await WithMailboxServiceAsync(service =>
            service.CreateFetchSnapshotAsync(state.MailboxId!.Value, sequenceSet, useUid, cancellationToken));
        logger.LogInformation(
            "IMAP FETCH from {Remote}: user={UserName}, mailbox={Mailbox}, sequenceSet={SequenceSet}, useUid={UseUid}, requested={Requested}, returned={Returned}.",
            remote,
            state.UserName ?? string.Empty,
            state.MailboxName ?? string.Empty,
            sequenceSet,
            useUid,
            request.LogSummary,
            items.Count);
        await foreach (var item in items.ReadAsync(cancellationToken))
        {
            var requiresMime = request.Envelope || request.Body || request.BodyStructure
                || request.BodySections.Any(x => x.Section.StartsWith("HEADER.FIELDS", StringComparison.OrdinalIgnoreCase));
            using var mimePermit = requiresMime && item.Size >= 1024 * 1024
                ? await AcquireLargeMimeAsync(cancellationToken) : null;
            var parts = new List<ImapFetchResponsePart>();
            if (useUid || request.Uid) parts.Add(ImapFetchResponsePart.Atom($"UID {item.Uid}"));
            if (request.Flags) parts.Add(ImapFetchResponsePart.Atom($"FLAGS ({item.Flags})"));
            if (request.Rfc822Size) parts.Add(ImapFetchResponsePart.Atom($"RFC822.SIZE {item.Size}"));
            if (request.InternalDate) parts.Add(ImapFetchResponsePart.Atom($"INTERNALDATE \"{item.InternalDate:dd-MMM-yyyy HH:mm:ss zzz}\""));

            byte[]? raw = null;
            MimeMessage? message = null;
            Stream? body = null;
            if (request.RequiresMessage)
            {
                try
                {
                    body = await WithMailboxServiceAsync(service => service.OpenMessageAsync(item.Id, cancellationToken));
                    if (requiresMime)
                    {
                        try
                        {
                            message = await MimeMessage.LoadAsync(body, cancellationToken);
                        }
                        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IOException)
                        {
                            message = new MimeMessage { Body = new TextPart("plain") { Text = string.Empty } };
                        }
                        body.Position = 0;
                    }
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException)
                {
                    logger.LogWarning(
                        exception,
                        "IMAP FETCH could not open stored content for mailboxMessageId={MailboxMessageId}; returning an empty safe literal.",
                        item.Id);
                    raw = [];
                    message = new MimeMessage { Body = new TextPart("plain") { Text = string.Empty } };
                }
            }

            if (request.Envelope && message is not null)
            {
                parts.Add(ImapFetchResponsePart.Atom($"ENVELOPE {BuildEnvelope(message)}"));
            }

            if (request.Body)
            {
                parts.Add(ImapFetchResponsePart.Atom($"BODY {BuildBodyStructure(message, raw, extended: false)}"));
            }

            if (request.BodyStructure)
            {
                parts.Add(ImapFetchResponsePart.Atom($"BODYSTRUCTURE {BuildBodyStructure(message, raw, extended: true)}"));
            }

            if (request.Rfc822 && body is not null)
                parts.Add(ImapFetchResponsePart.StreamLiteral("RFC822", body, 0, body.Length));
            else if (request.Rfc822 && raw is not null)
            {
                parts.Add(ImapFetchResponsePart.Literal("RFC822", raw));
            }

            if (body is not null)
            {
                (long HeaderEnd, long BodyStart) split = default;
                if (request.BodySections.Any(x => x.Section.Equals("HEADER", StringComparison.OrdinalIgnoreCase)
                    || x.Section.Equals("TEXT", StringComparison.OrdinalIgnoreCase)))
                    split = await FindHeaderBodySplitAsync(body, cancellationToken);
                foreach (var section in request.BodySections)
                {
                    if (section.Section.StartsWith("HEADER.FIELDS", StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add(ImapFetchResponsePart.Literal(section.ResponseName,
                            ApplyPartial(BuildSelectedHeaderBytes(message!, section.Section), section.Offset, section.Length)));
                        continue;
                    }
                    var kind = section.Section.Trim().ToUpperInvariant();
                    var start = kind == "TEXT" ? split.BodyStart : 0;
                    var length = kind switch
                    {
                        "HEADER" => split.HeaderEnd < 0 ? body.Length : split.HeaderEnd,
                        "TEXT" => split.BodyStart < 0 ? 0 : body.Length - split.BodyStart,
                        _ => body.Length
                    };
                    var appendCrLf = kind == "HEADER" && split.HeaderEnd < 0;
                    var offset = Math.Min((long)section.Offset.GetValueOrDefault(), length + (appendCrLf ? 2 : 0));
                    var available = length + (appendCrLf ? 2 : 0) - offset;
                    var count = section.Length is null ? available : Math.Min(available, section.Length.Value);
                    var streamBytes = Math.Max(0, Math.Min(count, length - offset));
                    parts.Add(ImapFetchResponsePart.StreamLiteral(section.ResponseName, body, start + Math.Min(offset, length),
                        count, streamBytes, appendCrLf ? Math.Max(0, offset - length) : 0));
                }
            }
            else if (raw is not null && message is not null)
            {
                foreach (var section in request.BodySections)
                {
                    parts.Add(ImapFetchResponsePart.Literal(
                        section.ResponseName,
                        ApplyPartial(BuildBodySection(raw, message, section.Section), section.Offset, section.Length)));
                }
            }

            parts.Add(ImapFetchResponsePart.Atom($"MODSEQ ({item.ModSeq})"));
            try
            {
                await WriteFetchResponseAsync(connection, item.Sequence, parts, cancellationToken);
            }
            finally
            {
                if (body is not null) await body.DisposeAsync();
            }
        }

        if (request.MarkSeen)
        {
            var unseenIds = new List<Guid>(256);
            await foreach (var item in items.ReadAsync(cancellationToken))
            {
                if (ParseFlags(item.Flags).Contains("\\Seen")) continue;
                unseenIds.Add(item.Id);
                if (unseenIds.Count < 256) continue;
                await WithMailboxServiceAsync(service =>
                    service.MarkSeenAsync(state.MailboxId!.Value, unseenIds.ToArray(), cancellationToken));
                unseenIds.Clear();
            }
            if (unseenIds.Count > 0)
                await WithMailboxServiceAsync(service =>
                    service.MarkSeenAsync(state.MailboxId!.Value, unseenIds, cancellationToken));
        }
    }

    private async Task<IDisposable> AcquireLargeMimeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var permit = _largeMimeLimit.TryAcquire();
            if (permit is not null) return permit;
            await Task.Delay(50, cancellationToken);
        }
    }

    private static async Task WriteFetchResponseAsync(
        ImapConnection connection,
        long sequence,
        IReadOnlyList<ImapFetchResponsePart> parts,
        CancellationToken cancellationToken)
    {
        await connection.WriteAsync($"* {sequence} FETCH (", cancellationToken);
        for (var index = 0; index < parts.Count; index++)
        {
            if (index > 0)
            {
                await connection.WriteAsync(" ", cancellationToken);
            }

            var part = parts[index];
            if (part.Bytes is null && part.Source is null)
            {
                await connection.WriteAsync(part.Name, cancellationToken);
                continue;
            }

            var length = part.Bytes?.Length ?? part.Length;
            await connection.WriteAsync($"{part.Name} {{{length}}}\r\n", cancellationToken);
            try
            {
                if (part.Bytes is not null) await connection.WriteBytesAsync(part.Bytes, cancellationToken);
                else if (part.Source is not null)
                {
                    part.Source.Position = part.Offset;
                    var remaining = part.StreamBytes;
                    var buffer = new byte[64 * 1024];
                    while (remaining > 0)
                    {
                        var read = await part.Source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                        if (read == 0) throw new EndOfStreamException("Stored message ended during IMAP literal output.");
                        await connection.WriteBytesAsync(buffer.AsMemory(0, read), cancellationToken);
                        remaining -= read;
                    }
                    if (part.Length > part.StreamBytes)
                    {
                        var suffix = new byte[] { 13, 10 };
                        await connection.WriteBytesAsync(suffix.AsMemory((int)part.SyntheticOffset,
                            (int)(part.Length - part.StreamBytes)), cancellationToken);
                    }
                }
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        await connection.WriteLineAsync(")", cancellationToken);
    }

    private static string BuildEnvelope(MimeMessage message)
    {
        var sender = new InternetAddressList();
        if (message.Sender is null)
        {
            sender.AddRange(message.From);
        }
        else
        {
            sender.Add(message.Sender);
        }

        var replyTo = message.ReplyTo.Count == 0 ? message.From : message.ReplyTo;
        var date = message.Date == DateTimeOffset.MinValue
            ? null
            : message.Date.ToString("ddd, dd MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var messageId = message.Headers["Message-ID"] ?? (string.IsNullOrWhiteSpace(message.MessageId) ? null : $"<{message.MessageId}>");
        return "("
               + $"{NString(date)} "
               + $"{NString(message.Subject)} "
               + $"{BuildEnvelopeAddressList(message.From)} "
               + $"{BuildEnvelopeAddressList(sender)} "
               + $"{BuildEnvelopeAddressList(replyTo)} "
               + $"{BuildEnvelopeAddressList(message.To)} "
               + $"{BuildEnvelopeAddressList(message.Cc)} "
               + $"{BuildEnvelopeAddressList(message.Bcc)} "
               + $"{NString(message.Headers["In-Reply-To"])} "
               + $"{NString(messageId)}"
               + ")";
    }

    private static MimeMessage LoadMimeMessageWithFallback(byte[] raw)
    {
        try
        {
            return MimeMessage.Load(new MemoryStream(raw));
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IOException)
        {
            return new MimeMessage { Body = new TextPart("plain") { Text = Encoding.UTF8.GetString(ExtractTextBytes(raw)) } };
        }
    }

    private static string BuildEnvelopeAddressList(IEnumerable<InternetAddress> addresses)
    {
        var mailboxes = FlattenAddresses(addresses).ToArray();
        if (mailboxes.Length == 0)
        {
            return "NIL";
        }

        return "(" + string.Join(' ', mailboxes.Select(BuildEnvelopeMailbox)) + ")";
    }

    private static IEnumerable<MailboxAddress> FlattenAddresses(IEnumerable<InternetAddress> addresses)
    {
        foreach (var address in addresses)
        {
            if (address is MailboxAddress mailbox)
            {
                yield return mailbox;
            }
            else if (address is GroupAddress group)
            {
                foreach (var member in FlattenAddresses(group.Members))
                {
                    yield return member;
                }
            }
        }
    }

    private static string BuildEnvelopeMailbox(MailboxAddress mailbox)
    {
        var at = mailbox.Address.LastIndexOf('@');
        var localPart = at > 0 ? mailbox.Address[..at] : mailbox.Address;
        var domain = at > 0 && at < mailbox.Address.Length - 1 ? mailbox.Address[(at + 1)..] : string.Empty;
        return $"({NString(mailbox.Name)} NIL {NString(localPart)} {NString(domain)})";
    }

    private static string BuildBodyStructure(MimeMessage? message, byte[]? raw, bool extended)
    {
        if (message?.Body is null)
        {
            var body = raw is null ? Array.Empty<byte>() : ExtractTextBytes(raw);
            var extension = extended ? " NIL NIL NIL NIL" : string.Empty;
            return $"(\"TEXT\" \"PLAIN\" NIL NIL NIL \"7BIT\" {body.Length} {CountLines(body)}{extension})";
        }

        var count = 0;
        return BuildBodyStructure(message.Body, extended, 0, ref count);
    }

    private static string BuildBodyStructure(MimeEntity entity, bool extended, int depth, ref int count)
    {
        if (depth > MaximumBodyStructureDepth || ++count > MaximumBodyStructureParts)
        {
            var limitExtension = extended ? " NIL NIL NIL NIL" : string.Empty;
            return $"(\"TEXT\" \"PLAIN\" NIL NIL NIL \"7BIT\" 0 0{limitExtension})";
        }

        if (entity is Multipart multipart)
        {
            var children = new List<string>();
            foreach (var child in multipart)
            {
                children.Add(BuildBodyStructure(child, extended, depth + 1, ref count));
            }
            var basic = $"({string.Join(' ', children)} {NString(multipart.ContentType.MediaSubtype.ToUpperInvariant())}";
            if (!extended) return basic + ")";
            return basic + $" {BuildParameterList(multipart.ContentType.Parameters)} {BuildDisposition(multipart.ContentDisposition)} NIL NIL)";
        }

        var bytes = WriteEntityBytes(entity);
        var mediaType = entity.ContentType.MediaType.ToUpperInvariant();
        var mediaSubtype = entity.ContentType.MediaSubtype.ToUpperInvariant();
        var parameters = BuildParameterList(entity.ContentType.Parameters);
        var contentId = string.IsNullOrWhiteSpace(entity.ContentId) ? entity.Headers["Content-ID"] : entity.ContentId;
        var description = entity.Headers["Content-Description"];
        var encoding = BuildTransferEncoding(entity);
        var size = bytes.Length;
        var extension = extended ? $" NIL {BuildDisposition(entity.ContentDisposition)} NIL NIL" : string.Empty;

        if (entity is MessagePart messagePart)
        {
            var nested = messagePart.Message;
            var envelope = nested is null ? "NIL" : BuildEnvelope(nested);
            var nestedBody = nested?.Body is null ? "NIL" : BuildBodyStructure(nested.Body, extended, depth + 1, ref count);
            return $"(\"MESSAGE\" \"RFC822\" {parameters} {NString(contentId)} {NString(description)} {NString(encoding)} {size} {envelope} {nestedBody} {CountLines(bytes)}{extension})";
        }

        if (entity is TextPart textPart)
        {
            var bodyBytes = ExtractTextBytes(WriteEntityBytes(textPart));
            return $"(\"TEXT\" {NString(mediaSubtype)} {parameters} {NString(contentId)} {NString(description)} {NString(encoding)} {bodyBytes.Length} {CountLines(bodyBytes)}{extension})";
        }

        return $"({NString(mediaType)} {NString(mediaSubtype)} {parameters} {NString(contentId)} {NString(description)} {NString(encoding)} {size}{extension})";
    }

    private static byte[] WriteEntityBytes(MimeEntity entity)
    {
        using var memory = new MemoryStream();
        entity.WriteTo(memory);
        return memory.ToArray();
    }

    private static string BuildParameterList(ParameterList? parameters)
    {
        if (parameters is null || !parameters.Any()) return "NIL";
        var values = parameters
            .SelectMany(parameter => new[] { Quote(parameter.Name.ToUpperInvariant()), Quote(parameter.Value ?? string.Empty) });
        return "(" + string.Join(' ', values) + ")";
    }

    private static string BuildDisposition(ContentDisposition? disposition)
    {
        if (disposition is null || string.IsNullOrWhiteSpace(disposition.Disposition)) return "NIL";
        return $"({NString(disposition.Disposition.ToUpperInvariant())} {BuildParameterList(disposition.Parameters)})";
    }

    private static string BuildTransferEncoding(MimeEntity entity)
    {
        var value = entity.Headers["Content-Transfer-Encoding"];
        if (string.IsNullOrWhiteSpace(value)) return "7BIT";
        return value.Trim().ToUpperInvariant();
    }

    private static byte[] BuildBodySection(byte[] raw, MimeMessage message, string section)
    {
        var normalized = section.Trim();
        if (normalized.Length == 0)
        {
            return raw;
        }

        var upper = normalized.ToUpperInvariant();
        if (upper == "HEADER")
        {
            return ExtractHeaderBytes(raw);
        }

        if (upper == "TEXT")
        {
            return ExtractTextBytes(raw);
        }

        if (upper.StartsWith("HEADER.FIELDS", StringComparison.Ordinal))
        {
            return BuildSelectedHeaderBytes(message, normalized);
        }

        return raw;
    }

    private static byte[] ApplyPartial(byte[] value, int? offset, int? length)
    {
        if (offset is null)
        {
            return value;
        }

        if (offset.Value >= value.Length)
        {
            return [];
        }

        var available = value.Length - offset.Value;
        var count = length is null ? available : Math.Min(length.Value, available);
        return value[offset.Value..(offset.Value + count)];
    }

    private static byte[] BuildSelectedHeaderBytes(MimeMessage message, string section)
    {
        var match = HeaderFieldsRegex().Match(section);
        if (!match.Success)
        {
            return Encoding.ASCII.GetBytes("\r\n");
        }

        var fields = match.Groups["fields"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var header in message.Headers)
        {
            if (fields.Contains(header.Field))
            {
                builder.Append(header.Field).Append(": ").Append(header.Value).Append("\r\n");
            }
        }

        builder.Append("\r\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static byte[] ExtractHeaderBytes(byte[] raw)
    {
        var split = FindHeaderBodySplit(raw);
        if (split.HeaderEnd < 0)
        {
            var fallback = new byte[raw.Length + 2];
            raw.CopyTo(fallback, 0);
            fallback[^2] = (byte)'\r';
            fallback[^1] = (byte)'\n';
            return fallback;
        }

        var length = split.HeaderEnd + split.SeparatorLength;
        return raw[..length];
    }

    private static byte[] ExtractTextBytes(byte[] raw)
    {
        var split = FindHeaderBodySplit(raw);
        return split.BodyStart < 0 ? Array.Empty<byte>() : raw[split.BodyStart..];
    }

    private static async Task<(long HeaderEnd, long BodyStart)> FindHeaderBodySplitAsync(
        Stream source, CancellationToken cancellationToken)
    {
        source.Position = 0;
        var buffer = new byte[64 * 1024];
        long position = 0;
        byte previous = 0;
        byte beforePrevious = 0;
        byte thirdPrevious = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                var current = buffer[index];
                if (thirdPrevious == '\r' && beforePrevious == '\n' && previous == '\r' && current == '\n')
                    return (position + 1, position + 1);
                if (previous == '\n' && current == '\n')
                    return (position + 1, position + 1);
                thirdPrevious = beforePrevious;
                beforePrevious = previous;
                previous = current;
                position++;
            }
        }
        return (-1, -1);
    }

    private static (int HeaderEnd, int BodyStart, int SeparatorLength) FindHeaderBodySplit(byte[] raw)
    {
        for (var index = 0; index <= raw.Length - 4; index++)
        {
            if (raw[index] == '\r'
                && raw[index + 1] == '\n'
                && raw[index + 2] == '\r'
                && raw[index + 3] == '\n')
            {
                return (index, index + 4, 4);
            }
        }

        for (var index = 0; index <= raw.Length - 2; index++)
        {
            if (raw[index] == '\n' && raw[index + 1] == '\n')
            {
                return (index, index + 2, 2);
            }
        }

        return (-1, -1, 0);
    }

    private static int CountLines(byte[] value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        var lines = 0;
        foreach (var character in value)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        return value[^1] == '\n' ? lines : lines + 1;
    }

    private static string NString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "NIL" : Quote(value);

    internal async Task ExpungeAsync(
        ImapConnection connection,
        ImapSessionState state,
        CancellationToken cancellationToken)
    {
        var expunged = await WithMailboxServiceAsync(service =>
            service.ExpungeAsync(state.MailboxId!.Value, cancellationToken));
        foreach (var item in expunged.OrderByDescending(x => x.Sequence))
        {
            await connection.WriteLineAsync($"* {item.Sequence} EXPUNGE", cancellationToken);
        }
    }

    private async Task StoreAsync(
        ImapConnection connection,
        ImapSessionState state,
        string arguments,
        bool useUid,
        CancellationToken cancellationToken)
    {
        var tokens = ImapTokenizer.Tokenize(arguments);
        if (tokens.Count < 3) throw new FormatException("STORE requires sequence set, operation, and flags.");
        var silent = tokens[1].EndsWith(".SILENT", StringComparison.OrdinalIgnoreCase);
        var operation = tokens[1].Replace(".SILENT", string.Empty, StringComparison.OrdinalIgnoreCase);
        await WithMailboxServiceAsync(service =>
            service.StoreFlagsAsync(
                state.MailboxId!.Value,
                tokens[0],
                useUid,
                operation,
                ParseFlags(tokens[2]),
                cancellationToken));
        if (!silent)
        {
            await FetchAsync(connection, state, $"{tokens[0]} (FLAGS UID)", "local", useUid, cancellationToken);
        }
    }

    internal async Task<CopyMoveResult> CopyMoveAsync(
        ImapConnection connection,
        ImapSessionState state,
        string arguments,
        bool useUid,
        bool move,
        CancellationToken cancellationToken)
    {
        var tokens = ImapTokenizer.Tokenize(arguments);
        if (tokens.Count != 2) throw new FormatException("COPY/MOVE requires sequence set and destination.");
        var result = await WithMailboxServiceAsync(service =>
            service.CopyAsync(state.MailboxId!.Value, tokens[0], useUid, tokens[1], move, cancellationToken));
        if (move)
        {
            foreach (var item in result.ExpungedItems.OrderByDescending(x => x.Sequence))
            {
                await connection.WriteLineAsync($"* {item.Sequence} EXPUNGE", cancellationToken);
            }
        }

        return result;
    }

    private async Task<T> WithMailboxServiceAsync<T>(Func<IMailboxService, Task<T>> action)
    {
        using var scope = scopeFactory.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IMailboxService>());
    }

    private async Task WithMailboxServiceAsync(Func<IMailboxService, Task> action)
    {
        using var scope = scopeFactory.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<IMailboxService>());
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

    private static IReadOnlyCollection<string> ParseFlags(string value) =>
        value.Trim().Trim('(', ')').Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static Task OkAsync(ImapConnection connection, string tag, string message, CancellationToken cancellationToken) =>
        connection.WriteLineAsync($"{tag} OK {message}", cancellationToken);

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string FormatSequenceSet(IReadOnlyList<long> values) => string.Join(',', values);

    internal static FormatException MalformedAppend() => new("Malformed APPEND command.");

    private static string DecodeMailboxName(string mailboxName)
    {
        if (mailboxName.IndexOf('&') < 0)
        {
            return mailboxName;
        }

        var result = new StringBuilder(mailboxName.Length);
        var index = 0;
        while (index < mailboxName.Length)
        {
            var character = mailboxName[index];
            if (character != '&')
            {
                result.Append(character);
                index++;
                continue;
            }

            var terminator = mailboxName.IndexOf('-', index + 1);
            if (terminator < 0)
            {
                throw MalformedAppend();
            }

            if (terminator == index + 1)
            {
                result.Append('&');
                index = terminator + 1;
                continue;
            }

            var encoded = mailboxName[(index + 1)..terminator].Replace(',', '/');
            var padding = encoded.Length % 4;
            if (padding != 0)
            {
                encoded = encoded.PadRight(encoded.Length + 4 - padding, '=');
            }

            try
            {
                var bytes = Convert.FromBase64String(encoded);
                if (bytes.Length % 2 != 0)
                {
                    throw MalformedAppend();
                }

                result.Append(Encoding.BigEndianUnicode.GetString(bytes));
            }
            catch (FormatException)
            {
                throw MalformedAppend();
            }

            index = terminator + 1;
        }

        return result.ToString();
    }

    private static string SpecialUseAttributes(string mailboxName)
    {
        var attributes = new List<string> { "\\HasNoChildren" };
        if (mailboxName.Equals("Sent", StringComparison.OrdinalIgnoreCase)
            || mailboxName.Equals("Sent Messages", StringComparison.OrdinalIgnoreCase)
            || mailboxName.Equals("Sent Items", StringComparison.OrdinalIgnoreCase))
        {
            attributes.Add("\\Sent");
        }
        else if (mailboxName.Equals("Drafts", StringComparison.OrdinalIgnoreCase))
        {
            attributes.Add("\\Drafts");
        }
        else if (mailboxName.Equals("Trash", StringComparison.OrdinalIgnoreCase)
                 || mailboxName.Equals("Deleted Items", StringComparison.OrdinalIgnoreCase))
        {
            attributes.Add("\\Trash");
        }
        else if (mailboxName.Equals("Junk", StringComparison.OrdinalIgnoreCase)
                 || mailboxName.Equals("Spam", StringComparison.OrdinalIgnoreCase))
        {
            attributes.Add("\\Junk");
        }
        else if (mailboxName.Equals("Archives", StringComparison.OrdinalIgnoreCase)
                 || mailboxName.Equals("Archive", StringComparison.OrdinalIgnoreCase))
        {
            attributes.Add("\\Archive");
        }

        return string.Join(' ', attributes);
    }

    private static AppendLiteralMarker? TryParseSoleLiteralMarker(string value)
    {
        var match = SoleLiteralRegex().Match(value);
        return match.Success ? ReadLiteralMarker(match) : null;
    }

    private static bool TryExpandInlineMailboxLiteral(
        string arguments,
        out string expandedArguments,
        out string mailboxLiteralKind)
    {
        expandedArguments = arguments;
        mailboxLiteralKind = "none";
        var match = InlineLeadingLiteralRegex().Match(arguments);
        if (!match.Success) return false;
        var marker = ReadLiteralMarker(match);
        var literalStart = match.Index + match.Length;
        if (!TryReadUtf8Text(arguments, literalStart, marker.Length, out var mailboxName, out var literalEnd))
        {
            throw MalformedAppend();
        }

        expandedArguments = Quote(mailboxName) + arguments[literalEnd..];
        mailboxLiteralKind = marker.Binary ? "binary-mailbox" : "mailbox";
        return true;
    }

    private static bool TryReadUtf8Text(
        string value,
        int start,
        long byteLength,
        out string text,
        out int end)
    {
        if (byteLength > int.MaxValue)
        {
            text = string.Empty;
            end = start;
            return false;
        }

        var bytes = new List<byte>((int)byteLength);
        var index = start;
        while (index < value.Length && bytes.Count < byteLength)
        {
            var scalar = char.ConvertToUtf32(value, index);
            var current = char.ConvertFromUtf32(scalar);
            var encoded = Encoding.UTF8.GetBytes(current);
            if (bytes.Count + encoded.Length > byteLength)
            {
                text = string.Empty;
                end = start;
                return false;
            }

            bytes.AddRange(encoded);
            index += current.Length;
        }

        if (bytes.Count != byteLength)
        {
            text = string.Empty;
            end = start;
            return false;
        }

        text = Encoding.UTF8.GetString(bytes.ToArray());
        end = index;
        return true;
    }

    private static AppendLiteralMarker ReadLiteralMarker(Match match)
    {
        if (!long.TryParse(match.Groups["length"].Value, CultureInfo.InvariantCulture, out var length))
        {
            throw MalformedAppend();
        }

        return new AppendLiteralMarker(
            length,
            match.Groups["plus"].Success,
            match.Groups["binary"].Success);
    }

    private static bool TryParseInternalDate(string value, out DateTimeOffset internalDate)
    {
        var normalized = InternalDateZoneRegex().Replace(value, " $1:$2");
        return DateTimeOffset.TryParseExact(
            normalized,
            ["d-MMM-yyyy HH:mm:ss zzz", "dd-MMM-yyyy HH:mm:ss zzz"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out internalDate);
    }

    private static void RequireAuthenticated(ImapSessionState state)
    {
        if (state.AccountId is null) throw new InvalidOperationException("Authenticate first.");
    }

    private static void RequireNotAuthenticated(ImapSessionState state)
    {
        if (state.AccountId is not null) throw new InvalidOperationException("Already authenticated.");
    }

    private static void RequireSelected(ImapSessionState state)
    {
        RequireAuthenticated(state);
        if (state.MailboxId is null) throw new InvalidOperationException("Select a mailbox first.");
    }

    private static void RequireWritable(ImapSessionState state)
    {
        if (state.ReadOnly) throw new InvalidOperationException("Mailbox is read-only.");
    }

    [GeneratedRegex(@"(?<binary>~)?\{(?<length>\d+)(?<plus>\+)?\}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralRegex();

    [GeneratedRegex(@"^\s*(?<binary>~)?\{(?<length>\d+)(?<plus>\+)?\}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SoleLiteralRegex();

    [GeneratedRegex(@"^\s*(?<binary>~)?\{(?<length>\d+)(?<plus>\+)?\}\s*(?:\r\n|\n|\r)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineLeadingLiteralRegex();

    [GeneratedRegex(@"\((?<fields>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderFieldsRegex();

    [GeneratedRegex(@" ([+-]\d{2})(\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex InternalDateZoneRegex();
}

internal sealed record ImapAppendRequest(
    string MailboxName,
    IReadOnlyCollection<string> Flags,
    DateTimeOffset? InternalDate,
    long LiteralLength,
    bool NonSynchronizing,
    bool BinaryLiteral,
    string MailboxLiteralKind,
    bool Utf8Wrapped);

internal sealed record AppendLiteralMarker(long Length, bool NonSynchronizing, bool Binary);

internal sealed record ImapFetchResponsePart(
    string Name, byte[]? Bytes, Stream? Source = null, long Offset = 0,
    long Length = 0, long StreamBytes = 0, long SyntheticOffset = 0)
{
    public static ImapFetchResponsePart Atom(string value) => new(value, null);
    public static ImapFetchResponsePart Literal(string name, byte[] value) => new(name, value);
    public static ImapFetchResponsePart StreamLiteral(string name, Stream source, long offset, long length,
        long? streamBytes = null, long syntheticOffset = 0) =>
        new(name, null, source, offset, length, streamBytes ?? length, syntheticOffset);
}

internal sealed record ImapBodySectionRequest(string Section, bool Peek, int? Offset = null, int? Length = null)
{
    public string ResponseName => Offset is null
        ? $"BODY[{NormalizeSectionName(Section)}]"
        : $"BODY[{NormalizeSectionName(Section)}]<{Offset.Value}>";

    private static string NormalizeSectionName(string section)
    {
        var normalized = section.Trim().ToUpperInvariant();
        if (normalized.StartsWith("HEADER.FIELDS", StringComparison.Ordinal)
            && normalized.Contains('(', StringComparison.Ordinal))
        {
            var start = normalized.IndexOf('(');
            var end = normalized.LastIndexOf(')');
            if (end > start)
            {
                var fields = normalized[(start + 1)..end]
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return $"HEADER.FIELDS ({string.Join(' ', fields)})";
            }
        }

        return normalized;
    }
}

internal sealed partial class ImapFetchRequest
{
    private ImapFetchRequest()
    {
    }

    public bool Uid { get; private init; }
    public bool Flags { get; private init; }
    public bool Rfc822Size { get; private init; }
    public bool InternalDate { get; private init; }
    public bool Envelope { get; private init; }
    public bool Body { get; private init; }
    public bool BodyStructure { get; private init; }
    public bool Rfc822 { get; private init; }
    public IReadOnlyList<ImapBodySectionRequest> BodySections { get; private init; } = [];
    public bool MarkSeen => Rfc822 || BodySections.Any(x => !x.Peek);
    public bool RequiresMessage => Envelope || Body || BodyStructure || Rfc822 || BodySections.Count > 0;
    public string LogSummary
    {
        get
        {
            var parts = new List<string>();
            if (Uid) parts.Add("UID");
            if (Flags) parts.Add("FLAGS");
            if (Rfc822Size) parts.Add("RFC822.SIZE");
            if (InternalDate) parts.Add("INTERNALDATE");
            if (Envelope) parts.Add("ENVELOPE");
            if (Body) parts.Add("BODY");
            if (BodyStructure) parts.Add("BODYSTRUCTURE");
            if (Rfc822) parts.Add("RFC822");
            parts.AddRange(BodySections.Select(x => x.ResponseName));
            return string.Join(',', parts);
        }
    }

    public static ImapFetchRequest Parse(string dataItems)
    {
        var normalized = dataItems.Trim();
        var macro = normalized.Trim('(', ')').Trim().ToUpperInvariant();
        var all = macro == "ALL";
        var fast = macro == "FAST";
        var full = macro == "FULL";
        var upper = normalized.ToUpperInvariant();
        var sections = BodySectionRegex().Matches(normalized)
            .Select(match => new ImapBodySectionRequest(
                match.Groups["section"].Value,
                match.Groups["peek"].Success,
                match.Groups["offset"].Success ? int.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture) : null,
                match.Groups["length"].Success ? int.Parse(match.Groups["length"].Value, CultureInfo.InvariantCulture) : null))
            .ToList();
        if (ContainsFetchItem(upper, "RFC822.HEADER"))
        {
            sections.Add(new ImapBodySectionRequest("HEADER", Peek: true));
        }

        if (ContainsFetchItem(upper, "RFC822.TEXT"))
        {
            sections.Add(new ImapBodySectionRequest("TEXT", Peek: false));
        }

        return new ImapFetchRequest
        {
            Uid = ContainsFetchItem(upper, "UID"),
            Flags = all || fast || full || ContainsFetchItem(upper, "FLAGS"),
            Rfc822Size = all || fast || full || ContainsFetchItem(upper, "RFC822.SIZE"),
            InternalDate = all || fast || full || ContainsFetchItem(upper, "INTERNALDATE"),
            Envelope = all || full || ContainsFetchItem(upper, "ENVELOPE"),
            Body = full || ContainsBareBody(upper),
            BodyStructure = ContainsFetchItem(upper, "BODYSTRUCTURE"),
            Rfc822 = ContainsFetchItem(upper, "RFC822"),
            BodySections = sections
        };
    }

    private static bool ContainsFetchItem(string upper, string item) =>
        Regex.IsMatch(
            upper,
            $@"(^|[\s(]){Regex.Escape(item)}($|[\s)])",
            RegexOptions.CultureInvariant);

    private static bool ContainsBareBody(string upper) =>
        Regex.IsMatch(
            upper,
            @"(^|[\s(])BODY($|[\s)])",
            RegexOptions.CultureInvariant);

    [GeneratedRegex(@"BODY(?<peek>\.PEEK)?\[(?<section>[^\]]*)\](?:<(?<offset>\d+)(?:\.(?<length>\d+))?>)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BodySectionRegex();
}

internal sealed class ImapAppendArgumentReader(string input)
{
    private int _index;

    public bool End => _index >= input.Length;
    public char Current => End ? '\0' : input[_index];

    public void SkipWhitespace()
    {
        while (!End && char.IsWhiteSpace(Current))
        {
            _index++;
        }
    }

    public string ReadAString()
    {
        SkipWhitespace();
        if (End) throw ImapServerService.MalformedAppend();
        return Current == '"' ? ReadQuotedString() : ReadAtom();
    }

    public string ReadList()
    {
        SkipWhitespace();
        if (End || Current != '(') throw ImapServerService.MalformedAppend();

        var start = _index;
        var depth = 0;
        var quoted = false;
        var escaped = false;
        while (!End)
        {
            var character = input[_index++];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (quoted && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted) continue;
            if (character == '(')
            {
                depth++;
                continue;
            }

            if (character == ')')
            {
                depth--;
                if (depth < 0) throw ImapServerService.MalformedAppend();
                if (depth == 0) return input[start.._index];
            }
        }

        throw ImapServerService.MalformedAppend();
    }

    public void ReadExpected(char expected)
    {
        if (End || Current != expected)
        {
            throw ImapServerService.MalformedAppend();
        }

        _index++;
    }

    private string ReadQuotedString()
    {
        _index++;
        var value = new StringBuilder();
        var escaped = false;
        while (!End)
        {
            var character = input[_index++];
            if (escaped)
            {
                value.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                return value.ToString();
            }

            value.Append(character);
        }

        throw ImapServerService.MalformedAppend();
    }

    private string ReadAtom()
    {
        var start = _index;
        while (!End && !char.IsWhiteSpace(Current))
        {
            if (Current is '(' or ')') throw ImapServerService.MalformedAppend();
            _index++;
        }

        if (_index == start) throw ImapServerService.MalformedAppend();
        return input[start.._index];
    }
}

internal sealed class ImapSessionState
{
    public Guid? AccountId { get; set; }
    public string? UserName { get; set; }
    public Guid? MailboxId { get; set; }
    public string? MailboxName { get; set; }
    public bool ReadOnly { get; set; }
    public bool TlsUpgradeRequested { get; set; }
    public long Exists { get; set; }
    public long UidNext { get; set; }
    public long HighestModSeq { get; set; }

    public void Deselect()
    {
        MailboxId = null;
        MailboxName = null;
        ReadOnly = false;
        Exists = 0;
        UidNext = 0;
        HighestModSeq = 0;
    }
}

internal static class ImapTokenizer
{
    public static IReadOnlyList<string> Tokenize(string input)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var escaped = false;
        var tokenStarted = false;
        var parenthesisDepth = 0;
        foreach (var character in input)
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                tokenStarted = true;
                continue;
            }

            if (quoted && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                tokenStarted = true;
                continue;
            }

            if (!quoted)
            {
                if (character == '(')
                {
                    parenthesisDepth++;
                    current.Append(character);
                    tokenStarted = true;
                    continue;
                }

                if (character == ')')
                {
                    parenthesisDepth--;
                    if (parenthesisDepth < 0) throw new FormatException("Unbalanced quoted string or list.");
                    current.Append(character);
                    tokenStarted = true;
                    continue;
                }

                if (char.IsWhiteSpace(character) && parenthesisDepth == 0)
                {
                    if (tokenStarted)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                        tokenStarted = false;
                    }
                    continue;
                }
            }

            current.Append(character);
            tokenStarted = true;
        }

        if (quoted || escaped || parenthesisDepth != 0) throw new FormatException("Unbalanced quoted string or list.");
        if (tokenStarted) result.Add(current.ToString());
        return result;
    }

    public static string Unquote(string value) => Tokenize(value).Single();
}

internal sealed class ImapConnection(Stream stream, TimeSpan timeout, long maximumLiteralBytes) : IAsyncDisposable
{
    private Stream _stream = stream;
    private readonly BufferedProtocolReader _reader = new(stream);

    public Stream Detach()
    {
        _reader.DiscardBufferedPlaintext();
        return _stream;
    }
    public void Attach(Stream stream)
    {
        _stream = stream;
        _reader.Attach(stream);
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await _reader.ReadLineAsync(65_536, false, timeout, cancellationToken);
        return line is null ? null : Encoding.UTF8.GetString(line.Value.Span);
    }

    public async Task<Stream> ReadLiteralAsync(long length, CancellationToken cancellationToken)
    {
        if (length < 0 || length > maximumLiteralBytes)
            throw new InvalidDataException($"Literal exceeds the {maximumLiteralBytes} byte limit.");
        var path = Path.Combine(Path.GetTempPath(), $"mailserver-imap-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await _reader.CopyExactlyAsync(output, length, timeout, cancellationToken);
            }

            return new DeleteOnDisposeImapFileStream(path);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public async Task<string> ReadLiteralTextAsync(long length, CancellationToken cancellationToken)
    {
        await using var literal = await ReadLiteralAsync(length, cancellationToken);
        using var memory = new MemoryStream();
        await literal.CopyToAsync(memory, cancellationToken);
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    public Task WriteLineAsync(string value, CancellationToken cancellationToken) =>
        WriteAsync(value + "\r\n", cancellationToken);

    public async Task WriteAsync(string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public async Task WriteBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public async Task CopyFromAsync(Stream source, CancellationToken cancellationToken)
    {
        await source.CopyToAsync(_stream, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        return _stream.DisposeAsync();
    }
}

internal sealed class DeleteOnDisposeImapFileStream(string path)
    : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
