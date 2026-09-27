namespace MailServer.Infrastructure;

/// <summary>
/// 原始邮件内容存储配置。
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public string RootPath { get; set; } = "data/messages";
    public long MaximumMessageBytes { get; set; } = 52_428_800;
}

/// <summary>
/// 外发队列处理参数，控制租约长度、重试次数和轮询频率。
/// </summary>
public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";
    public int BatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 120;
    public int MaximumAttempts { get; set; } = 12;
    public int PollIntervalSeconds { get; set; } = 5;
}

/// <summary>
/// 新账号默认创建的 IMAP 文件夹。
/// </summary>
public sealed class MailboxDefaultsOptions
{
    public const string SectionName = "MailboxDefaults";
    public List<string> Folders { get; set; } = ["INBOX", "Sent", "Drafts", "Trash", "Junk", "Archives"];
}

/// <summary>
/// 邮件规则与 Sent 副本去重相关参数。
/// </summary>
public sealed class MailRulesOptions
{
    public const string SectionName = "MailRules";
    public int SentCopyGraceSeconds { get; set; } = 60;
    public int SentCopyDeduplicationMinutes { get; set; } = 15;
}

/// <summary>
/// 有界配置快照缓存。SizeLimitBytes 是当前进程的预算；拆分部署应为每个进程分别配置。
/// </summary>
public sealed class ConfigurationCacheOptions
{
    public const string SectionName = "ConfigurationCache";
    public bool Enabled { get; set; } = true;
    public long SizeLimitBytes { get; set; } = 4 * 1024 * 1024;
    public long MaximumEntryBytes { get; set; } = 1024 * 1024;
    public int VersionPollSeconds { get; set; } = 3;
    public int AbsoluteRefreshSeconds { get; set; } = 60;
    public int MaximumConcurrentLoads { get; set; } = 2;
    public int RuleFallbackBatchSize { get; set; } = 64;
    public int StatusHeartbeatSeconds { get; set; } = 30;
    public int ProcessStaleSeconds { get; set; } = 90;
    public int ProcessRetentionHours { get; set; } = 24;
}

/// <summary>
/// 转发和 SRS 配置。SrsSecret 为空时不应启用跨域转发生产能力。
/// </summary>
public sealed class ForwardingOptions
{
    public const string SectionName = "Forwarding";
    public string SrsSecret { get; set; } = string.Empty;
    public int MaxHops { get; set; } = 5;
    public int MaxAgeDays { get; set; } = 21;
    public int MaxTargetsPerRule { get; set; } = 20;
}

/// <summary>
/// 插件搜索目录配置，宿主只加载已发布插件目录。
/// </summary>
public sealed class PluginOptions
{
    public const string SectionName = "Plugins";
    public string Path { get; set; } = "plugins";
}

public sealed class SecurityAssetsOptions
{
    public const string SectionName = "SecurityAssets";
    public string RootPath { get; set; } = "security-assets";
}

public sealed class MailServerLoggingOptions
{
    public const string SectionName = "MailServerLogging";
    public MailServerConsoleLoggingOptions Console { get; set; } = new();
    public MailServerFileLoggingOptions File { get; set; } = new();
    public string TimestampFormat { get; set; } = "yyyy/MM/dd HH:mm:ss";
}

public sealed class MailServerConsoleLoggingOptions
{
    public bool Enabled { get; set; } = true;
    public bool Color { get; set; } = true;
}

public sealed class MailServerFileLoggingOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "logs";
}
