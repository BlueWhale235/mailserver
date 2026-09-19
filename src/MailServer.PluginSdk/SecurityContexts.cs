namespace MailServer.PluginSdk.Security;

/// <summary>
/// 插件认证器的处理状态。NotHandled 允许宿主继续尝试其它认证方式。
/// </summary>
public enum AuthenticationStatus
{
    NotHandled,
    Success,
    Failure
}

/// <summary>
/// 插件认证结果。只有 Success 会建立会话身份，Failure 会终止当前认证尝试。
/// </summary>
public sealed record AuthenticationResult(
    AuthenticationStatus Status,
    string? Identity = null,
    string? Message = null)
{
    public static AuthenticationResult NotHandled() => new(AuthenticationStatus.NotHandled);
    public static AuthenticationResult Success(string identity) => new(AuthenticationStatus.Success, identity);
    public static AuthenticationResult Failure(string? message = null) => new(AuthenticationStatus.Failure, null, message);
}

/// <summary>
/// 连接刚建立时的上下文，适合做 IP 黑白名单、协议级准入和早期限流。
/// </summary>
public sealed record ConnectionContext(
    MailProtocol Protocol,
    string RemoteAddress,
    bool IsTls,
    PluginSettingsSnapshot? Settings = null);

/// <summary>
/// TLS 握手完成后的通知上下文，适合记录加密状态或基于证书信息做审计。
/// </summary>
public sealed record TlsEstablishedContext(
    MailProtocol Protocol,
    string RemoteAddress,
    PluginSettingsSnapshot? Settings = null);

/// <summary>
/// SMTP/IMAP 认证回调上下文。Secret 可能是密码、令牌或一次性凭证，插件不应记录明文。
/// </summary>
public sealed record AuthenticationContext(
    MailProtocol Protocol,
    string Mechanism,
    string? UserName,
    string? Secret,
    string RemoteAddress,
    bool IsTls,
    PluginSettingsSnapshot? Settings = null);
