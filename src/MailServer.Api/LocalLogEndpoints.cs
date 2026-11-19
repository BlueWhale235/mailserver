using MailServer.Infrastructure;

namespace MailServer.Api;

public static class LocalLogEndpoints
{
    public sealed record RetentionRequest(int RetentionDays);
    public static void MapLocalLogEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/settings/local-logs", async (LocalLogDatabase logs, CancellationToken token) =>
            Results.Ok(await logs.SettingsAsync(token))).RequirePermission(PermissionCodes.SettingsRead);
        api.MapPut("/settings/local-logs", async (RetentionRequest request, HttpContext http, LocalLogDatabase logs, CancellationToken token) =>
        {
            if (request.RetentionDays is < 1 or > 3650)
                return AdminApiProblems.Validation(http, "retentionDays", "日志保留天数必须为 1～3650 天。");
            return Results.Ok(await logs.SetRetentionAsync(request.RetentionDays, http.User.Identity?.Name ?? "admin", token));
        }).RequirePermission(PermissionCodes.SettingsManage);
    }
}
