using System.Security.Claims;
using MailServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Infrastructure;

/// <summary>
/// 管理后台权限代码表。新增页面或 API 时应优先复用读/管两级权限，而不是引入过细粒度代码。
/// </summary>
public static class PermissionCodes
{
    public const string FullAccess = "*";
    public const string IamManage = "iam.manage";
    public const string DashboardRead = "dashboard.read";
    public const string AuditRead = "audit.read";
    public const string DomainRead = "domain.read";
    public const string DomainManage = "domain.manage";
    public const string DomainQuarantineRead = "domain.quarantine.read";
    public const string DomainQuarantineManage = "domain.quarantine.manage";
    public const string ApiKeyRead = "api-key.read";
    public const string ApiKeyManage = "api-key.manage";
    public const string AdminAccountRead = "admin.account.read";
    public const string AdminAccountManage = "admin.account.manage";
    public const string FirewallRead = "firewall.read";
    public const string FirewallManage = "firewall.manage";
    public const string QueueRead = "queue.read";
    public const string QueueManage = "queue.manage";
    public const string SettingsRead = "settings.read";
    public const string SettingsManage = "settings.manage";
    public const string DnsRead = "dns.read";
    public const string DnsManage = "dns.manage";
    public const string TaskRead = "task.read";
    public const string TaskManage = "task.manage";

    public const string ApiKeyList = ApiKeyRead;
    public const string ApiKeyCreate = ApiKeyManage;
    public const string ApiKeyUpdate = ApiKeyManage;
    public const string ApiKeyDelete = ApiKeyManage;
    public const string ApiKeyRefresh = ApiKeyManage;
    public const string AdminAccountList = AdminAccountRead;
    public const string AdminAccountCreate = AdminAccountManage;
    public const string AdminAccountUpdate = AdminAccountManage;
    public const string AdminAccountDelete = AdminAccountManage;
    public const string DomainList = DomainRead;
    public const string DomainCreate = DomainManage;
    public const string DomainDetail = DomainRead;
    public const string DomainUpdate = DomainManage;
    public const string DomainDelete = DomainManage;
    public const string DomainQuotaUpdate = DomainManage;
    public const string DomainDnsDetail = DomainRead;
    public const string DomainDnsUpdate = DomainManage;
    public const string DomainDnsCheck = DomainRead;
    public const string DomainCertificateDetail = DomainRead;
    public const string DomainCertificateUpdate = DomainManage;
    public const string DomainCertificateDelete = DomainManage;
    public const string DomainAccountRead = "domain.account.read";
    public const string DomainAccountManage = "domain.account.manage";
    public const string DomainAliasRead = "domain.alias.read";
    public const string DomainAliasManage = "domain.alias.manage";
    public const string DomainAccountList = DomainAccountRead;
    public const string DomainAccountCreate = DomainAccountManage;
    public const string DomainAccountUpdate = DomainAccountManage;
    public const string DomainAccountPassword = DomainAccountManage;
    public const string DomainAliasList = DomainAliasRead;
    public const string DomainAliasCreate = DomainAliasManage;
    public const string DomainAliasUpdate = DomainAliasManage;
    public const string DomainAliasDelete = DomainAliasManage;
    public const string DomainRuleList = DomainRead;
    public const string DomainRuleCreate = DomainManage;
    public const string DomainRuleUpdate = DomainManage;
    public const string DomainRuleDelete = DomainManage;
    public const string DomainRuleReorder = DomainManage;
    public const string DomainAuditList = DomainRead;
    public const string DomainQuarantineList = DomainQuarantineRead;
    public const string DomainQuarantineRelease = DomainQuarantineManage;
    public const string DomainQuarantineDelete = DomainQuarantineManage;
    public const string DashboardView = DashboardRead;
    public const string AuditList = AuditRead;
    public const string FirewallList = FirewallRead;
    public const string FirewallCreate = FirewallManage;
    public const string FirewallUpdate = FirewallManage;
    public const string FirewallDelete = FirewallManage;
    public const string QueueList = QueueRead;
    public const string QueueRetry = QueueManage;
    public const string QueueDelete = QueueManage;
    public const string SettingsDetail = SettingsRead;
    public const string SettingsUpdate = SettingsManage;
    public const string DnsDiagnose = DnsRead;

    public static readonly IReadOnlySet<string> DomainScoped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        DomainRead,
        DomainManage,
        DomainQuarantineRead,
        DomainQuarantineManage, DomainAccountRead, DomainAccountManage, DomainAliasRead, DomainAliasManage
    };

    private static readonly IReadOnlyDictionary<string, string> ReadToManageMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DomainAccountRead] = DomainAccountManage,
            [DomainAliasRead] = DomainAliasManage,
            [ApiKeyRead] = ApiKeyManage,
            [AdminAccountRead] = AdminAccountManage,
            [FirewallRead] = FirewallManage,
            [QueueRead] = QueueManage,
            [SettingsRead] = SettingsManage,
            [DnsRead] = DnsManage,
            [TaskRead] = TaskManage,
            [DomainQuarantineRead] = DomainQuarantineManage,
            [DomainRead] = DomainManage
        };

    /// <summary>
    /// 只有域相关权限支持绑定到单个域，其它权限均按系统级授权处理。
    /// </summary>
    public static bool SupportsDomainScope(string code) => DomainScoped.Contains(code.Trim());

    /// <summary>
    /// 管理权限隐含读取权限，例如 domain.manage 自动拥有 domain.read。
    /// </summary>
    public static string? ManagementPermissionFor(string code)
    {
        var canonical = code.Trim().ToLowerInvariant();
        return ReadToManageMap.TryGetValue(canonical, out var management) ? management : null;
    }
}

public sealed record PermissionCatalogGroup(
    string Key,
    string Name,
    IReadOnlyList<PermissionCatalogItem> Permissions);

public sealed record PermissionCatalogItem(
    string Code,
    string Name,
    string Description,
    string GroupKey,
    bool SupportsDomainScope);

public sealed record PermissionGrantInput(
    string PermissionCode,
    string? ResourceType = null,
    string? ResourceKey = null);

public sealed record PermissionGrantView(
    PermissionSubjectType SubjectType,
    Guid SubjectId,
    string PermissionCode,
    string ResourceType,
    string ResourceKey,
    string CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record PermissionSubject(PermissionSubjectType Type, Guid Id);

/// <summary>
/// 当前主体可访问的域范围。AllDomains 为 true 时 DomainIds 不再限制访问。
/// </summary>
public sealed record PermittedDomainScope(bool AllDomains, IReadOnlySet<Guid> DomainIds)
{
    public bool HasAny => AllDomains || DomainIds.Count > 0;
}

/// <summary>
/// 权限快照，按一次请求或一次操作加载后在内存中判断，避免每个按钮/端点重复查库。
/// </summary>
public sealed class PermissionSnapshot
{
    public bool FullAccess { get; set; }
    public HashSet<string> SystemPermissions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllDomainPermissions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<Guid>> DomainPermissions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Has(string code, Guid? domainId = null)
    {
        if (FullAccess) return true;
        var canonical = code.Trim().ToLowerInvariant();
        var management = PermissionCodes.ManagementPermissionFor(canonical);
        if (domainId is null)
        {
            // 系统级检查允许管理权限隐含读取权限。
            return SystemPermissions.Contains(canonical)
                   || (management is not null && SystemPermissions.Contains(management));
        }

        return HasDomainPermission(canonical, domainId.Value)
               || (management is not null && HasDomainPermission(management, domainId.Value));
    }

    public bool HasAny(params string[] codes) =>
        FullAccess || codes.Any(code => Has(code) || HasAnyDomainPermission(code));

    public bool HasAnyDomainPermission(string code)
    {
        if (FullAccess) return true;
        return HasAllDomains(code) || DomainPermissions.Values.SelectMany(ids => ids).Distinct().Any(id => Has(code, id));
    }

    public bool HasAllDomains(string code)
    {
        if (FullAccess) return true;
        var canonical = code.Trim().ToLowerInvariant();
        var management = PermissionCodes.ManagementPermissionFor(canonical);
        if (AllDomainPermissions.Contains(canonical)
            || (management is not null && AllDomainPermissions.Contains(management)))
            return true;
        return canonical switch
        {
            PermissionCodes.DomainQuarantineRead or PermissionCodes.DomainAccountRead or PermissionCodes.DomainAliasRead =>
                AllDomainPermissions.Contains(PermissionCodes.DomainRead)
                || AllDomainPermissions.Contains(PermissionCodes.DomainManage),
            PermissionCodes.DomainQuarantineManage or PermissionCodes.DomainAccountManage or PermissionCodes.DomainAliasManage =>
                AllDomainPermissions.Contains(PermissionCodes.DomainManage),
            _ => false
        };
    }

    private bool HasDomainPermission(string code, Guid domainId)
    {
        if (AllDomainPermissions.Contains(code)
            || (DomainPermissions.TryGetValue(code, out var ids) && ids.Contains(domainId)))
            return true;

        var implied = code switch
        {
            // 隔离区权限可由更宽的域读/管权限隐含，方便旧角色平滑使用新增隔离功能。
            PermissionCodes.DomainQuarantineRead or PermissionCodes.DomainAccountRead or PermissionCodes.DomainAliasRead => PermissionCodes.DomainRead,
            PermissionCodes.DomainQuarantineManage or PermissionCodes.DomainAccountManage or PermissionCodes.DomainAliasManage => PermissionCodes.DomainManage,
            _ => null
        };
        return implied is not null
               && (AllDomainPermissions.Contains(implied)
                   || (DomainPermissions.TryGetValue(implied, out var impliedIds) && impliedIds.Contains(domainId)));
    }
}

/// <summary>
/// 权限服务，负责授权目录、授权写入和权限快照加载。
/// </summary>
public sealed class PermissionService(IDbContextFactory<MailDbContext> dbFactory)
{
    public const string DomainResourceType = "domain";
    public const string AllResources = "*";

    private static readonly IReadOnlyList<PermissionCatalogGroup> Catalog =
    [
        Group("full", "全部权限",
        [
            Item(PermissionCodes.FullAccess, "全部权限", "拥有后台和 API 的全部操作权限。", domainScope: false)
        ]),
        Group("identity", "身份与授权",
        [
            Item(PermissionCodes.IamManage, "授权管理", "管理管理员和 API Key 的权限授权。", domainScope: false),
            Item(PermissionCodes.ApiKeyRead, "API Key 只读", "查看 API Key 列表、状态和授权。", domainScope: false),
            Item(PermissionCodes.ApiKeyManage, "API Key 管理", "创建、刷新、授权和删除 API Key。", domainScope: false),
            Item(PermissionCodes.AdminAccountRead, "管理员只读", "查看后台管理员账号列表。", domainScope: false),
            Item(PermissionCodes.AdminAccountManage, "管理员管理", "创建、编辑、授权和删除后台管理员账号。", domainScope: false)
        ]),
        Group("domain", "域名",
        [
            Item(PermissionCodes.DomainRead, "域名只读", "查看授权范围内的域名、账号、别名、规则、DNS、证书、投递队列和日志。", domainScope: true),
            Item(PermissionCodes.DomainManage, "域名管理", "创建域名，管理授权范围内的域名配置、账号、别名、规则、DNS、证书、配额和投递队列。", domainScope: true),
            Item(PermissionCodes.DomainAccountRead, "账号只读", "查看账号状态与容量。", domainScope: true),
            Item(PermissionCodes.DomainAccountManage, "账号管理", "创建账号、修改状态容量和密码。", domainScope: true),
            Item(PermissionCodes.DomainAliasRead, "别名只读", "查看别名及其目标账号地址。", domainScope: true),
            Item(PermissionCodes.DomainAliasManage, "别名管理", "创建、修改、转移与删除别名。", domainScope: true),
            Item(PermissionCodes.DomainQuarantineRead, "审核与隔离只读", "查看授权域名下的隔离邮件、审核状态和原始邮件内容。", domainScope: true),
            Item(PermissionCodes.DomainQuarantineManage, "审核与隔离管理", "释放或删除授权域名下的隔离邮件。", domainScope: true)
        ]),
        Group("system", "系统功能",
        [
            Item(PermissionCodes.DashboardRead, "概览只读", "查看后台概览。", domainScope: false),
            Item(PermissionCodes.AuditRead, "审计日志只读", "查看全局审计日志、详情和导出审计 JSON。", domainScope: false),
            Item(PermissionCodes.FirewallRead, "防火墙只读", "查看防火墙规则。", domainScope: false),
            Item(PermissionCodes.FirewallManage, "防火墙管理", "创建、启停、编辑和删除防火墙规则。", domainScope: false),
            Item(PermissionCodes.QueueRead, "投递队列只读", "查看外发投递队列和原始邮件。", domainScope: false),
            Item(PermissionCodes.QueueManage, "投递队列管理", "立即重试或删除投递任务。", domainScope: false),
            Item(PermissionCodes.SettingsRead, "系统设置只读", "查看系统设置和通知模板。", domainScope: false),
            Item(PermissionCodes.SettingsManage, "系统设置管理", "修改系统设置和通知模板。", domainScope: false),
            Item(PermissionCodes.DnsRead, "DNS 诊断只读", "执行 DNS 诊断并查看结果。", domainScope: false),
            Item(PermissionCodes.DnsManage, "DNS 诊断管理", "预留给需要写入 DNS 诊断配置的管理操作。", domainScope: false)
        ]),
        Group("operations", "任务中心",
        [
            Item(PermissionCodes.TaskRead, "任务中心只读", "查看定时任务、执行历史和回收站。", domainScope: false),
            Item(PermissionCodes.TaskManage, "任务中心管理", "配置、执行任务以及恢复或永久清理回收项。", domainScope: false)
        ])
    ];

    private static readonly IReadOnlyDictionary<string, PermissionCatalogItem> CatalogByCode =
        Catalog.SelectMany(group => group.Permissions)
            .ToDictionary(item => item.Code, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PermissionCatalogGroup> GetCatalog() => Catalog;

    public static PermissionGrantInput FullAccessGrant() =>
        new(PermissionCodes.FullAccess, string.Empty, string.Empty);

    public async Task<PermissionSnapshot> GetSnapshotAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var subject = GetSubject(principal);
        if (subject is null) return new PermissionSnapshot();
        if (subject.Type == PermissionSubjectType.AdminUser
            && await IsPrimaryAdminAsync(subject.Id, cancellationToken))
        {
            return new PermissionSnapshot { FullAccess = true };
        }

        var grants = await LoadGrantsAsync(subject.Type, subject.Id, cancellationToken);
        return BuildSnapshot(grants);
    }

    public async Task<bool> IsPrimaryAdminAsync(Guid adminId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var primaryAdminId = await db.AdminUsers.AsNoTracking()
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return primaryAdminId == adminId;
    }

    public async Task<bool> HasPermissionAsync(
        ClaimsPrincipal principal,
        string permissionCode,
        Guid? domainId,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(principal, cancellationToken);
        return snapshot.Has(permissionCode, domainId);
    }

    public async Task<bool> HasAnyPermissionAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken,
        params string[] permissionCodes)
    {
        var snapshot = await GetSnapshotAsync(principal, cancellationToken);
        return snapshot.HasAny(permissionCodes);
    }

    public async Task<PermittedDomainScope> GetPermittedDomainScopeAsync(
        ClaimsPrincipal principal,
        string permissionCode,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(principal, cancellationToken);
        if (snapshot.HasAllDomains(permissionCode))
        {
            return new PermittedDomainScope(true, new HashSet<Guid>());
        }

        var canonical = permissionCode.Trim().ToLowerInvariant();
        var management = PermissionCodes.ManagementPermissionFor(canonical);
        var result = new HashSet<Guid>();
        if (snapshot.DomainPermissions.TryGetValue(canonical, out var ids))
        {
            result.UnionWith(ids);
        }

        if (management is not null && snapshot.DomainPermissions.TryGetValue(management, out var managementIds))
        {
            result.UnionWith(managementIds);
        }

        if (canonical is PermissionCodes.DomainQuarantineRead or PermissionCodes.DomainAccountRead or PermissionCodes.DomainAliasRead)
        {
            if (snapshot.DomainPermissions.TryGetValue(PermissionCodes.DomainRead, out var readIds))
                result.UnionWith(readIds);
            if (snapshot.DomainPermissions.TryGetValue(PermissionCodes.DomainManage, out var manageIds))
                result.UnionWith(manageIds);
        }
        else if ((canonical is PermissionCodes.DomainQuarantineManage or PermissionCodes.DomainAccountManage or PermissionCodes.DomainAliasManage)
                 && snapshot.DomainPermissions.TryGetValue(PermissionCodes.DomainManage, out var manageIds))
        {
            result.UnionWith(manageIds);
        }

        return new PermittedDomainScope(false, result);
    }

    public async Task<bool> CanDelegateGrantsAsync(
        ClaimsPrincipal principal,
        IReadOnlyCollection<PermissionGrantInput> grants,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeGrantInputs(grants);
        var snapshot = await GetSnapshotAsync(principal, cancellationToken);
        foreach (var grant in normalized)
        {
            if (grant.PermissionCode == PermissionCodes.FullAccess)
            {
                if (!snapshot.FullAccess) return false;
            }
            else if (PermissionCodes.SupportsDomainScope(grant.PermissionCode))
            {
                if (grant.ResourceKey == AllResources)
                {
                    if (!snapshot.HasAllDomains(grant.PermissionCode)) return false;
                }
                else if (!Guid.TryParse(grant.ResourceKey, out var domainId)
                         || !snapshot.Has(grant.PermissionCode, domainId))
                {
                    return false;
                }
            }
            else if (!snapshot.Has(grant.PermissionCode))
            {
                return false;
            }
        }
        return true;
    }

    public async Task<IReadOnlyList<PermissionGrantView>> GetGrantsAsync(
        PermissionSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await EnsureSubjectExistsAsync(db, subjectType, subjectId, cancellationToken);
        return await db.PermissionGrants.AsNoTracking()
            .Where(x => x.SubjectType == subjectType && x.SubjectId == subjectId)
            .OrderBy(x => x.PermissionCode)
            .ThenBy(x => x.ResourceType)
            .ThenBy(x => x.ResourceKey)
            .Select(x => new PermissionGrantView(
                x.SubjectType,
                x.SubjectId,
                x.PermissionCode,
                x.ResourceType,
                x.ResourceKey,
                x.CreatedBy,
                x.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PermissionGrantView>> ReplaceGrantsAsync(
        PermissionSubjectType subjectType,
        Guid subjectId,
        IReadOnlyCollection<PermissionGrantInput> grants,
        string actor,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeGrantInputs(grants);
        if (normalized.Count == 0)
            throw new InvalidOperationException("At least one permission grant is required.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await EnsureSubjectExistsAsync(db, subjectType, subjectId, cancellationToken);
        if (subjectType == PermissionSubjectType.AdminUser
            && await IsPrimaryAdminAsync(db, subjectId, cancellationToken))
        {
            throw new InvalidOperationException("The initial administrator always has global access and its permissions cannot be modified.");
        }

        await EnsureReferencedDomainsExistAsync(db, normalized, cancellationToken);
        if (subjectType == PermissionSubjectType.AdminUser)
        {
            await EnsureAdminKeepsManagerAsync(db, subjectId, normalized, cancellationToken);
        }

        var existingQuery = db.PermissionGrants
            .Where(x => x.SubjectType == subjectType && x.SubjectId == subjectId);
        if (db.Database.IsRelational())
        {
            await existingQuery.ExecuteDeleteAsync(cancellationToken);
        }
        else
        {
            db.PermissionGrants.RemoveRange(await existingQuery.ToListAsync(cancellationToken));
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var grant in normalized)
        {
            db.PermissionGrants.Add(new PermissionGrant
            {
                SubjectType = subjectType,
                SubjectId = subjectId,
                PermissionCode = grant.PermissionCode,
                ResourceType = grant.ResourceType ?? string.Empty,
                ResourceKey = grant.ResourceKey ?? string.Empty,
                CreatedBy = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
                CreatedAt = now
            });
        }

        db.AuditRecords.Add(new AuditRecord
        {
            Actor = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor,
            Action = "permission.grants.replace",
            Resource = $"{SubjectResource(subjectType)}:{subjectId}",
            Detail = string.Join('\n', normalized.Select(FormatGrant))
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetGrantsAsync(subjectType, subjectId, cancellationToken);
    }

    public async Task EnsureAdminCanBeDisabledOrDeletedAsync(
        Guid adminId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var admin = await db.AdminUsers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == adminId, cancellationToken)
            ?? throw new KeyNotFoundException("Administrator not found.");
        if (await IsPrimaryAdminAsync(db, adminId, cancellationToken))
            throw new InvalidOperationException("The initial administrator cannot be disabled or deleted.");
        if (!admin.IsEnabled) return;
        if (!await HasManagementGrantAsync(db, adminId, cancellationToken)) return;
        if (!await HasOtherEnabledManagerAsync(db, adminId, cancellationToken))
            throw new InvalidOperationException("At least one enabled administrator with permission management access is required.");
    }

    public async Task GrantFullAccessIfMissingAsync(
        PermissionSubjectType subjectType,
        Guid subjectId,
        string actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.PermissionGrants.AnyAsync(
                x => x.SubjectType == subjectType
                     && x.SubjectId == subjectId
                     && x.PermissionCode == PermissionCodes.FullAccess,
                cancellationToken))
        {
            return;
        }

        db.PermissionGrants.Add(new PermissionGrant
        {
            SubjectType = subjectType,
            SubjectId = subjectId,
            PermissionCode = PermissionCodes.FullAccess,
            ResourceType = string.Empty,
            ResourceKey = string.Empty,
            CreatedBy = string.IsNullOrWhiteSpace(actor) ? "system" : actor
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static PermissionSubject? GetSubject(ClaimsPrincipal principal)
    {
        var idValue = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? principal.FindFirstValue("sub");
        if (!Guid.TryParse(idValue, out var id)) return null;
        var authType = principal.FindFirstValue("auth_type");
        var subjectType = string.Equals(authType, "api_key", StringComparison.OrdinalIgnoreCase)
            ? PermissionSubjectType.ApiKey
            : PermissionSubjectType.AdminUser;
        return new PermissionSubject(subjectType, id);
    }

    public static IReadOnlyList<PermissionGrantInput> NormalizeGrantInputs(
        IReadOnlyCollection<PermissionGrantInput>? grants)
    {
        var result = new Dictionary<string, PermissionGrantInput>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in grants ?? [])
        {
            var code = input.PermissionCode.Trim();
            if (!CatalogByCode.TryGetValue(code, out var catalogItem))
                throw new InvalidOperationException($"Unknown permission: {code}");
            if (string.Equals(code, PermissionCodes.FullAccess, StringComparison.OrdinalIgnoreCase))
            {
                result.Clear();
                result[PermissionCodes.FullAccess] = FullAccessGrant();
                return result.Values.ToArray();
            }

            var resourceType = string.Empty;
            var resourceKey = string.Empty;
            if (catalogItem.SupportsDomainScope)
            {
                resourceType = DomainResourceType;
                resourceKey = string.IsNullOrWhiteSpace(input.ResourceKey)
                    ? AllResources
                    : input.ResourceKey.Trim();
                if (!string.Equals(resourceKey, AllResources, StringComparison.Ordinal)
                    && !Guid.TryParse(resourceKey, out _))
                {
                    throw new InvalidOperationException("Domain-scoped permissions require a valid domain id.");
                }
            }

            var key = $"{code}|{resourceType}|{resourceKey}";
            result[key] = new PermissionGrantInput(code, resourceType, resourceKey);
        }

        return result.Values.ToArray();
    }

    private async Task<IReadOnlyList<PermissionGrant>> LoadGrantsAsync(
        PermissionSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.PermissionGrants.AsNoTracking()
            .Where(x => x.SubjectType == subjectType && x.SubjectId == subjectId)
            .ToListAsync(cancellationToken);
    }

    private static PermissionSnapshot BuildSnapshot(IEnumerable<PermissionGrant> grants)
    {
        var snapshot = new PermissionSnapshot();
        foreach (var grant in grants)
        {
            if (string.Equals(grant.PermissionCode, PermissionCodes.FullAccess, StringComparison.OrdinalIgnoreCase))
            {
                snapshot.FullAccess = true;
                continue;
            }

            if (string.Equals(grant.ResourceType, DomainResourceType, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(grant.ResourceKey, AllResources, StringComparison.Ordinal))
                {
                    snapshot.AllDomainPermissions.Add(grant.PermissionCode);
                }
                else if (Guid.TryParse(grant.ResourceKey, out var domainId))
                {
                    if (!snapshot.DomainPermissions.TryGetValue(grant.PermissionCode, out var ids))
                    {
                        ids = [];
                        snapshot.DomainPermissions[grant.PermissionCode] = ids;
                    }

                    ids.Add(domainId);
                }
            }
            else
            {
                snapshot.SystemPermissions.Add(grant.PermissionCode);
            }
        }

        return snapshot;
    }

    private static PermissionCatalogGroup Group(string key, string name, IReadOnlyList<PermissionCatalogItem> permissions) =>
        new(
            key,
            name,
            permissions.Select(item => item with { GroupKey = key }).ToArray());

    private static PermissionCatalogItem Item(string code, string name, string description, bool domainScope) =>
        new(code, name, description, string.Empty, domainScope);

    private static string SubjectResource(PermissionSubjectType subjectType) =>
        subjectType == PermissionSubjectType.ApiKey ? "api-key" : "admin";

    private static string FormatGrant(PermissionGrantInput grant) =>
        string.IsNullOrWhiteSpace(grant.ResourceType)
            ? grant.PermissionCode
            : $"{grant.PermissionCode}:{grant.ResourceType}:{grant.ResourceKey}";

    private static async Task EnsureSubjectExistsAsync(
        MailDbContext db,
        PermissionSubjectType subjectType,
        Guid subjectId,
        CancellationToken cancellationToken)
    {
        var exists = subjectType switch
        {
            PermissionSubjectType.AdminUser => await db.AdminUsers.AnyAsync(x => x.Id == subjectId, cancellationToken),
            PermissionSubjectType.ApiKey => await db.ApiKeys.AnyAsync(x => x.Id == subjectId, cancellationToken),
            _ => false
        };
        if (!exists) throw new KeyNotFoundException("Permission subject not found.");
    }

    private static async Task EnsureReferencedDomainsExistAsync(
        MailDbContext db,
        IReadOnlyList<PermissionGrantInput> grants,
        CancellationToken cancellationToken)
    {
        var domainIds = grants
            .Where(x => string.Equals(x.ResourceType, DomainResourceType, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(x.ResourceKey, AllResources, StringComparison.Ordinal))
            .Select(x => Guid.Parse(x.ResourceKey!))
            .Distinct()
            .ToArray();
        if (domainIds.Length == 0) return;
        var existing = await db.Domains.CountAsync(x => domainIds.Contains(x.Id), cancellationToken);
        if (existing != domainIds.Length)
            throw new KeyNotFoundException("One or more permission domains were not found.");
    }

    private static async Task EnsureAdminKeepsManagerAsync(
        MailDbContext db,
        Guid adminId,
        IReadOnlyList<PermissionGrantInput> proposed,
        CancellationToken cancellationToken)
    {
        var admin = await db.AdminUsers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == adminId, cancellationToken)
            ?? throw new KeyNotFoundException("Administrator not found.");
        if (!admin.IsEnabled) return;
        var proposedHasManager = proposed.Any(x =>
            string.Equals(x.PermissionCode, PermissionCodes.FullAccess, StringComparison.OrdinalIgnoreCase)
            || string.Equals(x.PermissionCode, PermissionCodes.IamManage, StringComparison.OrdinalIgnoreCase));
        if (proposedHasManager) return;
        if (!await HasManagementGrantAsync(db, adminId, cancellationToken)) return;
        if (!await HasOtherEnabledManagerAsync(db, adminId, cancellationToken))
            throw new InvalidOperationException("At least one enabled administrator with permission management access is required.");
    }

    private static async Task<bool> IsPrimaryAdminAsync(
        MailDbContext db,
        Guid adminId,
        CancellationToken cancellationToken)
    {
        var primaryAdminId = await db.AdminUsers.AsNoTracking()
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return primaryAdminId == adminId;
    }

    private static Task<bool> HasManagementGrantAsync(
        MailDbContext db,
        Guid adminId,
        CancellationToken cancellationToken) =>
        db.PermissionGrants.AnyAsync(
            x => x.SubjectType == PermissionSubjectType.AdminUser
                 && x.SubjectId == adminId
                 && (x.PermissionCode == PermissionCodes.FullAccess || x.PermissionCode == PermissionCodes.IamManage),
            cancellationToken);

    private static Task<bool> HasOtherEnabledManagerAsync(
        MailDbContext db,
        Guid adminId,
        CancellationToken cancellationToken) =>
        db.AdminUsers.AnyAsync(
            admin => admin.IsEnabled
                     && admin.Id != adminId
                     && db.PermissionGrants.Any(grant =>
                         grant.SubjectType == PermissionSubjectType.AdminUser
                         && grant.SubjectId == admin.Id
                         && (grant.PermissionCode == PermissionCodes.FullAccess || grant.PermissionCode == PermissionCodes.IamManage)),
            cancellationToken);
}
