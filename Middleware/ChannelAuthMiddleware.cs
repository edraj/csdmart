using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.Extensions.Options;

namespace Dmart.Middleware;

// Port of dmart Python's utils/middleware.py::ChannelMiddleware. Gate keyed
// off the `x-channel-key` header that runs before route matching:
//
//   * settings.EnableChannelAuth = false  → no-op pass-through.
//   * No header on request                → if the path matches any
//                                            channel's allowed_api_patterns,
//                                            reject with 403/channel_auth.
//                                            Else pass through.
//   * Header present but unknown key      → 403/channel_auth.
//   * Header present with valid key       → path must match that channel's
//                                            allowed_api_patterns, else 403.
//
// Pattern semantics: `Regex.IsMatch` (anywhere-in-string) — equivalent to
// Python's `pattern.search(path)`. Patterns are pre-compiled in
// ChannelsRegistry to avoid per-request work.
public static class ChannelAuthMiddleware
{
    public const string ChannelKeyHeader = "x-channel-key";

    public static IApplicationBuilder UseChannelAuth(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            var settings = ctx.RequestServices.GetRequiredService<IOptions<DmartSettings>>().Value;
            if (!settings.EnableChannelAuth)
            {
                await next();
                return;
            }

            var registry = ctx.RequestServices.GetRequiredService<ChannelsRegistry>();
            var path = ctx.Request.Path.Value ?? "/";

            var headerValues = ctx.Request.Headers[ChannelKeyHeader];
            var channelKey = headerValues.Count > 0 ? headerValues.ToString() : null;

            if (string.IsNullOrEmpty(channelKey))
            {
                foreach (var ch in registry.Channels)
                {
                    foreach (var pattern in ch.AllowedApiPatterns)
                    {
                        if (pattern.IsMatch(path))
                        {
                            await WriteForbidden(ctx, "Requested method or path is forbidden");
                            return;
                        }
                    }
                }
                await next();
                return;
            }

            ChannelsRegistry.Channel? matched = null;
            foreach (var ch in registry.Channels)
            {
                if (ch.Keys.Contains(channelKey))
                {
                    matched = ch;
                    break;
                }
            }

            if (matched is null)
            {
                await WriteForbidden(ctx, "Requested method or path is forbidden [2]");
                return;
            }

            foreach (var pattern in matched.AllowedApiPatterns)
            {
                if (pattern.IsMatch(path))
                {
                    await next();
                    return;
                }
            }

            await WriteForbidden(ctx, "Requested method or path is forbidden [3]");
        });
    }

    private static async Task WriteForbidden(HttpContext ctx, string message)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        ctx.Response.ContentType = "application/json";
        var body = Response.Fail(InternalErrorCode.NOT_ALLOWED, message, ErrorTypes.ChannelAuth);
        await ctx.Response.WriteAsJsonAsync(body, DmartJsonContext.Default.Response);
    }
}
