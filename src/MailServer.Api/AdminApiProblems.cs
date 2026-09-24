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
}
