using System.Text.Json;
using Dmart.Models.Json;

namespace Dmart.Plugins.Native;

// Adapts a native .so API plugin behind IApiPlugin. Routes declared in
// get_info() are mounted, and each request is forwarded to the native
// handle_request() function as a JSON envelope.
internal sealed class NativeApiPlugin : IApiPlugin
{
    private readonly NativePluginHandle _handle;
    private readonly List<NativeRoute> _routes;

    public string Shortname { get; }

    public NativeApiPlugin(NativePluginHandle handle, string shortname, List<NativeRoute> routes)
    {
        _handle = handle;
        Shortname = shortname;
        _routes = routes;
    }

    public void MapRoutes(RouteGroupBuilder group)
    {
        foreach (var route in _routes)
        {
            var method = route.Method.ToUpperInvariant();
            var h = _handle;

            switch (method)
            {
                case "GET":    group.MapGet(route.Path, async (HttpContext ctx) => await HandleNative(ctx, h)); break;
                case "POST":   group.MapPost(route.Path, async (HttpContext ctx) => await HandleNative(ctx, h)); break;
                case "PUT":    group.MapPut(route.Path, async (HttpContext ctx) => await HandleNative(ctx, h)); break;
                case "DELETE": group.MapDelete(route.Path, async (HttpContext ctx) => await HandleNative(ctx, h)); break;
                default:       group.MapGet(route.Path, async (HttpContext ctx) => await HandleNative(ctx, h)); break;
            }
        }

        // Default GET / if no routes declared
        if (_routes.Count == 0)
        {
            var h = _handle;
            group.MapGet("/", async (HttpContext ctx) => await HandleNative(ctx, h));
        }
    }

    private static async Task HandleNative(HttpContext ctx, NativePluginHandle handle)
    {
        var query = new Dictionary<string, string>();
        foreach (var q in ctx.Request.Query)
            query[q.Key] = q.Value.ToString();

        var headers = new Dictionary<string, string>();
        foreach (var h in ctx.Request.Headers)
            headers[h.Key] = h.Value.ToString();

        string? body = null;
        if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContentType.Count > 0)
        {
            using var reader = new StreamReader(ctx.Request.Body);
            body = await reader.ReadToEndAsync();
        }

        var envelope = new NativeApiRequest
        {
            Method = ctx.Request.Method,
            Path = ctx.Request.Path.Value ?? "/",
            Query = query,
            Headers = headers,
            Body = body,
            User = ctx.User.Identity?.Name,
        };

        var requestJson = JsonSerializer.Serialize(envelope, DmartJsonContext.Default.NativeApiRequest);
        var resultJson = handle.CallHandleRequest(requestJson);

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(resultJson);
    }

    internal sealed record NativeRoute(string Method, string Path);
}

// JSON envelope sent to a native API plugin's handle_request().
public sealed record NativeApiRequest
{
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public Dictionary<string, string> Query { get; init; } = new();
    public Dictionary<string, string> Headers { get; init; } = new();
    public string? Body { get; init; }
    public string? User { get; init; }
}
