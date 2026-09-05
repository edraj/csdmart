using System.Reflection;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Dmart.Middleware;

// Serves the Catalog Svelte SPA — second embedded UI alongside CXB. Mirrors
// CxbMiddleware in structure:
//   1. Embedded resources first (native binary on glibc — ManifestEmbeddedFileProvider).
//   2. Filesystem fallback at {BaseDir}/catalog/ etc. (Docker / musl AOT).
//
// URL prefix is configurable via CAT_URL in config.env (default: /cat).
// The <base href="/cat/"> in catalog/index.html is rewritten at startup to
// whatever CAT_URL resolves to. SPA fallback turns any {catUrl}/* 404 into
// the rewritten index.html. config.json is served dynamically from the same
// source chain as the CXB middleware (DMART_CXB_CONFIG → ./config.json →
// ~/.dmart/config.json) so both SPAs share a single on-disk config.
public static class CatalogMiddleware
{
    public static IApplicationBuilder UseCatalog(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<DmartSettings>>().Value;
        var catUrl = settings.CatUrl?.Trim().TrimEnd('/') ?? "/cat";
        if (!catUrl.StartsWith('/')) catUrl = "/" + catUrl;
        var baseHref = catUrl + "/";  // <base href> needs trailing slash

        IFileProvider? fileProvider = null;

        // Strategy 1: embedded resources, read straight out of the embedded
        // manifest XML. This deliberately does NOT use
        // ManifestEmbeddedFileProvider: that helper fails in the fully static
        // musl build, which then fell through to strategy 2 — and the static
        // tarball ships one file, so there was nothing on disk to find and
        // /cat 404'd on the one artifact that is supposed to need nothing
        // beside it. The bytes were embedded all along; only the reader failed.
        // See ManifestXmlFileProvider for the full account.
        fileProvider = ManifestXmlFileProvider.TryCreate(
            Assembly.GetExecutingAssembly(), "catalog/dist/client");
        if (fileProvider is not null && !fileProvider.GetFileInfo("index.html").Exists)
            fileProvider = null;

        // Strategy 2: Filesystem fallback.
        if (fileProvider is null)
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "catalog"),
                Path.Combine(Directory.GetCurrentDirectory(), "catalog"),
                "/usr/lib/dmart/catalog",
                "/app/catalog",
            };
            foreach (var fsPath in candidates)
            {
                if (File.Exists(Path.Combine(fsPath, "index.html")))
                {
                    fileProvider = new PhysicalFileProvider(fsPath);
                    break;
                }
            }
        }

        // Nothing to serve. A dev build that never ran the UI build script is
        // the ordinary case, so this is not fatal — but it is logged rather
        // than skipped in silence, because silence is exactly how the static
        // binary shipped twice with /cat returning 404.
        if (fileProvider is null)
        {
            app.ApplicationServices.GetService<ILoggerFactory>()
               ?.CreateLogger("Dmart.Startup")
               .LogWarning("Catalog bundle not found (neither embedded nor on disk) — {Url} will 404", catUrl);
            return app;
        }

        // Pre-read index.html and rewrite <base href="/cat/"> to match CAT_URL.
        byte[]? indexHtmlBytes = null;
        var indexFile = fileProvider.GetFileInfo("index.html");
        if (indexFile.Exists)
        {
            using var stream = indexFile.CreateReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var html = reader.ReadToEnd();
            html = html.Replace("<base href=\"/cat/\"", $"<base href=\"{baseHref}\"");
            html = html.Replace("<base href='/cat/'", $"<base href='{baseHref}'");
            indexHtmlBytes = Encoding.UTF8.GetBytes(html);
        }

        // Dynamic config.json — shares the CXB lookup chain (DMART_CXB_CONFIG →
        // ./config.json → ~/.dmart/config.json) so both SPAs see the same config.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments($"{catUrl}/config.json"))
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
                        var rewritten = RewriteConfig(bytes, ctx);
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

        // Intercept direct requests for the catalog index.html to serve the
        // base-href-rewritten version.
        app.Use(async (ctx, next) =>
        {
            if (indexHtmlBytes is not null &&
                (ctx.Request.Path.Equals($"{catUrl}/index.html", StringComparison.OrdinalIgnoreCase) ||
                 ctx.Request.Path.Equals($"{catUrl}/", StringComparison.OrdinalIgnoreCase) ||
                 ctx.Request.Path.Equals(catUrl, StringComparison.OrdinalIgnoreCase)))
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength = indexHtmlBytes.Length;
                await ctx.Response.Body.WriteAsync(indexHtmlBytes);
                return;
            }
            await next();
        });

        // Serve static files at {catUrl} (everything except index.html above).
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = catUrl,
        });

        // SPA fallback — {catUrl}/* without file extension → rewritten index.html.
        app.Use(async (ctx, next) =>
        {
            await next();
            if (ctx.Response.StatusCode == 404
                && !ctx.Response.HasStarted
                && ctx.Request.Path.StartsWithSegments(catUrl)
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

    // Same auto-fill logic as CxbMiddleware.RewriteCxbConfig: insert
    // backend=<request-origin> when the admin hasn't configured one,
    // preserve any non-empty value verbatim, drop the legacy websocket field.
    private static byte[] RewriteConfig(byte[] source, HttpContext ctx)
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
                        if (string.IsNullOrWhiteSpace(configured))
                            writer.WriteString("backend", requestOrigin);
                        else
                            prop.WriteTo(writer);
                    }
                    else if (prop.NameEquals("websocket"))
                    {
                        // Legacy field — dropped (same as CXB).
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
