using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Middleware;

// Port of dmart/backend/main.py::set_middleware_response_headers.
//
// Responsibilities, in order:
//   1. CORS — if the configured AllowedCorsOrigins list is non-empty and the
//      request's Origin header matches one of the entries, reflect the origin
//      and set Access-Control-Allow-Credentials: true. If the list is non-empty
//      but the origin doesn't match, we emit NO CORS headers at all — the
//      browser will block the request. If the list is empty, fall back to
//      "same-host only" using {ListeningHost}:{ListeningPort} so we never open
//      up arbitrary reflection.
//   2. Static Allow-Headers, Allow-Methods, Max-Age, Expose-Headers so every
//      response carries the same CORS contract Python does.
//   3. Security headers (X-Content-Type-Options, X-Frame-Options, Referrer-Policy,
//      Permissions-Policy, Strict-Transport-Security) — added on every response
//      regardless of CORS outcome; plus a Content-Security-Policy on HTML.
//   4. No-cache Cache-Control on API responses + x-server-time timestamp.
//   5. Short-circuit OPTIONS preflight with 204 so the browser can complete the
//      preflight without hitting the route layer (which would 405).
//
// Reading settings via IOptions<DmartSettings> on every request picks up
// IOptionsMonitor-style live reloads if a future provider supports it, and
// keeps the middleware allocation-free otherwise.
public static class ResponseHeadersMiddleware
{
    // Static header values that Python sets verbatim — precomputed so we don't
    // concat strings per request.
    private const string AllowHeaders = "content-type, charset, authorization, accept-language, content-length";
    private const string AllowMethods = "OPTIONS, DELETE, POST, GET, PATCH, PUT";
    private const string MaxAge = "600";
    private const string ExposeHeaders = "x-server-time";
    private const string CacheControlNoCache = "no-cache, no-store, must-revalidate";
    private const string PermissionsPolicy = "geolocation=(), camera=(), microphone=()";
    private const string Hsts = "max-age=31536000; includeSubDomains";

    // The SPA is served same-origin with the API, so 'self' covers every script,
    // style, XHR and WebSocket it needs. Two deliberate relaxations:
    //   * style-src 'unsafe-inline' — the Svelte bundles inject scoped styles at
    //     runtime; without it the SPA renders unstyled.
    //   * img-src https://www.plantuml.com — the schema/diagram views render
    //     PlantUML images straight from that host.
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: https://www.plantuml.com; connect-src 'self'; "
        + "frame-ancestors 'none'; object-src 'none'; base-uri 'self'";

    public static IApplicationBuilder UseDmartResponseHeaders(
        this IApplicationBuilder app, PathString cxbPath, PathString catPath)
    {
        // Resolve once at setup — DmartSettings is singleton-scoped.
        var settings = app.ApplicationServices.GetRequiredService<IOptions<DmartSettings>>().Value;
        var allowlist = settings.ParseAllowedCorsOrigins();

        return app.Use(async (ctx, next) =>
        {
            var origin = ctx.Request.Headers.Origin.ToString();

            // Register an OnStarting callback so the headers land right before
            // the response body is flushed. Writing them here would be lost if
            // a downstream handler cleared the headers dictionary before
            // responding (which Kestrel does for some error paths).
            ctx.Response.OnStarting(() =>
            {
                var headers = ctx.Response.Headers;

                // --- CORS allowlist / fallback ---
                if (allowlist.Length > 0)
                {
                    if (!string.IsNullOrEmpty(origin) && Array.IndexOf(allowlist, origin) >= 0)
                    {
                        headers["Access-Control-Allow-Origin"] = origin;
                        headers["Access-Control-Allow-Credentials"] = "true";
                    }
                    // else: intentionally emit NO CORS headers, matching Python.
                }
                else
                {
                    // Fallback: same-host only. Reflect the origin iff it matches
                    // http://{ListeningHost}:{ListeningPort} — otherwise always
                    // respond with that canonical same-host form so the browser
                    // can see a deterministic value without us reflecting
                    // arbitrary origins.
                    // Use localhost instead of 0.0.0.0 — "0.0.0.0" is not a valid
                    // browser origin and causes CORS violations in the client.
                    var host = settings.ListeningHost == "0.0.0.0" ? "localhost" : settings.ListeningHost;
                    var defaultOrigin = $"http://{host}:{settings.ListeningPort}";
                    headers["Access-Control-Allow-Origin"] =
                        string.Equals(origin, defaultOrigin, StringComparison.Ordinal) ? origin : defaultOrigin;
                    headers["Access-Control-Allow-Credentials"] = "true";
                }

                // Vary: Origin ensures CDNs/proxies don't cache one origin's
                // CORS headers and serve them to a different origin.
                headers.Append("Vary", "Origin");

                // --- Static CORS contract ---
                headers["Access-Control-Allow-Headers"] = AllowHeaders;
                headers["Access-Control-Allow-Methods"] = AllowMethods;
                headers["Access-Control-Max-Age"] = MaxAge;
                headers["Access-Control-Expose-Headers"] = ExposeHeaders;

                // --- Cache-Control + timestamp ---
                // This callback is registered upstream of UseCxb()/UseCatalog(),
                // so whatever it writes here WINS over the static-file
                // middleware's own headers. Forcing no-store on the SPA bundles
                // made the browser re-download ~2.7 MB of content-hashed assets
                // on every single page load. Leave those responses alone so
                // StaticFileMiddleware's ETag/Last-Modified can serve 304s.
                //
                // index.html and config.json are the exceptions: they are the
                // un-hashed entry points that must never be served stale, or the
                // browser keeps booting a build whose hashed assets are gone.
                // Everything else (all API responses) keeps the no-store policy.
                if (!IsCacheableSpaAsset(ctx.Request.Path, cxbPath, catPath))
                {
                    headers["Cache-Control"] = CacheControlNoCache;
                    headers["Pragma"] = "no-cache";
                    headers["Expires"] = "0";
                }
                headers["x-server-time"] = TimeUtils.Now().ToString("o");

                // --- Security headers ---
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
                headers["Permissions-Policy"] = PermissionsPolicy;
                // HSTS must only be sent over HTTPS (RFC 6797).
                if (ctx.Request.IsHttps)
                    headers["Strict-Transport-Security"] = Hsts;

                // CSP only applies to documents, so scope it to HTML responses
                // rather than paying for it on every JSON body.
                //
                // /docs (Swagger UI) and the /oauth consent + error pages ship
                // their own <meta http-equiv> policies. A CSP header and a meta
                // CSP are enforced as an INTERSECTION, so adding ours would
                // silently break Swagger UI — its script/style come from
                // unpkg.com, which our 'self'-only policy forbids. Leave both
                // path trees to the policy they already declare.
                var isHtml = headers.ContentType.ToString()
                    .StartsWith("text/html", StringComparison.OrdinalIgnoreCase);
                if (isHtml
                    && !ctx.Request.Path.StartsWithSegments("/docs")
                    && !ctx.Request.Path.StartsWithSegments("/oauth"))
                {
                    headers["Content-Security-Policy"] = ContentSecurityPolicy;
                }

                return Task.CompletedTask;
            });

            // Preflight short-circuit. ASP.NET minimal APIs respond 405 to
            // OPTIONS by default since we don't register OPTIONS routes — so
            // we intercept here and return 204 with the CORS headers set via
            // the OnStarting callback above. Matches what FastAPI + CORSMiddleware
            // does in Python.
            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await next();
        });
    }

    // True for the content-hashed bundle files under {CXB_URL}/ and {CAT_URL}/
    // that are safe to revalidate instead of re-fetching.
    //
    // Excluded, so they keep no-store:
    //   * index.html / config.json — un-hashed entry points, must stay fresh.
    //   * extensionless paths — the SPA fallback in CxbMiddleware/CatalogMiddleware
    //     answers those with index.html, so they are the entry point too.
    private static bool IsCacheableSpaAsset(PathString path, PathString cxbPath, PathString catPath)
    {
        if (!path.StartsWithSegments(cxbPath, out var rest)
            && !path.StartsWithSegments(catPath, out rest))
            return false;

        var value = rest.Value;
        if (string.IsNullOrEmpty(value)) return false;
        if (!Path.HasExtension(value)) return false;

        return !value.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)
            && !value.EndsWith("/config.json", StringComparison.OrdinalIgnoreCase);
    }
}
