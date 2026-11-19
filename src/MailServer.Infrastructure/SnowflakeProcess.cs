using MailServer.Domain;
using Microsoft.Extensions.Configuration;

namespace MailServer.Infrastructure;

public static class SnowflakeProcess
{
    public static SnowflakeGenerator Acquire(IConfiguration configuration, string role)
    {
        if (configuration.GetValue("Node:Enabled", false) && configuration["Snowflake:MachineId"] is null)
            throw new InvalidOperationException("Multi-node deployments require an explicit, unique Snowflake:MachineId on each machine.");
        var machine = configuration.GetValue("Snowflake:MachineId", 0);
        var defaultSlot = role switch { "api" => 0, "smtp" => 1, "imap" => 2, "worker" => 3, "cli" => 4, "all" => 5, _ => throw new ArgumentException("Unknown process role.", nameof(role)) };
        var slot = configuration.GetValue("Snowflake:ProcessSlot", defaultSlot);
        if (machine is < 0 or > 127 || slot is < 0 or > 7)
            throw new InvalidOperationException("Snowflake MachineId must be 0..127 and ProcessSlot must be 0..7.");
        var worker = machine * 8 + slot;
        var directory = configuration["Snowflake:StateDirectory"] ?? "data/snowflake";
        var generator = new SnowflakeGenerator(worker, statePath: Path.Combine(directory, $"worker-{worker}.state"));
        SnowflakeIds.Configure(generator);
        return generator;
    }
}
