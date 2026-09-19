using MailServer.PluginSdk.Auditing;
using MailServer.PluginSdk.Delivery;
using MailServer.PluginSdk.Imap;
using MailServer.PluginSdk.Security;
using MailServer.PluginSdk.Smtp;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace MailServer.PluginSdk;

/// <summary>
/// 插件宿主和插件程序集之间的契约版本。修改这里意味着插件二进制兼容性发生变化，需要同步升级插件清单。
/// </summary>
public static class PluginContract
{
    public const string Version = "2.0";
}

/// <summary>
/// 插件对当前处理点的决策结果。Continue 表示放行，其它值会被宿主转换成对应协议响应或队列状态。
/// </summary>
public enum PluginDecisionKind
{
    Continue,
    Reject,
    Defer,
    Quarantine
}

/// <summary>
/// 插件中间件返回给宿主的统一决策。Message 会暴露到日志，部分场景也会返回给 SMTP/IMAP 客户端。
/// </summary>
public sealed record PluginDecision(PluginDecisionKind Kind, string? Message = null)
{
    public static PluginDecision Continue() => new(PluginDecisionKind.Continue);
}

/// <summary>
/// 标识当前插件回调来自哪个邮件协议，便于同一个插件复用认证、连接或审计逻辑。
/// </summary>
public enum MailProtocol
{
    Smtp,
    Imap
}

/// <summary>
/// 插件通过宿主发送邮件的请求。宿主会走正常入站链路，因此会触发规则、配额、队列和审计。
/// </summary>
public sealed record PluginMailSendRequest(
    string Sender,
    IReadOnlyList<string> Recipients,
    Stream Content,
    string? AuthenticatedUser = null,
    string RemoteAddress = "plugin",
    string HeloName = "plugin.local");

/// <summary>
/// 插件投递请求被宿主接受后的结果，包含本地投递数和进入外发队列的数量。
/// </summary>
public sealed record PluginMailSendResult(Guid MessageId, string ContentKey, int LocalDeliveries, int QueuedDeliveries);

/// <summary>
/// 插件可见的队列任务摘要，避免直接暴露 EF 实体和内部状态机。
/// </summary>
public sealed record PluginDeliveryJobInfo(
    Guid JobId,
    Guid MessageId,
    string Sender,
    string Recipient,
    string Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    string? LastError);

/// <summary>
/// 插件可见的域名信息，供策略插件按域名状态、外发模式或配额做判断。
/// </summary>
public sealed record PluginDomainInfo(
    Guid DomainId,
    string Name,
    bool IsActive,
    long QuotaBytes);

/// <summary>
/// 插件可见的账号信息，通常用于权限、自动归档或外部目录同步。
/// </summary>
public sealed record PluginAccountInfo(
    Guid AccountId,
    Guid DomainId,
    string Address,
    bool IsEnabled,
    long QuotaBytes);

/// <summary>
/// 收件人解析结果，帮助插件判断地址是本地账号、域内别名还是需要外发中继。
/// </summary>
public sealed record PluginRecipientResolution(
    string Address,
    bool IsLocal,
    Guid? AccountId,
    Guid? DomainId);

/// <summary>
/// 已保存邮件的元数据视图。正文需要通过 IPluginMessageStore.OpenReadAsync 单独读取。
/// </summary>
public sealed record PluginStoredMessageInfo(
    Guid MessageId,
    string ContentKey,
    string EnvelopeFrom,
    string? InternetMessageId,
    long Size,
    DateTimeOffset ReceivedAt);

/// <summary>
/// 插件可见的邮箱摘要，字段含义遵循 IMAP 的 UIDVALIDITY、UIDNEXT 和 MODSEQ 语义。
/// </summary>
public sealed record PluginMailboxInfo(
    Guid MailboxId,
    string Name,
    long Exists,
    long Unseen,
    long UidValidity,
    long UidNext,
    long HighestModSeq);

/// <summary>
/// 插件可见的邮箱邮件摘要，用于移动、复制、标记等批量操作后的结果回传。
/// </summary>
public sealed record PluginMailboxMessageInfo(
    Guid MailboxMessageId,
    Guid MessageId,
    Guid MailboxId,
    string MailboxName,
    long Uid,
    IReadOnlyList<string> Flags,
    long ModSeq,
    DateTimeOffset InternalDate,
    long Size,
    string ContentKey);

/// <summary>
/// 插件邮箱操作结果。Move/Copy/Flag 操作会返回源 UID、目标 UID 和涉及的消息 ID，便于外部系统对账。
/// </summary>
public sealed record PluginMailboxOperationResult(
    Guid MailboxId,
    string MailboxName,
    long UidValidity,
    IReadOnlyList<long> SourceUids,
    IReadOnlyList<long> TargetUids,
    IReadOnlyList<Guid> MessageIds);

/// <summary>
/// 插件选择邮箱邮件的条件。调用方至少应提供 Uids 或 MailboxMessageIds 中的一种。
/// </summary>
public sealed record PluginMailboxMessageSelector(
    IReadOnlyList<long>? Uids = null,
    IReadOnlyList<Guid>? MailboxMessageIds = null);

/// <summary>
/// 插件对 IMAP 标志的修改模式，对应替换、追加和移除三种语义。
/// </summary>
public enum PluginMailboxFlagOperation
{
    Set,
    Add,
    Remove
}

/// <summary>
/// 插件读取到的单个消息头。
/// </summary>
public sealed record PluginMessageHeader(string Name, string Value);

/// <summary>
/// 插件写入或覆盖消息头的请求项。宿主负责重新生成消息内容。
/// </summary>
public sealed record PluginMessageHeaderUpdate(string Name, string Value);

/// <summary>
/// 插件主动创建隔离记录的请求。Content 会被宿主保存为正常消息内容，再关联到隔离审核流程。
/// </summary>
public sealed record PluginQuarantineCreateRequest(
    string Sender,
    IReadOnlyList<string> Recipients,
    string RemoteAddress,
    Stream Content,
    string? Reason = null);

/// <summary>
/// 插件可见的隔离记录摘要，隐藏审核人等管理端细节。
/// </summary>
public sealed record PluginQuarantineRecord(
    Guid Id,
    Guid DomainId,
    Guid MessageId,
    string Sender,
    IReadOnlyList<string> Recipients,
    string RemoteAddress,
    string Reason,
    string Status,
    DateTimeOffset QuarantinedAt);

/// <summary>
/// 带来源信息的插件决策，用于宿主在隔离、日志和管理后台中展示触发插件。
/// </summary>
public sealed record PluginDecisionResult(PluginDecision Decision, string? PluginId, string? PluginName);

/// <summary>
/// 连接建立时运行的插件中间件。返回 Reject/Defer 会在协议欢迎语前终止连接。
/// </summary>
public interface IConnectionMiddleware
{
    Task<PluginDecision> InvokeAsync(ConnectionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// TLS 建立后的通知中间件。这里的异常只影响插件自身日志，不应破坏已建立的会话。
/// </summary>
public interface ITlsEstablishedMiddleware
{
    Task InvokeAsync(TlsEstablishedContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 自定义认证中间件。GetMechanisms 决定宿主向客户端公布哪些认证机制。
/// </summary>
public interface IAuthenticationMiddleware
{
    IReadOnlyCollection<string> GetMechanisms(MailProtocol protocol);
    Task<AuthenticationResult> AuthenticateAsync(AuthenticationContext context, CancellationToken cancellationToken);
}

/// <summary>
/// SMTP DATA 前的会话策略中间件。
/// </summary>
public interface ISmtpSessionMiddleware
{
    Task<PluginDecision> InvokeAsync(SmtpSessionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// SMTP MAIL FROM 阶段的发件人策略中间件。
/// </summary>
public interface ISmtpSenderMiddleware
{
    Task<PluginDecision> InvokeAsync(SmtpSenderContext context, CancellationToken cancellationToken);
}

/// <summary>
/// SMTP RCPT TO 阶段的收件人策略中间件。
/// </summary>
public interface ISmtpRecipientMiddleware
{
    Task<PluginDecision> InvokeAsync(SmtpRecipientContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 消息准入中间件，可返回 Quarantine 将消息放入隔离审核而不是直接拒绝。
/// </summary>
public interface IMessageAcceptanceMiddleware
{
    Task<PluginDecision> InvokeAsync(MessageAcceptanceContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 原始消息内容检查中间件。适合 MIME、附件、病毒扫描或 DLP 规则。
/// </summary>
public interface IMessageContentMiddleware
{
    Task<PluginDecision> InvokeAsync(MessageContentContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 消息被接受后的通知中间件，不参与是否接收邮件的决策。
/// </summary>
public interface IMessageAcceptedMiddleware
{
    Task InvokeAsync(MessageAcceptedContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 外发投递前中间件。Reject 会被视为永久失败，Defer 会进入重试。
/// </summary>
public interface IDeliveryMiddleware
{
    Task<PluginDecision> InvokeAsync(DeliveryContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 外发投递完成后的通知中间件。
/// </summary>
public interface IDeliveryCompletedMiddleware
{
    Task InvokeAsync(DeliveryCompletedContext context, CancellationToken cancellationToken);
}

/// <summary>
/// IMAP 会话策略中间件，通常用于登录后权限检查。
/// </summary>
public interface IImapSessionMiddleware
{
    Task<PluginDecision> InvokeAsync(ImapSessionContext context, CancellationToken cancellationToken);
}

/// <summary>
/// IMAP 命令策略中间件，适合限制删除、移动、写标志等敏感操作。
/// </summary>
public interface IImapCommandMiddleware
{
    Task<PluginDecision> InvokeAsync(ImapCommandContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 审计事件通知中间件，适合转发到 SIEM、Webhook 或插件自己的日志。
/// </summary>
public interface IAuditMiddleware
{
    Task InvokeAsync(AuditContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 插件可用的发信服务入口，复用宿主入站和投递链路，避免插件绕过配额、规则和审计。
/// </summary>
public interface IPluginMailSender
{
    Task<PluginMailSendResult> SendAsync(PluginMailSendRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 插件可用的队列控制接口，用于人工重试、延期、永久失败或取消外发任务。
/// </summary>
public interface IPluginDeliveryQueue
{
    Task<PluginDeliveryJobInfo?> GetAsync(Guid jobId, CancellationToken cancellationToken);
    Task RetryAsync(Guid jobId, CancellationToken cancellationToken);
    Task DeferAsync(Guid jobId, TimeSpan delay, string reason, CancellationToken cancellationToken);
    Task FailPermanentAsync(Guid jobId, string reason, CancellationToken cancellationToken);
    Task CancelAsync(Guid jobId, string reason, CancellationToken cancellationToken);
    Task AddNoteAsync(Guid jobId, string note, CancellationToken cancellationToken);
}

/// <summary>
/// 插件可用的目录查询接口，提供域、账号和收件人解析能力。
/// </summary>
public interface IPluginDirectory
{
    Task<PluginDomainInfo?> FindDomainAsync(string domainName, CancellationToken cancellationToken);
    Task<PluginAccountInfo?> FindAccountAsync(string address, CancellationToken cancellationToken);
    Task<PluginRecipientResolution> ResolveRecipientAsync(string address, CancellationToken cancellationToken);
}

/// <summary>
/// 插件可用的消息存储接口。替换内容或消息头属于高风险操作，调用方应确保 MIME 仍然有效。
/// </summary>
public interface IPluginMessageStore
{
    Task<PluginStoredMessageInfo?> GetAsync(Guid messageId, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(Guid messageId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginMessageHeader>> ReadHeadersAsync(Guid messageId, CancellationToken cancellationToken);
    Task ReplaceContentAsync(Guid messageId, Stream content, CancellationToken cancellationToken);
    Task UpsertHeadersAsync(Guid messageId, IReadOnlyList<PluginMessageHeaderUpdate> headers, CancellationToken cancellationToken);
}

/// <summary>
/// 插件可用的邮箱操作接口，语义尽量贴近 IMAP 命令，方便做自动归档或规则扩展。
/// </summary>
public interface IPluginMailboxStore
{
    Task<IReadOnlyList<PluginMailboxInfo>> ListMailboxesAsync(Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginMailboxInfo>> ListMailboxesAsync(string accountAddress, CancellationToken cancellationToken);
    Task<PluginMailboxInfo> CreateMailboxAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken);
    Task<PluginMailboxInfo> CreateMailboxAsync(string accountAddress, string mailboxName, CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginMailboxMessageInfo>> ListMessagesAsync(Guid accountId, string mailboxName, CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginMailboxMessageInfo>> ListMessagesAsync(string accountAddress, string mailboxName, CancellationToken cancellationToken);
    Task<PluginMailboxOperationResult> MoveAsync(Guid accountId, string sourceMailboxName, PluginMailboxMessageSelector selector, string destinationMailboxName, CancellationToken cancellationToken);
    Task<PluginMailboxOperationResult> CopyAsync(Guid accountId, string sourceMailboxName, PluginMailboxMessageSelector selector, string destinationMailboxName, CancellationToken cancellationToken);
    Task<PluginMailboxOperationResult> SetFlagsAsync(Guid accountId, string mailboxName, PluginMailboxMessageSelector selector, IReadOnlyCollection<string> flags, CancellationToken cancellationToken);
    Task<PluginMailboxOperationResult> AddFlagsAsync(Guid accountId, string mailboxName, PluginMailboxMessageSelector selector, IReadOnlyCollection<string> flags, CancellationToken cancellationToken);
    Task<PluginMailboxOperationResult> RemoveFlagsAsync(Guid accountId, string mailboxName, PluginMailboxMessageSelector selector, IReadOnlyCollection<string> flags, CancellationToken cancellationToken);
    Task<PluginMailboxOperationResult> MarkReadAsync(Guid accountId, string mailboxName, PluginMailboxMessageSelector selector, CancellationToken cancellationToken);
    Task<PluginMailboxOperationResult> MarkUnreadAsync(Guid accountId, string mailboxName, PluginMailboxMessageSelector selector, CancellationToken cancellationToken);
}

/// <summary>
/// 插件写审计日志的接口。actor/action/resource 应保持稳定，方便后续检索和告警。
/// </summary>
public interface IPluginAuditWriter
{
    Task WriteAsync(string actor, string action, string resource, string? detail, string? remoteAddress, CancellationToken cancellationToken);
}

/// <summary>
/// 插件私有状态存储接口，key 由插件自行定义，值使用 JsonElement 以保持契约稳定。
/// </summary>
public interface IPluginStateStore
{
    Task<JsonElement?> GetAsync(string pluginId, string key, CancellationToken cancellationToken);
    Task SetAsync(string pluginId, string key, JsonElement value, CancellationToken cancellationToken);
    Task DeleteAsync(string pluginId, string key, CancellationToken cancellationToken);
}

/// <summary>
/// 插件隔离区接口，插件只能操作自己创建或关联的隔离记录。
/// </summary>
public interface IPluginQuarantineStore
{
    Task<IReadOnlyList<PluginQuarantineRecord>> CreateAsync(
        string pluginId,
        PluginQuarantineCreateRequest request,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PluginQuarantineRecord>> ListPendingAsync(
        string pluginId,
        string? domainName,
        CancellationToken cancellationToken);
    Task ReleaseAsync(string pluginId, Guid quarantineId, string? note, CancellationToken cancellationToken);
    Task DeleteAsync(string pluginId, Guid quarantineId, string? note, CancellationToken cancellationToken);
}

/// <summary>
/// 插件入口类型。插件应在这里注册自己的中间件和服务，不应直接启动长期后台线程。
/// </summary>
public interface IMailServerPlugin
{
    void ConfigureServices(IServiceCollection services);
}

/// <summary>
/// 插件设置提供器。宿主运行时会通过它注入启用状态、排序和清单默认值。
/// </summary>
public interface IPluginSettingsProvider
{
    Task<PluginSettingsSnapshot> GetAsync(string pluginId, CancellationToken cancellationToken);
}

/// <summary>
/// 插件运行时设置快照。插件应读取这个快照，而不是直接解析 plugin.json 或数据库。
/// </summary>
public sealed record PluginSettingsSnapshot(
    bool Enabled,
    IReadOnlyDictionary<string, JsonElement> Values)
{
    public bool GetBoolean(string key, bool fallback = false) =>
        Values.TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    public int GetInteger(string key, int fallback = 0) =>
        Values.TryGetValue(key, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    public string GetString(string key, string fallback = "") =>
        Values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
}

/// <summary>
/// 插件设置类型，影响管理后台如何渲染配置项，以及宿主如何做基本清单校验。
/// </summary>
public enum PluginSettingType
{
    Text,
    Integer,
    Boolean,
    Select,
    Multiselect,
    Password
}

/// <summary>
/// Select/Multiselect 设置的可选值定义。
/// </summary>
public sealed class PluginSettingOption
{
    public required string Value { get; init; }
    public required string Label { get; init; }
}

/// <summary>
/// 插件清单中的单项设置定义。默认值来自 plugin.json，运行时可被管理后台覆盖。
/// </summary>
public sealed class PluginSettingDefinition
{
    public required string Key { get; init; }
    public PluginSettingType Type { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public JsonElement DefaultValue { get; init; }
    public bool Required { get; init; }
    public int? Minimum { get; init; }
    public int? Maximum { get; init; }
    public string? Placeholder { get; init; }
    public PluginSettingOption[] Options { get; init; } = [];
}

/// <summary>
/// 插件清单模型。宿主发现插件时先读取它，再决定是否加载程序集、注册服务和参与流水线。
/// </summary>
public sealed class PluginManifest
{
    public required string Id { get; init; }
    public bool System { get; init; }
    public required string Version { get; init; }
    public required string ContractVersion { get; init; }
    public required string EntryAssembly { get; init; }
    public int Order { get; init; }
    public string[] Hosts { get; init; } = [];
    public int TimeoutMilliseconds { get; init; } = 5000;
    public string FailureMode { get; init; } = "continue";
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public PluginSettingDefinition[] Settings { get; init; } = [];
}
