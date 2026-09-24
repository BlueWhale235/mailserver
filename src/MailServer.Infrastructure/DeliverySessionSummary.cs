using System.Text;
using System.Text.RegularExpressions;

namespace MailServer.Infrastructure;

/// <summary>将投递诊断压缩为可向管理员展示的安全摘要，避免保存认证材料和邮件正文。</summary>
public static partial class DeliverySessionSummary
{
    public const int MaximumBytes = 64 * 1024;
    public const string RedactedMarker = "[敏感信息已脱敏]";
    public const string TruncatedMarker = "[摘要已截断至 64 KiB]";
    private const string BodyMarker = "[邮件正文已省略]";

    [GeneratedRegex(@"(?i)(?<![\w@])(?:[\p{L}\p{N}._%+\-]+)@(?:[\p{L}\p{N}\-]+\.)+[\p{L}\p{N}\-]+")]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"(?i)\b(?:bearer|basic)\s+[^\s,;]+")]
    private static partial Regex AuthorizationValue();

    [GeneratedRegex("""(?i)\b(password|passwd|token|secret|api\s*[-_]?\s*key|access\s*[-_]?\s*key)\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s,;]+)""")]
    private static partial Regex NamedSecret();

    [GeneratedRegex(@"(?i)^\s*(?:C:\s*)?(?:AUTH\b|authorization\s*:|proxy-authorization\s*:|x-api-key\s*:)")]
    private static partial Regex SecretLine();

    [GeneratedRegex(@"(?i)^\s*(?:C:\s*)?DATA\s*$")]
    private static partial Regex DataCommand();

    [GeneratedRegex(@"(?i)^\s*(?:C:\s*)?(?:MIME-Version|Content-Type|Content-Transfer-Encoding)\s*:")]
    private static partial Regex MimeHeader();

    public static string? Sanitize(string? input)
    {
        if (input is null) return null;
        var normalized = input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var clean = new StringBuilder(Math.Min(normalized.Length, MaximumBytes));
        var redacted = false;
        foreach (var line in lines)
        {
            if ((line.Length == 0 && clean.Length > 0) || DataCommand().IsMatch(line) || MimeHeader().IsMatch(line))
            {
                if (clean.Length > 0) clean.Append('\n');
                clean.Append(BodyMarker);
                redacted = true;
                break;
            }

            var safe = new string(line.Where(character => character == '\t' || !char.IsControl(character)).ToArray());
            if (safe.Length != line.Length) redacted = true;
            if (SecretLine().IsMatch(safe))
            {
                if (clean.Length > 0) clean.Append('\n');
                clean.Append("[认证会话已省略]");
                redacted = true;
                break;
            }
            else
            {
                safe = ReplaceAndMark(NamedSecret(), safe, "$1=[已脱敏]", ref redacted);
                safe = ReplaceAndMark(AuthorizationValue(), safe, "[认证信息已脱敏]", ref redacted);
                safe = ReplaceAndMark(EmailAddress(), safe, "[地址已脱敏]", ref redacted);
                if (safe.Contains('@'))
                {
                    // 复杂或非标准邮箱地址也不应穿过通用正则后原样展示。
                    safe = "[含地址的行已省略]";
                    redacted = true;
                }
            }
            if (clean.Length > 0) clean.Append('\n');
            clean.Append(safe);
        }

        var summary = (redacted ? RedactedMarker + "\n" : string.Empty) + clean;
        if (Encoding.UTF8.GetByteCount(summary) <= MaximumBytes) return summary;

        var available = MaximumBytes - Encoding.UTF8.GetByteCount("\n" + TruncatedMarker);
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in summary.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > available) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.Append('\n').Append(TruncatedMarker).ToString();
    }

    private static string ReplaceAndMark(Regex regex, string input, string replacement, ref bool redacted)
    {
        var output = regex.Replace(input, replacement);
        if (!ReferenceEquals(output, input) && !string.Equals(output, input, StringComparison.Ordinal)) redacted = true;
        return output;
    }
}
