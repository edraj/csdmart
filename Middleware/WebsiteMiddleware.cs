using Dmart.Config;
using Dmart.Services.Website;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Dmart.Middleware;

// Serves the static site `dmart website build` writes, under WEBSITE_URL
// (default /website), from the build WEBSITE_DIR/current names.
//
// Unlike CXB and Catalog nothing is embedded: the site is content, built by
// an operator from the website space. Until a build exists the prefix falls
// through to routing and answers a plain 404 (the 404-envelope wrapper skips
// this prefix, as it does for the SPAs).
//
// URLs are extensionless: /website/data-model → data-model/index.html, the
// prefix itself → index.html. A miss under the prefix gets the build's
// 404.html with status 404. `current` is re-read only when its timestamp
// changes, so a rebuild goes live on the next request without a restart.
public static class WebsiteMiddleware
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static IApplicationBuilder UseWebsite(this IApplicationBuilder app, PathString prefix)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<DmartSettings>>().Value;
        var root = WebsiteBuilder.ResolveRoot(settings);
        var current = new CurrentBuild(root);

        return app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments(prefix, out var rest)
                || !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
            {
                await next();
                return;
            }

            var buildDir = current.Resolve();
            if (buildDir is null)
            {
                await next();
                return;
            }

            var file = Locate(buildDir, rest.Value ?? "");
            if (file is not null)
            {
                await SendAsync(ctx, file, StatusCodes.Status200OK);
                return;
            }

            var notFound = Path.Combine(buildDir, "404.html");
            if (File.Exists(notFound)) await SendAsync(ctx, notFound, StatusCodes.Status404NotFound);
            else ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        });
    }

    // The file a request path names inside the build, or null. Empty
    // segments, dot-segments (which covers "." and ".." and hidden files) and
    // backslashes are refused before touching the disk; the full-path
    // containment check then catches whatever a platform's path rules would
    // otherwise let climb out of the build directory.
    internal static string? Locate(string buildDir, string requestPath)
    {
        var rel = requestPath.Trim('/');
        if (rel.Contains('\\') || rel.Contains('\0')) return null;
        var segments = rel.Length == 0 ? [] : rel.Split('/');
        if (segments.Any(s => s.Length == 0 || s.StartsWith('.'))) return null;

        var candidate = segments.Length > 0 && Path.HasExtension(segments[^1])
            ? Path.Combine([buildDir, .. segments])
            : Path.Combine([buildDir, .. segments, "index.html"]);
        var full = Path.GetFullPath(candidate);
        var rootFull = Path.GetFullPath(buildDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.Ordinal)) return null;
        return File.Exists(full) ? full : null;
    }

    private static async Task SendAsync(HttpContext ctx, string file, int status)
    {
        var info = new FileInfo(file);
        var lastModified = new DateTimeOffset(info.LastWriteTimeUtc);
        var etag = $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";
        var headers = ctx.Response.GetTypedHeaders();

        if (status == StatusCodes.Status200OK
            && ctx.Request.Headers.IfNoneMatch.ToString() == etag)
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            ctx.Response.Headers.ETag = etag;
            return;
        }

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = ContentTypes.TryGetContentType(file, out var type)
            ? (type.StartsWith("text/", StringComparison.Ordinal) || type.EndsWith("xml", StringComparison.Ordinal)
                ? type + "; charset=utf-8"
                : type)
            : "application/octet-stream";
        ctx.Response.ContentLength = info.Length;
        headers.LastModified = lastModified;
        ctx.Response.Headers.ETag = etag;
        if (HttpMethods.IsHead(ctx.Request.Method)) return;
        await ctx.Response.SendFileAsync(file, ctx.RequestAborted);
    }

    // `current` names the live build; caching it by the pointer file's
    // timestamp turns the per-request cost into one stat call.
    private sealed class CurrentBuild(string root)
    {
        private readonly object _gate = new();
        private DateTime _stamp;
        private string? _dir;

        public string? Resolve()
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(Path.Combine(root, WebsiteBuilder.CurrentFile)); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            lock (_gate)
            {
                if (stamp != _stamp || (_dir is not null && !Directory.Exists(_dir)))
                {
                    _dir = WebsiteBuilder.ResolveCurrent(root);
                    _stamp = stamp;
                }
                return _dir;
            }
        }
    }
}
