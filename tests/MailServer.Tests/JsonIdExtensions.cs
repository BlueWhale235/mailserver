using System.Globalization;
using System.Text.Json;

namespace MailServer.Tests;

internal static class JsonIdExtensions
{
    public static long GetSnowflakeId(this JsonElement value) => value.ValueKind == JsonValueKind.String
        ? long.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetInt64();
}
