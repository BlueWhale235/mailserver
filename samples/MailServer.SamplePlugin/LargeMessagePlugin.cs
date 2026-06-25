using MailServer.PluginSdk;
using Microsoft.Extensions.DependencyInjection;

namespace MailServer.SamplePlugin;

public sealed class LargeMessagePlugin : IMailServerPlugin
{
    public void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IMessageAcceptanceMiddleware, LargeMessageMiddleware>();
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
