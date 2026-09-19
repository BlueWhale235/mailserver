namespace MailServer.PluginSdk.Smtp;

/// <summary>
/// SMTP 会话级策略上下文，在 DATA 接收前用于根据 HELO、远端地址和认证身份做整体判断。
/// </summary>
public sealed record SmtpSessionContext(string RemoteAddress, string? HeloName, string? AuthenticatedUser, bool IsTls);

/// <summary>
/// MAIL FROM 阶段上下文，适合做发件人策略、租户边界或认证用户与信封发件人的绑定检查。
/// </summary>
public sealed record SmtpSenderContext(
    string Sender,
    string RemoteAddress,
    string? AuthenticatedUser,
    Guid? AuthenticatedAccountId,
    PluginSettingsSnapshot? Settings = null);

/// <summary>
/// RCPT TO 阶段上下文。AcceptedRecipients 是当前事务内已接受的收件人，可用于限制群发或跨域策略。
/// </summary>
public sealed record SmtpRecipientContext(
    string Sender,
    string Recipient,
    IReadOnlyList<string> AcceptedRecipients,
    string RemoteAddress,
    string? AuthenticatedUser,
    Guid? AuthenticatedAccountId,
    PluginSettingsSnapshot? Settings = null);

/// <summary>
/// DATA 完成后的消息准入上下文，适合基于大小、发收件人组合或已保存内容键做最终策略判断。
/// </summary>
public sealed record MessageAcceptanceContext(
    string Sender,
    IReadOnlyList<string> Recipients,
    string ContentKey,
    long Size,
    string RemoteAddress = "",
    PluginSettingsSnapshot? Settings = null);

/// <summary>
/// 原始消息内容检查上下文。宿主会在调用前后复位流位置，插件读取后也应避免长期持有该 Stream。
/// </summary>
public sealed record MessageContentContext(
    string Sender,
    IReadOnlyList<string> Recipients,
    string BodyType,
    long Size,
    Stream Content,
    PluginSettingsSnapshot Settings);

/// <summary>
/// 消息已经被宿主持久化后的通知上下文。此阶段不再影响 SMTP 接受结果，适合异步审计或旁路集成。
/// </summary>
public sealed record MessageAcceptedContext(
    Guid MessageId,
    string Sender,
    IReadOnlyList<string> Recipients,
    string RemoteAddress,
    string? AuthenticatedUser,
    PluginSettingsSnapshot? Settings = null);
