namespace MailServer.PluginSdk.Imap;

/// <summary>
/// IMAP 会话级策略上下文，适合在认证后按用户或来源限制邮箱访问。
/// </summary>
public sealed record ImapSessionContext(string RemoteAddress, string? AuthenticatedUser, bool IsTls);

/// <summary>
/// IMAP 命令上下文。ReadOnly 标识当前命令是否可能改变邮箱状态，插件可据此做权限控制。
/// </summary>
public sealed record ImapCommandContext(
    string Command,
    string Arguments,
    Guid? AccountId,
    string? UserName,
    string? MailboxName,
    bool ReadOnly,
    string RemoteAddress,
    PluginSettingsSnapshot? Settings = null);
