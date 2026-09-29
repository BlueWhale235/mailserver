using System.Text.Json.Serialization;

namespace MailServer.Domain;

/// <summary>
/// 外发队列任务状态。Processing 必须配合 LeaseUntil 判断是否可被重新领取。
/// </summary>
public enum DeliveryStatus
{
    Pending = 0,
    Processing = 1,
    Delivered = 2,
    Deferred = 3,
    Failed = 4
}

public enum CertificateSource
{
    ManagedUpload = 0,
    ExternalPath = 1
}

public enum ConnectionSecurity
{
    Plain = 0,
    StartTls = 1,
    ImplicitTls = 2
}

public enum MailRuleDirection
{
    Incoming = 0,
    Outgoing = 1
}

public enum MailRuleMatchMode
{
    All = 0,
    Any = 1
}

public enum MailAliasMode
{
    SendReceive = 0,
    ReceiveOnly = 1,
    Disabled = 2
}

public enum MailRuleActionKind
{
    MoveToFolder = 0,
    ForwardTo = 1,
    Reject = 2
}

public enum MailRuleField
{
    Sender = 0,
    SenderDomain = 1,
    Recipient = 2,
    Cc = 3,
    RecipientOrCc = 4,
    Subject = 5,
    Body = 6,
    AttachmentName = 7,
    MessageSize = 8,
    MessageTime = 9,
    HasAttachment = 10
}

public enum MailRuleOperator
{
    ContainsAny = 0,
    ContainsAll = 1,
    EqualsAny = 2,
    NotContainsAny = 3,
    GreaterThan = 4,
    GreaterThanOrEqual = 5,
    LessThan = 6,
    LessThanOrEqual = 7,
    Before = 8,
    After = 9,
    Between = 10,
    Is = 11
}

public enum DomainDnsCheckStatus
{
    PendingConfiguration = 0,
    Checking = 1,
    Healthy = 2,
    Unhealthy = 3
}

public enum SentCopyState
{
    PendingClient = 0,
    MovePending = 1,
    Processing = 2,
    Completed = 3
}

public enum ScheduledTaskStatus { Idle, Running, Paused, Failed }
public enum TaskExecutionStatus { Running, Succeeded, Failed, Cancelled }
public enum CleanupResourceType { Domain, Account, Mailbox, MailboxMessage, ContentFile }
public enum CleanupQueueStatus { Pending, Processing, Completed, Failed, Restored }
public enum QuotaLedgerOperation { Add, Remove, Reconcile }
public enum QuarantineStatus { PendingReview, Released, Deleted }
public enum MailboxFolderDeletionStatus { Pending, Running, Failed, Completed }

public sealed class SystemSetting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

/// <summary>
/// 邮件域名，是账号、别名、证书、DNS 策略和配额的租户边界。
/// </summary>
public sealed class MailDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public string? MailHostName { get; set; }
    public string? SmtpHostName { get; set; }
    public string? OutboundIpAddresses { get; set; }
    public long QuotaBytes { get; set; } = 10_737_418_240;
    public long UsedBytes { get; set; }
    public bool DirectDeliverSameDomain { get; set; } = true;
    public string? DkimSelector { get; set; }
    public string? DkimPrivateKeyPath { get; set; }
    public string? DkimPublicKey { get; set; }
    public DomainCertificate? Certificate { get; set; }
    public DomainDnsSettings? DnsSettings { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public DateTimeOffset? PurgeAfter { get; set; }
}

/// <summary>
/// 域名 TLS 证书元数据。证书文件可能来自上传托管，也可能指向外部路径。
/// </summary>
public sealed class DomainCertificate
{
    public Guid DomainId { get; set; }
    [JsonIgnore]
    public MailDomain Domain { get; set; } = null!;
    public CertificateSource Source { get; set; }
    public string CertificatePath { get; set; } = string.Empty;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string SubjectAlternativeNames { get; set; } = string.Empty;
    public string Thumbprint { get; set; } = string.Empty;
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 域名 DNS 安全配置，覆盖 SPF、DMARC、MTA-STS、TLS-RPT 和 DANE。
/// </summary>
public sealed class DomainDnsSettings
{
    public Guid DomainId { get; set; }
    [JsonIgnore]
    public MailDomain Domain { get; set; } = null!;
    public bool SpfIncludeMx { get; set; } = true;
    public bool SpfIncludeA { get; set; }
    public string SpfIpv4 { get; set; } = string.Empty;
    public string SpfIpv6 { get; set; } = string.Empty;
    public string SpfIncludes { get; set; } = string.Empty;
    public string SpfCustomMechanisms { get; set; } = string.Empty;
    public string SpfAllPolicy { get; set; } = "~all";
    public string DmarcPolicy { get; set; } = "none";
    public string? DmarcSubdomainPolicy { get; set; }
    public int DmarcPercentage { get; set; } = 100;
    public string DmarcAggregateReportUris { get; set; } = string.Empty;
    public string DmarcForensicReportUris { get; set; } = string.Empty;
    public string DmarcDkimAlignment { get; set; } = "r";
    public string DmarcSpfAlignment { get; set; } = "r";
    public string DmarcFailureOptions { get; set; } = "0";
    public string MtaStsMode { get; set; } = "none";
    public string MtaStsPolicyId { get; set; } = "1";
    public int MtaStsMaxAgeSeconds { get; set; } = 604800;
    public string MtaStsMxPatterns { get; set; } = string.Empty;
    public string TlsReportUris { get; set; } = string.Empty;
    public bool DaneEnabled { get; set; }
    public string DaneTlsaRecords { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// MTA-STS 策略缓存，避免每次直连外发都重新拉取远端策略文件。
/// </summary>
public sealed class MtaStsPolicyCache
{
    public required string Domain { get; set; }
    public required string PolicyId { get; set; }
    public required string Mode { get; set; }
    public required string MxPatterns { get; set; }
    public int MaxAgeSeconds { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? LastRefreshAttemptAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// TLS 投递事件，用于生成 TLS-RPT 聚合报告。
/// </summary>
public sealed class TlsDeliveryEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string PolicyDomain { get; set; }
    public required string MxHost { get; set; }
    public required string PolicyType { get; set; }
    public string PolicyString { get; set; } = string.Empty;
    public required string ResultType { get; set; }
    public string FailureReason { get; set; } = string.Empty;
    public string SendingIp { get; set; } = string.Empty;
    public string ReceivingIp { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public bool Reported { get; set; }
}

/// <summary>
/// TLS-RPT 报告投递任务，失败后按队列策略重试。
/// </summary>
public sealed class TlsReportDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string PolicyDomain { get; set; }
    public required string ReportUri { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public required string ReportJson { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastError { get; set; }
    public string? LastManualRetryKey { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
}

/// <summary>
/// 邮箱账号。LocalPart 与 DomainId 共同决定完整邮箱地址。
/// </summary>
public sealed class MailAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }
    public MailDomain Domain { get; set; } = null!;
    public required string LocalPart { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsEnabled { get; set; } = true;
    public long QuotaBytes { get; set; } = 1_073_741_824;
    public long UsedBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public DateTimeOffset? PurgeAfter { get; set; }
}

/// <summary>
/// 域内别名，可指向本地账号或外部地址；Mode 控制是否允许收发双向使用。
/// </summary>
public sealed class MailAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }
    public MailDomain Domain { get; set; } = null!;
    public required string SourceLocalPart { get; set; }
    public Guid? TargetAccountId { get; set; }
    public MailAccount? TargetAccount { get; set; }
    public string? DestinationAddress { get; set; }
    public MailAliasMode Mode { get; set; } = MailAliasMode.SendReceive;
    // Retained for one schema generation so older API clients and databases can upgrade safely.
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MailRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }
    public MailDomain Domain { get; set; } = null!;
    public required string Name { get; set; }
    public MailRuleDirection Direction { get; set; }
    public MailRuleMatchMode MatchMode { get; set; }
    public required string TargetMailboxName { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int Priority { get; set; }
    public List<MailRuleCondition> Conditions { get; set; } = [];
    public List<MailRuleAction> Actions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MailRuleAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public MailRule Rule { get; set; } = null!;
    public MailRuleActionKind Kind { get; set; }
    public required string ValuesJson { get; set; }
    public int Position { get; set; }
}

public sealed class MailRuleCondition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public MailRule Rule { get; set; } = null!;
    public MailRuleField Field { get; set; }
    public MailRuleOperator Operator { get; set; }
    public required string ValuesJson { get; set; }
    public int Position { get; set; }
}

public sealed class DomainDnsCheck
{
    public Guid DomainId { get; set; }
    public MailDomain Domain { get; set; } = null!;
    public DomainDnsCheckStatus Status { get; set; } = DomainDnsCheckStatus.PendingConfiguration;
    public string DetailsJson { get; set; } = string.Empty;
    public string? LastError { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
}

public sealed class Mailbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public MailAccount Account { get; set; } = null!;
    public required string Name { get; set; }
    public long UidValidity { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public long UidNext { get; set; } = 1;
    public long HighestModSeq { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public DateTimeOffset? PurgeAfter { get; set; }
    public Guid? PendingDeletionJobId { get; set; }
}

public sealed class MailboxFolderDeletionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }
    public MailDomain Domain { get; set; } = null!;
    public Guid? AccountId { get; set; }
    public required string FolderName { get; set; }
    public MailboxFolderDeletionStatus Status { get; set; } = MailboxFolderDeletionStatus.Pending;
    public int AffectedAccountCount { get; set; }
    public int AffectedMailboxCount { get; set; }
    public int AffectedRuleCount { get; set; }
    public long TotalMessages { get; set; }
    public long ProcessedMessages { get; set; }
    public int Attempts { get; set; }
    public string RequestedBy { get; set; } = "system";
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StoredMessage
{
    public string? SourceTraceId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string ContentKey { get; set; }
    public required string EnvelopeFrom { get; set; }
    public string? InternetMessageId { get; set; }
    public string? InternetMessageIdHash { get; set; }
    public string? SourceType { get; set; }
    public Guid? AuthenticatedAccountId { get; set; }
    public string? AuthenticatedAccountName { get; set; }
    public string? SourceIp { get; set; }
    public long Size { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StoredMessageRecipient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public StoredMessage Message { get; set; } = null!;
    public required string Address { get; set; }
}

public sealed class MailboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MailboxId { get; set; }
    public Mailbox Mailbox { get; set; } = null!;
    public Guid MessageId { get; set; }
    public StoredMessage Message { get; set; } = null!;
    public long Uid { get; set; }
    public string Flags { get; set; } = string.Empty;
    public long ModSeq { get; set; } = 1;
    public DateTimeOffset InternalDate { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ScheduledDeleteAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public DateTimeOffset? PurgeAfter { get; set; }
}

public sealed class QuarantineRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DomainId { get; set; }
    public MailDomain Domain { get; set; } = null!;
    public Guid MessageId { get; set; }
    public StoredMessage Message { get; set; } = null!;
    public required string Sender { get; set; }
    public required string RecipientsJson { get; set; }
    public string RemoteAddress { get; set; } = string.Empty;
    public string? PluginId { get; set; }
    public string? PluginName { get; set; }
    public string Reason { get; set; } = "插件策略要求隔离审核";
    public QuarantineStatus Status { get; set; } = QuarantineStatus.PendingReview;
    public DateTimeOffset QuarantinedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
}

public sealed class ScheduledTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Type { get; set; }
    public required string Name { get; set; }
    public bool IsSystem { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 600;
    public int BatchSize { get; set; } = 100;
    public int MaximumAttempts { get; set; } = 3;
    public ScheduledTaskStatus Status { get; set; }
    public DateTimeOffset NextRunAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastRunAt { get; set; }
    public string SettingsJson { get; set; } = "{}";
}

public sealed class TaskExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScheduledTaskId { get; set; }
    public ScheduledTask ScheduledTask { get; set; } = null!;
    public TaskExecutionStatus Status { get; set; } = TaskExecutionStatus.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public int ProcessedCount { get; set; }
    public long ReleasedBytes { get; set; }
    public int Attempt { get; set; } = 1;
    public string? Error { get; set; }
}

public sealed class TaskLease
{
    public Guid ScheduledTaskId { get; set; }
    public required string Owner { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class CleanupQueueItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CleanupResourceType ResourceType { get; set; }
    public Guid? ResourceId { get; set; }
    public string? ContentKey { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? DomainId { get; set; }
    public long EstimatedBytes { get; set; }
    public CleanupQueueStatus Status { get; set; } = CleanupQueueStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset PurgeAfter { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class QuotaLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string IdempotencyKey { get; set; }
    public Guid AccountId { get; set; }
    public Guid DomainId { get; set; }
    public Guid? MailboxMessageId { get; set; }
    public QuotaLedgerOperation Operation { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}


public sealed class SentCopyJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public MailAccount Account { get; set; } = null!;
    public Guid MessageId { get; set; }
    public StoredMessage Message { get; set; } = null!;
    public Guid? RuleId { get; set; }
    public MailRule? Rule { get; set; }
    public required string TargetMailboxName { get; set; }
    public string? IdentityKey { get; set; }
    public SentCopyState State { get; set; } = SentCopyState.PendingClient;
    public Guid? ClientMailboxMessageId { get; set; }
    public Guid? ServerMailboxMessageId { get; set; }
    public DateTimeOffset ReadyAt { get; set; }
    public DateTimeOffset DeduplicateUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class DeliveryJob
{
    public string? SourceTraceId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public StoredMessage Message { get; set; } = null!;
    public required string Sender { get; set; }
    public required string Recipient { get; set; }
    public Guid? RouteDomainId { get; set; }
    public MailDomain? RouteDomain { get; set; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? LastError { get; set; }
    public Guid? OriginRuleId { get; set; }
    public int ForwardDepth { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FirewallRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Cidr { get; set; }
    public string? Note { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 跨进程配置代次。固定作用域由基础设施层维护，业务写入与代次递增必须同事务提交。
/// </summary>
public sealed class ConfigurationVersion
{
    public required string Scope { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 配置缓存进程心跳，用于管理端判断各协议进程是否已经应用最新代次。
/// </summary>
public sealed class ConfigurationProcessStatus
{
    public string? ResourceStatusJson { get; set; }
    public required string ProcessId { get; set; }
    public required string InstanceName { get; set; }
    public required string Roles { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public long RulesVersion { get; set; }
    public long FirewallVersion { get; set; }
    public long SettingsVersion { get; set; }
    public long RoutingVersion { get; set; }
    public long EstimatedCacheBytes { get; set; }
    public int EntryCount { get; set; }
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long ReloadFailures { get; set; }
    public string? LastError { get; set; }
}

public sealed class DeliveryAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeliveryJobId { get; set; }
    public DeliveryJob DeliveryJob { get; set; } = null!;
    public int AttemptNumber { get; set; }
    public bool Succeeded { get; set; }
    public bool PermanentFailure { get; set; }
    public string? RemoteHost { get; set; }
    public string? RemoteAddress { get; set; }
    public string Stage { get; set; } = "SMTP";
    public string? SmtpStatusCode { get; set; }
    public string? EnhancedStatusCode { get; set; }
    public string? TlsProtocol { get; set; }
    public string? TlsCipherSuite { get; set; }
    public long DurationMilliseconds { get; set; }
    public string? FailureCategory { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    public string? SessionSummary { get; set; }
    public string? Response { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class SrsAddressMapping
{
    public required string Token { get; set; }
    public Guid DomainId { get; set; }
    public MailDomain Domain { get; set; } = null!;
    public required string OriginalSender { get; set; }
    public int ForwardDepth { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string UserName { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum PermissionSubjectType
{
    AdminUser = 0,
    ApiKey = 1
}

public sealed class PermissionGrant
{
    public PermissionSubjectType SubjectType { get; set; }
    public Guid SubjectId { get; set; }
    public required string PermissionCode { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public string ResourceKey { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = "system";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ApiKeyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Prefix { get; set; }
    public required string SecretHash { get; set; }
    public string CreatedBy { get; set; } = "system";
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Resource { get; set; }
    public string? Detail { get; set; }
    public string? RemoteAddress { get; set; }
    public AuditCategory? Category { get; set; }
    public AuditStatus? Status { get; set; }
    public string? RequestId { get; set; }
    public string? RelatedId { get; set; }
    public long? DurationMilliseconds { get; set; }
    public string? ChangeSummaryJson { get; set; }
    public string? RawJson { get; set; }
    public string? PluginId { get; set; }
    public string? PluginName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum AuditCategory
{
    Create,
    Update,
    Delete,
    BackgroundTask,
    Security,
    Plugin,
    Other
}

public enum AuditStatus
{
    Running,
    Succeeded,
    Failed,
    Warning
}
