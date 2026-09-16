using Dmart.Auth;

namespace Dmart.Middleware;

/// <summary>
/// Turns <see cref="PasswordHashingCapacityException"/> into HTTP 503 with a
/// Retry-After header and the usual failed-Response JSON body.
/// </summary>
/// <remarks>
/// A middleware rather than a catch at each call site: the exception can come
/// from login, registration, profile password change and the managed
/// user-create path, and the correct answer is the same at all of them. One
/// place also means a future hashing caller cannot forget to translate it.
///
/// 503 rather than 429 is deliberate. 429 says the CLIENT asked too often —
/// the per-IP limiter's answer, and retrying from a different address would
/// work. This is the server declining to allocate right now: every client sees
/// it at once, and Retry-After is a real estimate rather than a penalty. The
/// body matches the shape the rate limiter's OnRejected handler writes, so
/// clients parse one failure envelope for both.
/// </remarks>
public sealed class PasswordHashingCapacityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ILogger<PasswordHashingCapacityMiddleware> log)
    {
        try
        {
            await next(context);
        }
        catch (PasswordHashingCapacityException ex)
        {
            // Nothing has been written yet in the normal case; if something has,
            // the connection is already committed to a status and the only
            // honest thing is to let it go.
            if (context.Response.HasStarted)
            {
                log.LogWarning(ex, "password hashing at capacity after the response had started");
                throw;
            }

            log.LogWarning(
                "password hashing at capacity for {Path}; answering 503 (retry after {RetryAfter}s). "
                + "Raise PASSWORD_HASH_MEMORY_BUDGET_MB, or lower PASSWORD_HASH_MEMORY_KB, if this is steady state",
                context.Request.Path, ex.RetryAfterSeconds);

            context.Response.Clear();
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            context.Response.Headers.RetryAfter =
                ex.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await context.Response.WriteAsync(
                "{\"status\":\"failed\",\"error\":{\"type\":\"server\",\"code\":503,"
                + "\"message\":\"password hashing is at capacity, retry shortly\"}}",
                context.RequestAborted);
        }
    }
}
