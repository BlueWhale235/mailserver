namespace MailServer.Infrastructure;

public enum RelaySecurity
{
    Auto = 0,
    Plain = 1,
    StartTls = 2,
    StartTlsWhenAvailable = 3,
    ImplicitTls = 4
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public string RootPath { get; set; } = "data/messages";
    public long MaximumMessageBytes { get; set; } = 52_428_800;
}

public sealed class RelayOptions
{
    public const string SectionName = "Relay";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 25;
    public string? Security { get; set; }
    public bool UseStartTls { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }

    public RelaySecurity EffectiveSecurity
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Security))
            {
                return Enum.TryParse<RelaySecurity>(Security, ignoreCase: true, out var parsed)
                    ? parsed
                    : throw new FormatException(
                        "Relay:Security must be Auto, Plain, StartTls, StartTlsWhenAvailable, or ImplicitTls.");
            }

            return Port == 465 && UseStartTls
                ? RelaySecurity.ImplicitTls
                : UseStartTls
                    ? RelaySecurity.StartTls
                    : RelaySecurity.Auto;
        }
    }
}

public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";
    public int BatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 120;
    public int MaximumAttempts { get; set; } = 12;
    public int PollIntervalSeconds { get; set; } = 5;
}

public sealed class MailboxDefaultsOptions
{
    public const string SectionName = "MailboxDefaults";
    public List<string> Folders { get; set; } = ["INBOX", "Sent", "Drafts", "Trash", "Junk", "Archives"];
}

public sealed class MailRulesOptions
{
    public const string SectionName = "MailRules";
    public int SentCopyGraceSeconds { get; set; } = 60;
    public int SentCopyDeduplicationMinutes { get; set; } = 15;
}

public sealed class ForwardingOptions
{
    public const string SectionName = "Forwarding";
    public string SrsSecret { get; set; } = string.Empty;
    public int MaxHops { get; set; } = 5;
    public int MaxAgeDays { get; set; } = 21;
    public int MaxTargetsPerRule { get; set; } = 20;
}

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
