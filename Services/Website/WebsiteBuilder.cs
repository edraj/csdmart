using System.Globalization;
using System.Text;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.Services.Website;

public sealed record WebsiteBuildOptions(
    string Space,
    string OutputRoot,
    string Mount,
    string? BaseUrl,
    WebsiteTemplate Template);

public sealed record WebsiteBuildResult(
    string BuildDir,
    IReadOnlyList<string> Files,
    int Pages,
    bool Home,
    IReadOnlyList<string> Warnings);

// Thrown for a build that must not replace the live site: the query failed,
// or it returned nothing to publish.
public sealed class WebsiteBuildException : Exception
{
    public WebsiteBuildException(string message) : base(message) { }

    // The other standard constructors (CA1032).
    public WebsiteBuildException() : base("website build failed") { }
    public WebsiteBuildException(string message, Exception inner) : base(message, inner) { }
}

// `dmart website build`: reads the website space AS AN ANONYMOUS VISITOR,
// renders it, and swaps the result in atomically.
//
// Reading as anonymous is the point, not a convenience. The space is public
// only when an operator has widened the `world` permission to it (seeding
// grants nothing — docs/website.md), and the build publishes exactly what that
// permission lets an anonymous reader see. One switch decides both "can the
// API serve this to strangers" and "does the site contain it"; an inactive
// entry is a draft and stays out of both.
//
// Layout under OutputRoot:
//   builds/<utc-stamp>/   one complete site per build
//   current               a one-line file naming the live build
// `current` is replaced with a rename, so the server (WebsiteMiddleware) sees
// either the old site or the new one and never a half-written mix. The two
// newest builds are kept so the previous one can be restored by hand.
public sealed class WebsiteBuilder(QueryService queries)
{
    public const string CurrentFile = "current";
    public const string BuildsDir = "builds";
    public const string AssetsDir = "_site";

    public async Task<WebsiteBuildResult> BuildAsync(WebsiteBuildOptions o, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        var pages = new List<WebsitePage>();
        foreach (var r in await FetchAsync(o.Space, "/pages", ct))
        {
            if (WebsitePage.FromRecord(r) is not { } page) continue;
            if (page.ContentType is not ("markdown" or "html" or "text"))
            {
                warnings.Add($"pages/{page.Shortname}: content_type '{page.ContentType}' has no page form — skipped");
                continue;
            }
            if (pages.Any(p => p.Path == page.Path))
            {
                warnings.Add($"pages/{page.Shortname}: URL /{page.Path} is already taken — skipped");
                continue;
            }
            pages.Add(page);
        }

        var site = SiteConfig.Default;
        LandingPage? home = null;
        foreach (var r in await FetchAsync(o.Space, "/site", ct))
        {
            var body = r.Obj("attributes")?.Obj("payload")?.Obj("body");
            switch (r.Str("shortname"))
            {
                case "config" when body is { } b:
                    site = SiteConfig.FromBody(b);
                    break;
                case "home" when body is not null:
                    home = LandingPage.FromRecord(r);
                    break;
            }
        }

        // Writing an empty site over a good one is the worst outcome available
        // here, so refuse rather than "succeed" with nothing. The usual cause
        // is a space nobody has made public yet.
        if (pages.Count == 0 && home is null)
            throw new WebsiteBuildException(
                $"space '{o.Space}' has nothing an anonymous visitor can read under /pages or /site — "
                + "refusing to publish an empty site. The space is private until the world permission "
                + "grants it (see docs/website.md).");

        foreach (var group in site.Nav)
            foreach (var item in group.Items)
                if (!pages.Any(p => p.Path == item.Page))
                    warnings.Add($"site/config: nav item '{item.Page}' matches no published page — left out");

        var baseUrl = o.BaseUrl ?? site.BaseUrl;
        var renderer = new WebsiteRenderer(o.Template, site, pages, o.Mount, baseUrl);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var page in pages)
            files[$"{page.Path}/index.html"] = renderer.RenderPage(page);
        if (home is not null) files["index.html"] = renderer.RenderHome(home);
        else warnings.Add("site/home: no landing page — the site root will 404");
        files["404.html"] = renderer.RenderNotFound();
        files["robots.txt"] = renderer.RenderRobots();
        if (baseUrl is not null) files["sitemap.xml"] = renderer.RenderSitemap(pages, home);
        else warnings.Add("no base URL (--base or site/config base_url) — no sitemap.xml or canonical links");

        var root = Path.GetFullPath(o.OutputRoot);
        var buildDir = Path.Combine(root, BuildsDir,
            DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(buildDir);
        foreach (var (rel, html) in files)
        {
            var full = Path.Combine(buildDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, html, Encoding.UTF8, ct);
        }
        var assetsDir = Path.Combine(buildDir, AssetsDir);
        Directory.CreateDirectory(assetsDir);
        foreach (var (name, bytes) in o.Template.Assets)
        {
            await File.WriteAllBytesAsync(Path.Combine(assetsDir, name), bytes, ct);
            files[$"{AssetsDir}/{name}"] = "";
        }

        await PublishAsync(root, Path.GetFileName(buildDir), ct);
        Prune(root, keep: 2);

        return new WebsiteBuildResult(
            buildDir, [.. files.Keys.Order(StringComparer.Ordinal)], pages.Count, home is not null, warnings);
    }

    // WEBSITE_DIR, or ~/.dmart/website — next to the default spaces folder.
    public static string ResolveRoot(Dmart.Config.DmartSettings s) => Path.GetFullPath(
        !string.IsNullOrWhiteSpace(s.WebsiteDir)
            ? s.WebsiteDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dmart", "website"));

    // The build this root currently serves, or null when there is none.
    // Shared with WebsiteMiddleware so the two can never disagree on layout.
    public static string? ResolveCurrent(string root)
    {
        try
        {
            var pointer = Path.Combine(root, CurrentFile);
            if (!File.Exists(pointer)) return null;
            var name = File.ReadAllText(pointer).Trim();
            // A single path segment, nothing that can climb out of builds/.
            if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.StartsWith('.'))
                return null;
            var dir = Path.Combine(root, BuildsDir, name);
            return Directory.Exists(dir) ? dir : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static async Task PublishAsync(string root, string buildName, CancellationToken ct)
    {
        var tmp = Path.Combine(root, $".{CurrentFile}.{Environment.ProcessId}.tmp");
        await File.WriteAllTextAsync(tmp, buildName + "\n", ct);
        File.Move(tmp, Path.Combine(root, CurrentFile), overwrite: true);
    }

    private static void Prune(string root, int keep)
    {
        var current = ResolveCurrent(root);
        var builds = new DirectoryInfo(Path.Combine(root, BuildsDir))
            .GetDirectories()
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .ToList();
        foreach (var old in builds.Skip(keep))
        {
            if (current is not null && Path.GetFullPath(old.FullName) == Path.GetFullPath(current)) continue;
            try { old.Delete(recursive: true); }
            catch (IOException) { /* in use or already gone; the next build retries */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Records on the wire shape — the exact JSON /public/query returns — so
    // the renderer reads what an anonymous API client would.
    private async Task<List<JsonElement>> FetchAsync(string space, string subpath, CancellationToken ct)
    {
        var q = new Query
        {
            Type = QueryType.Search,
            SpaceName = space,
            Subpath = subpath,
            ExactSubpath = true,
            FilterTypes = [ResourceType.Content],
            RetrieveJsonPayload = true,
            RetrieveTotal = false,
            SortBy = "shortname",
            Limit = 1000,
        };
        var resp = await queries.ExecuteAsync(q, actor: "anonymous", ct);
        if (resp.Status != Status.Success)
            throw new WebsiteBuildException(
                $"query {space}{subpath} failed: {resp.Error?.Message ?? "unknown error"}");

        var bytes = JsonSerializer.SerializeToUtf8Bytes(resp, DmartJsonContext.Default.Response);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.TryGetProperty("records", out var records)
               && records.ValueKind == JsonValueKind.Array
            ? [.. records.EnumerateArray().Select(r => r.Clone())]
            : [];
    }
}
