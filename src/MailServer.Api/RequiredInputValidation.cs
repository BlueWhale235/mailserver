using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace MailServer.Api;

/// <summary>Validate required reference fields before permission filters and handlers dereference input DTOs.</summary>
public static class RequiredInputValidation
{
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Property, bool Required)[]> Properties = new();

    public static EndpointFilterDelegate Create(EndpointFilterFactoryContext factory, EndpointFilterDelegate next)
    {
        var bodyIndex = Array.FindIndex(factory.MethodInfo.GetParameters(), parameter =>
            parameter.Name == "request" && !typeof(HttpRequest).IsAssignableFrom(parameter.ParameterType));
        if (bodyIndex < 0) return next;
        return async context =>
        {
            var errors = new Dictionary<string, string[]>();
            Validate(context.Arguments[bodyIndex], string.Empty, errors, 0);
            if (errors.Count == 0) return await next(context);
            return Results.ValidationProblem(errors, title: "请求字段无效",
                type: "urn:mailserver:problem:validation-error",
                extensions: new Dictionary<string, object?> { ["traceId"] = context.HttpContext.TraceIdentifier });
        };
    }

    private static void Validate(object? value, string path, Dictionary<string, string[]> errors, int depth)
    {
        if (value is null || depth > 8) return;
        if (value is string || value.GetType().IsValueType) return;
        if (value is IEnumerable sequence)
        {
            var index = 0;
            foreach (var item in sequence)
            {
                var itemPath = $"{path}[{index++}]";
                if (item is null) errors[itemPath] = ["此项不能为空。"];
                else Validate(item, itemPath, errors, depth + 1);
                if (errors.Count >= 100) return;
            }
            return;
        }
        var type = value.GetType();
        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true) return;
        var properties = Properties.GetOrAdd(type, static type =>
        {
            var nullability = new NullabilityInfoContext();
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                .Select(property => (property, nullability.Create(property).ReadState == NullabilityState.NotNull))
                .ToArray();
        });
        foreach (var (property, required) in properties)
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var field = string.IsNullOrEmpty(path) ? name : $"{path}.{name}";
            var child = property.GetValue(value);
            if (child is null && required) errors[field] = ["此字段不能为空。"];
            else Validate(child, field, errors, depth + 1);
            if (errors.Count >= 100) return;
        }
    }
}
