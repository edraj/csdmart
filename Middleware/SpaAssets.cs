using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Dmart.Middleware;

// Static-asset serving shared by the two embedded SPAs (CXB at /cxb, Catalog
// at /cat). Two things the plain UseStaticFiles did not do:
//
//   1. Cache headers. Vite names every file under assets/ by content hash, so
//      a URL never changes meaning: it can be cached for a year and marked
//      immutable, and the browser will not even revalidate it. Without a
//      Cache-Control header browsers fell back to heuristic caching and sent
//      ~40 conditional requests per visit. index.html and config.json are the
//      opposite — the entry points that must always be fresh — so they get
//      no-cache (revalidate every time; the ETag keeps that a 304).
//
//   2. Pre-compressed variants. The build emits `file.js.br` / `file.js.gz`
//      next to each asset (vite-plugin-compression2). When the client accepts
//      that encoding and the variant exists it is sent as-is with
//      Content-Encoding, which skips compressing a 400 kB chunk on every
//      request — the response-compression middleware leaves already-encoded
//      responses alone. When no variant exists nothing changes.
internal static class SpaAssets
{
    internal const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    internal const string NoCacheCacheControl = "no-cache";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    // Preference order: brotli is smaller; gzip is what every client accepts.
    private static readonly (string Encoding, string Suffix)[] Encodings =
    {
        ("br", ".br"),
        ("gzip", ".gz"),
    };

    public static IApplicationBuilder UseSpaStaticFiles(
        this IApplicationBuilder app, IFileProvider fileProvider, string requestPath)
    {
        app.Use(async (ctx, next) =>
        {
            if (!TryGetPrecompressed(ctx, fileProvider, requestPath, out var variant, out var encoding, out var contentType))
            {
                await next();
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength = variant.Length;
            ctx.Response.Headers.ContentEncoding = encoding;
            ctx.Response.Headers.CacheControl = ImmutableCacheControl;
            ctx.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
            if (HttpMethods.IsHead(ctx.Request.Method)) return;

            await using var stream = variant.CreateReadStream();
            await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = requestPath,
            OnPrepareResponse = r =>
            {
                var hashed = r.Context.Request.Path.StartsWithSegments(requestPath, out var rest) && IsHashedAsset(rest);
                r.Context.Response.Headers.CacheControl = hashed ? ImmutableCacheControl : NoCacheCacheControl;
            },
        });

        return app;
    }

    // Entry points are served from memory by the SPA middlewares; this is the
    // header they must carry so a deploy is picked up on the next navigation.
    public static void MarkNoCache(HttpResponse response) =>
        response.Headers.CacheControl = NoCacheCacheControl;

    // Everything Vite writes under assets/ carries a content hash in its name.
    private static bool IsHashedAsset(PathString pathUnderSpa) =>
        pathUnderSpa.StartsWithSegments("/assets");

    private static bool TryGetPrecompressed(
        HttpContext ctx, IFileProvider fileProvider, string requestPath,
        out IFileInfo variant, out string encoding, out string contentType)
    {
        variant = null!;
        encoding = null!;
        contentType = null!;

        var method = ctx.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)) return false;
        if (!ctx.Request.Path.StartsWithSegments(requestPath, out var rest) || !IsHashedAsset(rest)) return false;

        var relative = rest.Value!.TrimStart('/');
        foreach (var (enc, suffix) in Encodings)
        {
            if (!Accepts(ctx.Request, enc)) continue;
            var file = fileProvider.GetFileInfo(relative + suffix);
            if (!file.Exists || file.IsDirectory) continue;

            variant = file;
            encoding = enc;
            contentType = ContentTypes.TryGetContentType(relative, out var ct) ? ct : "application/octet-stream";
            return true;
        }
        return false;
    }

    private static bool Accepts(HttpRequest request, string encoding)
    {
        if (!StringWithQualityHeaderValue.TryParseList(request.Headers.AcceptEncoding, out var values)) return false;
        foreach (var v in values)
        {
            if (!v.Value.Equals(encoding, StringComparison.OrdinalIgnoreCase)) continue;
            return v.Quality is null || v.Quality > 0;
        }
        return false;
    }
}
