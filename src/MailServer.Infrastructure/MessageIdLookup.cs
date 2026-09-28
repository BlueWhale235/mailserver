using System.Security.Cryptography;
using System.Text;

namespace MailServer.Infrastructure;

public static class MessageIdLookup
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.StartsWith("Message-ID:", StringComparison.OrdinalIgnoreCase))
            value = value["Message-ID:".Length..].Trim();
        value = value.Trim().Trim('<', '>').Trim();
        return value.Length == 0 ? null : value;
    }

    public static string? Hash(string? value)
    {
        var normalized = Normalize(value);
        return string.IsNullOrEmpty(normalized) ? null
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
