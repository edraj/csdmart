namespace Dmart.Middleware;

public sealed class AuditLoggerMiddleware(ILogger<AuditLoggerMiddleware> log)
{
    public async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        var start = TimeUtils.Now();
        await next(http);
        log.LogInformation("{Method} {Path} -> {Status} ({Ms}ms)",
            http.Request.Method, http.Request.Path, http.Response.StatusCode,
            (int)(TimeUtils.Now() - start).TotalMilliseconds);
    }
}
