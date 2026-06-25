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
using Microsoft.Extensions.Options;

namespace MailServer.Imap;

public sealed class ImapServerOptions
{
    public const string SectionName = "Imap";
    public string HostName { get; set; } = "mail.localhost";
    public int CommandTimeoutSeconds { get; set; } = 1800;
    public int MaximumConnections { get; set; } = 200;
    public long MaximumAppendBytes { get; set; } = 52_428_800;
    public List<ImapEndpointOptions> Endpoints { get; set; } = [];
}

public sealed class ImapEndpointOptions
{
    public string Address { get; set; } = "0.0.0.0";
    public int Port { get; set; }
    public bool ImplicitTls { get; set; }
}

public sealed partial class ImapServerService(
    IServiceScopeFactory scopeFactory,
    IOptions<ImapServerOptions> options,
    IOptions<CertificatesOptions> certificates,
    ILogger<ImapServerService> logger) : BackgroundService
{
    private readonly ImapServerOptions _options = options.Value;
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _acceptLoops = [];
    private readonly CertificatesOptions _certificates = certificates.Value;
    private LoadedPemCertificate? _certificate;
    private SemaphoreSlim _connectionLimit = null!;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _certificate = LoadCertificate();
        _connectionLimit = new SemaphoreSlim(_options.MaximumConnections);
        foreach (var endpoint in _options.Endpoints)
        {
            if (endpoint.ImplicitTls && _certificate is null)
            {
                logger.LogWarning("IMAP endpoint {Port} is disabled because no TLS certificate is configured.", endpoint.Port);
                continue;
            }

            if (!IPAddress.TryParse(endpoint.Address, out var bindAddress))
            {
                throw new InvalidOperationException($"Invalid IMAP bind address: {endpoint.Address}");
            }

            var listener = new TcpListener(bindAddress, endpoint.Port);
            listener.Start();
            _listeners.Add(listener);
            _acceptLoops.Add(AcceptLoopAsync(listener, endpoint, stoppingToken));
            logger.LogInformation("IMAP listening on {Port}; implicit TLS={ImplicitTls}", endpoint.Port, endpoint.ImplicitTls);
        }

        return Task.WhenAll(_acceptLoops);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var listener in _listeners) listener.Stop();
        await base.StopAsync(cancellationToken);
        _certificate?.Dispose();
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
        ImapEndpointOptions endpoint,
        CancellationToken cancellationToken)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                var isTls = false;
                if (endpoint.ImplicitTls)
                {
                    stream = await UpgradeTlsAsync(stream, cancellationToken);
                    isTls = true;
                }

                await using var connection = new ImapConnection(
                    stream,
                    TimeSpan.FromSeconds(_options.CommandTimeoutSeconds),
                    _options.MaximumAppendBytes);
                await connection.WriteLineAsync(
                    "* OK [CAPABILITY IMAP4rev2 IMAP4rev1 UIDPLUS MOVE IDLE NAMESPACE ENABLE UTF8=ACCEPT] MailServer ready",
                    cancellationToken);
                var state = new ImapSessionState();

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await connection.ReadLineAsync(cancellationToken);
                    if (line is null) return;
                    var firstSpace = line.IndexOf(' ');
                    if (firstSpace <= 0)
                    {
                        await connection.WriteLineAsync("* BAD Invalid command", cancellationToken);
                        continue;
                    }

                    var tag = line[..firstSpace];
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
                            await connection.WriteLineAsync($"{tag} BAD UID requires a command", cancellationToken);
                            continue;
                        }

                        command = arguments[..uidSpace].ToUpperInvariant();
                        arguments = arguments[(uidSpace + 1)..].Trim();
                        useUid = true;
                    }

                    try
                    {
                        var keepRunning = await DispatchAsync(
                            connection,
                            state,
                            tag,
                            command,
                            arguments,
                            useUid,
                            remote,
                            isTls,
                            cancellationToken);
                        if (!keepRunning) return;
                        if (command == "STARTTLS" && state.TlsUpgradeRequested)
                        {
                            stream = await UpgradeTlsAsync(connection.Detach(), cancellationToken);
                            connection.Attach(stream);
                            isTls = true;
                            state = new ImapSessionState();
                        }
                    }
                    catch (KeyNotFoundException exception)
                    {
                        await connection.WriteLineAsync($"{tag} NO {Quote(exception.Message)}", cancellationToken);
                    }
                    catch (FormatException exception)
                    {
                        await connection.WriteLineAsync($"{tag} BAD {Quote(exception.Message)}", cancellationToken);
                    }
                    catch (InvalidOperationException exception)
                    {
                        await connection.WriteLineAsync($"{tag} NO {Quote(exception.Message)}", cancellationToken);
                    }
                }
            }
            catch (TimeoutException)
            {
                logger.LogInformation("IMAP client {Remote} timed out.", remote);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "IMAP session {Remote} failed.", remote);
            }
        }
    }

    private async Task<bool> DispatchAsync(
        ImapConnection connection,
        ImapSessionState state,
        string tag,
        string command,
        string arguments,
        bool useUid,
        string remote,
        bool isTls,
        CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "CAPABILITY":
                await connection.WriteLineAsync(
                    $"* CAPABILITY IMAP4rev2 IMAP4rev1 UIDPLUS MOVE IDLE NAMESPACE ENABLE UTF8=ACCEPT{(!isTls && _certificate is not null ? " STARTTLS" : string.Empty)}{(isTls ? " AUTH=PLAIN" : " LOGINDISABLED")}",
                    cancellationToken);
                await OkAsync(connection, tag, "CAPABILITY completed", cancellationToken);
                return true;
            case "NOOP":
                await OkAsync(connection, tag, "NOOP completed", cancellationToken);
                return true;
            case "LOGOUT":
                await connection.WriteLineAsync("* BYE Logging out", cancellationToken);
                await OkAsync(connection, tag, "LOGOUT completed", cancellationToken);
                return false;
            case "STARTTLS":
                RequireNotAuthenticated(state);
                if (isTls) throw new InvalidOperationException("TLS is already active.");
                if (_certificate is null) throw new InvalidOperationException("TLS is unavailable.");
                await OkAsync(connection, tag, "Begin TLS negotiation now", cancellationToken);
                state.TlsUpgradeRequested = true;
                return true;
            case "LOGIN":
                RequireNotAuthenticated(state);
                if (!isTls) throw new InvalidOperationException("TLS is required before authentication.");
                var loginTokens = ImapTokenizer.Tokenize(arguments);
                if (loginTokens.Count != 2) throw new FormatException("LOGIN requires user name and password.");
                await AuthenticateAsync(state, loginTokens[0], loginTokens[1], remote, isTls, cancellationToken);
                await OkAsync(connection, tag, "LOGIN completed", cancellationToken);
                return true;
            case "AUTHENTICATE":
                RequireNotAuthenticated(state);
                if (!isTls) throw new InvalidOperationException("TLS is required before authentication.");
                if (!arguments.Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only AUTHENTICATE PLAIN is supported.");
                await connection.WriteLineAsync("+", cancellationToken);
                var encoded = await connection.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException();
                var fields = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\0');
                if (fields.Length != 3) throw new FormatException("Invalid AUTHENTICATE PLAIN payload.");
                await AuthenticateAsync(state, fields[1], fields[2], remote, isTls, cancellationToken);
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
                await ListAsync(connection, state, command == "LSUB", cancellationToken);
                await OkAsync(connection, tag, $"{command} completed", cancellationToken);
                return true;
            case "SUBSCRIBE":
            case "UNSUBSCRIBE":
                RequireAuthenticated(state);
                await OkAsync(connection, tag, $"{command} completed", cancellationToken);
                return true;
            case "CREATE":
                RequireAuthenticated(state);
                await WithMailboxServiceAsync(service => service.CreateAsync(state.AccountId!.Value, ImapTokenizer.Unquote(arguments), cancellationToken));
                await OkAsync(connection, tag, "CREATE completed", cancellationToken);
                return true;
            case "DELETE":
                RequireAuthenticated(state);
                await WithMailboxServiceAsync(service => service.DeleteAsync(state.AccountId!.Value, ImapTokenizer.Unquote(arguments), cancellationToken));
                await OkAsync(connection, tag, "DELETE completed", cancellationToken);
                return true;
            case "SELECT":
            case "EXAMINE":
                RequireAuthenticated(state);
                await SelectAsync(connection, state, ImapTokenizer.Unquote(arguments), command == "EXAMINE", cancellationToken);
                await OkAsync(connection, tag, $"[{(command == "EXAMINE" ? "READ-ONLY" : "READ-WRITE")}] {command} completed", cancellationToken);
                return true;
            case "STATUS":
                RequireAuthenticated(state);
                await StatusAsync(connection, state, arguments, cancellationToken);
                await OkAsync(connection, tag, "STATUS completed", cancellationToken);
                return true;
            case "APPEND":
                RequireAuthenticated(state);
                await AppendAsync(connection, state, tag, arguments, cancellationToken);
                return true;
            case "CHECK":
                RequireSelected(state);
                await OkAsync(connection, tag, "CHECK completed", cancellationToken);
                return true;
            case "CLOSE":
                RequireSelected(state);
                if (!state.ReadOnly)
                {
                    await WithMailboxServiceAsync(service => service.ExpungeAsync(state.MailboxId!.Value, cancellationToken));
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
                await WithMailboxServiceAsync(service => service.ExpungeAsync(state.MailboxId!.Value, cancellationToken));
                await OkAsync(connection, tag, "EXPUNGE completed", cancellationToken);
                return true;
            case "SEARCH":
                RequireSelected(state);
                await SearchAsync(connection, state, arguments, useUid, cancellationToken);
                await OkAsync(connection, tag, "SEARCH completed", cancellationToken);
                return true;
            case "FETCH":
                RequireSelected(state);
                await FetchAsync(connection, state, arguments, useUid, cancellationToken);
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
                await CopyMoveAsync(state, arguments, useUid, command == "MOVE", cancellationToken);
                await OkAsync(connection, tag, $"{command} completed", cancellationToken);
                return true;
            case "IDLE":
                RequireAuthenticated(state);
                await connection.WriteLineAsync("+ idling", cancellationToken);
                var done = await connection.ReadLineAsync(cancellationToken);
                if (!string.Equals(done, "DONE", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Expected DONE.");
                await OkAsync(connection, tag, "IDLE terminated", cancellationToken);
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
        if (accountId is null) throw new InvalidOperationException("Authentication failed.");
        var pipeline = scope.ServiceProvider.GetService<PluginPipeline>();
        if (pipeline is not null)
        {
            var decision = await pipeline.OnImapSessionAsync(
                new ImapSessionContext(remote, address, isTls),
                cancellationToken);
            if (decision.Kind != PluginDecisionKind.Continue)
                throw new InvalidOperationException(decision.Message ?? "Rejected by policy.");
        }

        state.AccountId = accountId;
        state.UserName = address.ToLowerInvariant();
    }

    private async Task ListAsync(
        ImapConnection connection,
        ImapSessionState state,
        bool subscribedOnly,
        CancellationToken cancellationToken)
    {
        var mailboxes = await WithMailboxServiceAsync(service => service.ListAsync(state.AccountId!.Value, cancellationToken));
        foreach (var mailbox in mailboxes)
        {
            await connection.WriteLineAsync(
                $"* {(subscribedOnly ? "LSUB" : "LIST")} (\\HasNoChildren) \"/\" {Quote(mailbox.Name)}",
                cancellationToken);
        }
    }

    private async Task SelectAsync(
        ImapConnection connection,
        ImapSessionState state,
        string mailboxName,
        bool readOnly,
        CancellationToken cancellationToken)
    {
        var mailbox = await WithMailboxServiceAsync(service =>
            service.SelectAsync(state.AccountId!.Value, mailboxName, cancellationToken));
        if (mailbox is null) throw new KeyNotFoundException("Mailbox not found.");
        state.MailboxId = mailbox.Id;
        state.MailboxName = mailbox.Name;
        state.ReadOnly = readOnly;
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

    private async Task AppendAsync(
        ImapConnection connection,
        ImapSessionState state,
        string tag,
        string arguments,
        CancellationToken cancellationToken)
    {
        var match = LiteralRegex().Match(arguments);
        if (!match.Success) throw new FormatException("APPEND requires a literal.");
        var literalLength = long.Parse(match.Groups["length"].Value, CultureInfo.InvariantCulture);
        var nonSynchronizing = match.Groups["plus"].Success;
        var beforeLiteral = arguments[..match.Index].Trim();
        var tokens = ImapTokenizer.Tokenize(beforeLiteral);
        if (tokens.Count == 0) throw new FormatException("APPEND requires a mailbox.");
        var mailboxName = tokens[0];
        var flags = tokens.Count > 1 && tokens[1].StartsWith('(')
            ? ParseFlags(tokens[1])
            : [];
        if (!nonSynchronizing)
        {
            await connection.WriteLineAsync("+ Ready for literal data", cancellationToken);
        }

        await using var literal = await connection.ReadLiteralAsync(literalLength, cancellationToken);
        var item = await WithMailboxServiceAsync(service =>
            service.AppendAsync(state.AccountId!.Value, mailboxName, literal, flags, null, cancellationToken));
        await connection.ReadOptionalLineEndingAsync(cancellationToken);
        await connection.WriteLineAsync(
            $"{tag} OK [APPENDUID {DateTimeOffset.UtcNow.ToUnixTimeSeconds()} {item.Uid}] APPEND completed",
            cancellationToken);
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

    private async Task FetchAsync(
        ImapConnection connection,
        ImapSessionState state,
        string arguments,
        bool useUid,
        CancellationToken cancellationToken)
    {
        var separator = arguments.IndexOf(' ');
        if (separator < 0) throw new FormatException("FETCH requires sequence set and data items.");
        var sequenceSet = arguments[..separator];
        var dataItems = arguments[(separator + 1)..].ToUpperInvariant();
        var items = await WithMailboxServiceAsync(service =>
            service.FetchAsync(state.MailboxId!.Value, sequenceSet, useUid, cancellationToken));
        foreach (var item in items)
        {
            var fields = new List<string>();
            if (useUid || dataItems.Contains("UID", StringComparison.Ordinal)) fields.Add($"UID {item.Uid}");
            if (dataItems.Contains("FLAGS", StringComparison.Ordinal) || dataItems is "ALL" or "FAST" or "FULL")
                fields.Add($"FLAGS ({item.Flags})");
            if (dataItems.Contains("RFC822.SIZE", StringComparison.Ordinal) || dataItems is "ALL" or "FULL")
                fields.Add($"RFC822.SIZE {item.Size}");
            if (dataItems.Contains("INTERNALDATE", StringComparison.Ordinal) || dataItems is "ALL" or "FULL")
                fields.Add($"INTERNALDATE \"{item.InternalDate:dd-MMM-yyyy HH:mm:ss zzz}\"");
            fields.Add($"MODSEQ ({item.ModSeq})");
            var includeBody = dataItems.Contains("BODY[]", StringComparison.Ordinal)
                              || dataItems.Contains("BODY.PEEK[]", StringComparison.Ordinal)
                              || dataItems.Contains("RFC822", StringComparison.Ordinal);
            if (!includeBody)
            {
                await connection.WriteLineAsync($"* {item.Sequence} FETCH ({string.Join(' ', fields)})", cancellationToken);
                continue;
            }

            await using var body = await WithMailboxServiceAsync(service => service.OpenMessageAsync(item.Id, cancellationToken));
            await connection.WriteAsync(
                $"* {item.Sequence} FETCH ({string.Join(' ', fields)} BODY[] {{{item.Size}}}\r\n",
                cancellationToken);
            await connection.CopyFromAsync(body, cancellationToken);
            await connection.WriteLineAsync(")", cancellationToken);
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
            await FetchAsync(connection, state, $"{tokens[0]} (FLAGS UID)", useUid, cancellationToken);
        }
    }

    private async Task CopyMoveAsync(
        ImapSessionState state,
        string arguments,
        bool useUid,
        bool move,
        CancellationToken cancellationToken)
    {
        var tokens = ImapTokenizer.Tokenize(arguments);
        if (tokens.Count != 2) throw new FormatException("COPY/MOVE requires sequence set and destination.");
        await WithMailboxServiceAsync(service =>
            service.CopyAsync(state.MailboxId!.Value, tokens[0], useUid, tokens[1], move, cancellationToken));
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

    private static IReadOnlyCollection<string> ParseFlags(string value) =>
        value.Trim().Trim('(', ')').Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static Task OkAsync(ImapConnection connection, string tag, string message, CancellationToken cancellationToken) =>
        connection.WriteLineAsync($"{tag} OK {message}", cancellationToken);

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

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

    [GeneratedRegex(@"\{(?<length>\d+)(?<plus>\+)?\}\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex LiteralRegex();
}

internal sealed class ImapSessionState
{
    public Guid? AccountId { get; set; }
    public string? UserName { get; set; }
    public Guid? MailboxId { get; set; }
    public string? MailboxName { get; set; }
    public bool ReadOnly { get; set; }
    public bool TlsUpgradeRequested { get; set; }

    public void Deselect()
    {
        MailboxId = null;
        MailboxName = null;
        ReadOnly = false;
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
        var parenthesisDepth = 0;
        foreach (var character in input)
        {
            if (escaped)
            {
                current.Append(character);
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

            if (!quoted)
            {
                if (character == '(') parenthesisDepth++;
                if (character == ')') parenthesisDepth--;
                if (char.IsWhiteSpace(character) && parenthesisDepth == 0)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }
            }

            current.Append(character);
        }

        if (quoted || parenthesisDepth != 0) throw new FormatException("Unbalanced quoted string or list.");
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    public static string Unquote(string value) => Tokenize(value).Single();
}

internal sealed class ImapConnection(Stream stream, TimeSpan timeout, long maximumLiteralBytes) : IAsyncDisposable
{
    private Stream _stream = stream;
    private readonly byte[] _singleByte = new byte[1];

    public Stream Detach() => _stream;
    public void Attach(Stream stream) => _stream = stream;

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(128);
        while (bytes.Count <= 65_536)
        {
            var read = await _stream.ReadAsync(_singleByte, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken);
            if (read == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (_singleByte[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            bytes.Add(_singleByte[0]);
        }
        throw new InvalidDataException("IMAP command line is too long.");
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
                var buffer = new byte[64 * 1024];
                var remaining = length;
                while (remaining > 0)
                {
                    var read = await _stream.ReadAsync(
                        buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                        cancellationToken).AsTask().WaitAsync(timeout, cancellationToken);
                    if (read == 0) throw new EndOfStreamException();
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    remaining -= read;
                }
            }

            return new DeleteOnDisposeImapFileStream(path);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public async Task ReadOptionalLineEndingAsync(CancellationToken cancellationToken)
    {
        var first = await _stream.ReadAsync(_singleByte, cancellationToken);
        if (first == 0) return;
        if (_singleByte[0] == '\n') return;
        if (_singleByte[0] != '\r') throw new InvalidDataException("Expected line ending after literal.");
        var second = await _stream.ReadAsync(_singleByte, cancellationToken);
        if (second == 0 || _singleByte[0] != '\n') throw new InvalidDataException("Expected line ending after literal.");
    }

    public Task WriteLineAsync(string value, CancellationToken cancellationToken) =>
        WriteAsync(value + "\r\n", cancellationToken);

    public async Task WriteAsync(string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public async Task CopyFromAsync(Stream source, CancellationToken cancellationToken)
    {
        await source.CopyToAsync(_stream, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

internal sealed class DeleteOnDisposeImapFileStream(string path)
    : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
