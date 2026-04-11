namespace Dmart.Middleware;

public sealed class AuditLoggerMiddleware(ILogger<AuditLoggerMiddleware> log)
{
    public async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        var start = DateTime.UtcNow;
        await next(http);
        log.LogInformation("{Method} {Path} -> {Status} ({Ms}ms)",
            http.Request.Method, http.Request.Path, http.Response.StatusCode,
            (int)(DateTime.UtcNow - start).TotalMilliseconds);
    }
}
