using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MailServer.Infrastructure;

public static class SnowflakeJson
{
    public static void Configure(JsonSerializerOptions options)
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            foreach (var property in type.Properties)
                if ((property.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                    || property.Name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase))
                    && IsNumericId(property.PropertyType))
                    property.NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString;
        });
        options.TypeInfoResolver = resolver;
    }

    private static bool IsNumericId(Type type) => type == typeof(long) || type == typeof(long?)
        || type == typeof(long[]) || typeof(IEnumerable<long>).IsAssignableFrom(type);
}
