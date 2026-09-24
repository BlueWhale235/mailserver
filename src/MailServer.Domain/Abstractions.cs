namespace MailServer.Domain;

/// <summary>
/// 计划任务的一次运行结果，用于管理后台展示处理数量和释放空间。
/// </summary>
public sealed record ScheduledTaskRunResult(int ProcessedCount, long ReleasedBytes, string? Detail = null);

/// <summary>
/// 回收站展示项，代表一个等待恢复或到期清理的软删除资源。
/// </summary>
public sealed record RecycleBinItem(Guid QueueId, CleanupResourceType ResourceType, Guid? ResourceId,
    string DisplayName, long EstimatedBytes, DateTimeOffset DeletedAt, DateTimeOffset PurgeAfter, CleanupQueueStatus Status);


/// <summary>
/// 创建隔离邮件的请求。调用方提供原始内容流，服务负责保存内容并生成审核记录。
/// </summary>
public sealed record QuarantineCreateRequest(
    string Sender,
    IReadOnlyList<string> Recipients,
    string RemoteAddress,
    Stream Content,
    string? PluginId,
    string? PluginName,
    string? Reason);

/// <summary>
/// 隔离区列表项，供管理后台审核、释放或删除邮件。
/// </summary>
public sealed record QuarantineListItem(
    Guid Id,
    Guid DomainId,
    Guid MessageId,
    string Sender,
    IReadOnlyList<string> Recipients,
    string RemoteAddress,
    string? PluginId,
    string? PluginName,
    string Reason,
    QuarantineStatus Status,
    DateTimeOffset QuarantinedAt,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote,
    long Size,
    string? InternetMessageId);

/// <summary>
/// 隔离邮件原文视图，管理后台用于人工审核内容。
/// </summary>
public sealed record QuarantineDetailView(QuarantineListItem Record, string FileName, string Content, bool Truncated);

/// <summary>
/// 隔离邮件原始文件下载。调用方负责在响应结束后释放内容流。
/// </summary>
public sealed record QuarantineRawDownload(string FileName, Stream Content);

/// <summary>
/// 计划任务处理器契约。新增维护任务时实现它并在依赖注入中注册。
/// </summary>
public interface IScheduledTaskHandler
{
    string Type { get; }
    Task<ScheduledTaskRunResult> RunAsync(ScheduledTask task, CancellationToken cancellationToken);
}

public interface ITaskScheduler
{
    Task EnsureDefaultsAsync(CancellationToken cancellationToken);
    Task RunDueAsync(CancellationToken cancellationToken);
    Task<Guid> RunNowAsync(Guid taskId, CancellationToken cancellationToken);
}

public interface ITaskExecutionService
{
    Task<IReadOnlyList<ScheduledTask>> ListTasksAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskExecution>> ListExecutionsAsync(int take, CancellationToken cancellationToken);
    Task UpdateAsync(Guid taskId, bool enabled, int intervalMinutes, int timeoutSeconds, int batchSize, CancellationToken cancellationToken);
}

public interface IRecycleBinService
{
    Task<IReadOnlyList<RecycleBinItem>> ListAsync(CancellationToken cancellationToken);
    Task RestoreAsync(Guid queueId, string actor, CancellationToken cancellationToken);
    Task PurgeAsync(Guid queueId, CancellationToken cancellationToken);
}

public interface ICleanupQueueService
{
    Task EnqueueAsync(CleanupQueueItem item, CancellationToken cancellationToken);
    Task<ScheduledTaskRunResult> PurgeDueAsync(int batchSize, CancellationToken cancellationToken);
}

public interface IQuotaLedgerService
{
    Task ApplyAsync(Guid accountId, Guid domainId, Guid? mailboxMessageId, long bytes,
        QuotaLedgerOperation operation, string idempotencyKey, CancellationToken cancellationToken);
}

public interface IQuotaReconciliationService
{
    Task<ScheduledTaskRunResult> ReconcileAsync(CancellationToken cancellationToken);
}


public interface IQuarantineService
{
    Task<IReadOnlyList<QuarantineRecord>> CreateAsync(QuarantineCreateRequest request, CancellationToken cancellationToken);
    Task<PagedQuarantineResult> SearchAsync(Guid domainId, string? search, QuarantineStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<QuarantineDetailView> GetRawAsync(Guid domainId, Guid id, CancellationToken cancellationToken);
    Task<QuarantineRawDownload> OpenRawAsync(Guid domainId, Guid id, CancellationToken cancellationToken);
    Task ReleaseAsync(Guid domainId, Guid id, string actor, string? note, CancellationToken cancellationToken);
    Task DeleteAsync(Guid domainId, Guid id, string actor, string? note, CancellationToken cancellationToken);
}

/// <summary>
/// 隔离区分页结果，Total 是当前筛选条件下的总数。
/// </summary>
public sealed record PagedQuarantineResult(IReadOnlyList<QuarantineListItem> Items, int Page, int PageSize, int Total);

/// <summary>
/// 内容存储返回的描述符。Key 是后续读取原始邮件内容的稳定引用。
/// </summary>
public sealed record ContentDescriptor(string Key, long Size);

/// <summary>
/// 入站信封信息，来自 SMTP MAIL/RCPT/AUTH 阶段，不等同于 MIME 头部里的 From/To。
/// </summary>
public sealed record InboundEnvelope(
    string Sender,
    IReadOnlyList<string> Recipients,
    string? AuthenticatedUser,
    string RemoteAddress,
    string HeloName,
    Guid? AuthenticatedAccountId = null);

/// <summary>
/// 入站邮件被接受后的结果，包含原始内容键以及本地/外发分发数量。
/// </summary>
public sealed record InboundAcceptance(Guid MessageId, string ContentKey, int LocalDeliveries, int QueuedDeliveries);

/// <summary>
/// 一次外发投递尝试的结果。PermanentFailure 决定队列是否继续重试。
/// </summary>
public sealed record DeliveryDiagnostic(
    string Stage,
    string? RemoteAddress = null,
    string? SmtpStatusCode = null,
    string? EnhancedStatusCode = null,
    string? TlsProtocol = null,
    string? TlsCipherSuite = null,
    long DurationMilliseconds = 0,
    string? FailureCategory = null,
    string? SessionSummary = null);

public sealed record DeliveryResult(
    bool Succeeded,
    bool PermanentFailure,
    string Response,
    string? RemoteHost = null,
    DeliveryDiagnostic? Diagnostic = null)
{
    public static DeliveryResult Success(string response, string? host = null, DeliveryDiagnostic? diagnostic = null) => new(true, false, response, host, diagnostic);
    public static DeliveryResult Temporary(string response, string? host = null, DeliveryDiagnostic? diagnostic = null) => new(false, false, response, host, diagnostic);
    public static DeliveryResult Permanent(string response, string? host = null, DeliveryDiagnostic? diagnostic = null) => new(false, true, response, host, diagnostic);
}

/// <summary>
/// 邮箱摘要，字段含义遵循 IMAP 计数、UIDVALIDITY、UIDNEXT 和 MODSEQ 语义。
/// </summary>
public sealed record MailboxSummary(Guid Id, string Name, long Exists, long Unseen, long UidValidity, long UidNext, long HighestModSeq);

/// <summary>
/// 单封邮箱内邮件的协议视图。
/// </summary>
public sealed record MailboxItem(
    Guid Id,
    long Sequence,
    long Uid,
    string Flags,
    long ModSeq,
    DateTimeOffset InternalDate,
    long Size,
    string ContentKey);

/// <summary>
/// 邮箱 APPEND 结果，用于返回新 UID 和邮箱 UIDNEXT。
/// </summary>
public sealed record AppendResult(MailboxItem Item, long UidValidity, long UidNext);

/// <summary>
/// EXPUNGE 后需要通知客户端的序号和 UID。
/// </summary>
public sealed record ExpungedMailboxItem(long Sequence, long Uid);

/// <summary>
/// COPY/MOVE 操作结果，包含源/目标 UID 映射以及 MOVE 导致的 expunge 信息。
/// </summary>
public sealed record CopyMoveResult(
    long SourceUidValidity,
    long TargetUidValidity,
    IReadOnlyList<long> SourceUids,
    IReadOnlyList<long> TargetUids,
    IReadOnlyList<ExpungedMailboxItem> ExpungedItems);

/// <summary>
/// MIME 检查识别出的风险标签，供搜索、审计和插件策略复用。
/// </summary>
public enum MimeRiskFlag
{
    RemoteResource,
    HtmlScript,
    HtmlStyle,
    AttachmentPathTraversal,
    ArchiveAttachment,
    SvgAttachment,
    PdfAttachment,
    SignedOrEncrypted,
    DeepMimeTree,
    TooManyMimeParts,
    AuthenticationResults,
    ListUnsubscribe,
    AutoSubmitted,
    BulkPrecedence,
    ReplyToDifferentFromFrom
}

/// <summary>
/// MIME 附件摘要，SafeFileName 是展示或落盘时应优先使用的安全文件名。
/// </summary>
public sealed record MimeAttachmentInfo(
    string FileName,
    string SafeFileName,
    string ContentType,
    string? ContentId,
    bool IsInline,
    IReadOnlyList<MimeRiskFlag> RiskFlags);

/// <summary>
/// 日历邀请摘要，用于识别 iCalendar 更新、取消或重复邀请。
/// </summary>
public sealed record CalendarInviteInfo(string? Method, string? Uid, int? Sequence);

/// <summary>
/// 邮件认证相关头部摘要，方便搜索和后续安全策略扩展。
/// </summary>
public sealed record MessageAuthenticationResult(
    IReadOnlyList<string> AuthenticationResults,
    IReadOnlyList<string> ArcResults,
    IReadOnlyList<string> DkimSignatures);

/// <summary>
/// MIME 检查结果，包含可搜索文本、附件、风险标签和常见邮件自动化头部。
/// </summary>
public sealed record MimeInspectionResult(
    string Text,
    IReadOnlyList<MimeAttachmentInfo> Attachments,
    IReadOnlyList<MimeRiskFlag> RiskFlags,
    CalendarInviteInfo? CalendarInvite,
    MessageAuthenticationResult Authentication,
    IReadOnlyList<string> ListUnsubscribe,
    string? ListUnsubscribePost,
    IReadOnlyList<string> ReplyToAddresses);

/// <summary>
/// 可替换时钟抽象，用于测试和后台任务统一获取 UTC 时间。
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// MIME 检查服务抽象，集中处理原始邮件解析、文本提取和文件名/头部清洗。
/// </summary>
public interface IMimeInspectionService
{
    MimeInspectionResult Inspect(byte[] raw);
    MimeInspectionResult Inspect(Stream content);
    string ExtractSearchableText(byte[] raw);
    string SanitizeFileName(string? fileName);
    string SanitizeHeaderPhrase(string value);
}

/// <summary>
/// 消息内容存储抽象。实现应保证 Save 成功后可通过 ContentKey 重新打开原始内容。
/// </summary>
public interface IMessageContentStore
{
    Task<ContentDescriptor> SaveAsync(Stream content, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);
    Task DeleteIfUnreferencedAsync(string key, CancellationToken cancellationToken);
}

/// <summary>
/// 凭据哈希服务抽象，集中封装密码/API Key 的哈希、校验和升级逻辑。
/// </summary>
public interface ICredentialService
{
    string HashPassword(string password);
    bool VerifyPassword(string hash, string password, out string? upgradedHash);
    string HashApiSecret(string secret);
    bool VerifyApiSecret(string hash, string secret);
}

/// <summary>
/// 邮箱账号认证抽象，供 SMTP、IMAP 和管理操作复用同一身份解析逻辑。
/// </summary>
public interface IAccountAuthenticator
{
    Task<Guid?> AuthenticateMailboxAsync(string address, string password, CancellationToken cancellationToken);
    Task<Guid?> ResolveMailboxIdentityAsync(string address, CancellationToken cancellationToken);
}

/// <summary>
/// SMTP 信封发件人授权检查，防止认证用户冒用未授权地址。
/// </summary>
public interface ISmtpSenderAuthorizer
{
    Task<bool> IsAuthorizedSenderAsync(Guid accountId, string sender, CancellationToken cancellationToken);
}

/// <summary>
/// 入站邮件服务抽象。SMTP、退信和插件发信都通过它进入统一分发流程。
/// </summary>
public interface IInboundMessageService
{
    Task<bool> IsLocalRecipientAsync(string address, CancellationToken cancellationToken);
    Task<InboundAcceptance> AcceptAsync(InboundEnvelope envelope, Stream content, CancellationToken cancellationToken);
}

/// <summary>
/// 外发队列抽象，封装租约、完成、重试和删除状态转换。
/// </summary>
public interface IDeliveryQueue
{
    Task<IReadOnlyList<DeliveryJob>> LeaseAsync(string owner, int count, TimeSpan duration, CancellationToken cancellationToken);
    Task CompleteAsync(Guid jobId, DeliveryResult result, CancellationToken cancellationToken);
    Task RetryNowAsync(Guid jobId, CancellationToken cancellationToken, int? expectedAttempts = null);
    Task DeleteAsync(Guid jobId, CancellationToken cancellationToken);
}

/// <summary>
/// Direct MX 外发传输抽象。
/// </summary>
public interface IOutboundTransport
{
    Task<DeliveryResult> DeliverAsync(DeliveryJob job, Stream content, CancellationToken cancellationToken);
}

/// <summary>
/// 邮箱操作服务抽象，承载 IMAP 命令背后的 UID、flag、copy/move 和 APPEND 语义。
/// </summary>
public interface IMailboxService
{
    Task<IReadOnlyList<MailboxSummary>> ListAsync(Guid accountId, CancellationToken cancellationToken);
    Task EnsureDefaultFoldersAsync(Guid accountId, CancellationToken cancellationToken);
    Task<MailboxSummary?> SelectAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken);
    Task<MailboxSummary> CreateAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken);
    Task DeleteAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken);
    Task RenameAsync(Guid accountId, string existingMailboxName, string newMailboxName, CancellationToken cancellationToken);
    Task SubscribeAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken);
    Task UnsubscribeAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken);
    Task<IReadOnlyList<MailboxSummary>> ListSubscribedAsync(Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MailboxItem>> FetchAsync(Guid mailboxId, string sequenceSet, bool useUid, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> SearchAsync(Guid mailboxId, string criteria, bool useUid, CancellationToken cancellationToken);
    Task StoreFlagsAsync(Guid mailboxId, string sequenceSet, bool useUid, string operation, IReadOnlyCollection<string> flags, CancellationToken cancellationToken);
    Task MarkSeenAsync(Guid mailboxId, IReadOnlyCollection<Guid> mailboxMessageIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<ExpungedMailboxItem>> ExpungeAsync(Guid mailboxId, CancellationToken cancellationToken);
    Task<CopyMoveResult> CopyAsync(Guid mailboxId, string sequenceSet, bool useUid, string destination, bool move, CancellationToken cancellationToken);
    Task<AppendResult> AppendAsync(Guid accountId, string mailboxName, Stream content, IReadOnlyCollection<string> flags, DateTimeOffset? internalDate, CancellationToken cancellationToken);
    Task<Stream> OpenMessageAsync(Guid mailboxMessageId, CancellationToken cancellationToken);
}
