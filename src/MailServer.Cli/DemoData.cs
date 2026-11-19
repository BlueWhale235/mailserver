using System.Security.Cryptography;
using System.Text;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

internal static class DemoData
{
    private const string TeamDomain = "demo.example.test";
    private const string LabDomain = "lab.example.test";
    private const string Actor = "demo-seed";
    private static readonly MailboxAddress Alice = new("演示成员 Alice", $"alice@{TeamDomain}");
    private static readonly MailboxAddress Support = new("演示支持团队", $"support@{TeamDomain}");
    private static readonly MailboxAddress Tester = new("演示测试员", $"tester@{LabDomain}");

    public static async Task SeedAsync(IServiceProvider services, CancellationToken token)
    {
        var admin = services.GetRequiredService<AdminApplicationService>();
        var mailboxes = services.GetRequiredService<IMailboxService>();
        var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using var db = await factory.CreateDbContextAsync(token);
        var domains = 0;
        var accounts = 0;
        var aliases = 0;
        var messages = 0;

        var team = await EnsureDomainAsync(TeamDomain);
        var lab = await EnsureDomainAsync(LabDomain);
        var alice = await EnsureAccountAsync(team, Alice);
        var support = await EnsureAccountAsync(team, Support);
        var tester = await EnsureAccountAsync(lab, Tester);

        if (team is { } teamId && support is { } target
            && !await db.Aliases.IgnoreQueryFilters().AnyAsync(x => x.DomainId == teamId && x.SourceLocalPart == "help", token)
            && !await db.Accounts.IgnoreQueryFilters().AnyAsync(x => x.DomainId == teamId && x.LocalPart == "help", token))
        {
            await admin.CreateAliasAsync($"help@{TeamDomain}", target, MailAliasMode.ReceiveOnly, Actor, token);
            aliases++;
        }

        var now = DateTimeOffset.UtcNow;
        await AppendAsync(alice, "INBOX", "welcome", Support, Alice, "欢迎使用 MailServer",
            "这是一封本机演示邮件。你可以在「运维中心 → 邮件查询」中打开详情、预览正文或下载原文。",
            now.AddMinutes(-45), ["\\Seen"]);
        await AppendAsync(support, "INBOX", "ticket", Alice, Support, "工单 #DEMO-001：检查邮箱配置",
            "请核对本机监听端口和 SQLite 数据库。附件是本次演示的检查清单。",
            now.AddMinutes(-35), ["\\Flagged"], attachment: true);
        await AppendAsync(tester, "INBOX", "cross-domain", Support, Tester, "跨域演示：测试环境巡检",
            "这是第二个示例域名的收件箱。邮件已写入本机存储，没有经过外部投递。",
            now.AddMinutes(-25), []);
        await AppendAsync(alice, "Sent", "reply", Alice, Support, "回复：本机演示环境已就绪",
            "已检查后台地址和邮件查询。此记录仅用来展示已发送文件夹的邮件副本。",
            now.AddMinutes(-15), ["\\Seen"]);
        await AppendAsync(support, "Drafts", "draft", Support, Alice, "草稿：下一次演示安排",
            "下次可在后台展示别名、邮件原文和附件。此草稿没有发送。",
            now.AddMinutes(-5), ["\\Draft"]);

        Console.WriteLine($"Demo data added: {domains} domain(s), {accounts} account(s), {aliases} alias(es), {messages} message(s).");

        async Task<long?> EnsureDomainAsync(string name)
        {
            var existing = await db.Domains.IgnoreQueryFilters().Where(x => x.Name == name)
                .Select(x => new { x.Id, x.IsActive, x.DeletedAt }).SingleOrDefaultAsync(token);
            if (existing is not null) return existing.IsActive && existing.DeletedAt is null ? existing.Id : null;
            var domain = await admin.CreateDomainAsync(name, null, null, Actor, token);
            domains++;
            return domain.Id;
        }

        async Task<long?> EnsureAccountAsync(long? domainId, MailboxAddress address)
        {
            if (domainId is not { } id) return null;
            var localPart = address.Address.Split('@', 2)[0];
            var existing = await db.Accounts.IgnoreQueryFilters().Where(x => x.DomainId == id && x.LocalPart == localPart)
                .Select(x => new { x.Id, x.IsEnabled, x.DeletedAt }).SingleOrDefaultAsync(token);
            if (existing is not null) return existing.IsEnabled && existing.DeletedAt is null ? existing.Id : null;
            if (await db.Aliases.IgnoreQueryFilters().AnyAsync(x => x.DomainId == id && x.SourceLocalPart == localPart, token))
                return null;

            // 演示通过管理后台查看；邮箱账号不使用公开的固定密码。
            var account = await admin.CreateAccountAsync(
                address.Address, Convert.ToHexString(RandomNumberGenerator.GetBytes(24)), null, Actor, token);
            accounts++;
            return account.Id;
        }

        async Task AppendAsync(long? accountId, string folder, string key, MailboxAddress from, MailboxAddress to,
            string subject, string body, DateTimeOffset date, string[] flags, bool attachment = false)
        {
            if (accountId is not { } id) return;
            var messageId = $"mailserver-demo-{key}@{TeamDomain}";
            var hash = MessageIdLookup.Hash(messageId);
            if (await db.Messages.AnyAsync(x => x.InternetMessageIdHash == hash && x.InternetMessageId == messageId, token)
                || !await db.Mailboxes.AnyAsync(x => x.AccountId == id && x.Name == folder && x.PendingDeletionJobId == null, token))
                return;

            var builder = new BodyBuilder { TextBody = body };
            if (attachment)
                builder.Attachments.Add("demo-checklist.txt",
                    Encoding.UTF8.GetBytes("1. 查看域名与邮箱账号\r\n2. 打开邮件详情与正文\r\n3. 下载邮件原文\r\n"),
                    new ContentType("text", "plain") { Charset = "utf-8" });
            using var mail = new MimeMessage { Subject = subject, MessageId = messageId, Date = date, Body = builder.ToMessageBody() };
            mail.From.Add(from);
            mail.To.Add(to);
            await using var content = new MemoryStream();
            await mail.WriteToAsync(content, token);
            content.Position = 0;

            // 只追加本地邮箱副本，不进入 SMTP 路由，也不创建外发任务。
            await mailboxes.AppendAsync(id, folder, content, flags, date, token);
            messages++;
        }
    }
}
