namespace MailServer.Infrastructure;

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
    public bool UseStartTls { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
}

public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";
    public int BatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 120;
    public int MaximumAttempts { get; set; } = 12;
    public int PollIntervalSeconds { get; set; } = 5;
}

public sealed class PluginOptions
{
    public const string SectionName = "Plugins";
    public string Path { get; set; } = "plugins";
}
