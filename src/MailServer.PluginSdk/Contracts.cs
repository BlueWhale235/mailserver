using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace MailServer.PluginSdk;

public static class PluginContract
{
    public const string Version = "1.0";
}

public enum PluginDecisionKind
{
    Continue,
    Reject,
    Defer,
    Quarantine
}

public sealed record PluginDecision(PluginDecisionKind Kind, string? Message = null)
{
    public static PluginDecision Continue() => new(PluginDecisionKind.Continue);
}

public enum MailProtocol
{
    Smtp,
    Imap
}

public enum AuthenticationStatus
{
    NotHandled,
    Success,
    Failure
}

public sealed record AuthenticationResult(
    AuthenticationStatus Status,
    string? Identity = null,
    string? Message = null)
{
    public static AuthenticationResult NotHandled() => new(AuthenticationStatus.NotHandled);
    public static AuthenticationResult Success(string identity) => new(AuthenticationStatus.Success, identity);
    public static AuthenticationResult Failure(string? message = null) => new(AuthenticationStatus.Failure, null, message);
}

public sealed record ConnectionContext(
    MailProtocol Protocol,
    string RemoteAddress,
    bool IsTls,
    PluginSettingsSnapshot? Settings = null);

public sealed record TlsEstablishedContext(
    MailProtocol Protocol,
    string RemoteAddress,
    PluginSettingsSnapshot? Settings = null);

public sealed record AuthenticationContext(
    MailProtocol Protocol,
    string Mechanism,
    string? UserName,
    string? Secret,
    string RemoteAddress,
    bool IsTls,
    PluginSettingsSnapshot? Settings = null);

public sealed record SmtpSessionContext(string RemoteAddress, string? HeloName, string? AuthenticatedUser, bool IsTls);
public sealed record SmtpSenderContext(
    string Sender,
    string RemoteAddress,
    string? AuthenticatedUser,
    Guid? AuthenticatedAccountId,
    PluginSettingsSnapshot? Settings = null);
public sealed record SmtpRecipientContext(
    string Sender,
    string Recipient,
    IReadOnlyList<string> AcceptedRecipients,
    string RemoteAddress,
    string? AuthenticatedUser,
    Guid? AuthenticatedAccountId,
    PluginSettingsSnapshot? Settings = null);
public sealed record MessageAcceptanceContext(
    string Sender,
    IReadOnlyList<string> Recipients,
    string ContentKey,
    long Size,
    string RemoteAddress = "",
    PluginSettingsSnapshot? Settings = null);
public sealed record MessageContentContext(
    string Sender,
    IReadOnlyList<string> Recipients,
    string BodyType,
    long Size,
    Stream Content,
    PluginSettingsSnapshot Settings);
public sealed record DeliveryContext(Guid JobId, string Sender, string Recipient, int Attempt);
public sealed record MessageAcceptedContext(
    Guid MessageId,
    string Sender,
    IReadOnlyList<string> Recipients,
    string RemoteAddress,
    string? AuthenticatedUser,
    PluginSettingsSnapshot? Settings = null);
public sealed record DeliveryCompletedContext(
    Guid JobId,
    string Sender,
    string Recipient,
    int Attempt,
    bool Succeeded,
    bool PermanentFailure,
    string Response,
    string? RemoteHost,
    PluginSettingsSnapshot? Settings = null);
public sealed record ImapSessionContext(string RemoteAddress, string? AuthenticatedUser, bool IsTls);
public sealed record ImapCommandContext(
    string Command,
    string Arguments,
    Guid? AccountId,
    string? UserName,
    string? MailboxName,
    bool ReadOnly,
    string RemoteAddress,
    PluginSettingsSnapshot? Settings = null);
public sealed record AuditContext(string Actor, string Action, string Resource, string? Detail);

public interface IConnectionMiddleware
{
    Task<PluginDecision> InvokeAsync(ConnectionContext context, CancellationToken cancellationToken);
}

public interface ITlsEstablishedMiddleware
{
    Task InvokeAsync(TlsEstablishedContext context, CancellationToken cancellationToken);
}

public interface IAuthenticationMiddleware
{
    IReadOnlyCollection<string> GetMechanisms(MailProtocol protocol);
    Task<AuthenticationResult> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken);
}

public interface ISmtpSessionMiddleware
{
    Task<PluginDecision> InvokeAsync(SmtpSessionContext context, CancellationToken cancellationToken);
}

public interface ISmtpSenderMiddleware
{
    Task<PluginDecision> InvokeAsync(SmtpSenderContext context, CancellationToken cancellationToken);
}

public interface ISmtpRecipientMiddleware
{
    Task<PluginDecision> InvokeAsync(SmtpRecipientContext context, CancellationToken cancellationToken);
}

public interface IMessageAcceptanceMiddleware
{
    Task<PluginDecision> InvokeAsync(MessageAcceptanceContext context, CancellationToken cancellationToken);
}

public interface IMessageContentMiddleware
{
    Task<PluginDecision> InvokeAsync(MessageContentContext context, CancellationToken cancellationToken);
}

public interface IMessageAcceptedMiddleware
{
    Task InvokeAsync(MessageAcceptedContext context, CancellationToken cancellationToken);
}

public interface IDeliveryMiddleware
{
    Task<PluginDecision> InvokeAsync(DeliveryContext context, CancellationToken cancellationToken);
}

public interface IDeliveryCompletedMiddleware
{
    Task InvokeAsync(DeliveryCompletedContext context, CancellationToken cancellationToken);
}

public interface IImapSessionMiddleware
{
    Task<PluginDecision> InvokeAsync(ImapSessionContext context, CancellationToken cancellationToken);
}

public interface IImapCommandMiddleware
{
    Task<PluginDecision> InvokeAsync(ImapCommandContext context, CancellationToken cancellationToken);
}

public interface IAuditMiddleware
{
    Task InvokeAsync(AuditContext context, CancellationToken cancellationToken);
}

public interface IMailServerPlugin
{
    void ConfigureServices(IServiceCollection services);
}

public interface IPluginSettingsProvider
{
    Task<PluginSettingsSnapshot> GetAsync(string pluginId, CancellationToken cancellationToken);
}

public sealed record PluginSettingsSnapshot(
    bool Enabled,
    IReadOnlyDictionary<string, JsonElement> Values)
{
    public bool GetBoolean(string key, bool fallback = false) =>
        Values.TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    public int GetInteger(string key, int fallback = 0) =>
        Values.TryGetValue(key, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    public string GetString(string key, string fallback = "") =>
        Values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
}

public enum PluginSettingType
{
    Text,
    Integer,
    Boolean,
    Select,
    Multiselect,
    Password
}

public sealed class PluginSettingOption
{
    public required string Value { get; init; }
    public required string Label { get; init; }
}

public sealed class PluginSettingDefinition
{
    public required string Key { get; init; }
    public PluginSettingType Type { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public JsonElement DefaultValue { get; init; }
    public bool Required { get; init; }
    public int? Minimum { get; init; }
    public int? Maximum { get; init; }
    public string? Placeholder { get; init; }
    public PluginSettingOption[] Options { get; init; } = [];
}

public sealed class PluginManifest
{
    public required string Id { get; init; }
    public bool System { get; init; }
    public required string Version { get; init; }
    public required string ContractVersion { get; init; }
    public required string EntryAssembly { get; init; }
    public int Order { get; init; }
    public string[] Hosts { get; init; } = [];
    public int TimeoutMilliseconds { get; init; } = 5000;
    public string FailureMode { get; init; } = "continue";
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public PluginSettingDefinition[] Settings { get; init; } = [];
}
