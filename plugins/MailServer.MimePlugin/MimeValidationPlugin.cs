using MailServer.PluginSdk;
using MailServer.PluginSdk.Smtp;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MailServer.MimePlugin;

public sealed class MimeValidationPlugin : IMailServerPlugin
{
    public void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IMessageContentMiddleware, MimeValidationMiddleware>();
}

public sealed class MimeValidationMiddleware : IMessageContentMiddleware
{
    public async Task<PluginDecision> InvokeAsync(
        MessageContentContext context,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[context.Content.Length];
        context.Content.Position = 0;
        await context.Content.ReadExactlyAsync(bytes, cancellationToken);
        var separator = FindHeaderSeparator(bytes);
        var bodyOffset = separator < 0 ? 0 : separator + 4;
        var bodyLength = bytes.Length - bodyOffset;
        var hasHeaders = separator > 0 && LooksLikeHeaders(bytes.AsSpan(0, separator));

        if (bodyLength == 0 && !hasHeaders)
        {
            return context.Settings.GetString("emptyBodyPolicy", "accept") == "accept"
                ? PluginDecision.Continue()
                : Reject("Empty messages are not allowed.");
        }
        if (bodyLength == 0 && hasHeaders
            && context.Settings.GetString("headerOnlyPolicy", "accept") != "accept")
            return Reject("Header-only messages are not allowed.");

        if (context.Settings.GetBoolean("requireEightBitMimeDeclaration", true)
            && bytes.AsSpan(bodyOffset).IndexOfAnyInRange((byte)128, byte.MaxValue) >= 0
            && !context.BodyType.Equals("8BITMIME", StringComparison.OrdinalIgnoreCase))
            return Reject("8-bit message body requires BODY=8BITMIME.");

        if (!context.Settings.GetBoolean("validateMimeStructure", true))
            return PluginDecision.Continue();
        try
        {
            context.Content.Position = 0;
            if (!hasHeaders)
                return PluginDecision.Continue();
            var message = await MimeMessage.LoadAsync(context.Content, cancellationToken);
            var maximumParts = context.Settings.GetInteger("maximumMimeParts", 1000);
            var maximumDepth = context.Settings.GetInteger("maximumNestingDepth", 30);
            var (parts, depth) = Measure(message.Body, 1);
            if (parts > maximumParts) return Reject("MIME part count exceeds the configured limit.");
            if (depth > maximumDepth) return Reject("MIME nesting depth exceeds the configured limit.");
            return PluginDecision.Continue();
        }
        catch (Exception exception) when (exception is FormatException or IOException or InvalidOperationException)
        {
            return Reject("Malformed MIME message.");
        }
    }

    private static (int Parts, int Depth) Measure(MimeEntity? entity, int depth)
    {
        if (entity is null) return (0, depth);
        if (entity is not Multipart multipart) return (1, depth);
        var parts = 1;
        var maximumDepth = depth;
        foreach (var child in multipart)
        {
            var measured = Measure(child, depth + 1);
            parts += measured.Parts;
            maximumDepth = Math.Max(maximumDepth, measured.Depth);
        }
        return (parts, maximumDepth);
    }

    private static int FindHeaderSeparator(ReadOnlySpan<byte> bytes) =>
        bytes.IndexOf("\r\n\r\n"u8);

    private static bool LooksLikeHeaders(ReadOnlySpan<byte> bytes)
    {
        var firstLineEnd = bytes.IndexOf("\r\n"u8);
        var firstLine = firstLineEnd < 0 ? bytes : bytes[..firstLineEnd];
        var colon = firstLine.IndexOf((byte)':');
        return colon > 0 && firstLine[..colon].IndexOfAnyExceptInRange((byte)33, (byte)126) < 0;
    }

    private static PluginDecision Reject(string message) =>
        new(PluginDecisionKind.Reject, message);
}
