using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MailServer.Infrastructure;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        if (Database.IsSqlite())
        {
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
            entity.ToTable("mail_domains");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasIndex(x => x.MailHostName).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(255);
            entity.Property(x => x.MailHostName).HasMaxLength(255);
            entity.Property(x => x.SmtpHostName).HasMaxLength(255);
            entity.Property(x => x.OutboundIpAddresses).HasMaxLength(1024);
            entity.Property(x => x.QuotaBytes).HasDefaultValue(10_737_418_240L);
            entity.Property(x => x.DirectDeliverSameDomain).HasDefaultValue(true);
            entity.Property(x => x.OutboundMode).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.DkimSelector).HasMaxLength(63);
            entity.Property(x => x.DkimPrivateKeyPath).HasMaxLength(2048);
            entity.Property(x => x.DkimPublicKey).HasMaxLength(4096);
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
        });

        modelBuilder.Entity<MailAccount>(entity =>
        {
            entity.ToTable("mail_accounts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DomainId, x.LocalPart }).IsUnique();
            entity.HasAlternateKey(x => new { x.Id, x.DomainId });
            entity.Property(x => x.LocalPart).HasMaxLength(128);
            entity.Property(x => x.PasswordHash).HasMaxLength(1024);
            entity.HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
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
                .OnDelete(DeleteBehavior.NoAction);
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
        });

        modelBuilder.Entity<MailRuleAction>(entity =>
        {
            entity.ToTable("mail_rule_actions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.RuleId, x.Position });
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ValuesJson).HasMaxLength(4096);
            entity.HasOne(x => x.Rule).WithMany(x => x.Actions).HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DomainDnsCheck>(entity =>
        {
            entity.ToTable("domain_dns_checks");
            entity.HasKey(x => x.DomainId);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.DetailsJson).HasMaxLength(32768);
            entity.Property(x => x.LastError).HasMaxLength(4096);
            entity.HasOne(x => x.Domain).WithOne().HasForeignKey<DomainDnsCheck>(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Mailbox>(entity =>
        {
            entity.ToTable("mailboxes");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AccountId, x.Name }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(255);
            entity.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
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

        modelBuilder.Entity<MailboxMessage>(entity =>
        {
            entity.ToTable("mailbox_messages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.MailboxId, x.Uid }).IsUnique();
            entity.Property(x => x.Flags).HasMaxLength(512);
            entity.HasOne(x => x.Mailbox).WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeliveryJob>(entity =>
        {
            entity.ToTable("delivery_jobs");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Status, x.NextAttemptAt });
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.RouteMode).HasConversion<string>().HasMaxLength(32);
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
            entity.Property(x => x.Actor).HasMaxLength(256);
            entity.Property(x => x.Action).HasMaxLength(128);
            entity.Property(x => x.Resource).HasMaxLength(512);
            entity.Property(x => x.RemoteAddress).HasMaxLength(128);
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
