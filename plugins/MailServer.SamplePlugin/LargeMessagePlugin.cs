using MailServer.PluginSdk;
using Microsoft.Extensions.DependencyInjection;

namespace MailServer.SamplePlugin;

public sealed class LargeMessagePlugin : IMailServerPlugin
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IMessageAcceptanceMiddleware, LargeMessageMiddleware>();
        services.AddSingleton<IAuthenticationMiddleware, ExampleBearerAuthenticationMiddleware>();
    }
}

public sealed class LargeMessageMiddleware : IMessageAcceptanceMiddleware
{
    private const long MaximumBytes = 10 * 1024 * 1024;

    public Task<PluginDecision> InvokeAsync(
        MessageAcceptanceContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            context.Size <= MaximumBytes
                ? PluginDecision.Continue()
                : new PluginDecision(PluginDecisionKind.Reject, "Message rejected by sample size policy."));
    }
}

public sealed class ExampleBearerAuthenticationMiddleware : IAuthenticationMiddleware
{
    public IReadOnlyCollection<string> GetMechanisms(MailProtocol protocol) => ["XOAUTH2", "OAUTHBEARER"];

    public Task<AuthenticationResult> AuthenticateAsync(
        AuthenticationContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expectedToken = context.Settings?.GetString("exampleBearerToken");
        var identity = context.Settings?.GetString("exampleIdentity");
        if (string.IsNullOrWhiteSpace(expectedToken) || string.IsNullOrWhiteSpace(identity))
            return Task.FromResult(AuthenticationResult.Failure("Example authentication plugin is not configured."));
        return Task.FromResult(
            string.Equals(context.Secret, expectedToken, StringComparison.Ordinal)
                ? AuthenticationResult.Success(identity)
                : AuthenticationResult.Failure("Bearer token is invalid."));
    }
}
