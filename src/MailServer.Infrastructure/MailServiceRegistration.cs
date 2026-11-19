using DnsClient;
using MailServer.Domain;
using MailServer.PluginSdk;
using Microsoft.Extensions.DependencyInjection;

namespace MailServer.Infrastructure;

/// <summary>Application modules remain lazy singletons, shared by the combined host and plugin adapters.</summary>
internal static class MailServiceRegistration
{
    public static void AddMailMessageServices(this IServiceCollection services)
    {
        services.AddMessageStorage();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IMimeInspectionService, MimeInspectionService>();
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddSingleton<IAccountAuthenticator, AccountAuthenticator>();
        services.AddSingleton<ISmtpSenderAuthorizer, SmtpSenderAuthorizer>();
        services.AddSingleton<IInboundMessageService, InboundMessageService>();
        services.AddSingleton<IDeliveryQueue, DeliveryQueue>();
        services.AddSingleton<IOutboundTransport, DirectMxTransport>();
        services.AddSingleton<IMailboxService, MailboxService>();
        services.AddSingleton<MessageFetchMetadataCache>();
        services.AddSingleton<MailboxFolderManagementService>();
        services.AddSingleton(_ => new LookupClient(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(5), Retries = 2 }));
        services.AddSingleton<SystemSettingsService>();
        services.AddSingleton<RelayIdentity>();
        services.AddSingleton<RelaySettingsService>();
        services.AddSingleton<DomainCertificateProvider>();
        services.AddSingleton<MailRuleService>();
        services.AddSingleton<ConfigurationSnapshotCache>();
        services.AddSingleton<ConfigurationVersionService>();
        services.AddSingleton<SrsService>();
        services.AddSingleton<SentCopyService>();
        services.AddSingleton<IpFirewallService>();
        services.AddSingleton<TlsEventWriter>();
        services.AddSingleton<TransportSecurityPolicyService>();
        services.AddSingleton<IQuotaLedgerService, QuotaLedgerService>();
        services.AddSingleton<QuarantineService>();
        services.AddSingleton<IQuarantineService>(provider => provider.GetRequiredService<QuarantineService>());
    }

    public static void AddMailManagementServices(this IServiceCollection services)
    {
        services.AddSingleton<DnsDiagnosticService>();
        services.AddSingleton<AuditLogService>();
        services.AddSingleton<PermissionService>();
        services.AddSingleton<DomainCertificateService>();
        services.AddSingleton<DomainDnsService>();
        services.AddSingleton<AdminApplicationService>();
        services.AddSingleton<OperationsMailService>();
        services.AddSingleton<AccountDirectoryService>();
    }

    public static void AddMailMaintenanceServices(this IServiceCollection services)
    {
        services.AddSingleton<IQuotaReconciliationService, QuotaReconciliationService>();
        services.AddSingleton<CleanupQueueService>();
        services.AddSingleton<ICleanupQueueService>(provider => provider.GetRequiredService<CleanupQueueService>());
        services.AddSingleton<IRecycleBinService>(provider => provider.GetRequiredService<CleanupQueueService>());
        services.AddSingleton<IScheduledTaskHandler, CleanupTaskHandler>();
        services.AddSingleton<IScheduledTaskHandler, QuotaTaskHandler>();
        services.AddSingleton<IScheduledTaskHandler, CacheCleanupTaskHandler>();
        services.AddSingleton<IScheduledTaskHandler, OrphanStorageTaskHandler>();
        services.AddSingleton<IScheduledTaskHandler, StorageIntegrityTaskHandler>();
        services.AddSingleton<IScheduledTaskHandler, MailboxFolderDeletionTaskHandler>();
        services.AddSingleton<TaskExecutionService>();
        services.AddSingleton<ITaskScheduler>(provider => provider.GetRequiredService<TaskExecutionService>());
        services.AddSingleton<ITaskExecutionService>(provider => provider.GetRequiredService<TaskExecutionService>());
    }

    public static void AddMailPluginAdapters(this IServiceCollection services)
    {
        services.AddSingleton<PluginSettingsService>();
        services.AddSingleton<IPluginSettingsProvider>(provider => provider.GetRequiredService<PluginSettingsService>());
        services.AddSingleton<IPluginMailSender, PluginMailSender>();
        services.AddSingleton<IPluginDeliveryQueue, PluginDeliveryQueue>();
        services.AddSingleton<IPluginDirectory, PluginDirectory>();
        services.AddSingleton<IPluginMessageStore, PluginMessageStore>();
        services.AddSingleton<IPluginMailboxStore, PluginMailboxStore>();
        services.AddSingleton<IPluginAuditWriter, PluginAuditWriter>();
        services.AddSingleton<IPluginStateStore, PluginStateStore>();
        services.AddSingleton<IPluginQuarantineStore, PluginQuarantineStore>();
    }
}
