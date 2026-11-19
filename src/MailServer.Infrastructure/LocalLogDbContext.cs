using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MailServer.Infrastructure;

public sealed class LocalLogSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

public sealed class LocalLogDbContext(DbContextOptions<LocalLogDbContext> options) : DbContext(options)
{
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<TaskExecution> TaskExecutions => Set<TaskExecution>();
    public DbSet<PerformanceOperation> PerformanceOperations => Set<PerformanceOperation>();
    public DbSet<PerformanceMinute> PerformanceMinutes => Set<PerformanceMinute>();
    public DbSet<PerformanceSql> PerformanceSql => Set<PerformanceSql>();
    public DbSet<PerformanceProcess> PerformanceProcesses => Set<PerformanceProcess>();
    public DbSet<LocalLogSetting> Settings => Set<LocalLogSetting>();

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
        modelBuilder.Entity<TaskExecution>(entity =>
        {
            entity.ToTable("task_executions");
            entity.Ignore(x => x.ScheduledTask);
            entity.HasIndex(x => x.StartedAt);
            entity.Property(x => x.TaskName).HasMaxLength(128);
            entity.Property(x => x.TaskType).HasMaxLength(64);
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ScheduledTaskId, x.StartedAt });
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Error).HasMaxLength(4096);
        });
        modelBuilder.Entity<DeliveryAttempt>(entity =>
        {
            entity.ToTable("delivery_attempts");
            entity.Ignore(x => x.DeliveryJob);
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
        });

        modelBuilder.Entity<LocalLogSetting>(entity => { entity.ToTable("local_log_settings"); entity.HasKey(x => x.Key); });
        var dates = new ValueConverter<DateTimeOffset, long>(x => x.UtcTicks, x => new DateTimeOffset(x, TimeSpan.Zero));
        var nullableDates = new ValueConverter<DateTimeOffset?, long?>(x => x.HasValue ? x.Value.UtcTicks : null,
            x => x.HasValue ? new DateTimeOffset(x.Value, TimeSpan.Zero) : null);
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(dates);
                if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(nullableDates);
                if (property.ClrType == typeof(long) && property.IsPrimaryKey())
                    property.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
    }
}
