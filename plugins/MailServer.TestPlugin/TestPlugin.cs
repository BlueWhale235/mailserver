using System.Net.Mail;
using System.Text;
using MailServer.PluginSdk;
using MailServer.PluginSdk.Smtp;
using Microsoft.Extensions.DependencyInjection;

namespace MailServer.TestPluginPlugin;

public sealed class TestPlugin : IMailServerPlugin
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IMessageAcceptanceMiddleware, TestMiddleware>();
    }
}

public sealed class TestMiddleware : IMessageAcceptanceMiddleware
{
    private const string NotificationSender = "mail-notify@1605960.xyz";
    private const string NotificationRecipient = "zjb235@aliyun.com";

    private static readonly HashSet<string> WatchedSenders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "zjb235@qq.com",
            "123@1605960.xyz"
        };

    private readonly IPluginMailSender _mailSender;

    public TestMiddleware(IPluginMailSender mailSender)
    {
        _mailSender = mailSender;
    }

    public async Task<PluginDecision> InvokeAsync(
        MessageAcceptanceContext context,
        CancellationToken cancellationToken)
    {
        var sender = NormalizeAddress(context.Sender);

        // 不属于监控发件人，继续正常接收
        if (sender is null || !WatchedSenders.Contains(sender))
        {
            return PluginDecision.Continue();
        }

        // 防止插件自己发送的通知再次触发
        if (sender.Equals(
                NotificationSender,
                StringComparison.OrdinalIgnoreCase))
        {
            return PluginDecision.Continue();
        }

        try
        {
            await SendNotificationAsync(
                sender,
                context,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 通知发送失败不影响原邮件
            Console.Error.WriteLine(
                $"[TestPlugin] Failed to send notification: {ex}");
        }

        return PluginDecision.Continue();
    }

    private async Task SendNotificationAsync(
        string originalSender,
        MessageAcceptanceContext context,
        CancellationToken cancellationToken)
    {
        var content = BuildNotificationMessage(
            originalSender,
            context);

        await using var contentStream = new MemoryStream(
            Encoding.UTF8.GetBytes(content),
            writable: false);

        var request = new PluginMailSendRequest(
            Sender: NotificationSender,
            Recipients:
            [
                NotificationRecipient
            ],
            Content: contentStream,
            AuthenticatedUser: null,
            RemoteAddress: "test-plugin",
            HeloName: "test-plugin.local");

        var result = await _mailSender.SendAsync(
            request,
            cancellationToken);

        Console.WriteLine(
            "[TestPlugin] Notification sent. " +
            $"MessageId={result.MessageId}, " +
            $"LocalDeliveries={result.LocalDeliveries}, " +
            $"QueuedDeliveries={result.QueuedDeliveries}");
    }

    private static string BuildNotificationMessage(
        string originalSender,
        MessageAcceptanceContext context)
    {
        var recipients = context.Recipients.Count > 0
            ? string.Join(", ", context.Recipients)
            : "(none)";

        var now = DateTimeOffset.Now;

        // IPluginMailSender 接收的是完整 RFC 邮件内容，
        // 因此需要自己构造邮件头和正文。
        return
            $"From: MailServer Notification <{NotificationSender}>\r\n" +
            $"To: {NotificationRecipient}\r\n" +
            $"Subject: {EncodeSubject($"检测到来自 {originalSender} 的邮件")}\r\n" +
            $"Date: {now:R}\r\n" +
            $"Message-ID: <{Guid.NewGuid():N}@test-plugin.local>\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "X-MailServer-Plugin: TestPlugin\r\n" +
            "\r\n" +
            "MailServer 插件检测到一封匹配规则的邮件。\r\n" +
            "\r\n" +
            $"发件人：{originalSender}\r\n" +
            $"原始收件人：{recipients}\r\n" +
            $"邮件大小：{context.Size} bytes\r\n" +
            $"远程地址：{context.RemoteAddress}\r\n" +
            $"检测时间：{now:yyyy-MM-dd HH:mm:ss zzz}\r\n";
    }

    private static string EncodeSubject(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);

        return
            $"=?UTF-8?B?{Convert.ToBase64String(bytes)}?=";
    }

    private static string? NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return new MailAddress(value.Trim()).Address;
        }
        catch (FormatException)
        {
            return value
                .Trim()
                .Trim('<', '>');
        }
    }
}
