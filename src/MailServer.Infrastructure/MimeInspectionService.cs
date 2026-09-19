using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MailServer.Domain;
using MimeKit;

namespace MailServer.Infrastructure;

public sealed partial class MimeInspectionService : IMimeInspectionService
{
    private const int MaximumInspectionDepth = 64;
    private const int MaximumInspectionParts = 2048;
    private const int MaximumIndexedTextChars = 512 * 1024;
    private static readonly char[] InvalidFileNameCharacters = Path.GetInvalidFileNameChars();

    static MimeInspectionService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public MimeInspectionResult Inspect(Stream content)
    {
        using var memory = new MemoryStream();
        content.CopyTo(memory);
        return Inspect(memory.ToArray());
    }

    public MimeInspectionResult Inspect(byte[] raw)
    {
        var risks = new HashSet<MimeRiskFlag>();
        var message = LoadMimeMessage(raw);
        var attachments = new List<MimeAttachmentInfo>();
        var text = new StringBuilder();
        CalendarInviteInfo? calendar = null;
        var count = 0;
        if (message.Body is not null)
        {
            WalkEntity(message.Body, 0, ref count, text, attachments, risks, ref calendar);
        }
        else
        {
            AppendLimited(text, Encoding.UTF8.GetString(ExtractTextBytes(raw)));
        }

        AddMessageHeaderRisks(message, risks);
        return new MimeInspectionResult(
            text.ToString(),
            attachments,
            risks.OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray(),
            calendar,
            new MessageAuthenticationResult(
                message.Headers.Where(x => x.Field.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToArray(),
                message.Headers.Where(x => x.Field.StartsWith("ARC-", StringComparison.OrdinalIgnoreCase)).Select(x => $"{x.Field}: {x.Value}").ToArray(),
                message.Headers.Where(x => x.Field.Equals("DKIM-Signature", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToArray()),
            message.Headers.Where(x => x.Field.Equals("List-Unsubscribe", StringComparison.OrdinalIgnoreCase)).Select(x => x.Value).ToArray(),
            message.Headers["List-Unsubscribe-Post"],
            message.ReplyTo.Mailboxes.Select(x => x.Address).ToArray());
    }

    public string ExtractSearchableText(byte[] raw) => Inspect(raw).Text;

    public string SanitizeFileName(string? fileName)
    {
        var value = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName.Trim();
        value = value.Replace('\\', '/');
        value = value.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "attachment";
        value = DrivePrefixRegex().Replace(value, string.Empty);
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsControl(character) || InvalidFileNameCharacters.Contains(character) ? '_' : character);
        }

        var sanitized = builder.ToString().Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized)) sanitized = "attachment";
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
        return reserved.Contains(Path.GetFileNameWithoutExtension(sanitized)) ? $"_{sanitized}" : sanitized;
    }

    public string SanitizeHeaderPhrase(string value) =>
        value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace('\0', ' ')
            .Trim();

    private void WalkEntity(
        MimeEntity entity,
        int depth,
        ref int count,
        StringBuilder text,
        List<MimeAttachmentInfo> attachments,
        HashSet<MimeRiskFlag> risks,
        ref CalendarInviteInfo? calendar)
    {
        if (depth > MaximumInspectionDepth)
        {
            risks.Add(MimeRiskFlag.DeepMimeTree);
            return;
        }

        if (++count > MaximumInspectionParts)
        {
            risks.Add(MimeRiskFlag.TooManyMimeParts);
            return;
        }

        if (entity is Multipart multipart)
        {
            if (multipart.ContentType.MediaSubtype.Equals("signed", StringComparison.OrdinalIgnoreCase)
                || multipart.ContentType.MediaSubtype.Equals("encrypted", StringComparison.OrdinalIgnoreCase))
            {
                risks.Add(MimeRiskFlag.SignedOrEncrypted);
            }

            foreach (var child in multipart)
            {
                WalkEntity(child, depth + 1, ref count, text, attachments, risks, ref calendar);
            }

            return;
        }

        if (entity is MessagePart { Message.Body: not null } messagePart)
        {
            AddAttachment(entity, attachments, risks);
            WalkEntity(messagePart.Message.Body, depth + 1, ref count, text, attachments, risks, ref calendar);
            return;
        }

        if (entity is TextPart textPart)
        {
            if (textPart.ContentType.MimeType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase))
            {
                calendar ??= ParseCalendar(textPart);
            }
            else if (!textPart.IsAttachment)
            {
                AppendLimited(text, textPart.IsHtml ? ExtractHtmlText(ReadTextPart(textPart), risks) : ReadTextPart(textPart));
            }
        }

        AddAttachment(entity, attachments, risks);
    }

    private void AddAttachment(MimeEntity entity, List<MimeAttachmentInfo> attachments, HashSet<MimeRiskFlag> risks)
    {
        var fileName = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
        var hasAttachmentDisposition = entity.ContentDisposition?.IsAttachment == true;
        var isInline = entity.ContentDisposition?.Disposition.Equals(ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase) == true;
        var hasInlineFile = isInline && !string.IsNullOrWhiteSpace(fileName);
        var messageAttachment = entity is MessagePart;
        if (!hasAttachmentDisposition && !hasInlineFile && !messageAttachment && string.IsNullOrWhiteSpace(fileName)) return;

        var safe = SanitizeFileName(fileName);
        var itemRisks = new HashSet<MimeRiskFlag>();
        if (!string.IsNullOrWhiteSpace(fileName) && !string.Equals(fileName, safe, StringComparison.Ordinal))
        {
            itemRisks.Add(MimeRiskFlag.AttachmentPathTraversal);
        }

        var mimeType = entity.ContentType.MimeType.ToLowerInvariant();
        var extension = Path.GetExtension(safe).ToLowerInvariant();
        if (extension is ".zip" or ".7z" or ".rar" or ".gz" or ".tar" || mimeType.Contains("zip", StringComparison.Ordinal))
        {
            itemRisks.Add(MimeRiskFlag.ArchiveAttachment);
        }
        if (extension == ".svg" || mimeType == "image/svg+xml") itemRisks.Add(MimeRiskFlag.SvgAttachment);
        if (extension == ".pdf" || mimeType == "application/pdf") itemRisks.Add(MimeRiskFlag.PdfAttachment);
        if (mimeType is "application/pkcs7-mime" or "application/pgp-encrypted") itemRisks.Add(MimeRiskFlag.SignedOrEncrypted);

        foreach (var risk in itemRisks) risks.Add(risk);
        attachments.Add(new MimeAttachmentInfo(
            fileName ?? "message.eml",
            safe,
            entity.ContentType.MimeType,
            entity.ContentId,
            isInline,
            itemRisks.OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray()));
    }

    private static MimeMessage LoadMimeMessage(byte[] raw)
    {
        try
        {
            return MimeMessage.Load(new MemoryStream(raw));
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or IOException)
        {
            return new MimeMessage { Body = new TextPart("plain") { Text = Encoding.UTF8.GetString(ExtractTextBytes(raw)) } };
        }
    }

    private static string ReadTextPart(TextPart textPart)
    {
        try
        {
            return textPart.Text ?? string.Empty;
        }
        catch (Exception) when (textPart.Content is not null)
        {
            using var memory = new MemoryStream();
            textPart.Content.DecodeTo(memory);
            return Encoding.UTF8.GetString(memory.ToArray());
        }
    }

    private static string ExtractHtmlText(string html, HashSet<MimeRiskFlag> risks)
    {
        if (ScriptRegex().IsMatch(html)) risks.Add(MimeRiskFlag.HtmlScript);
        if (StyleRegex().IsMatch(html)) risks.Add(MimeRiskFlag.HtmlStyle);
        if (RemoteResourceRegex().IsMatch(html)) risks.Add(MimeRiskFlag.RemoteResource);
        var withoutDangerousBlocks = StyleRegex().Replace(ScriptRegex().Replace(html, " "), " ");
        var withoutTags = HtmlTagRegex().Replace(withoutDangerousBlocks, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static void AppendLimited(StringBuilder builder, string value)
    {
        if (builder.Length >= MaximumIndexedTextChars || string.IsNullOrEmpty(value)) return;
        var remaining = MaximumIndexedTextChars - builder.Length;
        if (builder.Length > 0) builder.Append('\n');
        builder.Append(value.Length <= remaining ? value : value[..remaining]);
    }

    private static CalendarInviteInfo? ParseCalendar(TextPart part)
    {
        var text = ReadTextPart(part);
        if (string.IsNullOrWhiteSpace(text)) return new CalendarInviteInfo(part.ContentType.Parameters["method"], null, null);
        string? uid = null;
        int? sequence = null;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("UID:", StringComparison.OrdinalIgnoreCase)) uid = line[4..];
            if (line.StartsWith("SEQUENCE:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[9..], out var parsed)) sequence = parsed;
        }

        return new CalendarInviteInfo(part.ContentType.Parameters["method"], uid, sequence);
    }

    private static void AddMessageHeaderRisks(MimeMessage message, HashSet<MimeRiskFlag> risks)
    {
        if (message.Headers.Contains("Authentication-Results")
            || message.Headers.Any(x => x.Field.StartsWith("ARC-", StringComparison.OrdinalIgnoreCase))
            || message.Headers.Contains("DKIM-Signature"))
        {
            risks.Add(MimeRiskFlag.AuthenticationResults);
        }
        if (message.Headers.Contains("List-Unsubscribe")) risks.Add(MimeRiskFlag.ListUnsubscribe);
        if (message.Headers.Contains("Auto-Submitted")) risks.Add(MimeRiskFlag.AutoSubmitted);
        var precedence = message.Headers["Precedence"];
        if (precedence is not null && precedence.Contains("bulk", StringComparison.OrdinalIgnoreCase))
        {
            risks.Add(MimeRiskFlag.BulkPrecedence);
        }
        var from = message.From.Mailboxes.Select(x => x.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (message.ReplyTo.Mailboxes.Any(x => !from.Contains(x.Address)))
        {
            risks.Add(MimeRiskFlag.ReplyToDifferentFromFrom);
        }
    }

    private static byte[] ExtractTextBytes(byte[] raw)
    {
        for (var i = 0; i < raw.Length - 3; i++)
        {
            if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n')
            {
                return raw[(i + 4)..];
            }
        }

        return [];
    }

    [GeneratedRegex(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("""\b(?:src|href)\s*=\s*["']?https?://""", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteResourceRegex();

    [GeneratedRegex(@"^[A-Za-z]:")]
    private static partial Regex DrivePrefixRegex();
}
