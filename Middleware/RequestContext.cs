namespace Dmart.Middleware;

// Per-request context: current user shortname, language, request id.
public sealed class RequestContext
{
    public string? UserShortname { get; set; }
    public string Language { get; set; } = "en";
    public string RequestId { get; set; } = Guid.NewGuid().ToString("n");
}

public sealed class RequestContextMiddleware(RequestContext ctx)
{
    public async Task InvokeAsync(HttpContext http, RequestDelegate next)
    {
        ctx.RequestId = http.TraceIdentifier;
        ctx.UserShortname = http.User.Identity?.Name;
        ctx.Language = http.Request.Headers.AcceptLanguage.FirstOrDefault() ?? "en";
        await next(http);
    }
}
