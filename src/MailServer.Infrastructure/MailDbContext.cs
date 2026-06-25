using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MailServer.Infrastructure;

public sealed class MailDbContext(DbContextOptions<MailDbContext> options) : DbContext(options)
{
    public DbSet<MailDomain> Domains => Set<MailDomain>();
    public DbSet<MailAccount> Accounts => Set<MailAccount>();
    public DbSet<MailAlias> Aliases => Set<MailAlias>();
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<StoredMessage> Messages => Set<StoredMessage>();
    public DbSet<MailboxMessage> MailboxMessages => Set<MailboxMessage>();
    public DbSet<DeliveryJob> DeliveryJobs => Set<DeliveryJob>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<ApiKeyRecord> ApiKeys => Set<ApiKeyRecord>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

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
            entity.Property(x => x.Name).HasMaxLength(255);
            entity.Property(x => x.OutboundMode).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<MailAccount>(entity =>
        {
            entity.ToTable("mail_accounts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DomainId, x.LocalPart }).IsUnique();
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
            entity.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeliveryAttempt>(entity =>
        {
            entity.ToTable("delivery_attempts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.DeliveryJobId);
            entity.HasOne(x => x.DeliveryJob).WithMany().HasForeignKey(x => x.DeliveryJobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminUser>(entity =>
        {
            entity.ToTable("admin_users");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.UserName).IsUnique();
            entity.Property(x => x.UserName).HasMaxLength(128);
            entity.Property(x => x.PasswordHash).HasMaxLength(1024);
        });

        modelBuilder.Entity<ApiKeyRecord>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Prefix).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.Prefix).HasMaxLength(32);
            entity.Property(x => x.SecretHash).HasMaxLength(128);
            entity.Property(x => x.Scopes).HasMaxLength(1024);
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
    }
}
