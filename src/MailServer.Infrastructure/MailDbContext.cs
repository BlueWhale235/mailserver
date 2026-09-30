using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MailServer.Infrastructure;

/// <summary>
/// 邮件系统的 EF Core 数据模型入口。领域服务应通过这里持久化状态，不直接拼接数据库表名。
/// </summary>
public sealed class MailDbContext(DbContextOptions<MailDbContext> options) : DbContext(options)
{
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        using var performance = PerformanceTelemetry.Stage("database.save-changes");
        using (PerformanceTelemetry.Stage("database.detect-changes"))
            ChangeTracker.DetectChanges();

        // Entries() and EF's SaveChanges both detect changes automatically. The explicit pass above
        // has already detected the inbound graph, so avoid scanning it twice more on the common path.
        var autoDetect = ChangeTracker.AutoDetectChangesEnabled;
        ChangeTracker.AutoDetectChangesEnabled = false;
        bool routingChanged;
        try
        {
            using (PerformanceTelemetry.Stage("database.prepare-entries"))
            {
                var entries = ChangeTracker.Entries().ToArray();
                foreach (var entry in entries.Where(x => x.State == EntityState.Added))
                {
                    if (entry.Entity is StoredMessage message) message.SourceTraceId ??= PerformanceTelemetry.TraceId;
                    if (entry.Entity is DeliveryJob job) job.SourceTraceId ??= PerformanceTelemetry.TraceId ?? job.Message?.SourceTraceId;
                }
                routingChanged = entries.Any(entry =>
                    entry.Entity switch
                    {
                        MailDomain => RoutingFieldsChanged(entry, nameof(MailDomain.Name), nameof(MailDomain.IsActive),
                            nameof(MailDomain.DeletedAt)),
                        MailAccount => RoutingFieldsChanged(entry, nameof(MailAccount.DomainId), nameof(MailAccount.LocalPart),
                            nameof(MailAccount.IsEnabled), nameof(MailAccount.DeletedAt)),
                        MailAlias => RoutingFieldsChanged(entry, nameof(MailAlias.DomainId), nameof(MailAlias.SourceLocalPart),
                            nameof(MailAlias.TargetAccountId), nameof(MailAlias.Mode), nameof(MailAlias.IsEnabled)),
                        _ => false
                    });
            }
            if (!routingChanged)
            {
                using var persist = PerformanceTelemetry.Stage("database.persist");
                return await base.SaveChangesAsync(cancellationToken);
            }
        }
        finally { ChangeTracker.AutoDetectChangesEnabled = autoDetect; }

        if (!Database.IsRelational() || Database.CurrentTransaction is not null)
        {
            await ConfigurationVersionService.BumpAsync(this, ConfigurationScopes.Routing, cancellationToken);
            using var persist = PerformanceTelemetry.Stage("database.persist");
            return await base.SaveChangesAsync(cancellationToken);
        }

        await using var transaction = await Database.BeginTransactionAsync(cancellationToken);
        var version = await ConfigurationVersionService.BumpAsync(this, ConfigurationScopes.Routing, cancellationToken);
        int saved;
        using (PerformanceTelemetry.Stage("database.persist"))
            saved = await base.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        ConfigurationSnapshotCache.NotifyRoutingCommitted(version);
        return saved;
    }

    public async Task NotifyRoutingCommittedAsync(CancellationToken cancellationToken)
    {
        var version = await ConfigurationVersions.AsNoTracking()
            .Where(x => x.Scope == ConfigurationScopes.Routing)
            .Select(x => (long?)x.Version).SingleOrDefaultAsync(cancellationToken);
        if (version is not null) ConfigurationSnapshotCache.NotifyRoutingCommitted(version.Value);
    }

    private static bool RoutingFieldsChanged(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry,
        params string[] fields) => entry.State is EntityState.Added or EntityState.Deleted
        || entry.State == EntityState.Modified && fields.Any(field => entry.Property(field).IsModified);

    public DbSet<MailDomain> Domains => Set<MailDomain>();
    public DbSet<DomainCertificate> DomainCertificates => Set<DomainCertificate>();
    public DbSet<DomainDnsSettings> DomainDnsSettings => Set<DomainDnsSettings>();
    public DbSet<MailAccount> Accounts => Set<MailAccount>();
    public DbSet<MailAlias> Aliases => Set<MailAlias>();
    public DbSet<MailRule> MailRules => Set<MailRule>();
    public DbSet<MailRuleCondition> MailRuleConditions => Set<MailRuleCondition>();
    public DbSet<MailRuleAction> MailRuleActions => Set<MailRuleAction>();
    public DbSet<DomainDnsCheck> DomainDnsChecks => Set<DomainDnsCheck>();
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<MailboxFolderDeletionJob> MailboxFolderDeletionJobs => Set<MailboxFolderDeletionJob>();
    public DbSet<StoredMessage> Messages => Set<StoredMessage>();
    public DbSet<StoredMessageRecipient> MessageRecipients => Set<StoredMessageRecipient>();
    public DbSet<MailboxMessage> MailboxMessages => Set<MailboxMessage>();
    public DbSet<DeliveryJob> DeliveryJobs => Set<DeliveryJob>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<SrsAddressMapping> SrsAddressMappings => Set<SrsAddressMapping>();
    public DbSet<SentCopyJob> SentCopyJobs => Set<SentCopyJob>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<PermissionGrant> PermissionGrants => Set<PermissionGrant>();
    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<FirewallRule> FirewallRules => Set<FirewallRule>();
    public DbSet<ConfigurationVersion> ConfigurationVersions => Set<ConfigurationVersion>();
    public DbSet<ConfigurationProcessStatus> ConfigurationProcessStatuses => Set<ConfigurationProcessStatus>();
    public DbSet<MtaStsPolicyCache> MtaStsPolicyCaches => Set<MtaStsPolicyCache>();
    public DbSet<TlsDeliveryEvent> TlsDeliveryEvents => Set<TlsDeliveryEvent>();
    public DbSet<TlsReportDelivery> TlsReportDeliveries => Set<TlsReportDelivery>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();
    public DbSet<TaskLease> TaskLeases => Set<TaskLease>();
    public DbSet<CleanupQueueItem> CleanupQueue => Set<CleanupQueueItem>();
    public DbSet<QuotaLedgerEntry> QuotaLedger => Set<QuotaLedgerEntry>();
    public DbSet<QuarantineRecord> QuarantineRecords => Set<QuarantineRecord>();
    public DbSet<PerformanceOperation> PerformanceOperations => Set<PerformanceOperation>();
    public DbSet<PerformanceMinute> PerformanceMinutes => Set<PerformanceMinute>();
    public DbSet<PerformanceSql> PerformanceSql => Set<PerformanceSql>();
    public DbSet<PerformanceProcess> PerformanceProcesses => Set<PerformanceProcess>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PerformanceOperation>(entity =>
        {
            entity.ToTable("performance_operations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TraceId).HasMaxLength(32);
            entity.Property(x => x.SourceTraceId).HasMaxLength(32);
            entity.Property(x => x.ProcessId).HasMaxLength(128);
            entity.Property(x => x.Module).HasMaxLength(32);
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.Status).HasMaxLength(16);
            entity.HasIndex(x => new { x.StartedAt, x.Id });
            entity.HasIndex(x => x.TraceId);
            entity.HasIndex(x => x.SourceTraceId);
            entity.HasIndex(x => x.MessageId);
            entity.HasIndex(x => x.DeliveryJobId);
        });
        modelBuilder.Entity<PerformanceMinute>(entity =>
        {
            entity.ToTable("performance_minutes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProcessId).HasMaxLength(128);
            entity.Property(x => x.Kind).HasMaxLength(16);
            entity.Property(x => x.Module).HasMaxLength(32);
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.HasIndex(x => new { x.Minute, x.Kind });
            entity.HasIndex(x => new { x.ProcessId, x.Minute, x.Kind, x.Module, x.Name }).IsUnique();
        });
        modelBuilder.Entity<PerformanceSql>(entity =>
        {
            entity.ToTable("performance_sql"); entity.HasKey(x => x.Fingerprint);
            entity.Property(x => x.Fingerprint).HasMaxLength(64);
            entity.Property(x => x.Provider).HasMaxLength(16);
            entity.Property(x => x.Template).HasMaxLength(2048);
            entity.HasIndex(x => x.LastSeenAt);
        });
        modelBuilder.Entity<PerformanceProcess>(entity =>
        {
            entity.ToTable("performance_processes"); entity.HasKey(x => x.ProcessId);
            entity.Property(x => x.ProcessId).HasMaxLength(128);
        });
        modelBuilder.Entity<StoredMessage>().Property(x => x.SourceTraceId).HasMaxLength(32);
        modelBuilder.Entity<DeliveryJob>().Property(x => x.SourceTraceId).HasMaxLength(32);
        if (Database.IsSqlite())
        {
            // SQLite 没有原生 DateTimeOffset 类型，统一转成 UTC ticks，保证三种数据库的时间比较语义接近。
            var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
                value => value.UtcDateTime.Ticks,
                value => new DateTimeOffset(value, TimeSpan.Zero));
            var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
                value => value.HasValue ? value.Value.UtcDateTime.Ticks : null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entity.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset))
                        property.SetValueConverter(dateTimeOffsetConverter);
                    else if (property.ClrType == typeof(DateTimeOffset?))
                        property.SetValueConverter(nullableDateTimeOffsetConverter);
                }
            }
        }

        modelBuilder.Entity<MailDomain>(entity =>
        {
            // 域名是租户边界；软删除过滤器避免历史邮件、队列和审计因直接级联删除而丢失。
            entity.ToTable("mail_domains");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => x.MailHostName).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(255);
            entity.Property(x => x.MailHostName).HasMaxLength(255);
            entity.Property(x => x.SmtpHostName).HasMaxLength(255);
            entity.Property(x => x.OutboundIpAddresses).HasMaxLength(1024);
            entity.Property(x => x.QuotaBytes).HasDefaultValue(10_737_418_240L);
            entity.Property(x => x.UsedBytes).HasDefaultValue(0L);
            entity.HasIndex(x => x.DeletedAt);
            entity.Property(x => x.DirectDeliverSameDomain).HasDefaultValue(true);
            entity.Property(x => x.DkimSelector).HasMaxLength(63);
            entity.Property(x => x.DkimPrivateKeyPath).HasMaxLength(2048);
            entity.Property(x => x.DkimPublicKey).HasMaxLength(4096);
            entity.HasQueryFilter(x => x.DeletedAt == null);
        });

        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.ToTable("system_settings");
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(128);
            entity.Property(x => x.Value).HasMaxLength(8192);
        });

        modelBuilder.Entity<ConfigurationVersion>(entity =>
        {
            entity.ToTable("configuration_versions");
            entity.HasKey(x => x.Scope);
            entity.Property(x => x.Scope).HasMaxLength(32);
        });

        modelBuilder.Entity<ConfigurationProcessStatus>(entity =>
        {
            entity.ToTable("configuration_process_statuses");
            entity.HasKey(x => x.ProcessId);
            entity.HasIndex(x => x.LastSeenAt);
            entity.Property(x => x.ProcessId).HasMaxLength(160);
            entity.Property(x => x.InstanceName).HasMaxLength(128);
            entity.Property(x => x.Roles).HasMaxLength(128);
            entity.Property(x => x.LastError).HasMaxLength(2048);
            entity.Property(x => x.ResourceStatusJson).HasMaxLength(8192);
        });

        modelBuilder.Entity<DomainCertificate>(entity =>
        {
            entity.ToTable("domain_certificates");
            entity.HasKey(x => x.DomainId);
            entity.Property(x => x.Source).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.CertificatePath).HasMaxLength(2048);
            entity.Property(x => x.PrivateKeyPath).HasMaxLength(2048);
            entity.Property(x => x.Subject).HasMaxLength(1024);
            entity.Property(x => x.Issuer).HasMaxLength(1024);
            entity.Property(x => x.SubjectAlternativeNames).HasMaxLength(4096);
            entity.Property(x => x.Thumbprint).HasMaxLength(128);
            entity.HasOne(x => x.Domain)
                .WithOne(x => x.Certificate)
                .HasForeignKey<DomainCertificate>(x => x.DomainId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.Domain.DeletedAt == null);
        });

        modelBuilder.Entity<DomainDnsSettings>(entity =>
        {
            entity.ToTable("domain_dns_settings");
            entity.HasKey(x => x.DomainId);
            entity.Property(x => x.SpfIpv4).HasMaxLength(4096);
            entity.Property(x => x.SpfIpv6).HasMaxLength(4096);
            entity.Property(x => x.SpfIncludes).HasMaxLength(4096);
            entity.Property(x => x.SpfCustomMechanisms).HasMaxLength(4096);
            entity.Property(x => x.SpfAllPolicy).HasMaxLength(16);
            entity.Property(x => x.DmarcPolicy).HasMaxLength(16);
            entity.Property(x => x.DmarcSubdomainPolicy).HasMaxLength(16);
            entity.Property(x => x.DmarcAggregateReportUris).HasMaxLength(4096);
            entity.Property(x => x.DmarcForensicReportUris).HasMaxLength(4096);
            entity.Property(x => x.DmarcDkimAlignment).HasMaxLength(4);
            entity.Property(x => x.DmarcSpfAlignment).HasMaxLength(4);
            entity.Property(x => x.DmarcFailureOptions).HasMaxLength(32);
            entity.Property(x => x.MtaStsMode).HasMaxLength(16);
            entity.Property(x => x.MtaStsPolicyId).HasMaxLength(32);
            entity.Property(x => x.MtaStsMxPatterns).HasMaxLength(4096);
            entity.Property(x => x.TlsReportUris).HasMaxLength(4096);
            entity.Property(x => x.DaneTlsaRecords).HasMaxLength(8192);
            if (Database.ProviderName == "MySql.EntityFrameworkCore")
            {
                entity.Property(x => x.SpfIpv4).HasColumnType("text");
                entity.Property(x => x.SpfIpv6).HasColumnType("text");
                entity.Property(x => x.SpfIncludes).HasColumnType("text");
                entity.Property(x => x.SpfCustomMechanisms).HasColumnType("text");
                entity.Property(x => x.DmarcAggregateReportUris).HasColumnType("text");
                entity.Property(x => x.DmarcForensicReportUris).HasColumnType("text");
                entity.Property(x => x.MtaStsMxPatterns).HasColumnType("text");
                entity.Property(x => x.TlsReportUris).HasColumnType("text");
                entity.Property(x => x.DaneTlsaRecords).HasColumnType("text");
            }
            entity.HasOne(x => x.Domain)
                .WithOne(x => x.DnsSettings)
                .HasForeignKey<DomainDnsSettings>(x => x.DomainId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.Domain.DeletedAt == null);
        });

        modelBuilder.Entity<MailAccount>(entity =>
        {
            entity.ToTable("mail_accounts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DomainId, x.LocalPart }).IsUnique();
            entity.HasAlternateKey(x => new { x.Id, x.DomainId });
            entity.Property(x => x.LocalPart).HasMaxLength(128);
            entity.Property(x => x.PasswordHash).HasMaxLength(1024);
            entity.Property(x => x.UsedBytes).HasDefaultValue(0L);
            entity.HasIndex(x => x.DeletedAt);
            entity.HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.DeletedAt == null);
        });

        modelBuilder.Entity<MailAlias>(entity =>
        {
            entity.ToTable("mail_aliases");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DomainId, x.SourceLocalPart }).IsUnique();
            entity.Property(x => x.SourceLocalPart).HasMaxLength(128);
            entity.Property(x => x.DestinationAddress).HasMaxLength(320);
            entity.Property(x => x.Mode).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.TargetAccount)
                .WithMany()
                .HasForeignKey(x => new { x.TargetAccountId, x.DomainId })
                .HasPrincipalKey(x => new { x.Id, x.DomainId })
                // 别名与目标账号必须留在同一域内；删除账号不自动删除别名，避免误删转发配置。
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasQueryFilter(x => x.Domain.DeletedAt == null);
        });

        modelBuilder.Entity<MailRule>(entity =>
        {
            entity.ToTable("mail_rules");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DomainId, x.Direction, x.Priority });
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.Direction).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.MatchMode).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.TargetMailboxName).HasMaxLength(255);
            entity.HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.Domain.DeletedAt == null);
        });

        modelBuilder.Entity<MailRuleCondition>(entity =>
        {
            entity.ToTable("mail_rule_conditions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RuleId, x.Position });
            entity.Property(x => x.Field).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Operator).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ValuesJson).HasMaxLength(4096);
            entity.HasOne(x => x.Rule).WithMany(x => x.Conditions).HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.Rule.Domain.DeletedAt == null);
        });

        modelBuilder.Entity<MailRuleAction>(entity =>
        {
            entity.ToTable("mail_rule_actions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RuleId, x.Position });
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ValuesJson).HasMaxLength(4096);
            entity.HasOne(x => x.Rule).WithMany(x => x.Actions).HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.Rule.Domain.DeletedAt == null);
        });

        modelBuilder.Entity<DomainDnsCheck>(entity =>
        {
            entity.ToTable("domain_dns_checks");
            entity.HasKey(x => x.DomainId);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.DetailsJson).HasMaxLength(32768);
            entity.Property(x => x.LastError).HasMaxLength(4096);
            entity.HasOne(x => x.Domain).WithOne().HasForeignKey<DomainDnsCheck>(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.Domain.DeletedAt == null);
        });

        modelBuilder.Entity<Mailbox>(entity =>
        {
            entity.ToTable("mailboxes");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.Name }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(255);
            entity.HasIndex(x => x.DeletedAt);
            entity.HasIndex(x => x.PendingDeletionJobId);
            entity.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.DeletedAt == null);
        });

        modelBuilder.Entity<MailboxFolderDeletionJob>(entity =>
        {
            entity.ToTable("mailbox_folder_deletion_jobs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DomainId, x.Status, x.CreatedAt });
            entity.Property(x => x.FolderName).HasMaxLength(255);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.RequestedBy).HasMaxLength(320);
            entity.Property(x => x.LastError).HasMaxLength(4096);
            entity.HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.Domain.DeletedAt == null);
        });

        modelBuilder.Entity<StoredMessage>(entity =>
        {
            entity.ToTable("stored_messages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.ContentKey);
            entity.Property(x => x.ContentKey).HasMaxLength(64);
            entity.Property(x => x.EnvelopeFrom).HasMaxLength(320);
            entity.Property(x => x.InternetMessageId).HasMaxLength(998);
            entity.Property(x => x.InternetMessageIdHash).HasMaxLength(64);
            entity.HasIndex(x => x.InternetMessageIdHash);
            entity.HasIndex(x => x.ReceivedAt);
            entity.Property(x => x.SourceType).HasMaxLength(32);
            entity.Property(x => x.AuthenticatedAccountName).HasMaxLength(320);
            entity.Property(x => x.SourceIp).HasMaxLength(128);
        });

        modelBuilder.Entity<StoredMessageRecipient>(entity =>
        {
            entity.ToTable("stored_message_recipients");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Address).HasMaxLength(320);
            entity.HasIndex(x => x.Address);
            entity.HasIndex(x => x.MessageId);
            entity.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuarantineRecord>(entity =>
        {
            // 隔离记录引用已保存消息内容，审核释放或删除只改变状态，不直接改原始邮件。
            entity.ToTable("quarantine_records");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DomainId, x.Status, x.QuarantinedAt });
            entity.HasIndex(x => x.MessageId);
            entity.Property(x => x.Sender).HasMaxLength(320);
            entity.Property(x => x.RecipientsJson).HasMaxLength(4096);
            entity.Property(x => x.RemoteAddress).HasMaxLength(128);
            entity.Property(x => x.PluginId).HasMaxLength(128);
            entity.Property(x => x.PluginName).HasMaxLength(256);
            entity.Property(x => x.Reason).HasMaxLength(2048);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ReviewedBy).HasMaxLength(256);
            entity.Property(x => x.ReviewNote).HasMaxLength(2048);
            entity.HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => x.Domain.DeletedAt == null);
        });

        modelBuilder.Entity<ScheduledTask>(entity =>
        {
            entity.ToTable("scheduled_tasks");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Type).IsUnique();
            entity.HasIndex(x => new { x.IsEnabled, x.NextRunAt });
            entity.Property(x => x.Type).HasMaxLength(64);
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.SettingsJson).HasMaxLength(8192);
        });
        modelBuilder.Entity<TaskExecution>(entity =>
        {
            entity.ToTable("task_executions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ScheduledTaskId, x.StartedAt });
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Error).HasMaxLength(4096);
            entity.HasOne(x => x.ScheduledTask).WithMany().HasForeignKey(x => x.ScheduledTaskId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TaskLease>(entity =>
        {
            entity.ToTable("task_leases");
            entity.HasKey(x => x.ScheduledTaskId);
            entity.Property(x => x.Owner).HasMaxLength(256);
        });
        modelBuilder.Entity<CleanupQueueItem>(entity =>
        {
            entity.ToTable("cleanup_queue");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Status, x.PurgeAfter });
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId });
            entity.Property(x => x.ResourceType).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ContentKey).HasMaxLength(64);
            entity.Property(x => x.LastError).HasMaxLength(4096);
        });
        modelBuilder.Entity<QuotaLedgerEntry>(entity =>
        {
            entity.ToTable("quota_ledger");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.HasIndex(x => new { x.AccountId, x.CreatedAt });
            entity.Property(x => x.IdempotencyKey).HasMaxLength(256);
            entity.Property(x => x.Operation).HasConversion<string>().HasMaxLength(32);
        });
        modelBuilder.Entity<MailboxMessage>(entity =>
        {
            entity.ToTable("mailbox_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.QuotaReleased).HasDefaultValue(false);
            entity.HasIndex(x => new { x.MailboxId, x.Uid }).IsUnique();
            entity.HasIndex(x => x.DeletedAt);
            entity.HasIndex(x => x.ScheduledDeleteAt);
            entity.Property(x => x.Flags).HasMaxLength(512);
            entity.HasOne(x => x.Mailbox).WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => x.DeletedAt == null);
        });

        modelBuilder.Entity<DeliveryJob>(entity =>
        {
            entity.ToTable("delivery_jobs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Status, x.NextAttemptAt });
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Sender).HasMaxLength(320);
            entity.Property(x => x.Recipient).HasMaxLength(320);
            entity.Property(x => x.LeaseOwner).HasMaxLength(128);
            entity.HasIndex(x => x.OriginRuleId);
            entity.HasIndex(x => x.RouteDomainId);
            entity.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.RouteDomain).WithMany().HasForeignKey(x => x.RouteDomainId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<FirewallRule>(entity =>
        {
            entity.ToTable("firewall_rules");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Cidr).IsUnique();
            entity.Property(x => x.Cidr).HasMaxLength(64);
            entity.Property(x => x.Note).HasMaxLength(512);
        });

        modelBuilder.Entity<DeliveryAttempt>(entity =>
        {
            entity.ToTable("delivery_attempts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.DeliveryJobId);
            entity.HasIndex(x => x.CreatedAt);
            entity.Property(x => x.Stage).HasMaxLength(32);
            entity.Property(x => x.RemoteHost).HasMaxLength(255);
            entity.Property(x => x.RemoteAddress).HasMaxLength(64);
            entity.Property(x => x.SmtpStatusCode).HasMaxLength(16);
            entity.Property(x => x.EnhancedStatusCode).HasMaxLength(16);
            entity.Property(x => x.TlsProtocol).HasMaxLength(32);
            entity.Property(x => x.TlsCipherSuite).HasMaxLength(128);
            entity.Property(x => x.FailureCategory).HasMaxLength(64);
            entity.Property(x => x.SessionSummary).HasMaxLength(65536);
            entity.Property(x => x.Response).HasMaxLength(4096);
            entity.HasOne(x => x.DeliveryJob).WithMany().HasForeignKey(x => x.DeliveryJobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SrsAddressMapping>(entity =>
        {
            entity.ToTable("srs_address_mappings");
            entity.HasKey(x => x.Token);
            entity.HasIndex(x => x.ExpiresAt);
            entity.Property(x => x.Token).HasMaxLength(96);
            entity.Property(x => x.OriginalSender).HasMaxLength(320);
            entity.HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(x => x.Domain.DeletedAt == null);
        });

        modelBuilder.Entity<SentCopyJob>(entity =>
        {
            entity.ToTable("sent_copy_jobs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.State, x.ReadyAt });
            entity.HasIndex(x => new { x.AccountId, x.DeduplicateUntil });
            entity.Property(x => x.TargetMailboxName).HasMaxLength(255);
            entity.Property(x => x.IdentityKey).HasMaxLength(64);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(32);
            entity.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Rule).WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.SetNull);
            entity.HasQueryFilter(x => x.Account.DeletedAt == null);
        });

        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.ToTable("admin_users");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.UserName).IsUnique();
            entity.Property(x => x.UserName).HasMaxLength(128);
            entity.Property(x => x.PasswordHash).HasMaxLength(1024);
        });

        modelBuilder.Entity<PermissionGrant>(entity =>
        {
            entity.ToTable("permission_grants");
            entity.HasKey(x => new { x.SubjectType, x.SubjectId, x.PermissionCode, x.ResourceType, x.ResourceKey });
            entity.HasIndex(x => new { x.PermissionCode, x.ResourceType, x.ResourceKey });
            entity.Property(x => x.SubjectType).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.PermissionCode).HasMaxLength(128);
            entity.Property(x => x.ResourceType).HasMaxLength(64).HasDefaultValue(string.Empty);
            entity.Property(x => x.ResourceKey).HasMaxLength(128).HasDefaultValue(string.Empty);
            entity.Property(x => x.CreatedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<ApiKeyRecord>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Prefix).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.Prefix).HasMaxLength(32);
            entity.Property(x => x.SecretHash).HasMaxLength(128);
            entity.Property(x => x.CreatedBy).HasMaxLength(256);
            entity.HasIndex(x => x.LastUsedAt);
        });

        modelBuilder.Entity<AuditRecord>(entity =>
        {
            entity.ToTable("audit_records");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => x.Category);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.PluginId);
            entity.HasIndex(x => new { x.Action, x.Resource, x.RequestId });
            entity.Property(x => x.Actor).HasMaxLength(256);
            entity.Property(x => x.Action).HasMaxLength(128);
            entity.Property(x => x.Resource).HasMaxLength(512);
            entity.Property(x => x.RemoteAddress).HasMaxLength(128);
            entity.Property(x => x.Category).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.RequestId).HasMaxLength(128);
            entity.Property(x => x.RelatedId).HasMaxLength(256);
            entity.Property(x => x.PluginId).HasMaxLength(128);
            entity.Property(x => x.PluginName).HasMaxLength(256);
            entity.Property(x => x.ChangeSummaryJson).HasMaxLength(32768);
            entity.Property(x => x.RawJson).HasMaxLength(262144);
        });

        modelBuilder.Entity<MtaStsPolicyCache>(entity =>
        {
            entity.ToTable("mta_sts_policy_cache");
            entity.HasKey(x => x.Domain);
            entity.Property(x => x.Domain).HasMaxLength(255);
            entity.Property(x => x.PolicyId).HasMaxLength(32);
            entity.Property(x => x.Mode).HasMaxLength(16);
            entity.Property(x => x.MxPatterns).HasMaxLength(4096);
            entity.Property(x => x.LastError).HasMaxLength(4096);
            entity.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<TlsDeliveryEvent>(entity =>
        {
            entity.ToTable("tls_delivery_events");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Reported, x.OccurredAt });
            entity.Property(x => x.PolicyDomain).HasMaxLength(255);
            entity.Property(x => x.MxHost).HasMaxLength(255);
            entity.Property(x => x.PolicyType).HasMaxLength(32);
            entity.Property(x => x.PolicyString).HasMaxLength(8192);
            entity.Property(x => x.ResultType).HasMaxLength(64);
            entity.Property(x => x.FailureReason).HasMaxLength(4096);
            entity.Property(x => x.SendingIp).HasMaxLength(64);
            entity.Property(x => x.ReceivingIp).HasMaxLength(64);
        });

        modelBuilder.Entity<TlsReportDelivery>(entity =>
        {
            entity.ToTable("tls_report_deliveries");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CompletedAt, x.NextAttemptAt });
            entity.HasIndex(x => x.LeaseUntil);
            entity.Property(x => x.PolicyDomain).HasMaxLength(255);
            entity.Property(x => x.ReportUri).HasMaxLength(2048);
            entity.Property(x => x.ReportJson).HasMaxLength(1_048_576);
            entity.Property(x => x.LastError).HasMaxLength(4096);
            entity.Property(x => x.LastManualRetryKey).HasMaxLength(32);
            entity.Property(x => x.LeaseOwner).HasMaxLength(128);
        });
    }
}
