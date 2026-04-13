using System.Diagnostics;

namespace Dmart.Middleware;

// Structured per-request logging — mirrors Python's set_logging() in main.py.
// Logs: method, path, status code, duration, user, and correlation ID.
// Skips OPTIONS and /cxb static asset requests to reduce noise (matching Python).
public static class RequestLoggingMiddleware
{
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var sw = Stopwatch.StartNew();
            await next();
            sw.Stop();

            var path = ctx.Request.Path.Value ?? "/";

            // Python skips OPTIONS and static asset requests.
            if (HttpMethods.IsOptions(ctx.Request.Method)) return;
            if (path.StartsWith("/cxb/") && path != "/cxb/config.json") return;

            var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Dmart.RequestLog");

            var status = ctx.Response.StatusCode;
            var method = ctx.Request.Method;
            var user = ctx.User.Identity?.Name ?? "anonymous";
            var correlationId = ctx.Response.Headers["X-Correlation-ID"].ToString();
            var durationMs = sw.ElapsedMilliseconds;

            if (status >= 500)
                log.LogError("HTTP {Method} {Path} → {Status} ({Duration}ms) user={User} cid={Cid}",
                    method, path, status, durationMs, user, correlationId);
            else if (status >= 400)
                log.LogWarning("HTTP {Method} {Path} → {Status} ({Duration}ms) user={User} cid={Cid}",
                    method, path, status, durationMs, user, correlationId);
            else
                log.LogInformation("HTTP {Method} {Path} → {Status} ({Duration}ms) user={User} cid={Cid}",
                    method, path, status, durationMs, user, correlationId);
        });
    }
}
