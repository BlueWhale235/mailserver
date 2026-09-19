using System.Text.Json;
using System.Text.Json.Nodes;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailServer.Infrastructure;

public sealed record AuditSearchRequest(
    string? Search,
    AuditCategory? Category,
    AuditStatus? Status,
    string? NodeId,
    string? Actor,
    string? PluginId,
    DateTimeOffset? From,
    DateTimeOffset? To);

public sealed record AuditListItemView(
    Guid Id,
    string Number,
    string Content,
    AuditCategory Category,
    AuditStatus Status,
    string Actor,
    string? NodeId,
    string? RequestId,
    string? PluginId,
    string? PluginName,
    DateTimeOffset CreatedAt);

public sealed record AuditFieldChangeView(string Field, string? OldValue, string? NewValue);

public sealed record AuditDetailView(
    Guid Id,
    string Number,
    string Content,
    AuditCategory Category,
    AuditStatus Status,
    string Actor,
    string Action,
    string Resource,
    string? Detail,
    string? RemoteAddress,
    string? NodeId,
    string? RequestId,
    string? RelatedId,
    long? DurationMilliseconds,
    string? PluginId,
    string? PluginName,
    DateTimeOffset CreatedAt,
    IReadOnlyList<AuditFieldChangeView> Changes,
    string RawJson);

public sealed record AuditExportRequest(Guid[] Ids);

public sealed class AuditLogService(
    IDbContextFactory<MailDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string[] SensitiveKeys =
    [
        "password", "passwd", "pwd", "secret", "token", "apiKey", "api_key", "privateKey",
        "private_key", "credential", "authorization", "auth", "jwt", "cookie"
    ];

    public async Task<PagedResult<AuditListItemView>> SearchAsync(
        AuditSearchRequest request,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = ApplyFilters(db.AuditRecords.AsNoTracking(), request);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new PagedResult<AuditListItemView>(
            rows.Select(ToListItem).ToArray(),
            page,
            pageSize,
            total);
    }

    public async Task<AuditDetailView> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.AuditRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Audit record not found.");
        return ToDetail(record);
    }

    public async Task<byte[]> ExportAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return JsonSerializer.SerializeToUtf8Bytes(Array.Empty<AuditDetailView>(), JsonOptions);
        if (ids.Count > 1000) throw new InvalidOperationException("A single export can include at most 1000 audit records.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var idSet = ids.ToHashSet();
        var records = await db.AuditRecords.AsNoTracking()
            .Where(x => idSet.Contains(x.Id))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        return JsonSerializer.SerializeToUtf8Bytes(records.Select(ToDetail).ToArray(), JsonOptions);
    }

    public async Task RecordPluginDecisionAsync(
        PluginAuditEvent audit,
        CancellationToken cancellationToken)
    {
        if (audit.DecisionKind == PluginDecisionKindName.Continue) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuditRecords.Add(CreatePluginRecord(audit));
        await db.SaveChangesAsync(cancellationToken);
    }

    public AuditRecord CreatePluginRecord(PluginAuditEvent audit)
    {
        var status = audit.Failed ? AuditStatus.Failed :
            audit.DecisionKind is PluginDecisionKindName.Defer or PluginDecisionKindName.Quarantine
                ? AuditStatus.Warning
                : AuditStatus.Succeeded;
        var message = string.IsNullOrWhiteSpace(audit.Message) ? audit.DecisionKind.ToString() : audit.Message!;
        var payload = new Dictionary<string, object?>
        {
            ["pluginId"] = audit.PluginId,
            ["pluginName"] = audit.PluginName,
            ["stage"] = audit.Stage,
            ["decision"] = audit.DecisionKind.ToString(),
            ["message"] = message,
            ["elapsedMilliseconds"] = audit.ElapsedMilliseconds,
            ["requestId"] = audit.RequestId,
            ["nodeId"] = audit.NodeId ?? Environment.MachineName
        };
        return new AuditRecord
        {
            Actor = string.IsNullOrWhiteSpace(audit.PluginId) ? "plugin" : $"plugin:{audit.PluginId}",
            Action = audit.Failed ? "plugin.runtime.error" : $"plugin.runtime.{audit.DecisionKind.ToString().ToLowerInvariant()}",
            Resource = string.IsNullOrWhiteSpace(audit.Resource) ? "plugin:runtime" : audit.Resource!,
            Detail = $"{audit.Stage}: {message}",
            Category = AuditCategory.Plugin,
            Status = status,
            NodeId = audit.NodeId ?? Environment.MachineName,
            RequestId = audit.RequestId,
            RelatedId = audit.RelatedId,
            DurationMilliseconds = audit.ElapsedMilliseconds,
            PluginId = audit.PluginId,
            PluginName = audit.PluginName,
            RawJson = SafeJson(payload)
        };
    }

    public static AuditCategory InferCategory(AuditRecord record)
    {
        if (record.Category is { } category) return category;
        var action = record.Action;
        if (action.StartsWith("plugin.", StringComparison.OrdinalIgnoreCase)
            || record.Actor.StartsWith("plugin", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(record.PluginId))
            return AuditCategory.Plugin;
        if (action.Contains("create", StringComparison.OrdinalIgnoreCase)) return AuditCategory.Create;
        if (action.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || action.Contains("revoke", StringComparison.OrdinalIgnoreCase)
            || action.Contains("purge", StringComparison.OrdinalIgnoreCase))
            return AuditCategory.Delete;
        if (action.StartsWith("task.", StringComparison.OrdinalIgnoreCase)
            || action.Contains("reconcile", StringComparison.OrdinalIgnoreCase)
            || action.Contains("cleanup", StringComparison.OrdinalIgnoreCase)
            || action.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
            return AuditCategory.BackgroundTask;
        if (action.Contains("password", StringComparison.OrdinalIgnoreCase)
            || action.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || action.Contains("firewall", StringComparison.OrdinalIgnoreCase))
            return AuditCategory.Security;
        if (action.Contains("update", StringComparison.OrdinalIgnoreCase)
            || action.Contains("enable", StringComparison.OrdinalIgnoreCase)
            || action.Contains("disable", StringComparison.OrdinalIgnoreCase)
            || action.Contains("settings", StringComparison.OrdinalIgnoreCase))
            return AuditCategory.Update;
        return AuditCategory.Other;
    }

    public static AuditStatus InferStatus(AuditRecord record)
    {
        if (record.Status is { } status) return status;
        if (record.Action.Contains("fail", StringComparison.OrdinalIgnoreCase)
            || record.Action.Contains("error", StringComparison.OrdinalIgnoreCase))
            return AuditStatus.Failed;
        if (record.Action.Contains("defer", StringComparison.OrdinalIgnoreCase)
            || record.Action.Contains("warning", StringComparison.OrdinalIgnoreCase)
            || record.Action.Contains("quarantine", StringComparison.OrdinalIgnoreCase))
            return AuditStatus.Warning;
        return AuditStatus.Succeeded;
    }

    public static string SafeJson(object? value)
    {
        var node = JsonSerializer.SerializeToNode(value, JsonOptions);
        RedactNode(node);
        return node?.ToJsonString(JsonOptions) ?? "{}";
    }

    private static IQueryable<AuditRecord> ApplyFilters(IQueryable<AuditRecord> query, AuditSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var search = request.Search.Trim();
            query = query.Where(x =>
                x.Action.Contains(search)
                || x.Resource.Contains(search)
                || x.Actor.Contains(search)
                || (x.Detail != null && x.Detail.Contains(search))
                || (x.RequestId != null && x.RequestId.Contains(search))
                || (x.PluginId != null && x.PluginId.Contains(search))
                || (x.PluginName != null && x.PluginName.Contains(search)));
        }
        if (request.Category is { } category)
        {
            query = category switch
            {
                AuditCategory.Plugin => query.Where(x => x.Category == category
                    || (x.Category == null && (x.Action.StartsWith("plugin.") || x.Actor.StartsWith("plugin") || x.PluginId != null))),
                AuditCategory.Create => query.Where(x => x.Category == category || (x.Category == null && x.Action.Contains("create"))),
                AuditCategory.Delete => query.Where(x => x.Category == category
                    || (x.Category == null && (x.Action.Contains("delete") || x.Action.Contains("revoke") || x.Action.Contains("purge")))),
                AuditCategory.BackgroundTask => query.Where(x => x.Category == category
                    || (x.Category == null && (x.Action.StartsWith("task.") || x.Action.Contains("reconcile") || x.Action.Contains("cleanup") || x.Action.Contains("snapshot")))),
                AuditCategory.Security => query.Where(x => x.Category == category
                    || (x.Category == null && (x.Action.Contains("password") || x.Action.Contains("api-key") || x.Action.Contains("firewall")))),
                AuditCategory.Update => query.Where(x => x.Category == category
                    || (x.Category == null && (x.Action.Contains("update") || x.Action.Contains("enable") || x.Action.Contains("disable") || x.Action.Contains("settings")))),
                _ => query.Where(x => x.Category == category)
            };
        }
        if (request.Status is { } status)
        {
            query = status switch
            {
                AuditStatus.Failed => query.Where(x => x.Status == status || (x.Status == null && (x.Action.Contains("fail") || x.Action.Contains("error")))),
                AuditStatus.Warning => query.Where(x => x.Status == status
                    || (x.Status == null && (x.Action.Contains("defer") || x.Action.Contains("warning") || x.Action.Contains("quarantine")))),
                AuditStatus.Succeeded => query.Where(x => x.Status == status
                    || (x.Status == null && !x.Action.Contains("fail") && !x.Action.Contains("error") && !x.Action.Contains("defer") && !x.Action.Contains("warning"))),
                _ => query.Where(x => x.Status == status)
            };
        }
        if (!string.IsNullOrWhiteSpace(request.NodeId)) query = query.Where(x => x.NodeId == request.NodeId);
        if (!string.IsNullOrWhiteSpace(request.Actor)) query = query.Where(x => x.Actor.Contains(request.Actor));
        if (!string.IsNullOrWhiteSpace(request.PluginId)) query = query.Where(x => x.PluginId == request.PluginId || x.Actor == "plugin:" + request.PluginId);
        if (request.From is { } from) query = query.Where(x => x.CreatedAt >= from);
        if (request.To is { } to) query = query.Where(x => x.CreatedAt <= to);
        return query;
    }

    private static AuditListItemView ToListItem(AuditRecord record)
    {
        var category = InferCategory(record);
        var status = InferStatus(record);
        return new AuditListItemView(
            record.Id,
            ShortId(record.Id),
            Content(record, category),
            category,
            status,
            DisplayActor(record),
            record.NodeId,
            record.RequestId,
            record.PluginId,
            record.PluginName,
            record.CreatedAt);
    }

    private static AuditDetailView ToDetail(AuditRecord record)
    {
        var category = InferCategory(record);
        var status = InferStatus(record);
        return new AuditDetailView(
            record.Id,
            ShortId(record.Id),
            Content(record, category),
            category,
            status,
            DisplayActor(record),
            record.Action,
            record.Resource,
            RedactText(record.Detail),
            record.RemoteAddress,
            record.NodeId,
            record.RequestId,
            record.RelatedId,
            record.DurationMilliseconds,
            record.PluginId,
            record.PluginName,
            record.CreatedAt,
            ParseChanges(record.ChangeSummaryJson),
            BuildRawJson(record, category, status));
    }

    private static string Content(AuditRecord record, AuditCategory category)
    {
        if (category == AuditCategory.Plugin && !string.IsNullOrWhiteSpace(record.PluginName))
            return $"{record.PluginName} {Label(record.Action)}";
        return Label(record.Action);
    }

    private static readonly IReadOnlyDictionary<string, string> ChineseActionLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["domain.create"] = "创建域名",
            ["domain.update"] = "编辑域名",
            ["domain.details.update"] = "编辑域名",
            ["domain.delete"] = "删除域名",
            ["account.create"] = "创建账号",
            ["account.update"] = "编辑账号",
            ["account.soft-delete"] = "删除账号",
            ["account.batch-soft-delete"] = "批量删除账号",
            ["alias.create"] = "创建别名",
            ["alias.update"] = "编辑别名",
            ["alias.batch-mode"] = "批量编辑别名",
            ["alias.delete"] = "删除别名",
            ["alias.batch-delete"] = "批量删除别名",
            ["mail-rule.create"] = "创建收发信规则",
            ["mail-rule.update"] = "编辑收发信规则",
            ["mail-rule.reorder"] = "调整收发信规则顺序",
            ["mail-rule.delete"] = "删除收发信规则",
            ["api-key.create"] = "创建 API Key",
            ["api-key.refresh"] = "刷新 API Key",
            ["api-key.revoke"] = "撤销 API Key",
            ["api-key.delete"] = "删除 API Key",
            ["admin.create"] = "创建管理员",
            ["admin.update"] = "编辑管理员",
            ["admin.delete"] = "删除管理员",
            ["task.run"] = "执行后台任务",
            ["task.update"] = "编辑后台任务",
            ["plugin.settings.update"] = "修改插件配置",
            ["plugin.order.update"] = "调整插件顺序",
            ["plugin.load.succeeded"] = "插件加载成功",
            ["plugin.load.failed"] = "插件加载失败",
            ["plugin.runtime.reject"] = "插件拒绝处理",
            ["plugin.runtime.defer"] = "插件延迟处理",
            ["plugin.runtime.quarantine"] = "插件隔离邮件",
            ["plugin.runtime.cancel"] = "插件取消处理",
            ["plugin.runtime.error"] = "插件运行异常",
            ["firewall.create"] = "创建防火墙规则",
            ["firewall.update"] = "编辑防火墙规则",
            ["firewall.delete"] = "删除防火墙规则",
            ["certificate.create"] = "创建证书",
            ["certificate.update"] = "编辑证书",
            ["certificate.delete"] = "删除证书",
            ["quarantine.release"] = "释放隔离邮件",
            ["quarantine.delete"] = "删除隔离邮件",
            ["mailbox-folder.create"] = "创建邮箱文件夹",
            ["mailbox-folder.delete"] = "删除邮箱文件夹"
        };

    private static string Label(string action)
    {
        if (ChineseActionLabels.TryGetValue(action, out var label)) return label;
        return action switch
    {
        "domain.create" => "创建域名",
        "domain.update" or "domain.details.update" => "编辑域名",
        "domain.delete" or "domain.soft-delete" => "删除域名",
        "account.create" => "创建账号",
        "account.update" => "编辑账号",
        "account.soft-delete" or "account.batch-soft-delete" => "删除账号",
        "alias.create" => "创建别名",
        "alias.update" or "alias.batch-mode" => "编辑别名",
        "alias.delete" or "alias.batch-delete" => "删除别名",
        "mail-rule.create" => "创建收发信规则",
        "mail-rule.update" or "mail-rule.reorder" => "编辑收发信规则",
        "mail-rule.delete" => "删除收发信规则",
        "api-key.create" => "创建 API Key",
        "api-key.refresh" => "刷新 API Key",
        "api-key.revoke" or "api-key.delete" => "删除 API Key",
        "admin.create" => "创建管理员",
        "admin.update" => "编辑管理员",
        "admin.delete" => "删除管理员",
        "task.run" => "后台任务执行",
        "task.update" => "编辑后台任务",
        "plugin.settings.update" => "插件配置变更",
        "plugin.order.update" => "插件顺序调整",
        "plugin.load.failed" => "插件加载失败",
        "plugin.runtime.reject" => "插件拒绝",
        "plugin.runtime.defer" => "插件延迟",
        "plugin.runtime.quarantine" => "插件隔离",
        "plugin.runtime.cancel" => "插件取消",
        "plugin.runtime.error" => "插件异常",
        _ => FallbackLabel(action)
        };
    }

    private static string FallbackLabel(string action)
    {
        var parts = action.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "其他操作";

        var resource = parts[0].ToLowerInvariant() switch
        {
            "firewall" => "防火墙规则",
            "certificate" or "cert" => "证书",
            "queue" or "delivery" => "投递队列",
            "quarantine" => "隔离邮件",
            "folder" or "mailbox-folder" => "邮箱文件夹",
            "plugin" => "插件",
            "task" => "后台任务",
            "node" => "节点",
            _ => parts[0]
        };
        var operation = parts[^1].ToLowerInvariant() switch
        {
            "create" => "创建",
            "created" => "创建成功",
            "update" or "edit" => "编辑",
            "delete" or "remove" => "删除",
            "enable" => "启用",
            "disable" => "停用",
            "fail" or "failed" or "error" => "失败",
            "run" or "execute" => "执行",
            "retry" => "重试",
            _ => "操作"
        };
        return $"{operation}{resource}";
    }

    private static string DisplayActor(AuditRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.PluginName)) return record.PluginName;
        if (!string.IsNullOrWhiteSpace(record.PluginId)) return $"plugin:{record.PluginId}";
        return record.Actor;
    }

    private static string BuildRawJson(AuditRecord record, AuditCategory category, AuditStatus status)
    {
        if (!string.IsNullOrWhiteSpace(record.RawJson))
        {
            try
            {
                var node = JsonNode.Parse(record.RawJson);
                RedactNode(node);
                if (node is JsonObject obj)
                {
                    obj["operationIp"] = record.RemoteAddress;
                    return obj.ToJsonString(JsonOptions);
                }

                return new JsonObject
                {
                    ["operationIp"] = record.RemoteAddress,
                    ["data"] = node
                }.ToJsonString(JsonOptions);
            }
            catch (JsonException)
            {
            }
        }

        return SafeJson(new
        {
            record.Id,
            record.Actor,
            record.Action,
            record.Resource,
            Detail = record.Detail,
            Category = category.ToString(),
            Status = status.ToString(),
            operationIp = record.RemoteAddress,
            record.NodeId,
            record.RequestId,
            record.RelatedId,
            record.DurationMilliseconds,
            record.PluginId,
            record.PluginName,
            record.CreatedAt
        });
    }

    private static IReadOnlyList<AuditFieldChangeView> ParseChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<AuditFieldChangeView[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void RedactNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(x => x.Key).ToArray())
                {
                    if (IsSensitiveKey(key)) obj[key] = "***";
                    else RedactNode(obj[key]);
                }
                break;
            case JsonArray array:
                foreach (var child in array) RedactNode(child);
                break;
        }
    }

    private static bool IsSensitiveKey(string key) =>
        SensitiveKeys.Any(sensitive => key.Contains(sensitive, StringComparison.OrdinalIgnoreCase));

    private static string? RedactText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return SensitiveKeys.Any(key => value.Contains(key, StringComparison.OrdinalIgnoreCase))
            ? "***"
            : value;
    }

    private static string ShortId(Guid id) => id.ToString("N")[..8];
}

public sealed record PluginAuditEvent(
    string Stage,
    PluginDecisionKindName DecisionKind,
    string? Message,
    string? PluginId,
    string? PluginName,
    string? Resource,
    string? RequestId,
    string? RelatedId,
    string? NodeId,
    long? ElapsedMilliseconds,
    bool Failed = false);

public enum PluginDecisionKindName
{
    Continue,
    Reject,
    Defer,
    Quarantine,
    Cancel,
    Modify,
    Error
}
