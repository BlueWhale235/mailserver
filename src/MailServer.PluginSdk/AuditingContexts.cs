namespace MailServer.PluginSdk.Auditing;

/// <summary>
/// 审计事件上下文。插件接收的是业务动作摘要，不应假设所有数据库字段都会出现在 Detail 中。
/// </summary>
public sealed record AuditContext(string Actor, string Action, string Resource, string? Detail);
