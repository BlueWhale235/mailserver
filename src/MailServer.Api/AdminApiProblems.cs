namespace MailServer.Api;

public static class AdminApiProblems
{
    private static Dictionary<string, object?> Trace(HttpContext http) =>
        new() { ["traceId"] = http.TraceIdentifier };

    public static IResult Validation(HttpContext http, string field, string detail) =>
        Results.ValidationProblem(
            new Dictionary<string, string[]> { [field] = [detail] },
            statusCode: StatusCodes.Status400BadRequest,
            title: "请求字段无效",
            type: "urn:mailserver:problem:validation-error",
            detail: detail,
            extensions: Trace(http));

    public static IResult Conflict(HttpContext http, string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "资源状态冲突",
            detail: detail,
            type: "urn:mailserver:problem:conflict",
            extensions: Trace(http));

    public static IResult NotFound(HttpContext http, string detail) =>
        Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "资源不存在",
            detail: detail,
            type: "urn:mailserver:problem:not-found",
            extensions: Trace(http));

    public static IResult Unauthorized(HttpContext http) =>
        Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "尚未登录或会话已失效",
            detail: "请重新登录后重试。",
            type: "urn:mailserver:problem:unauthorized",
            extensions: Trace(http));

    public static IResult InvalidCredentials(HttpContext http) =>
        Results.Problem(
            statusCode: StatusCodes.Status401Unauthorized,
            title: "登录失败",
            detail: "管理员账号或密码错误。",
            type: "urn:mailserver:problem:unauthorized",
            extensions: Trace(http));

    public static IResult Forbidden(HttpContext http) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "没有操作权限",
            detail: "当前账号没有执行此操作的权限，请联系管理员检查授权范围。",
            type: "urn:mailserver:problem:forbidden",
            extensions: Trace(http));
}
