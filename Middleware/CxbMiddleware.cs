using System.Reflection;
using Dmart.Config;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Dmart.Middleware;

// Serves the CXB Svelte SPA from embedded resources at /cxb.
//
// Mirrors Python's dmart/main.py SPAStaticFiles mount:
//   1. Embedded files are served at /cxb/* with correct MIME types
//   2. Paths without a file extension that return 404 fall back to
//      index.html (SPA client-side routing)
//   3. /cxb/config.json is served dynamically from the settings
//      fallback chain: $DMART_CXB_CONFIG → ./config.json →
//      {spaces_folder}/config.json → ~/.dmart/config.json → embedded
//   4. Static assets get Cache-Control: public, max-age=86400
//
// The ManifestEmbeddedFileProvider reads the embedded resource manifest
// that GenerateEmbeddedFilesManifest=true produces at build time.
// Each file in cxb-dist/ is linked under "cxb/" via LinkBase="cxb"
// in the .csproj, so the provider sees them at paths like "cxb/index.html".
public static class CxbMiddleware
{
    public static IApplicationBuilder UseCxb(this IApplicationBuilder app)
    {
        var assembly = Assembly.GetExecutingAssembly();
        ManifestEmbeddedFileProvider? subProvider;
        try
        {
            // Try the sub-provider directly first (more resilient to AOT quirks).
            subProvider = new ManifestEmbeddedFileProvider(assembly, "cxb/dist/client");
        }
        catch
        {
            try
            {
                // Fallback: root provider — some AOT runtimes need this path.
                var rootProvider = new ManifestEmbeddedFileProvider(assembly);
                var check = rootProvider.GetFileInfo("cxb/dist/client/index.html");
                if (!check.Exists) return app;
                subProvider = new ManifestEmbeddedFileProvider(assembly, "cxb/dist/client");
            }
            catch
            {
                // No embedded manifest at all — CXB not built/embedded.
                return app;
            }
        }

        // Verify CXB files exist in the provider.
        var indexFile = subProvider.GetFileInfo("index.html");
        if (!indexFile.Exists) return app;
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = subProvider,
            RequestPath = "/cxb",
        });

        // Dynamic config.json endpoint — mirrors Python's fallback chain.
        app.Use(async (ctx, next) =>
        {
            var settings = ctx.RequestServices.GetRequiredService<IOptions<DmartSettings>>().Value;
            if (ctx.Request.Path.StartsWithSegments("/cxb/config.json"))
            {
                // Fallback chain: env path → ./config.json → spaces/config.json → ~/.dmart/config.json → embedded
                var paths = new[]
                {
                    Environment.GetEnvironmentVariable("DMART_CXB_CONFIG"),
                    "config.json",
                    Path.Combine(settings.SpacesRoot, "config.json"),
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".dmart", "config.json"),
                };
                foreach (var p in paths)
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.Headers["Cache-Control"] = "no-cache";
                        await ctx.Response.SendFileAsync(p);
                        return;
                    }
                }
                // Fall through to the embedded config.json (served by StaticFiles above)
            }
            await next();
        });

        // SPA fallback — any /cxb/* path that didn't match a static file
        // and doesn't have a file extension gets index.html. This enables
        // client-side routing (Routify/SvelteKit).
        app.Use(async (ctx, next) =>
        {
            await next();
            if (ctx.Response.StatusCode == 404
                && !ctx.Response.HasStarted
                && ctx.Request.Path.StartsWithSegments("/cxb")
                && !Path.HasExtension(ctx.Request.Path.Value))
            {
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/html";
                var file = subProvider.GetFileInfo("index.html");
                if (file.Exists)
                {
                    await using var stream = file.CreateReadStream();
                    await stream.CopyToAsync(ctx.Response.Body);
                }
            }
        });

        return app;
    }
}
