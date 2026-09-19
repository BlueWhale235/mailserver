using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MailServer.Infrastructure;

/// <summary>
/// 邮件系统的 EF Core 数据模型入口。领域服务应通过这里持久化状态，不直接拼接数据库表名。
/// </summary>
public sealed class MailDbContext(DbContextOptions<MailDbContext> options) : DbContext(options)
{
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
    public DbSet<MtaStsPolicyCache> MtaStsPolicyCaches => Set<MtaStsPolicyCache>();
    public DbSet<TlsDeliveryEvent> TlsDeliveryEvents => Set<TlsDeliveryEvent>();
    public DbSet<TlsReportDelivery> TlsReportDeliveries => Set<TlsReportDelivery>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();
    public DbSet<TaskLease> TaskLeases => Set<TaskLease>();
    public DbSet<CleanupQueueItem> CleanupQueue => Set<CleanupQueueItem>();
    public DbSet<QuotaLedgerEntry> QuotaLedger => Set<QuotaLedgerEntry>();
    public DbSet<ClusterNode> ClusterNodes => Set<ClusterNode>();
    public DbSet<ClusterConfiguration> ClusterConfigurations => Set<ClusterConfiguration>();
    public DbSet<ClusterCertificateAuthority> ClusterCertificateAuthorities => Set<ClusterCertificateAuthority>();
    public DbSet<ClusterJoinInvitation> ClusterJoinInvitations => Set<ClusterJoinInvitation>();
    public DbSet<ClusterNodeCertificate> ClusterNodeCertificates => Set<ClusterNodeCertificate>();
    public DbSet<ClusterCertificateRevocation> ClusterCertificateRevocations => Set<ClusterCertificateRevocation>();
    public DbSet<ClusterPluginPackage> ClusterPluginPackages => Set<ClusterPluginPackage>();
    public DbSet<ClusterPluginPackageFile> ClusterPluginPackageFiles => Set<ClusterPluginPackageFile>();
    public DbSet<ClusterSnapshot> ClusterSnapshots => Set<ClusterSnapshot>();
    public DbSet<ReplicationLog> ReplicationLogs => Set<ReplicationLog>();
    public DbSet<ReplicationCursor> ReplicationCursors => Set<ReplicationCursor>();
    public DbSet<ClusterLease> ClusterLeases => Set<ClusterLease>();
    public DbSet<ClusterEvent> ClusterEvents => Set<ClusterEvent>();
    public DbSet<QuarantineRecord> QuarantineRecords => Set<QuarantineRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
            entity.Property(x => x.NodeId).HasMaxLength(128);
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
        modelBuilder.Entity<ClusterNode>(entity =>
        {
            entity.ToTable("cluster_nodes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(128);
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.Endpoint).HasMaxLength(2048);
            entity.Property(x => x.Version).HasMaxLength(64);
            entity.Property(x => x.CertificateThumbprint).HasMaxLength(128);
            entity.Property(x => x.PluginPackageSetHash).HasMaxLength(128);
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.LastError).HasMaxLength(4096);
        });
        modelBuilder.Entity<ClusterConfiguration>(entity =>
        {
            entity.ToTable("cluster_configuration");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.PrimaryNodeId).HasMaxLength(128);
            entity.Property(x => x.ClusterCaThumbprint).HasMaxLength(128);
            entity.Property(x => x.ProtectedCaPrivateKeyPem).HasMaxLength(32768);
            entity.Property(x => x.CaCertificatePem).HasMaxLength(32768);
            entity.Property(x => x.CreatedBy).HasMaxLength(256);
        });
        modelBuilder.Entity<ClusterCertificateAuthority>(entity =>
        {
            entity.ToTable("cluster_certificate_authorities");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Thumbprint).IsUnique();
            entity.Property(x => x.Thumbprint).HasMaxLength(128);
            entity.Property(x => x.CertificatePem).HasMaxLength(32768);
            entity.Property(x => x.ProtectedPrivateKeyPem).HasMaxLength(32768);
            entity.Property(x => x.CreatedBy).HasMaxLength(256);
        });
        modelBuilder.Entity<ClusterJoinInvitation>(entity =>
        {
            entity.ToTable("cluster_join_invitations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.TokenHash).HasMaxLength(128);
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.CreatedBy).HasMaxLength(256);
        });
        modelBuilder.Entity<ClusterNodeCertificate>(entity =>
        {
            entity.ToTable("cluster_node_certificates");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Thumbprint).IsUnique();
            entity.HasIndex(x => x.NodeId);
            entity.Property(x => x.NodeId).HasMaxLength(128);
            entity.Property(x => x.Thumbprint).HasMaxLength(128);
            entity.Property(x => x.CertificatePem).HasMaxLength(32768);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        });
        modelBuilder.Entity<ClusterCertificateRevocation>(entity =>
        {
            entity.ToTable("cluster_certificate_revocations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Thumbprint).IsUnique();
            entity.Property(x => x.Thumbprint).HasMaxLength(128);
            entity.Property(x => x.NodeId).HasMaxLength(128);
            entity.Property(x => x.Reason).HasMaxLength(512);
        });
        modelBuilder.Entity<ClusterPluginPackage>(entity =>
        {
            entity.ToTable("cluster_plugin_packages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.PluginId).IsUnique();
            entity.Property(x => x.PluginId).HasMaxLength(128);
            entity.Property(x => x.Version).HasMaxLength(64);
            entity.Property(x => x.ManifestHash).HasMaxLength(128);
            entity.Property(x => x.PackageHash).HasMaxLength(128);
        });
        modelBuilder.Entity<ClusterPluginPackageFile>(entity =>
        {
            entity.ToTable("cluster_plugin_package_files");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.PluginId, x.RelativePath }).IsUnique();
            entity.Property(x => x.PluginId).HasMaxLength(128);
            entity.Property(x => x.RelativePath).HasMaxLength(512);
            entity.Property(x => x.ContentHash).HasMaxLength(128);
        });
        modelBuilder.Entity<ClusterSnapshot>(entity =>
        {
            entity.ToTable("cluster_snapshots");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.NodeId, x.CreatedAt });
            entity.Property(x => x.NodeId).HasMaxLength(128);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.LastError).HasMaxLength(4096);
        });
        modelBuilder.Entity<ReplicationLog>(entity =>
        {
            entity.ToTable("replication_log");
            entity.HasKey(x => x.Sequence);
            entity.Property(x => x.Sequence).ValueGeneratedOnAdd();
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => x.BatchId);
            entity.Property(x => x.EntityType).HasMaxLength(128);
            entity.Property(x => x.EntityKey).HasMaxLength(256);
            entity.Property(x => x.Operation).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.PayloadJson).HasMaxLength(65535);
            entity.Property(x => x.ContentKey).HasMaxLength(64);
        });
        modelBuilder.Entity<ReplicationCursor>(entity =>
        {
            entity.ToTable("replication_cursors");
            entity.HasKey(x => x.NodeId);
            entity.Property(x => x.NodeId).HasMaxLength(128);
            entity.Property(x => x.SyncStatus).HasConversion<string>().HasMaxLength(32).HasDefaultValue(ClusterSyncStatus.Idle);
            entity.Property(x => x.PauseReason).HasMaxLength(1024);
            entity.Property(x => x.LastError).HasMaxLength(4096);
            entity.Property(x => x.LastDetailJson).HasMaxLength(4096);
        });
        modelBuilder.Entity<ClusterLease>(entity =>
        {
            entity.ToTable("cluster_leases");
            entity.HasKey(x => x.Name);
            entity.Property(x => x.Name).HasMaxLength(64);
            entity.Property(x => x.OwnerNodeId).HasMaxLength(128);
        });
        modelBuilder.Entity<ClusterEvent>(entity =>
        {
            entity.ToTable("cluster_events");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.CreatedAt);
            entity.Property(x => x.Type).HasMaxLength(64);
            entity.Property(x => x.NodeId).HasMaxLength(128);
            entity.Property(x => x.Detail).HasMaxLength(4096);
        });

        modelBuilder.Entity<MailboxMessage>(entity =>
        {
            entity.ToTable("mailbox_messages");
            entity.HasKey(x => x.Id);
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
            entity.Property(x => x.Actor).HasMaxLength(256);
            entity.Property(x => x.Action).HasMaxLength(128);
            entity.Property(x => x.Resource).HasMaxLength(512);
            entity.Property(x => x.RemoteAddress).HasMaxLength(128);
            entity.Property(x => x.Category).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.NodeId).HasMaxLength(128);
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
            entity.Property(x => x.PolicyDomain).HasMaxLength(255);
            entity.Property(x => x.ReportUri).HasMaxLength(2048);
            entity.Property(x => x.ReportJson).HasMaxLength(1_048_576);
            entity.Property(x => x.LastError).HasMaxLength(4096);
        });
    }
}
