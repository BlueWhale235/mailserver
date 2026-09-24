using System.Text.Json;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.RegularExpressions;

namespace MailServer.Infrastructure;

public sealed record DeliverySettingsView(
    int BatchSize,
    int LeaseSeconds,
    int MaximumAttempts,
    int PollIntervalSeconds);

public sealed record SmtpSettingsView(int CommandTimeoutSeconds);

public sealed record MailboxSettingsView(IReadOnlyList<string> DefaultFolders);

public sealed record MessageTemplateSettingsView(
    string SenderDisplayName,
    string PermanentFailureSubject,
    string PermanentFailureBody,
    string RetryExhaustedSubject,
    string RetryExhaustedBody);

public static class MessageTemplateDefaults
{
    public static MessageTemplateSettingsView Create() => new(
        "Mail Delivery Subsystem",
        "Delivery Status Notification (Failure)",
        """
        <section style="font-family:Arial,sans-serif;line-height:1.6;color:#0f172a">
          <h2>Delivery failed</h2>
          <p>This is an automatically generated delivery status notification.</p>
          <p>Delivery to <strong>{recipient}</strong> failed permanently.</p>
          <p><strong>Remote response</strong></p>
          <pre style="white-space:pre-wrap;background:#f8fafc;border:1px solid #e2e8f0;padding:12px;border-radius:6px">{reason}</pre>
          <p>Original queue id: <code>{queueId}</code></p>
        </section>
        """,
        "Delivery Status Notification (Retry exhausted)",
        """
        <section style="font-family:Arial,sans-serif;line-height:1.6;color:#0f172a">
          <h2>Delivery could not be completed</h2>
          <p>This is an automatically generated delivery status notification.</p>
          <p>Delivery to <strong>{recipient}</strong> could not be completed after <strong>{attempts}</strong> attempts.</p>
          <p><strong>Last error</strong></p>
          <pre style="white-space:pre-wrap;background:#f8fafc;border:1px solid #e2e8f0;padding:12px;border-radius:6px">{reason}</pre>
          <p>Original queue id: <code>{queueId}</code></p>
        </section>
        """);
}

public sealed record CertificateSelectionSettingsView(
    string? DefaultMailHostName,
    bool AllowSingleCertificateFallback,
    IReadOnlyList<string> AvailableMailHostNames,
    string? EffectiveDefaultMailHostName);

public sealed class SystemSettingsService(
    IDbContextFactory<MailDbContext> dbFactory,
    IOptions<DeliveryOptions> deliveryDefaults,
    IOptions<MailboxDefaultsOptions> mailboxDefaults,
    IOptions<CertificatesOptions> certificateDefaults)
{
    private const string DeliveryPrefix = "delivery:";
    private const string MailboxPrefix = "mailbox-defaults:";
    private const string MailboxFoldersKey = "folders";
    private const string CertificatePrefix = "certificates:";
    private const string CertificateDefaultHostKey = "default-mail-host-name";
    private const string CertificateSingleFallbackKey = "allow-single-certificate-fallback";
    private const string SmtpPrefix = "smtp:";
    private const string SmtpCommandTimeoutKey = "command-timeout-seconds";
    private const string TemplatePrefix = "message-templates:";
    private const string TemplateSenderNameKey = "sender-display-name";
    private const string TemplateFailureSubjectKey = "failure-subject";
    private const string TemplatePermanentFailureSubjectKey = "permanent-failure-subject";
    private const string TemplatePermanentFailureBodyKey = "permanent-failure-body";
    private const string TemplateRetryExhaustedSubjectKey = "retry-exhausted-subject";
    private const string TemplateRetryExhaustedBodyKey = "retry-exhausted-body";

    private readonly DeliveryOptions _deliveryDefaults = deliveryDefaults.Value;
    private readonly MailboxDefaultsOptions _mailboxDefaults = mailboxDefaults.Value;
    private readonly CertificatesOptions _certificateDefaults = certificateDefaults.Value;

    public async Task<DeliveryOptions> GetDeliveryOptionsAsync(CancellationToken cancellationToken)
    {
        var settings = await GetDeliverySettingsAsync(cancellationToken);
        return new DeliveryOptions
        {
            BatchSize = settings.BatchSize,
            LeaseSeconds = settings.LeaseSeconds,
            MaximumAttempts = settings.MaximumAttempts,
            PollIntervalSeconds = settings.PollIntervalSeconds
        };
    }

    public async Task<DeliverySettingsView> GetDeliverySettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var values = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key.StartsWith(DeliveryPrefix))
            .ToDictionaryAsync(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return new DeliverySettingsView(
            ReadInt(values, "batch-size", _deliveryDefaults.BatchSize),
            ReadInt(values, "lease-seconds", _deliveryDefaults.LeaseSeconds),
            ReadInt(values, "maximum-attempts", _deliveryDefaults.MaximumAttempts),
            ReadInt(values, "poll-interval-seconds", _deliveryDefaults.PollIntervalSeconds));
    }

    public async Task<DeliverySettingsView> SaveDeliverySettingsAsync(
        DeliverySettingsView settings,
        CancellationToken cancellationToken)
    {
        Validate(settings);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await UpsertAsync(db, "batch-size", settings.BatchSize, cancellationToken);
        await UpsertAsync(db, "lease-seconds", settings.LeaseSeconds, cancellationToken);
        await UpsertAsync(db, "maximum-attempts", settings.MaximumAttempts, cancellationToken);
        await UpsertAsync(db, "poll-interval-seconds", settings.PollIntervalSeconds, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<SmtpSettingsView> GetSmtpSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var values = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key.StartsWith(SmtpPrefix))
            .ToDictionaryAsync(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);
        return new SmtpSettingsView(ReadInt(values, SmtpPrefix, SmtpCommandTimeoutKey, 300));
    }

    public async Task<SmtpSettingsView> SaveSmtpSettingsAsync(
        SmtpSettingsView settings,
        CancellationToken cancellationToken)
    {
        Validate(settings);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await UpsertAsync(db, SmtpPrefix, SmtpCommandTimeoutKey, settings.CommandTimeoutSeconds.ToString(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<MailboxSettingsView> GetMailboxSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var value = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == MailboxPrefix + MailboxFoldersKey)
            .Select(x => x.Value)
            .SingleOrDefaultAsync(cancellationToken);
        return new MailboxSettingsView(ReadMailboxFolders(value));
    }

    public async Task<MailboxSettingsView> SaveMailboxSettingsAsync(
        MailboxSettingsView settings,
        CancellationToken cancellationToken)
    {
        var normalized = MailboxNames.NormalizeDefaults(settings.DefaultFolders);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await UpsertAsync(
            db,
            MailboxPrefix,
            MailboxFoldersKey,
            JsonSerializer.Serialize(normalized),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new MailboxSettingsView(normalized);
    }

    public async Task<MailboxSettingsView> GetDomainMailboxSettingsAsync(
        Guid domainId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Domains.AsNoTracking().AnyAsync(x => x.Id == domainId, cancellationToken))
            throw new KeyNotFoundException("Domain not found.");
        var value = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == DomainMailboxFoldersKey(domainId))
            .Select(x => x.Value)
            .SingleOrDefaultAsync(cancellationToken);
        if (value is not null) return new MailboxSettingsView(ReadMailboxFolders(value));
        return await GetMailboxSettingsAsync(cancellationToken);
    }

    public async Task<MailboxSettingsView> SaveDomainMailboxSettingsAsync(
        Guid domainId,
        MailboxSettingsView settings,
        CancellationToken cancellationToken)
    {
        var normalized = MailboxNames.NormalizeDefaults(settings.DefaultFolders);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Domains.AsNoTracking().AnyAsync(x => x.Id == domainId, cancellationToken))
            throw new KeyNotFoundException("Domain not found.");
        await UpsertAsync(
            db,
            DomainMailboxPrefix(domainId),
            MailboxFoldersKey,
            JsonSerializer.Serialize(normalized),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new MailboxSettingsView(normalized);
    }

    internal static string DomainMailboxFoldersKey(Guid domainId) =>
        DomainMailboxPrefix(domainId) + MailboxFoldersKey;

    private static string DomainMailboxPrefix(Guid domainId) => $"{MailboxPrefix}{domainId:N}:";

    public async Task<CertificateSelectionSettingsView> GetCertificateSelectionSettingsAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var values = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key.StartsWith(CertificatePrefix))
            .ToDictionaryAsync(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var available = await db.DomainCertificates.AsNoTracking()
            .Where(x => x.Domain.IsActive && x.Domain.MailHostName != null)
            .Select(x => x.Domain.MailHostName!)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        var configured = values.TryGetValue(CertificatePrefix + CertificateDefaultHostKey, out var savedHost)
            ? NormalizeHostNameOrNull(savedHost)
            : NormalizeHostNameOrNull(_certificateDefaults.DefaultMailHostName);
        var allowSingleFallback = ReadBool(
            values,
            CertificateSingleFallbackKey,
            _certificateDefaults.AllowSingleCertificateFallback);
        var availableSet = available.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effective = configured is not null && availableSet.Contains(configured)
            ? configured
            : allowSingleFallback && available.Count == 1
                ? available[0]
                : null;

        return new CertificateSelectionSettingsView(configured, allowSingleFallback, available, effective);
    }

    public async Task<CertificateSelectionSettingsView> SaveCertificateSelectionSettingsAsync(
        CertificateSelectionSettingsView settings,
        CancellationToken cancellationToken)
    {
        var defaultHostName = NormalizeHostNameOrNull(settings.DefaultMailHostName);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await UpsertAsync(
            db,
            CertificatePrefix,
            CertificateDefaultHostKey,
            defaultHostName ?? string.Empty,
            cancellationToken);
        await UpsertAsync(
            db,
            CertificatePrefix,
            CertificateSingleFallbackKey,
            settings.AllowSingleCertificateFallback ? "true" : "false",
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await GetCertificateSelectionSettingsAsync(cancellationToken);
    }

    public async Task<MessageTemplateSettingsView> GetMessageTemplateSettingsAsync(
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var values = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key.StartsWith(TemplatePrefix))
            .ToDictionaryAsync(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var defaults = MessageTemplateDefaults.Create();
        var legacySubject = ReadString(values, TemplateFailureSubjectKey, defaults.PermanentFailureSubject);
        return new MessageTemplateSettingsView(
            ReadString(values, TemplateSenderNameKey, defaults.SenderDisplayName),
            ReadString(values, TemplatePermanentFailureSubjectKey, legacySubject),
            ReadString(values, TemplatePermanentFailureBodyKey, defaults.PermanentFailureBody),
            ReadString(values, TemplateRetryExhaustedSubjectKey, legacySubject),
            ReadString(values, TemplateRetryExhaustedBodyKey, defaults.RetryExhaustedBody));
    }

    public async Task<MessageTemplateSettingsView> SaveMessageTemplateSettingsAsync(
        MessageTemplateSettingsView settings,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(settings);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await UpsertAsync(db, TemplatePrefix, TemplateSenderNameKey, normalized.SenderDisplayName, cancellationToken);
        await UpsertAsync(db, TemplatePrefix, TemplatePermanentFailureSubjectKey, normalized.PermanentFailureSubject, cancellationToken);
        await UpsertAsync(db, TemplatePrefix, TemplatePermanentFailureBodyKey, normalized.PermanentFailureBody, cancellationToken);
        await UpsertAsync(db, TemplatePrefix, TemplateRetryExhaustedSubjectKey, normalized.RetryExhaustedSubject, cancellationToken);
        await UpsertAsync(db, TemplatePrefix, TemplateRetryExhaustedBodyKey, normalized.RetryExhaustedBody, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return normalized;
    }

    public static string RenderTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        var rendered = template;
        foreach (var value in values)
        {
            rendered = rendered.Replace($"{{{value.Key}}}", value.Value, StringComparison.OrdinalIgnoreCase);
        }
        return rendered;
    }

    public static string RenderHtmlTemplate(string template, IReadOnlyDictionary<string, string> values)
    {
        string rendered;
        try
        {
            MessageTemplateHtmlPolicy.Validate(template);
            rendered = template;
        }
        catch (FormatException)
        {
            // 旧版本可能保存过不安全的正文；发信时把它当纯文本展示，避免执行标记。
            rendered = $"<pre>{WebUtility.HtmlEncode(template)}</pre>";
        }
        foreach (var value in values)
        {
            rendered = rendered.Replace(
                $"{{{value.Key}}}",
                WebUtility.HtmlEncode(value.Value),
                StringComparison.OrdinalIgnoreCase);
        }

        return rendered;
    }

    public static string HtmlToPlainText(string html)
    {
        var withBreaks = Regex.Replace(html, "</(p|div|section|h[1-6]|li|tr)>", "\n", RegexOptions.IgnoreCase);
        withBreaks = Regex.Replace(withBreaks, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(withBreaks, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string key,
        int defaultValue) =>
        values.TryGetValue(DeliveryPrefix + key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values,
        string prefix,
        string key,
        int defaultValue) =>
        values.TryGetValue(prefix + key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool defaultValue) =>
        values.TryGetValue(CertificatePrefix + key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;

    private static string ReadString(
        IReadOnlyDictionary<string, string> values,
        string key,
        string defaultValue) =>
        values.TryGetValue(TemplatePrefix + key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : defaultValue;

    private IReadOnlyList<string> ReadMailboxFolders(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<List<string>>(value);
                if (saved is not null)
                {
                    return MailboxNames.NormalizeDefaults(saved);
                }
            }
            catch (JsonException)
            {
                // Fall back to configured defaults if the database setting is corrupt.
            }
        }

        return MailboxNames.NormalizeDefaults(
            _mailboxDefaults.Folders.Count == 0 ? MailboxNames.ThunderbirdDefaults : _mailboxDefaults.Folders);
    }

    private static async Task UpsertAsync(
        MailDbContext db,
        string key,
        int value,
        CancellationToken cancellationToken)
    {
        await UpsertAsync(db, DeliveryPrefix, key, value.ToString(), cancellationToken);
    }

    private static async Task UpsertAsync(
        MailDbContext db,
        string prefix,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        var fullKey = prefix + key;
        var setting = await db.SystemSettings.SingleOrDefaultAsync(x => x.Key == fullKey, cancellationToken);
        if (setting is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = fullKey, Value = value });
        }
        else
        {
            setting.Value = value;
        }
    }

    private static void Validate(DeliverySettingsView settings)
    {
        if (settings.BatchSize is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(settings), "Batch size must be between 1 and 200.");
        if (settings.LeaseSeconds is < 10 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(settings), "Lease seconds must be between 10 and 3600.");
        if (settings.MaximumAttempts is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(settings), "Maximum attempts must be between 1 and 100.");
        if (settings.PollIntervalSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(settings), "Poll interval seconds must be between 1 and 300.");
    }

    private static void Validate(SmtpSettingsView settings)
    {
        if (settings.CommandTimeoutSeconds is < 5 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(settings), "SMTP command timeout must be between 5 and 3600 seconds.");
    }

    private static MessageTemplateSettingsView Normalize(MessageTemplateSettingsView settings)
    {
        var senderName = settings.SenderDisplayName?.Trim() ?? string.Empty;
        var permanentSubject = settings.PermanentFailureSubject?.Trim() ?? string.Empty;
        var retrySubject = settings.RetryExhaustedSubject?.Trim() ?? string.Empty;
        var permanentBody = settings.PermanentFailureBody?.Trim() ?? string.Empty;
        var retryBody = settings.RetryExhaustedBody?.Trim() ?? string.Empty;
        if (senderName.Length is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(settings), "Sender display name must contain between 1 and 120 characters.");
        if (permanentSubject.Length is < 1 or > 200 || retrySubject.Length is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(settings), "Template subjects must contain between 1 and 200 characters.");
        if (permanentSubject.Contains('\r') || permanentSubject.Contains('\n')
            || retrySubject.Contains('\r') || retrySubject.Contains('\n'))
            throw new FormatException("Template subjects cannot contain line breaks.");
        if (permanentBody.Length is < 1 or > 8000 || retryBody.Length is < 1 or > 8000)
            throw new ArgumentOutOfRangeException(nameof(settings), "Message template bodies must contain between 1 and 8000 characters.");
        MessageTemplateHtmlPolicy.Validate(permanentBody);
        MessageTemplateHtmlPolicy.Validate(retryBody);
        return new MessageTemplateSettingsView(senderName, permanentSubject, permanentBody, retrySubject, retryBody);
    }

    private static string? NormalizeHostNameOrNull(string? hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName)) return null;
        var normalized = hostName.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length > 255
            || normalized.Contains(' ', StringComparison.Ordinal)
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-')))
        {
            throw new FormatException("Invalid mail host name.");
        }

        return normalized;
    }
}
