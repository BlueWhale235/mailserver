using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MailServer.Infrastructure;

/// <summary>Owns background work independently of the lazily resolved application services.</summary>
public static class BackgroundServiceRegistration
{
    public static IServiceCollection AddMailServerBackgroundServices(
        this IServiceCollection services, IConfiguration configuration, ResourceRuntime runtime)
    {
        var role = runtime.Role;
        if (runtime.Node.Enabled) services.AddHostedService(provider => provider.GetRequiredService<NodeStatusService>());
        services.AddHostedService<AdaptiveResourceHostedService>();
        if (configuration.GetValue("BackgroundServices:ProcessWakeEnabled", !runtime.Node.Enabled))
            services.AddHostedService<ProcessWakeService>();

        services.AddHostedService(provider => provider.GetRequiredService<LocalLogWriter>());
        services.AddSingleton<LocalLogMaintenanceLease>();
        services.AddHostedService<LocalLogRetentionService>();
        services.AddHostedService<PerformanceStorage>();
        if (configuration.GetValue("EfficiencyDiagnostics:Enabled", false))
            services.AddHostedService<EfficiencyRuntimeSampler>();

        if (role is "api" or "smtp" or "worker" or "all")
        {
            services.AddSingleton(provider => new RelayRootService(
                provider.GetRequiredService<RelaySettingsService>(),
                provider.GetRequiredService<RelayIdentity>(),
                provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RelayRootService>>(), role));
            services.AddHostedService(provider => provider.GetRequiredService<RelayRootService>());
        }
        if (role is "smtp" or "imap" or "all")
            services.AddHostedService(provider => provider.GetRequiredService<DomainCertificateProvider>());
        if (runtime.UsesCache || runtime.Node.Enabled)
            services.AddHostedService<ConfigurationCacheMonitor>();

        if (role is "worker" or "all")
        {
            services.AddHostedService<MessageSearchBackfillService>();
            services.AddHostedService(provider => provider.GetRequiredService<TlsEventWriter>());
            services.AddHostedService<TlsReportBackgroundService>();
            services.AddHostedService<MaintenanceBackgroundService>();
        }
        // A standalone protocol host can explicitly own Sent copies without a delivery worker.
        if (configuration.GetValue("BackgroundServices:SentCopiesEnabled", role is "worker" or "all"))
            services.AddHostedService<SentCopyBackgroundService>();
        return services;
    }
}
