namespace MailServer.PluginSdk.Delivery;

/// <summary>
/// 投递前策略上下文，适合外发合规检查、灰度暂停或按尝试次数改变投递行为。
/// </summary>
public sealed record DeliveryContext(long JobId, string Sender, string Recipient, int Attempt);

/// <summary>
/// 投递完成通知上下文，适合同步外部工单、告警系统或插件自己的统计状态。
/// </summary>
public sealed record DeliveryCompletedContext(
    long JobId,
    string Sender,
    string Recipient,
    int Attempt,
    bool Succeeded,
    bool PermanentFailure,
    string Response,
    string? RemoteHost,
    PluginSettingsSnapshot? Settings = null);
