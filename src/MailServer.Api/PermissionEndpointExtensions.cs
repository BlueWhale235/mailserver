using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

public static class PermissionEndpointExtensions
{
    public static RouteHandlerBuilder RequirePermission(
        this RouteHandlerBuilder builder,
        string permissionCode) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var allowed = await context.HttpContext.RequestServices
                .GetRequiredService<PermissionService>()
                .HasPermissionAsync(context.HttpContext.User, permissionCode, null, context.HttpContext.RequestAborted);
            return allowed ? await next(context) : Results.Forbid();
        });

    public static RouteHandlerBuilder RequireDomainPermission(
        this RouteHandlerBuilder builder,
        string permissionCode,
        string routeValueName = "id") =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            if (!TryRouteGuid(context.HttpContext, routeValueName, out var domainId))
                return Results.BadRequest(new { error = "Domain id is required." });
            var allowed = await context.HttpContext.RequestServices
                .GetRequiredService<PermissionService>()
                .HasPermissionAsync(context.HttpContext.User, permissionCode, domainId, context.HttpContext.RequestAborted);
            return allowed ? await next(context) : Results.Forbid();
        });

    public static RouteHandlerBuilder RequireAnyDomainPermission(
        this RouteHandlerBuilder builder,
        string permissionCode) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var scope = await context.HttpContext.RequestServices
                .GetRequiredService<PermissionService>()
                .GetPermittedDomainScopeAsync(context.HttpContext.User, permissionCode, context.HttpContext.RequestAborted);
            return scope.HasAny ? await next(context) : Results.Forbid();
        });

    public static RouteHandlerBuilder RequireAllDomainsPermission(
        this RouteHandlerBuilder builder,
        string permissionCode) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var snapshot = await context.HttpContext.RequestServices
                .GetRequiredService<PermissionService>()
                .GetSnapshotAsync(context.HttpContext.User, context.HttpContext.RequestAborted);
            return snapshot.HasAllDomains(permissionCode) ? await next(context) : Results.Forbid();
        });

    public static RouteHandlerBuilder RequireBatchDomainPermission(
        this RouteHandlerBuilder builder,
        string permissionCode) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var domainIds = context.Arguments
                .SelectMany(ExtractDomainIds)
                .Where(x => x != Guid.Empty)
                .Distinct()
                .ToArray();
            if (domainIds.Length == 0)
                return Results.BadRequest(new { error = "At least one domain id is required." });
            var permissions = context.HttpContext.RequestServices.GetRequiredService<PermissionService>();
            foreach (var domainId in domainIds)
            {
                if (!await permissions.HasPermissionAsync(
                        context.HttpContext.User,
                        permissionCode,
                        domainId,
                        context.HttpContext.RequestAborted))
                {
                    return Results.Forbid();
                }
            }

            return await next(context);
        });

    public static RouteHandlerBuilder RequireAccountDomainPermission(
        this RouteHandlerBuilder builder,
        string permissionCode,
        string routeValueName = "id") =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            if (!TryRouteGuid(context.HttpContext, routeValueName, out var accountId))
                return Results.BadRequest(new { error = "Account id is required." });
            var domainId = await ResolveAccountDomainAsync(context.HttpContext, accountId);
            if (domainId is null) return Results.NotFound();
            var allowed = await context.HttpContext.RequestServices
                .GetRequiredService<PermissionService>()
                .HasPermissionAsync(context.HttpContext.User, permissionCode, domainId.Value, context.HttpContext.RequestAborted);
            return allowed ? await next(context) : Results.Forbid();
        });

    public static RouteHandlerBuilder RequireAliasDomainPermission(
        this RouteHandlerBuilder builder,
        string permissionCode,
        string routeValueName = "id") =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            if (!TryRouteGuid(context.HttpContext, routeValueName, out var aliasId))
                return Results.BadRequest(new { error = "Alias id is required." });
            var domainId = await ResolveAliasDomainAsync(context.HttpContext, aliasId);
            if (domainId is null) return Results.NotFound();
            var allowed = await context.HttpContext.RequestServices
                .GetRequiredService<PermissionService>()
                .HasPermissionAsync(context.HttpContext.User, permissionCode, domainId.Value, context.HttpContext.RequestAborted);
            return allowed ? await next(context) : Results.Forbid();
        });

    public static RouteHandlerBuilder RequireAddressDomainPermission(
        this RouteHandlerBuilder builder,
        string permissionCode,
        params string[] propertyNames) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var address = context.Arguments
                .Select(argument => ExtractStringProperty(argument, propertyNames))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(address) || !TryDomainFromAddress(address, out var domainName))
                return Results.BadRequest(new { error = "A valid email address is required." });
            var domainId = await ResolveDomainIdAsync(context.HttpContext, domainName);
            if (domainId is null) return Results.NotFound();
            var allowed = await context.HttpContext.RequestServices
                .GetRequiredService<PermissionService>()
                .HasPermissionAsync(context.HttpContext.User, permissionCode, domainId.Value, context.HttpContext.RequestAborted);
            return allowed ? await next(context) : Results.Forbid();
        });

    private static bool TryRouteGuid(HttpContext http, string routeValueName, out Guid value)
    {
        var raw = http.Request.RouteValues[routeValueName]?.ToString();
        return Guid.TryParse(raw, out value);
    }

    private static IEnumerable<Guid> ExtractDomainIds(object? argument)
    {
        if (argument is null) yield break;
        if (argument is DomainBatchRequest batch)
        {
            foreach (var id in batch.DomainIds) yield return id;
            yield break;
        }

    }

    private static string? ExtractStringProperty(object? argument, string[] propertyNames)
    {
        if (argument is null) return null;
        foreach (var name in propertyNames)
        {
            var property = argument.GetType().GetProperty(name);
            if (property?.GetValue(argument) is string value)
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryDomainFromAddress(string address, out string domainName)
    {
        var normalized = address.Trim();
        var at = normalized.LastIndexOf('@');
        if (at <= 0 || at == normalized.Length - 1)
        {
            domainName = string.Empty;
            return false;
        }

        domainName = normalized[(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(domainName);
    }

    private static async Task<Guid?> ResolveDomainIdAsync(HttpContext http, string domainName)
    {
        var factory = http.RequestServices.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
        return await db.Domains.AsNoTracking()
            .Where(x => x.Name == domainName)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(http.RequestAborted);
    }

    private static async Task<Guid?> ResolveAccountDomainAsync(HttpContext http, Guid accountId)
    {
        var factory = http.RequestServices.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
        return await db.Accounts.AsNoTracking()
            .Where(x => x.Id == accountId)
            .Select(x => (Guid?)x.DomainId)
            .SingleOrDefaultAsync(http.RequestAborted);
    }

    private static async Task<Guid?> ResolveAliasDomainAsync(HttpContext http, Guid aliasId)
    {
        var factory = http.RequestServices.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
        return await db.Aliases.AsNoTracking()
            .Where(x => x.Id == aliasId)
            .Select(x => (Guid?)x.DomainId)
            .SingleOrDefaultAsync(http.RequestAborted);
    }
}
