using System.Reflection;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Dmart.Middleware;

// Serves the CXB Svelte SPA from either:
//   1. Embedded resources (native binary on host — ManifestEmbeddedFileProvider)
//   2. Filesystem at {BaseDir}/cxb/ (Docker — native AOT on musl doesn't
//      support ManifestEmbeddedFileProvider reliably)
//
// The URL prefix is configurable via CXB_URL in config.env (default: /cxb).
// The <base href> in index.html is rewritten at startup to match CXB_URL.
// SPA fallback: {cxbUrl}/* paths without file extensions → index.html.
// Dynamic config.json with Python-parity fallback chain.
public static class CxbMiddleware
{
    public static IApplicationBuilder UseCxb(this IApplicationBuilder app)
    {
        // Read CXB_URL from settings — normalize to start with / and end with /
        var settings = app.ApplicationServices.GetRequiredService<IOptions<DmartSettings>>().Value;
        var cxbUrl = settings.CxbUrl?.Trim().TrimEnd('/') ?? "/cxb";
        if (!cxbUrl.StartsWith('/')) cxbUrl = "/" + cxbUrl;
        var baseHref = cxbUrl + "/";  // <base href> needs trailing slash

        // Concrete type, not IFileProvider: there is exactly one source now
        // (CA1859 flags the interface as a needless indirection).
        ManifestXmlFileProvider? fileProvider = null;

        // The SPA is served from the binary's own embedded resources, and only
        // from there. There used to be a filesystem fallback at
        // {BaseDir}/cxb, /usr/lib/dmart/cxb and /app/cxb, which existed
        // solely because ManifestEmbeddedFileProvider does not survive
        // NativeAOT on musl — the Alpine package shipped a second copy of the
        // assets to that path to compensate. ManifestXmlFileProvider reads the
        // embedded manifest XML directly and works on glibc and musl alike, so
        // the duplicate is gone and dmart serves its UIs on its own.
        //
        // Consequence worth knowing: embedded is now the ONLY path, on every
        // artifact. If it regresses, every UI 404s everywhere rather than only
        // on musl. That is why the release asserts /cxb/ and /cat/ actually
        // serve — on all four tarballs and on the container image — before
        // anything is published.
        fileProvider = ManifestXmlFileProvider.TryCreate(
            Assembly.GetExecutingAssembly(), "cxb/dist/client");
        if (fileProvider is not null && !fileProvider.GetFileInfo("index.html").Exists)
            fileProvider = null;

        // Nothing to serve. A dev build that never ran the UI build script is
        // the ordinary case, so this is not fatal — but it is logged rather
        // than skipped in silence, because silence is exactly how the static
        // binary shipped twice with /cxb returning 404.
        if (fileProvider is null)
        {
            app.ApplicationServices.GetService<ILoggerFactory>()
               ?.CreateLogger("Dmart.Startup")
               .LogWarning("CXB bundle not found (neither embedded nor on disk) — {Url} will 404", cxbUrl);
            return app;
        }

        // Pre-read index.html and rewrite <base href="/cxb/"> to match CXB_URL.
        // Done once at startup so there's no per-request cost.
        byte[]? indexHtmlBytes = null;
        var indexFile = fileProvider.GetFileInfo("index.html");
        if (indexFile.Exists)
        {
            using var stream = indexFile.CreateReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var html = reader.ReadToEnd();
            html = html.Replace("<base href=\"/cxb/\"", $"<base href=\"{baseHref}\"");
            html = html.Replace("<base href='/cxb/'", $"<base href='{baseHref}'");
            indexHtmlBytes = Encoding.UTF8.GetBytes(html);
        }

        // Dynamic config.json — MUST be before UseStaticFiles so it intercepts
        // {cxbUrl}/config.json before the embedded/filesystem static file is served.
        // Rewrites `backend` and `websocket` fields based on the incoming
        // request's scheme+host so the SPA always targets whatever URL the
        // browser used to reach dmart (even through reverse proxies).
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments($"{cxbUrl}/config.json"))
            {
                var paths = new[]
                {
                    Environment.GetEnvironmentVariable("DMART_CXB_CONFIG"),
                    "config.json",
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".dmart", "config.json"),
                };
                foreach (var p in paths)
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        var bytes = await File.ReadAllBytesAsync(p);
                        var rewritten = RewriteCxbConfig(bytes, ctx);
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.Headers["Cache-Control"] = "no-cache";
                        ctx.Response.ContentLength = rewritten.Length;
                        await ctx.Response.Body.WriteAsync(rewritten);
                        return;
                    }
                }
            }
            await next();
        });

        // Browser auto-requests /favicon.ico at the root — redirect to CXB's.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Redirect($"{cxbUrl}/favicon.ico");
                return;
            }
            await next();
        });

        // Intercept direct requests for index.html to serve the rewritten version.
        app.Use(async (ctx, next) =>
        {
            if (indexHtmlBytes is not null &&
                (ctx.Request.Path.Equals($"{cxbUrl}/index.html", StringComparison.OrdinalIgnoreCase) ||
                 ctx.Request.Path.Equals($"{cxbUrl}/", StringComparison.OrdinalIgnoreCase) ||
                 ctx.Request.Path.Equals(cxbUrl, StringComparison.OrdinalIgnoreCase)))
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength = indexHtmlBytes.Length;
                await ctx.Response.Body.WriteAsync(indexHtmlBytes);
                return;
            }
            await next();
        });

        // Serve static files at {cxbUrl} (everything except index.html which is handled above).
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = cxbUrl,
        });

        // SPA fallback — {cxbUrl}/* without file extension → rewritten index.html.
        app.Use(async (ctx, next) =>
        {
            await next();
            if (ctx.Response.StatusCode == 404
                && !ctx.Response.HasStarted
                && ctx.Request.Path.StartsWithSegments(cxbUrl)
                && !Path.HasExtension(ctx.Request.Path.Value)
                && indexHtmlBytes is not null)
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.Body.WriteAsync(indexHtmlBytes);
            }
        });

        return app;
    }

    // Parse config.json and only FILL IN `backend` when it's missing or
    // blank — an explicit value set by the admin (e.g. a reverse-proxy
    // public URL that differs from the request host) is respected verbatim.
    // The SPA derives its WebSocket URL (ws(s)://{host}/ws) from `backend` at
    // the call site, so config carries only that single source of truth.
    private static byte[] RewriteCxbConfig(byte[] source, HttpContext ctx)
    {
        var requestOrigin = $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}";

        try
        {
            using var doc = JsonDocument.Parse(source);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return source;

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                var sawBackend = false;
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.NameEquals("backend"))
                    {
                        sawBackend = true;
                        var configured = prop.Value.ValueKind == JsonValueKind.String
                            ? prop.Value.GetString()
                            : null;
                        // Only auto-fill when the admin hasn't set a value.
                        // Preserve anything non-empty exactly as written.
                        if (string.IsNullOrWhiteSpace(configured))
                            writer.WriteString("backend", requestOrigin);
                        else
                            prop.WriteTo(writer);
                    }
                    else if (prop.NameEquals("websocket"))
                    {
                        // Legacy field — dropped so stale config.json files on
                        // disk don't leak obsolete URLs into the SPA.
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                if (!sawBackend) writer.WriteString("backend", requestOrigin);
                writer.WriteEndObject();
            }
            return ms.ToArray();
        }
        catch (JsonException)
        {
            return source;
        }
    }
}
