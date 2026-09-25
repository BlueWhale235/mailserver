using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace MailServer.Api;

public static class AccountDirectoryEndpoints
{
    public static void MapAccountDirectory(this RouteGroupBuilder api)
    {
        api.MapGet("/account-directories", async (string? query, int? page, HttpContext http,
            PermissionService permissions, AdminApplicationService administration, CancellationToken ct) =>
        {
            var accounts = await permissions.GetPermittedDomainScopeAsync(http.User, PermissionCodes.DomainAccountRead, ct);
            var aliases = await permissions.GetPermittedDomainScopeAsync(http.User, PermissionCodes.DomainAliasRead, ct);
            if (!accounts.HasAny && !aliases.HasAny) return Results.Forbid();
            if (page is < 1 or > 1000000) return Invalid(http, "page", "页码无效。");
            var scope = new PermittedDomainScope(accounts.AllDomains || aliases.AllDomains, accounts.DomainIds.Concat(aliases.DomainIds).ToHashSet());
            return Results.Ok(await administration.SearchPermissionDomainsAsync(query, page ?? 1, 20, scope, ct));
        });
        var group = api.MapGroup("/domains/{id:guid}/account-directory");
        group.AddEndpointFilter(async (context, next) =>
        {
            var id = Guid.Parse(context.HttpContext.Request.RouteValues["id"]!.ToString()!);
            var snapshot = await context.HttpContext.RequestServices.GetRequiredService<PermissionService>()
                .GetSnapshotAsync(context.HttpContext.User, context.HttpContext.RequestAborted);
            context.HttpContext.Items["directory-permissions"] = snapshot;
            if (!snapshot.Has(PermissionCodes.DomainAccountRead, id) && !snapshot.Has(PermissionCodes.DomainAliasRead, id))
                return Results.Forbid();
            try { return await next(context); }
            catch (KeyNotFoundException ex) { return Problem(context.HttpContext, 404, ex.Message); }
        });
        group.MapGet("", async (Guid id, HttpContext http, AccountDirectoryService directory, CancellationToken ct) =>
        {
            var p = Permissions(http);
            return Results.Ok(new {
                domainName = await directory.DomainNameAsync(id, ct),
                canReadDomain = p.Has(PermissionCodes.DomainRead, id),
                canReadAccounts = p.Has(PermissionCodes.DomainAccountRead, id),
                canReadAliases = p.Has(PermissionCodes.DomainAliasRead, id),
                canManageAccounts = p.Has(PermissionCodes.DomainAccountManage, id),
                canManageAliases = p.Has(PermissionCodes.DomainAliasManage, id)
            });
        });
        group.MapGet("/search", async (Guid id, string? query, bool? isEnabled, int? page, int? pageSize,
            HttpContext http, AccountDirectoryService directory, CancellationToken ct) =>
        {
            if (!ValidPage(page, pageSize)) return Invalid(http, "page", "页码必须大于零，每页数量必须在 5 到 100 之间。");
            var p = Permissions(http);
            return Results.Ok(await directory.SearchAsync(id, query, isEnabled, page ?? 1, pageSize ?? 20,
                p.Has(PermissionCodes.DomainAccountRead, id), p.Has(PermissionCodes.DomainAliasRead, id), null, ct));
        });
        group.MapGet("/accounts/{accountId:guid}", async (Guid id, Guid accountId,
            HttpContext http, AccountDirectoryService directory, CancellationToken ct) =>
        {
            var p = Permissions(http);
            var result = await directory.SearchAsync(id, null, null, 1, 5,
                p.Has(PermissionCodes.DomainAccountRead, id), p.Has(PermissionCodes.DomainAliasRead, id), accountId, ct);
            return result.Items.Count == 0 ? Problem(http, 404, "邮箱账号不存在。") : Results.Ok(result.Items[0]);
        });
        group.MapGet("/aliases", async (Guid id, Guid? accountId, bool? pending, string? query, int? page, int? pageSize,
            HttpContext http, AccountDirectoryService directory, CancellationToken ct) =>
        {
            if (!Permissions(http).Has(PermissionCodes.DomainAliasRead, id)) return Results.Forbid();
            if (!ValidPage(page, pageSize)) return Invalid(http, "page", "页码必须大于零，每页数量必须在 5 到 100 之间。");
            return Results.Ok(await directory.AliasesAsync(id, accountId, pending == true, query, page ?? 1, pageSize ?? 20, ct));
        });
        group.MapGet("/targets", async (Guid id, string? query, int? page, HttpContext http,
            AccountDirectoryService directory, IDbContextFactory<MailDbContext> factory, CancellationToken ct) =>
        {
            if (!Permissions(http).Has(PermissionCodes.DomainAliasRead, id)) return Results.Forbid();
            if (page is < 1 or > 1000000) return Invalid(http, "page", "页码无效。");
            await directory.DomainNameAsync(id, ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var search = query?.Trim().ToLowerInvariant() ?? "";
            var rows = db.Accounts.AsNoTracking().Where(x => x.DomainId == id && x.IsEnabled
                && (x.LocalPart + "@" + x.Domain.Name).Contains(search));
            var total = await rows.CountAsync(ct);
            var items = await rows.OrderBy(x => x.LocalPart).ThenBy(x => x.Id).Skip(((page ?? 1) - 1) * 20).Take(20)
                .Select(x => new { x.Id, address = x.LocalPart + "@" + x.Domain.Name }).ToListAsync(ct);
            return Results.Ok(new { items, totalCount = total, page = page ?? 1, pageSize = 20 });
        });
    }

    private static PermissionSnapshot Permissions(HttpContext http) => (PermissionSnapshot)http.Items["directory-permissions"]!;
    private static bool ValidPage(int? page, int? size) => page is null or >= 1 and <= 1000000 && size is null or >= 5 and <= 100;
    public static IResult Problem(HttpContext http, int status, string detail) => Results.Problem(statusCode: status,
        title: status == 404 ? "对象不存在" : "操作冲突", detail: detail,
        type: $"https://mailserver.local/problems/account-directory-{status}",
        extensions: new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier });
    public static IResult Invalid(HttpContext http, string field, string detail) => Results.ValidationProblem(
        new Dictionary<string, string[]> { [field] = [detail] }, title: "请检查输入内容", detail: detail,
        type: "https://mailserver.local/problems/account-directory-validation",
        extensions: new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier });
}
