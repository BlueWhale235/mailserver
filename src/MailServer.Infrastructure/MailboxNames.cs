namespace MailServer.Infrastructure;

public static class MailboxNames
{
    public static readonly string[] SystemDefaults =
        ["INBOX", "Sent", "Drafts", "Trash", "Junk", "Archives"];

    public static readonly string[] ThunderbirdDefaults = SystemDefaults;

    public static bool IsSystemFolder(string name)
    {
        var normalized = Normalize(name);
        return SystemDefaults.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public static string Normalize(string name)
    {
        var value = name.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value) || value.Contains("..", StringComparison.Ordinal) || value.Length > 255)
        {
            throw new FormatException("Invalid mailbox name.");
        }

        return value.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? "INBOX" : value;
    }

    public static bool IsSent(string name) =>
        name.Equals("Sent", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Sent Messages", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Sent Items", StringComparison.OrdinalIgnoreCase);

    public static bool Equals(string left, string right) =>
        Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> NormalizeDefaults(IEnumerable<string> folders)
    {
        var result = new List<string>(SystemDefaults);
        var seen = new HashSet<string>(SystemDefaults, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            var normalized = Normalize(folder);
            if (seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }
}
