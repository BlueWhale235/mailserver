using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailServer.Migrations.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class InitialMailServer2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "audit_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Resource = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteAddress = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RequestId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    RelatedId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    ChangeSummaryJson = table.Column<string>(type: "TEXT", maxLength: 32768, nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", maxLength: 262144, nullable: true),
                    PluginId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PluginName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cleanup_queue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ContentKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EstimatedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PurgeAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleanup_queue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_certificate_authorities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Thumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CertificatePem = table.Column<string>(type: "TEXT", maxLength: 32768, nullable: false),
                    ProtectedPrivateKeyPem = table.Column<string>(type: "TEXT", maxLength: 32768, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NotBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    NotAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_certificate_authorities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_certificate_revocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Thumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_certificate_revocations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_configuration",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PrimaryNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ClusterCaThumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProtectedCaPrivateKeyPem = table.Column<string>(type: "TEXT", maxLength: 32768, nullable: true),
                    CaCertificatePem = table.Column<string>(type: "TEXT", maxLength: 32768, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_configuration", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_join_invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_join_invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_leases",
                columns: table => new
                {
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerNodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Term = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_leases", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "cluster_node_certificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Thumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CertificatePem = table.Column<string>(type: "TEXT", maxLength: 32768, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    NotBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    NotAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    GraceUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_node_certificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_nodes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastHeartbeatAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AppliedSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    SafeSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    CertificateThumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PluginPackageSetHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    InitializationProgress = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    JoinedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_nodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_plugin_package_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    ScannedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_plugin_package_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_plugin_packages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ManifestHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PackageHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ScannedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_plugin_packages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BaseSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TotalItems = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedItems = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "firewall_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Cidr = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_firewall_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mail_domains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    MailHostName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SmtpHostName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    OutboundIpAddresses = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    QuotaBytes = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 10737418240L),
                    UsedBytes = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    DirectDeliverSameDomain = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    DkimSelector = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    DkimPrivateKeyPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    DkimPublicKey = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true),
                    PurgeAfter = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_domains", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mta_sts_policy_cache",
                columns: table => new
                {
                    Domain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    PolicyId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MxPatterns = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    MaxAgeSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRefreshAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mta_sts_policy_cache", x => x.Domain);
                });

            migrationBuilder.CreateTable(
                name: "permission_grants",
                columns: table => new
                {
                    SubjectType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SubjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PermissionCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false, defaultValue: ""),
                    ResourceKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: ""),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permission_grants", x => new { x.SubjectType, x.SubjectId, x.PermissionCode, x.ResourceType, x.ResourceKey });
                });

            migrationBuilder.CreateTable(
                name: "quota_ledger",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MailboxMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quota_ledger", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "replication_cursors",
                columns: table => new
                {
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    LastAppliedSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    LastBatchCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastContentDownloads = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    SyncStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "Idle"),
                    PauseReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastDetailJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_replication_cursors", x => x.NodeId);
                });

            migrationBuilder.CreateTable(
                name: "replication_log",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    EntityKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", maxLength: 65535, nullable: false),
                    ContentKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_replication_log", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsClusterTask = table.Column<bool>(type: "INTEGER", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    BatchSize = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    NextRunAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRunAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SettingsJson = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stored_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContentKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EnvelopeFrom = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    InternetMessageId = table.Column<string>(type: "TEXT", maxLength: 998, nullable: true),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stored_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "system_settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "task_leases",
                columns: table => new
                {
                    ScheduledTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_leases", x => x.ScheduledTaskId);
                });

            migrationBuilder.CreateTable(
                name: "tls_delivery_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyDomain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    MxHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    PolicyType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    PolicyString = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    ResultType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SendingIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReceivingIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Reported = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tls_delivery_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tls_report_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PolicyDomain = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ReportUri = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    PeriodStart = table.Column<long>(type: "INTEGER", nullable: false),
                    PeriodEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    ReportJson = table.Column<string>(type: "TEXT", maxLength: 1048576, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tls_report_deliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "domain_certificates",
                columns: table => new
                {
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CertificatePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    PrivateKeyPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Issuer = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SubjectAlternativeNames = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Thumbprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    NotBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    NotAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_certificates", x => x.DomainId);
                    table.ForeignKey(
                        name: "FK_domain_certificates_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "domain_dns_checks",
                columns: table => new
                {
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DetailsJson = table.Column<string>(type: "TEXT", maxLength: 32768, nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CheckedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_dns_checks", x => x.DomainId);
                    table.ForeignKey(
                        name: "FK_domain_dns_checks_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "domain_dns_settings",
                columns: table => new
                {
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpfIncludeMx = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpfIncludeA = table.Column<bool>(type: "INTEGER", nullable: false),
                    SpfIpv4 = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SpfIpv6 = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SpfIncludes = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SpfCustomMechanisms = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SpfAllPolicy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DmarcPolicy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DmarcSubdomainPolicy = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    DmarcPercentage = table.Column<int>(type: "INTEGER", nullable: false),
                    DmarcAggregateReportUris = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    DmarcForensicReportUris = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    DmarcDkimAlignment = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    DmarcSpfAlignment = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    DmarcFailureOptions = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MtaStsMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    MtaStsPolicyId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MtaStsMaxAgeSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MtaStsMxPatterns = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    TlsReportUris = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    DaneEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DaneTlsaRecords = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_dns_settings", x => x.DomainId);
                    table.ForeignKey(
                        name: "FK_domain_dns_settings_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mail_accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocalPart = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    QuotaBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedBytes = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true),
                    PurgeAfter = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_accounts", x => x.Id);
                    table.UniqueConstraint("AK_mail_accounts_Id_DomainId", x => new { x.Id, x.DomainId });
                    table.ForeignKey(
                        name: "FK_mail_accounts_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mail_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MatchMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetMailboxName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_rules_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mailbox_folder_deletion_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FolderName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AffectedAccountCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AffectedMailboxCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AffectedRuleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalMessages = table.Column<long>(type: "INTEGER", nullable: false),
                    ProcessedMessages = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_folder_deletion_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mailbox_folder_deletion_jobs_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "srs_address_mappings",
                columns: table => new
                {
                    Token = table.Column<string>(type: "TEXT", maxLength: 96, nullable: false),
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalSender = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ForwardDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_srs_address_mappings", x => x.Token);
                    table.ForeignKey(
                        name: "FK_srs_address_mappings_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_executions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduledTaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ProcessedCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleasedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_executions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_executions_scheduled_tasks_ScheduledTaskId",
                        column: x => x.ScheduledTaskId,
                        principalTable: "scheduled_tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "delivery_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sender = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Recipient = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    RouteDomainId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LeaseUntil = table.Column<long>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    OriginRuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ForwardDepth = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_jobs_mail_domains_RouteDomainId",
                        column: x => x.RouteDomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_delivery_jobs_stored_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "stored_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "quarantine_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sender = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    RecipientsJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    RemoteAddress = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PluginName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    QuarantinedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReviewedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ReviewedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ReviewNote = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quarantine_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_quarantine_records_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_quarantine_records_stored_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "stored_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "mail_aliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DomainId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceLocalPart = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TargetAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DestinationAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_aliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_aliases_mail_accounts_TargetAccountId_DomainId",
                        columns: x => new { x.TargetAccountId, x.DomainId },
                        principalTable: "mail_accounts",
                        principalColumns: new[] { "Id", "DomainId" });
                    table.ForeignKey(
                        name: "FK_mail_aliases_mail_domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "mail_domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mailboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    UidValidity = table.Column<long>(type: "INTEGER", nullable: false),
                    UidNext = table.Column<long>(type: "INTEGER", nullable: false),
                    HighestModSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true),
                    PurgeAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    PendingDeletionJobId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailboxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mailboxes_mail_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "mail_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mail_rule_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ValuesJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_rule_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_rule_actions_mail_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "mail_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mail_rule_conditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Operator = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ValuesJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mail_rule_conditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mail_rule_conditions_mail_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "mail_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sent_copy_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetMailboxName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    IdentityKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClientMailboxMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ServerMailboxMessageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReadyAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeduplicateUntil = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sent_copy_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sent_copy_jobs_mail_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "mail_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_sent_copy_jobs_mail_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "mail_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_sent_copy_jobs_stored_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "stored_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeliveryJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Succeeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    PermanentFailure = table.Column<bool>(type: "INTEGER", nullable: false),
                    RemoteHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    RemoteAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SmtpStatusCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    EnhancedStatusCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    TlsProtocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TlsCipherSuite = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    FailureCategory = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    NextRetryAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SessionSummary = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: true),
                    Response = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_attempts_delivery_jobs_DeliveryJobId",
                        column: x => x.DeliveryJobId,
                        principalTable: "delivery_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mailbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Uid = table.Column<long>(type: "INTEGER", nullable: false),
                    Flags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ModSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    InternalDate = table.Column<long>(type: "INTEGER", nullable: false),
                    ScheduledDeleteAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeletedBy = table.Column<string>(type: "TEXT", nullable: true),
                    PurgeAfter = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_mailbox_messages_mailboxes_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "mailboxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_mailbox_messages_stored_messages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "stored_messages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_users_UserName",
                table: "admin_users",
                column: "UserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_LastUsedAt",
                table: "api_keys",
                column: "LastUsedAt");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_Prefix",
                table: "api_keys",
                column: "Prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_Category",
                table: "audit_records",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_CreatedAt",
                table: "audit_records",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_PluginId",
                table: "audit_records",
                column: "PluginId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_records_Status",
                table: "audit_records",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_cleanup_queue_ResourceType_ResourceId",
                table: "cleanup_queue",
                columns: new[] { "ResourceType", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_cleanup_queue_Status_PurgeAfter",
                table: "cleanup_queue",
                columns: new[] { "Status", "PurgeAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_cluster_certificate_authorities_Thumbprint",
                table: "cluster_certificate_authorities",
                column: "Thumbprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cluster_certificate_revocations_Thumbprint",
                table: "cluster_certificate_revocations",
                column: "Thumbprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cluster_events_CreatedAt",
                table: "cluster_events",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_cluster_join_invitations_TokenHash",
                table: "cluster_join_invitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cluster_node_certificates_NodeId",
                table: "cluster_node_certificates",
                column: "NodeId");

            migrationBuilder.CreateIndex(
                name: "IX_cluster_node_certificates_Thumbprint",
                table: "cluster_node_certificates",
                column: "Thumbprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cluster_plugin_package_files_PluginId_RelativePath",
                table: "cluster_plugin_package_files",
                columns: new[] { "PluginId", "RelativePath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cluster_plugin_packages_PluginId",
                table: "cluster_plugin_packages",
                column: "PluginId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cluster_snapshots_NodeId_CreatedAt",
                table: "cluster_snapshots",
                columns: new[] { "NodeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_CreatedAt",
                table: "delivery_attempts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_DeliveryJobId",
                table: "delivery_attempts",
                column: "DeliveryJobId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_jobs_MessageId",
                table: "delivery_jobs",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_jobs_OriginRuleId",
                table: "delivery_jobs",
                column: "OriginRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_jobs_RouteDomainId",
                table: "delivery_jobs",
                column: "RouteDomainId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_jobs_Status_NextAttemptAt",
                table: "delivery_jobs",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_firewall_rules_Cidr",
                table: "firewall_rules",
                column: "Cidr",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_accounts_DeletedAt",
                table: "mail_accounts",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_mail_accounts_DomainId_LocalPart",
                table: "mail_accounts",
                columns: new[] { "DomainId", "LocalPart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_aliases_DomainId_SourceLocalPart",
                table: "mail_aliases",
                columns: new[] { "DomainId", "SourceLocalPart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_aliases_TargetAccountId_DomainId",
                table: "mail_aliases",
                columns: new[] { "TargetAccountId", "DomainId" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_domains_DeletedAt",
                table: "mail_domains",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_mail_domains_MailHostName",
                table: "mail_domains",
                column: "MailHostName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_domains_Name",
                table: "mail_domains",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mail_rule_actions_RuleId_Position",
                table: "mail_rule_actions",
                columns: new[] { "RuleId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_rule_conditions_RuleId_Position",
                table: "mail_rule_conditions",
                columns: new[] { "RuleId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_mail_rules_DomainId_Direction_Priority",
                table: "mail_rules",
                columns: new[] { "DomainId", "Direction", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_folder_deletion_jobs_DomainId_Status_CreatedAt",
                table: "mailbox_folder_deletion_jobs",
                columns: new[] { "DomainId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_messages_DeletedAt",
                table: "mailbox_messages",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_messages_MailboxId_Uid",
                table: "mailbox_messages",
                columns: new[] { "MailboxId", "Uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_messages_MessageId",
                table: "mailbox_messages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_messages_ScheduledDeleteAt",
                table: "mailbox_messages",
                column: "ScheduledDeleteAt");

            migrationBuilder.CreateIndex(
                name: "IX_mailboxes_AccountId_Name",
                table: "mailboxes",
                columns: new[] { "AccountId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_mailboxes_DeletedAt",
                table: "mailboxes",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_mailboxes_PendingDeletionJobId",
                table: "mailboxes",
                column: "PendingDeletionJobId");

            migrationBuilder.CreateIndex(
                name: "IX_mta_sts_policy_cache_ExpiresAt",
                table: "mta_sts_policy_cache",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_permission_grants_PermissionCode_ResourceType_ResourceKey",
                table: "permission_grants",
                columns: new[] { "PermissionCode", "ResourceType", "ResourceKey" });

            migrationBuilder.CreateIndex(
                name: "IX_quarantine_records_DomainId_Status_QuarantinedAt",
                table: "quarantine_records",
                columns: new[] { "DomainId", "Status", "QuarantinedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_quarantine_records_MessageId",
                table: "quarantine_records",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_quota_ledger_AccountId_CreatedAt",
                table: "quota_ledger",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_quota_ledger_IdempotencyKey",
                table: "quota_ledger",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_replication_log_BatchId",
                table: "replication_log",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_replication_log_CreatedAt",
                table: "replication_log",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_tasks_IsEnabled_NextRunAt",
                table: "scheduled_tasks",
                columns: new[] { "IsEnabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_tasks_Type",
                table: "scheduled_tasks",
                column: "Type",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sent_copy_jobs_AccountId_DeduplicateUntil",
                table: "sent_copy_jobs",
                columns: new[] { "AccountId", "DeduplicateUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_sent_copy_jobs_MessageId",
                table: "sent_copy_jobs",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_sent_copy_jobs_RuleId",
                table: "sent_copy_jobs",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_sent_copy_jobs_State_ReadyAt",
                table: "sent_copy_jobs",
                columns: new[] { "State", "ReadyAt" });

            migrationBuilder.CreateIndex(
                name: "IX_srs_address_mappings_DomainId",
                table: "srs_address_mappings",
                column: "DomainId");

            migrationBuilder.CreateIndex(
                name: "IX_srs_address_mappings_ExpiresAt",
                table: "srs_address_mappings",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_stored_messages_ContentKey",
                table: "stored_messages",
                column: "ContentKey");

            migrationBuilder.CreateIndex(
                name: "IX_task_executions_ScheduledTaskId_StartedAt",
                table: "task_executions",
                columns: new[] { "ScheduledTaskId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tls_delivery_events_Reported_OccurredAt",
                table: "tls_delivery_events",
                columns: new[] { "Reported", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tls_report_deliveries_CompletedAt_NextAttemptAt",
                table: "tls_report_deliveries",
                columns: new[] { "CompletedAt", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_users");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "audit_records");

            migrationBuilder.DropTable(
                name: "cleanup_queue");

            migrationBuilder.DropTable(
                name: "cluster_certificate_authorities");

            migrationBuilder.DropTable(
                name: "cluster_certificate_revocations");

            migrationBuilder.DropTable(
                name: "cluster_configuration");

            migrationBuilder.DropTable(
                name: "cluster_events");

            migrationBuilder.DropTable(
                name: "cluster_join_invitations");

            migrationBuilder.DropTable(
                name: "cluster_leases");

            migrationBuilder.DropTable(
                name: "cluster_node_certificates");

            migrationBuilder.DropTable(
                name: "cluster_nodes");

            migrationBuilder.DropTable(
                name: "cluster_plugin_package_files");

            migrationBuilder.DropTable(
                name: "cluster_plugin_packages");

            migrationBuilder.DropTable(
                name: "cluster_snapshots");

            migrationBuilder.DropTable(
                name: "delivery_attempts");

            migrationBuilder.DropTable(
                name: "domain_certificates");

            migrationBuilder.DropTable(
                name: "domain_dns_checks");

            migrationBuilder.DropTable(
                name: "domain_dns_settings");

            migrationBuilder.DropTable(
                name: "firewall_rules");

            migrationBuilder.DropTable(
                name: "mail_aliases");

            migrationBuilder.DropTable(
                name: "mail_rule_actions");

            migrationBuilder.DropTable(
                name: "mail_rule_conditions");

            migrationBuilder.DropTable(
                name: "mailbox_folder_deletion_jobs");

            migrationBuilder.DropTable(
                name: "mailbox_messages");

            migrationBuilder.DropTable(
                name: "mta_sts_policy_cache");

            migrationBuilder.DropTable(
                name: "permission_grants");

            migrationBuilder.DropTable(
                name: "quarantine_records");

            migrationBuilder.DropTable(
                name: "quota_ledger");

            migrationBuilder.DropTable(
                name: "replication_cursors");

            migrationBuilder.DropTable(
                name: "replication_log");

            migrationBuilder.DropTable(
                name: "sent_copy_jobs");

            migrationBuilder.DropTable(
                name: "srs_address_mappings");

            migrationBuilder.DropTable(
                name: "system_settings");

            migrationBuilder.DropTable(
                name: "task_executions");

            migrationBuilder.DropTable(
                name: "task_leases");

            migrationBuilder.DropTable(
                name: "tls_delivery_events");

            migrationBuilder.DropTable(
                name: "tls_report_deliveries");

            migrationBuilder.DropTable(
                name: "delivery_jobs");

            migrationBuilder.DropTable(
                name: "mailboxes");

            migrationBuilder.DropTable(
                name: "mail_rules");

            migrationBuilder.DropTable(
                name: "scheduled_tasks");

            migrationBuilder.DropTable(
                name: "stored_messages");

            migrationBuilder.DropTable(
                name: "mail_accounts");

            migrationBuilder.DropTable(
                name: "mail_domains");
        }
    }
}
