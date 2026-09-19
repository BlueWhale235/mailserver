using System.Text.RegularExpressions;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace MailServer.Api;

public sealed class SwaggerEndpointDescriptionOperationFilter : IOperationFilter
{
    private static readonly IReadOnlyDictionary<string, OperationText> Descriptions =
        new Dictionary<string, OperationText>(StringComparer.OrdinalIgnoreCase)
        {
            ["GET /api/v1/health"] = new(
                "检查服务健康状态",
                "检查管理 API 与数据库连接是否可用，返回当前服务健康状态。"),
            ["POST /api/v1/auth/token"] = new(
                "获取访问令牌",
                "使用后台管理员用户名和密码换取 JWT Bearer Token，用于调用受保护的管理 API。"),

            ["GET /api/v1/firewall/rules"] = new(
                "查询防火墙规则",
                "返回后台防火墙规则列表，包括 CIDR、启用状态和备注。"),
            ["POST /api/v1/firewall/rules"] = new(
                "创建防火墙规则",
                "新增一条 IP 或网段防火墙规则，用于控制服务访问来源。"),
            ["PUT /api/v1/firewall/rules/{id}"] = new(
                "更新防火墙规则",
                "按规则 ID 修改防火墙规则的启用状态和备注信息。"),
            ["DELETE /api/v1/firewall/rules/{id}"] = new(
                "删除防火墙规则",
                "按规则 ID 删除指定防火墙规则。"),

            ["GET /api/v1/domains"] = new(
                "查询域名列表",
                "返回当前账号有权限查看的邮件域名列表。"),
            ["GET /api/v1/domains/search"] = new(
                "搜索域名",
                "按关键字、启用状态、证书状态、出站模式和到期状态分页搜索域名。"),
            ["GET /api/v1/domains/{id}"] = new(
                "获取域名详情",
                "按域名 ID 获取域名配置、状态和基础详情。"),
            ["POST /api/v1/domains"] = new(
                "创建域名",
                "创建新的 Direct MX 邮件域名，并可同时设置 DKIM 私钥路径。"),
            ["PATCH /api/v1/domains/{id}"] = new(
                "修改域名",
                "按域名 ID 局部更新启用状态和 DKIM 配置。"),
            ["DELETE /api/v1/domains/{id}"] = new(
                "删除域名",
                "按域名 ID 删除指定邮件域名及其相关配置。"),
            ["POST /api/v1/domains/batch/delete"] = new(
                "批量删除域名",
                "按域名 ID 列表批量删除多个邮件域名。"),
            ["PUT /api/v1/domains/{id}/details"] = new(
                "保存域名完整配置",
                "保存域名主机名、SMTP 主机名、出站 IP、启用状态和同域直投策略。"),
            ["PUT /api/v1/domains/{id}/quota"] = new(
                "设置域名配额",
                "按域名 ID 更新域名级邮件存储配额。"),
            ["POST /api/v1/domains/{id}/certificate/inspect-upload"] = new(
                "检查上传证书",
                "上传证书和私钥进行解析校验，但不保存证书文件。"),
            ["POST /api/v1/domains/{id}/certificate/upload"] = new(
                "上传并保存证书",
                "上传证书和私钥，校验通过后绑定到指定域名。"),
            ["POST /api/v1/domains/{id}/certificate/inspect-path"] = new(
                "检查证书路径",
                "读取服务器本地证书和私钥路径并校验证书内容。"),
            ["PUT /api/v1/domains/{id}/certificate/path"] = new(
                "绑定证书路径",
                "将服务器本地证书和私钥路径绑定到指定域名。"),
            ["DELETE /api/v1/domains/{id}/certificate"] = new(
                "删除域名证书",
                "移除指定域名当前绑定的 TLS 证书配置。"),
            ["GET /api/v1/domains/{id}/dns"] = new(
                "获取 DNS 配置",
                "返回指定域名的 DNS 建议配置和当前保存值。"),
            ["PUT /api/v1/domains/{id}/dns"] = new(
                "保存 DNS 配置",
                "保存指定域名的 DNS 相关设置。"),
            ["POST /api/v1/domains/{id}/dkim"] = new(
                "生成 DKIM 配置",
                "为指定域名生成 DKIM selector、私钥和对应 DNS 记录。"),
            ["POST /api/v1/domains/{id}/dns/validate"] = new(
                "校验 DNS 配置",
                "校验指定域名已保存的 DNS 配置是否满足邮件收发要求。"),
            ["GET /api/v1/domains/{id}/dns/check"] = new(
                "获取 DNS 检查结果",
                "返回指定域名当前 DNS 检查状态和诊断结果。"),
            ["POST /api/v1/domains/{id}/dns/check"] = new(
                "执行 DNS 检查",
                "立即对指定域名执行 DNS 检查并返回结果。"),
            ["POST /api/v1/domains/batch/dns-check"] = new(
                "批量执行 DNS 检查",
                "对多个域名批量执行 DNS 检查。"),
            ["GET /api/v1/domains/{id}/health"] = new(
                "获取域名健康状态",
                "返回指定域名的证书、DNS、账号和投递健康概览。"),

            ["GET /api/v1/domains/{id}/accounts"] = new(
                "查询域名账号",
                "返回指定域名下的邮箱账号列表。"),
            ["GET /api/v1/domains/{id}/accounts/search"] = new(
                "搜索域名账号",
                "按关键字、启用状态、配额状态和分页参数搜索域名内邮箱账号。"),
            ["POST /api/v1/domains/{id}/accounts"] = new(
                "创建域名账号",
                "在指定域名下创建邮箱账号，并设置初始密码和可选配额。"),
            ["GET /api/v1/domains/{id}/aliases"] = new(
                "查询域名别名",
                "返回指定域名下的邮箱别名列表。"),
            ["POST /api/v1/domains/{id}/aliases"] = new(
                "创建域名别名",
                "在指定域名下创建邮箱别名并绑定目标账号。"),
            ["PUT /api/v1/domains/{id}/aliases/{aliasId}"] = new(
                "更新域名别名",
                "修改指定域名下别名的本地部分、目标账号或收发模式。"),
            ["GET /api/v1/domains/{id}/rules"] = new(
                "查询邮件规则",
                "返回指定域名下的收信或发信邮件规则；direction=Incoming 为收信规则，Outgoing 为发信规则。"),
            ["PUT /api/v1/domains/{id}/rules/order"] = new(
                "调整邮件规则顺序",
                "按规则 ID 顺序重新排列指定方向的邮件规则；发信规则的顺序决定拒绝与转发的执行先后。"),
            ["GET /api/v1/domains/{id}/rule-folders"] = new(
                "查询规则文件夹",
                "返回指定域名账号可用于邮件规则动作的文件夹列表。"),
            ["POST /api/v1/domains/{id}/rules"] = new(
                "创建邮件规则",
                "在指定域名下创建邮件处理规则。"),
            ["PUT /api/v1/domains/{id}/rules/{ruleId}"] = new(
                "更新邮件规则",
                "修改指定域名下邮件规则的条件、动作和启用状态。"),
            ["DELETE /api/v1/domains/{id}/rules/{ruleId}"] = new(
                "删除邮件规则",
                "删除指定域名下的一条邮件规则。"),
            ["GET /api/v1/domains/{id}/audit"] = new(
                "查询域名审计日志",
                "返回指定域名相关的管理操作和系统审计记录。"),

            ["GET /api/v1/accounts"] = new(
                "查询全局账号",
                "返回当前账号有权限查看的邮箱账号列表。"),
            ["POST /api/v1/accounts"] = new(
                "创建全局账号",
                "使用完整邮箱地址创建邮箱账号，并设置密码和可选配额。"),
            ["POST /api/v1/accounts/{id}/password"] = new(
                "重置账号密码",
                "按账号 ID 更新邮箱账号登录密码。"),
            ["PATCH /api/v1/accounts/{id}"] = new(
                "修改账号状态",
                "按账号 ID 更新邮箱账号启用状态和存储配额。"),
            ["GET /api/v1/aliases"] = new(
                "查询全局别名",
                "返回当前账号有权限查看的邮箱别名列表。"),
            ["POST /api/v1/aliases"] = new(
                "创建全局别名",
                "使用完整邮箱地址创建邮箱别名并绑定目标账号。"),
            ["DELETE /api/v1/aliases/{id}"] = new(
                "删除全局别名",
                "按别名 ID 删除指定邮箱别名。"),

            ["GET /api/v1/permissions/catalog"] = new(
                "查询权限目录",
                "返回系统支持的权限分组、权限代码和权限说明。"),
            ["GET /api/v1/admins"] = new(
                "查询管理员",
                "返回后台管理员账号列表。"),
            ["POST /api/v1/admins"] = new(
                "创建管理员",
                "创建后台管理员账号，并可同时写入初始授权。"),
            ["PATCH /api/v1/admins/{id}"] = new(
                "更新管理员",
                "按管理员 ID 修改用户名、启用状态和可选密码。"),
            ["DELETE /api/v1/admins/{id}"] = new(
                "删除管理员",
                "按管理员 ID 删除后台管理员账号。"),
            ["GET /api/v1/admins/{id}/grants"] = new(
                "查询管理员授权",
                "返回指定管理员账号拥有的权限授权列表。"),
            ["PUT /api/v1/admins/{id}/grants"] = new(
                "替换管理员授权",
                "用请求中的授权列表整体替换指定管理员账号的权限。"),
            ["POST /api/v1/admins/me/password"] = new(
                "修改当前管理员密码",
                "当前登录管理员使用旧密码修改自己的后台登录密码。"),

            ["GET /api/v1/api-keys"] = new(
                "查询 API Key",
                "返回已创建 API Key 的名称、前缀、状态和使用时间。"),
            ["POST /api/v1/api-keys"] = new(
                "创建 API Key",
                "创建新的 API Key，并按请求授予接口访问权限。密钥明文只返回一次。"),
            ["DELETE /api/v1/api-keys/{id}"] = new(
                "删除 API Key",
                "按 API Key ID 删除指定访问密钥。"),
            ["POST /api/v1/api-keys/{id}/refresh"] = new(
                "刷新 API Key",
                "重新生成指定 API Key 的密钥明文，旧值会立即失效。"),
            ["GET /api/v1/api-keys/{id}/grants"] = new(
                "查询 API Key 授权",
                "返回指定 API Key 拥有的权限授权列表。"),
            ["PUT /api/v1/api-keys/{id}/grants"] = new(
                "替换 API Key 授权",
                "用请求中的授权列表整体替换指定 API Key 的权限。"),

            ["GET /api/v1/queues"] = new(
                "查询投递队列",
                "按工作区、状态、关键字和分页参数返回外发邮件投递任务。"),
            ["GET /api/v1/queues/{id}/raw"] = new(
                "获取原始邮件",
                "按投递任务 ID 返回队列中的原始邮件内容。"),
            ["GET /api/v1/queues/{id}/attempts"] = new(
                "查询投递尝试",
                "返回指定投递任务的分阶段 Direct MX 尝试、远端响应与 TLS 诊断。"),
            ["POST /api/v1/queues/{id}/retry"] = new(
                "重试投递任务",
                "立即将指定失败或待处理投递任务重新加入投递流程。"),
            ["DELETE /api/v1/queues/{id}"] = new(
                "删除投递任务",
                "按投递任务 ID 从队列中删除指定任务。"),
            ["GET /api/v1/delivery/tls-reports"] = new(
                "查询 TLS 报告投递",
                "按状态、关键字和分页参数返回 TLS-RPT 聚合报告投递记录。"),
            ["GET /api/v1/delivery/tls-reports/{id}"] = new(
                "获取 TLS 报告详情",
                "返回指定 TLS-RPT 聚合报告的投递状态和 JSON 正文。"),
            ["POST /api/v1/delivery/tls-reports/{id}/retry"] = new(
                "重试 TLS 报告投递",
                "将尚未完成的 TLS-RPT 报告重新安排为立即投递。"),
            ["GET /api/v1/domains/{domainId}/queue"] = new(
                "查询域名投递队列",
                "按域名、关键字、状态和分页参数查询投递任务。"),
            ["GET /api/v1/domains/{domainId}/queue/{id}/raw"] = new(
                "获取域名队列原始邮件",
                "返回指定域名下投递任务的原始邮件内容。"),
            ["POST /api/v1/domains/{domainId}/queue/{id}/retry"] = new(
                "重试域名投递任务",
                "重新投递指定域名下的一条投递任务。"),
            ["DELETE /api/v1/domains/{domainId}/queue/{id}"] = new(
                "删除域名投递任务",
                "删除指定域名下的一条投递队列任务。"),

            ["GET /api/v1/settings/delivery"] = new(
                "获取投递设置",
                "返回后台邮件投递批量大小、租约、重试次数和轮询间隔设置。"),
            ["PUT /api/v1/settings/delivery"] = new(
                "保存投递设置",
                "更新后台邮件投递批量大小、租约、重试次数和轮询间隔。"),
            ["GET /api/v1/settings/mailboxes"] = new(
                "获取邮箱默认设置",
                "返回新邮箱账号默认创建的文件夹列表。"),
            ["PUT /api/v1/settings/mailboxes"] = new(
                "保存邮箱默认设置",
                "更新新邮箱账号默认创建的文件夹列表。"),
            ["GET /api/v1/settings/message-templates"] = new(
                "获取通知模板设置",
                "返回系统通知发件人名称和失败通知模板。"),
            ["PUT /api/v1/settings/message-templates"] = new(
                "保存通知模板设置",
                "更新系统通知发件人名称和失败通知模板内容。"),
            ["GET /api/v1/settings/certificates"] = new(
                "获取证书选择设置",
                "返回默认邮件主机名和证书回退策略设置。"),
            ["PUT /api/v1/settings/certificates"] = new(
                "保存证书选择设置",
                "更新默认邮件主机名和单证书回退策略。"),
            ["GET /api/v1/diagnostics/dns"] = new(
                "执行 DNS 诊断",
                "按域名和可选 DKIM selector 执行 DNS 诊断并返回检测结果。")
        };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var key = GetOperationKey(context);
        if (Descriptions.TryGetValue(key, out var text))
        {
            operation.Summary = text.Summary;
            operation.Description = text.Description;
            return;
        }

        operation.Summary = $"{context.ApiDescription.HttpMethod} {context.ApiDescription.RelativePath}";
        operation.Description = "MailServer 管理 API。";
    }

    private static string GetOperationKey(OperationFilterContext context)
    {
        var method = context.ApiDescription.HttpMethod?.ToUpperInvariant() ?? "GET";
        var path = context.ApiDescription.RelativePath ?? string.Empty;
        var queryStart = path.IndexOf('?', StringComparison.Ordinal);
        if (queryStart >= 0)
        {
            path = path[..queryStart];
        }

        path = path.Trim('/');
        path = Regex.Replace(path, "\\{([^}:]+):[^}]+\\}", "{$1}");
        return $"{method} /{path}";
    }

    private sealed record OperationText(string Summary, string Description);
}
