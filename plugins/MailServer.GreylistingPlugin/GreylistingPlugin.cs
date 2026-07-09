using System.Collections.Concurrent;
using System.Net;
using MailServer.PluginSdk;
using Microsoft.Extensions.DependencyInjection;

namespace MailServer.GreylistingPlugin;

public sealed class GreylistingPlugin : IMailServerPlugin
{
    public void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IMessageAcceptanceMiddleware, GreylistingMiddleware>();
}

public sealed class GreylistingMiddleware : IMessageAcceptanceMiddleware
{
    private static readonly ConcurrentDictionary<string, GreylistRecord> Records = new(StringComparer.OrdinalIgnoreCase);

    public Task<PluginDecision> InvokeAsync(MessageAcceptanceContext context, CancellationToken cancellationToken)
    {
        var settings = context.Settings ?? new PluginSettingsSnapshot(true, new Dictionary<string, System.Text.Json.JsonElement>());
        var now = DateTimeOffset.UtcNow;
        var minimumRetry = TimeSpan.FromMinutes(Math.Clamp(settings.GetInteger("minimumRetryMinutes", 5), 0, 1440));
        var expiry = TimeSpan.FromDays(Math.Clamp(settings.GetInteger("recordExpiryDays", 30), 1, 365));
        Cleanup(now, expiry);

        var key = BuildKey(context, settings);
        var record = Records.AddOrUpdate(
            key,
            _ => new GreylistRecord(now, null),
            (_, current) => current.LastAcceptedAt is not null && now - current.LastAcceptedAt.Value > expiry
                ? new GreylistRecord(now, null)
                : current);

        if (record.LastAcceptedAt is not null)
        {
            Records[key] = record with { LastAcceptedAt = now };
            return Task.FromResult(PluginDecision.Continue());
        }

        if (now - record.FirstSeenAt < minimumRetry)
        {
            var waitMinutes = Math.Max(1, (int)Math.Ceiling((minimumRetry - (now - record.FirstSeenAt)).TotalMinutes));
            return Task.FromResult(new PluginDecision(
                PluginDecisionKind.Defer,
                $"Greylisted; retry after about {waitMinutes} minute(s)."));
        }

        Records[key] = record with { LastAcceptedAt = now };
        return Task.FromResult(PluginDecision.Continue());
    }

    private static string BuildKey(MessageAcceptanceContext context, PluginSettingsSnapshot settings)
    {
        var remote = NormalizeRemote(context.RemoteAddress);
        var sender = settings.GetBoolean("includeSender", true) ? context.Sender : "*";
        var recipients = settings.GetBoolean("includeRecipients", true)
            ? string.Join(',', context.Recipients.Order(StringComparer.OrdinalIgnoreCase))
            : "*";
        var includeIp = settings.GetBoolean("includeRemoteIp", true);
        return $"{(includeIp ? remote : "*")}|{sender}|{recipients}";
    }

    private static string NormalizeRemote(string remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return "unknown";
        var trimmed = remote.Trim();
        if (IPEndPoint.TryParse(trimmed, out var endpoint)) return endpoint.Address.ToString();
        var colon = trimmed.LastIndexOf(':');
        if (colon > 0 && IPAddress.TryParse(trimmed[..colon], out var address)) return address.ToString();
        return trimmed;
    }

    private static void Cleanup(DateTimeOffset now, TimeSpan expiry)
    {
        foreach (var pair in Records)
        {
            var reference = pair.Value.LastAcceptedAt ?? pair.Value.FirstSeenAt;
            if (now - reference > expiry)
                Records.TryRemove(pair.Key, out _);
        }
    }

    private sealed record GreylistRecord(DateTimeOffset FirstSeenAt, DateTimeOffset? LastAcceptedAt);
}
