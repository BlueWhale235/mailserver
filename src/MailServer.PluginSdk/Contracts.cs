using Microsoft.Extensions.DependencyInjection;

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

public sealed record SmtpSessionContext(string RemoteAddress, string? HeloName, string? AuthenticatedUser, bool IsTls);
public sealed record MessageAcceptanceContext(string Sender, IReadOnlyList<string> Recipients, string ContentKey, long Size);
public sealed record DeliveryContext(Guid JobId, string Sender, string Recipient, int Attempt);
public sealed record ImapSessionContext(string RemoteAddress, string? AuthenticatedUser, bool IsTls);
public sealed record AuditContext(string Actor, string Action, string Resource, string? Detail);

public interface ISmtpSessionMiddleware
{
    Task<PluginDecision> InvokeAsync(SmtpSessionContext context, CancellationToken cancellationToken);
}

public interface IMessageAcceptanceMiddleware
{
    Task<PluginDecision> InvokeAsync(MessageAcceptanceContext context, CancellationToken cancellationToken);
}

public interface IDeliveryMiddleware
{
    Task<PluginDecision> InvokeAsync(DeliveryContext context, CancellationToken cancellationToken);
}

public interface IImapSessionMiddleware
{
    Task<PluginDecision> InvokeAsync(ImapSessionContext context, CancellationToken cancellationToken);
}

public interface IAuditMiddleware
{
    Task InvokeAsync(AuditContext context, CancellationToken cancellationToken);
}

public interface IMailServerPlugin
{
    void ConfigureServices(IServiceCollection services);
}

public sealed class PluginManifest
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string ContractVersion { get; init; }
    public required string EntryAssembly { get; init; }
    public int Order { get; init; }
    public string[] Hosts { get; init; } = [];
    public int TimeoutMilliseconds { get; init; } = 5000;
    public string FailureMode { get; init; } = "continue";
}
