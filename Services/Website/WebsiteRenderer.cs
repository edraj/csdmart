using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Dmart.Services.Website;

// Turns website-space content into finished HTML documents. Pure: no I/O, no
// database — WebsiteBuilder fetches and writes, this only renders, which is
// what lets the tests pin every output shape without a server.
//
// TRUST. Markdown is rendered with raw HTML disabled, and link targets are
// limited to http(s), mailto, tel and relative URLs, so a markdown author
// cannot put script on the page. A page whose content_type is html is taken
// as written: its author is an editor with write access to the website
// space, and re-sanitizing would strip markup they deliberately wrote. That
// is a trust boundary worth stating — the served pages carry a CSP without
// 'unsafe-inline' for scripts (ResponseHeadersMiddleware), which is what
// keeps inline script and on* handlers in such a page from running.
public sealed partial class WebsiteRenderer
{
    // Diagrams are drawn in the browser. Rendering them at build time would
    // need a headless browser, and the engine is 5.6 MB — too much to embed in
    // a binary that targets a 416 MB board. Pinned and integrity-checked, and
    // only pages that contain a diagram load it.
    public const string MermaidScript =
        "<script defer src=\"https://cdn.jsdelivr.net/npm/mermaid@12.0.0/dist/mermaid.min.js\" "
        + "integrity=\"sha384-xzghz1GQ5u9HCpVskeDPqMsdogD1yvuMQbEK53+wi+G70+6J1AG0L2cfi9PHjDWI\" "
        + "crossorigin=\"anonymous\"></script>";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .Build();
    // Deliberately NOT UseAdvancedExtensions(): it includes GenericAttributes,
    // whose `{onclick=…}` syntax would hand markdown authors arbitrary
    // attributes and undo DisableHtml().

    private readonly WebsiteTemplate _template;
    private readonly SiteConfig _site;
    private readonly string _mount;
    private readonly string? _base;
    private readonly HashSet<string> _pagePaths;
    private readonly Dictionary<string, WebsitePage> _byPath;
    private readonly HashSet<string> _docPaths;

    // mount: the URL path the site is served under ("/website", or "" for a
    // site at the root of its host). base: the public origin for canonical
    // URLs and the sitemap, or null when unknown.
    public WebsiteRenderer(
        WebsiteTemplate template, SiteConfig site, IReadOnlyList<WebsitePage> pages,
        string mount, string? baseUrl)
    {
        _template = template;
        _site = site;
        _mount = NormalizeMount(mount);
        _base = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim().TrimEnd('/');
        _byPath = new Dictionary<string, WebsitePage>(StringComparer.Ordinal);
        foreach (var p in pages) _byPath.TryAdd(p.Path, p);
        _pagePaths = [.. _byPath.Keys];
        _docPaths = [.. site.Nav.SelectMany(g => g.Items).Select(i => i.Page).Where(_pagePaths.Contains)];
    }

    public static string NormalizeMount(string? mount)
    {
        var m = (mount ?? "").Trim().Trim('/');
        return m.Length == 0 ? "" : "/" + m;
    }

    public string PageUrl(string path) => $"{_mount}/{path}";
    public string HomeUrl => _mount + "/";
    public string AssetsUrl => _mount + "/_site";

    // --------------------------------------------------------------- pages

    public string RenderPage(WebsitePage page)
    {
        var isDoc = _docPaths.Contains(page.Path);
        var content = page.ContentType switch
        {
            "markdown" => Markdown(page.Body),
            "html" => page.Body,
            "text" => $"<pre>{Esc(page.Body)}</pre>",
            _ => "",
        };
        var heading = HasLeadingHeading(page.Body, page.ContentType) ? "" : $"<h1>{Esc(page.Title)}</h1>\n";
        return Document(
            bodyClass: isDoc ? "page-doc" : "page-standalone",
            title: page.Title + _site.TitleSuffix,
            description: page.Description,
            canonicalPath: page.Path,
            ogType: "article",
            currentUrl: PageUrl(page.Path),
            isDoc: isDoc,
            main: $"<article class=\"prose\">\n{heading}{content}\n</article>");
    }

    public string RenderNotFound() => Document(
        bodyClass: "page-standalone",
        title: "Page not found" + _site.TitleSuffix,
        description: "",
        canonicalPath: null,
        ogType: "website",
        currentUrl: null,
        isDoc: false,
        main: "<article class=\"prose not-found\">\n<h1>Page not found</h1>\n"
            + $"<p>There is no page at this address. <a href=\"{Esc(HomeUrl)}\">Go to the home page</a>.</p>\n</article>");

    public string RenderHome(LandingPage home)
    {
        var sb = new StringBuilder();

        sb.Append("<section class=\"hero\">\n<div class=\"hero-inner\">\n");
        if (!string.IsNullOrEmpty(home.Badge))
            sb.Append("<p class=\"hero-badge\">").Append(Esc(home.Badge)).Append("</p>\n");
        var lines = home.TitleLines.Count > 0 ? home.TitleLines : [home.Title];
        sb.Append("<h1 class=\"hero-title\">");
        for (var i = 0; i < lines.Count; i++)
        {
            // The last line carries the accent, and only when there is more
            // than one — a single line is the whole title, not its tail.
            var accent = lines.Count > 1 && i == lines.Count - 1 ? " accent" : "";
            sb.Append("<span class=\"hero-title-line").Append(accent).Append("\">")
              .Append(Esc(lines[i])).Append("</span>");
        }
        sb.Append("</h1>\n");
        if (!string.IsNullOrEmpty(home.Subtitle))
            sb.Append("<p class=\"hero-subtitle\">").Append(Inline(home.Subtitle)).Append("</p>\n");
        AppendActions(sb, home.Actions);
        sb.Append("</div>\n");
        if (_template.Figure(home.Figure) is { } figure)
            sb.Append("<div class=\"hero-figure\">").Append(figure).Append("</div>\n");
        sb.Append("</section>\n");

        if (home.Stats.Count > 0)
        {
            sb.Append("<section class=\"stats\" aria-label=\"Key figures\"><ul class=\"stats-inner\">");
            foreach (var s in home.Stats)
                sb.Append("<li class=\"stat\"><span class=\"stat-value\">").Append(Esc(s.Value))
                  .Append("</span><span class=\"stat-label\">").Append(Esc(s.Label)).Append("</span></li>");
            sb.Append("</ul></section>\n");
        }

        foreach (var section in home.Sections) AppendSection(sb, section);

        return Document(
            bodyClass: "page-home",
            // The home page's displayname is its whole title; no suffix.
            title: home.Title,
            description: home.Description,
            canonicalPath: "",
            ogType: "website",
            currentUrl: HomeUrl,
            isDoc: false,
            main: sb.ToString());
    }

    private void AppendSection(StringBuilder sb, LandingSection s)
    {
        var kind = s.Kind is "cards" or "steps" or "figures" or "explainer" or "prose" or "banner" ? s.Kind : "prose";
        sb.Append("<section class=\"band band-").Append(kind).Append('"');
        if (!string.IsNullOrEmpty(s.Id) && SectionIdRegex().IsMatch(s.Id))
            sb.Append(" id=\"").Append(s.Id).Append('"');
        sb.Append(">\n<h2 class=\"section-title\">").Append(Esc(s.Title));
        if (kind == "banner" && !string.IsNullOrEmpty(s.Accent))
            sb.Append("<br><span class=\"accent\">").Append(Esc(s.Accent)).Append("</span>");
        sb.Append("</h2>\n");

        switch (kind)
        {
            case "cards":
                AppendBody(sb, s.Body);
                sb.Append("<div class=\"cards\">\n");
                foreach (var item in s.Items)
                {
                    var tag = string.IsNullOrEmpty(item.Href) ? "div" : "a";
                    sb.Append('<').Append(tag).Append(" class=\"card\"");
                    if (tag == "a") AppendHref(sb, item.Href!);
                    sb.Append('>');
                    if (_template.Icon(item.Icon) is { } icon)
                        sb.Append("<span class=\"card-icon\">").Append(icon).Append("</span>");
                    sb.Append("<h3 class=\"card-title\">").Append(Esc(item.Title)).Append("</h3>");
                    if (!string.IsNullOrEmpty(item.Body))
                        sb.Append("<div class=\"card-body\">").Append(Markdown(item.Body)).Append("</div>");
                    if (tag == "a") sb.Append("<span class=\"card-arrow\" aria-hidden=\"true\">→</span>");
                    sb.Append("</").Append(tag).Append(">\n");
                }
                sb.Append("</div>\n");
                break;

            case "steps":
                AppendBody(sb, s.Body);
                sb.Append("<ol class=\"steps\">\n");
                for (var i = 0; i < s.Items.Count; i++)
                {
                    var item = s.Items[i];
                    sb.Append("<li class=\"step\"><span class=\"step-num\">")
                      .Append((i + 1).ToString("00", CultureInfo.InvariantCulture))
                      .Append("</span><div class=\"step-content\"><h3 class=\"step-title\">")
                      .Append(Esc(item.Title)).Append("</h3>");
                    if (!string.IsNullOrEmpty(item.Body))
                        sb.Append("<div class=\"step-body\">").Append(Markdown(item.Body)).Append("</div>");
                    sb.Append("</div></li>\n");
                }
                sb.Append("</ol>\n");
                break;

            case "figures":
                sb.Append("<div class=\"figures-grid\"><div class=\"figures-main\">")
                  .Append(Markdown(s.Body)).Append("</div>");
                if (s.Items.Count > 0)
                {
                    sb.Append("<dl class=\"figures\">");
                    foreach (var item in s.Items)
                        sb.Append("<div class=\"figure\"><dt>").Append(Esc(item.Title))
                          .Append("</dt><dd>").Append(Inline(item.Body)).Append("</dd></div>");
                    sb.Append("</dl>");
                }
                sb.Append("</div>\n");
                break;

            // An animated figure from the template (one <g class="sc"> per
            // scene) captioned by the items, in order. site.js plays it and
            // enables the scene buttons; without script the figure shows its
            // last scene and every caption reads as a plain list.
            case "explainer":
                AppendBody(sb, s.Body);
                sb.Append("<div class=\"explainer\">\n<div class=\"explainer-stage\">");
                if (_template.Figure(s.Figure) is { } art) sb.Append(art);
                sb.Append("<button type=\"button\" class=\"explainer-toggle\" aria-pressed=\"false\" hidden>Pause</button>");
                sb.Append("</div>\n<ol class=\"explainer-scenes\">\n");
                for (var i = 0; i < s.Items.Count; i++)
                {
                    var item = s.Items[i];
                    sb.Append("<li class=\"scene\"><h3 class=\"scene-title\"><button type=\"button\" class=\"scene-jump\" disabled>")
                      .Append("<span class=\"scene-num\">").Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append("</span>")
                      .Append(Esc(item.Title)).Append("</button></h3>");
                    sb.Append("<span class=\"scene-bar\" aria-hidden=\"true\"></span>");
                    if (!string.IsNullOrEmpty(item.Body))
                        sb.Append("<div class=\"scene-body\">").Append(Markdown(item.Body)).Append("</div>");
                    sb.Append("</li>\n");
                }
                sb.Append("</ol>\n</div>\n");
                break;

            default:
                AppendBody(sb, s.Body);
                break;
        }

        AppendActions(sb, s.Actions);
        sb.Append("</section>\n");
    }

    private void AppendBody(StringBuilder sb, string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
            sb.Append("<div class=\"section-body\">").Append(Markdown(body)).Append("</div>\n");
    }

    private void AppendActions(StringBuilder sb, IReadOnlyList<LandingAction> actions)
    {
        if (actions.Count == 0) return;
        sb.Append("<div class=\"actions\">");
        foreach (var a in actions)
        {
            var style = a.Style is "primary" or "secondary" or "link" ? a.Style : "primary";
            sb.Append("<a class=\"btn btn-").Append(style).Append('"');
            AppendHref(sb, a.Href);
            sb.Append('>').Append(Esc(a.Label)).Append("</a>");
        }
        sb.Append("</div>\n");
    }

    private void AppendHref(StringBuilder sb, string href)
    {
        var url = Href(href);
        sb.Append(" href=\"").Append(Esc(url)).Append('"');
        if (IsExternal(url)) sb.Append(" rel=\"noopener\"");
    }

    // ------------------------------------------------------------ document

    private string Document(
        string bodyClass, string title, string description, string? canonicalPath,
        string ogType, string? currentUrl, bool isDoc, string main)
    {
        var canonical = _base is not null && canonicalPath is not null
            ? $"{_base}{_mount}/{canonicalPath}"
            : null;
        var ogImage = _base is not null && _template.Assets.ContainsKey("og-card.png")
            ? $"{_base}{AssetsUrl}/og-card.png"
            : null;

        var head = new List<string>
        {
            "<meta charset=\"utf-8\">",
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
            $"<title>{Esc(title)}</title>",
        };
        if (description.Length > 0) head.Add($"<meta name=\"description\" content=\"{Esc(description)}\">");
        if (canonical is not null) head.Add($"<link rel=\"canonical\" href=\"{Esc(canonical)}\">");
        head.Add($"<meta property=\"og:type\" content=\"{ogType}\">");
        head.Add($"<meta property=\"og:site_name\" content=\"{Esc(_site.Brand)}\">");
        head.Add($"<meta property=\"og:title\" content=\"{Esc(title)}\">");
        if (description.Length > 0) head.Add($"<meta property=\"og:description\" content=\"{Esc(description)}\">");
        if (canonical is not null) head.Add($"<meta property=\"og:url\" content=\"{Esc(canonical)}\">");
        if (ogImage is not null) head.Add($"<meta property=\"og:image\" content=\"{Esc(ogImage)}\">");
        head.Add($"<meta name=\"twitter:card\" content=\"{(ogImage is null ? "summary" : "summary_large_image")}\">");

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lang"] = "en",
            ["head"] = string.Join("\n  ", head),
            ["assets"] = Esc(AssetsUrl),
            ["version"] = _template.Version,
            ["scripts"] = main.Contains("<pre class=\"mermaid\">", StringComparison.Ordinal) ? MermaidScript : "",
            ["body_class"] = bodyClass,
            ["home"] = Esc(HomeUrl),
            ["brand"] = Esc(_site.Brand),
            ["header_links"] = HeaderLinks(currentUrl, isDoc),
            ["sidebar"] = isDoc ? Sidebar(currentUrl) : "",
            ["main"] = main,
            ["footer"] = Markdown(_site.Footer),
        };
        // One pass, and substituted values are never re-scanned: content that
        // happens to contain "{{main}}" stays literal text.
        return PlaceholderRegex().Replace(_template.Layout,
            m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private string HeaderLinks(string? currentUrl, bool isDoc)
    {
        var sb = new StringBuilder();
        foreach (var link in _site.HeaderLinks)
        {
            var url = Href(link.Href);
            var current = link.Docs ? isDoc : currentUrl is not null && url == currentUrl;
            sb.Append("<a class=\"header-link\" href=\"").Append(Esc(url)).Append('"');
            if (current) sb.Append(" aria-current=\"page\"");
            if (IsExternal(url)) sb.Append(" rel=\"noopener\"");
            sb.Append('>').Append(Esc(link.Label)).Append("</a>");
        }
        return sb.ToString();
    }

    private string Sidebar(string? currentUrl)
    {
        var sb = new StringBuilder("<aside class=\"sidebar\" id=\"sidebar\"><nav class=\"sidebar-nav\" aria-label=\"Documentation\">");
        foreach (var group in _site.Nav)
        {
            var items = group.Items.Where(i => _byPath.ContainsKey(i.Page)).ToList();
            if (items.Count == 0) continue;
            sb.Append("<div class=\"nav-group\">");
            if (group.Title.Length > 0)
                sb.Append("<p class=\"nav-group-title\">").Append(Esc(group.Title)).Append("</p>");
            sb.Append("<ul class=\"nav-links\">");
            foreach (var item in items)
            {
                var url = PageUrl(item.Page);
                sb.Append("<li><a href=\"").Append(Esc(url)).Append('"');
                if (url == currentUrl) sb.Append(" aria-current=\"page\"");
                sb.Append('>').Append(Esc(item.Label ?? _byPath[item.Page].Title)).Append("</a></li>");
            }
            sb.Append("</ul></div>");
        }
        sb.Append("</nav></aside>");
        return sb.ToString();
    }

    // ------------------------------------------------------- sitemap/robots

    public string RenderSitemap(IEnumerable<WebsitePage> pages, LandingPage? home)
    {
        if (_base is null) throw new InvalidOperationException("a sitemap needs absolute URLs — no base URL");
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");
        void Url(string path, string? updatedAt)
        {
            sb.Append("  <url>\n    <loc>").Append(Esc($"{_base}{_mount}/{path}")).Append("</loc>");
            if (updatedAt is { Length: >= 10 })
                sb.Append("\n    <lastmod>").Append(Esc(updatedAt[..10])).Append("</lastmod>");
            sb.Append("\n  </url>\n");
        }
        if (home is not null) Url("", home.UpdatedAt);
        foreach (var p in pages) Url(p.Path, p.UpdatedAt);
        sb.Append("</urlset>\n");
        return sb.ToString();
    }

    public string RenderRobots() =>
        _base is null
            ? "User-agent: *\nAllow: /\n"
            : $"User-agent: *\nAllow: /\nSitemap: {_base}{_mount}/sitemap.xml\n";

    // ------------------------------------------------------------ markdown

    public string Markdown(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var doc = Markdig.Markdown.Parse(md, Pipeline);
        foreach (var link in doc.Descendants<LinkInline>())
            link.Url = Href(link.Url ?? "");
        foreach (var auto in doc.Descendants<AutolinkInline>())
            auto.Url = Href(auto.Url);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.Replace<CodeBlockRenderer>(new DiagramAwareCodeBlockRenderer());
        renderer.Render(doc);
        writer.Flush();
        // Raw HTML is disabled, so a literal <table> here can only have come
        // from a markdown table — markup typed into the source is escaped.
        return writer.ToString()
            .Replace("<table>", "<div class=\"table-wrap\"><table>", StringComparison.Ordinal)
            .Replace("</table>", "</table></div>", StringComparison.Ordinal)
            .TrimEnd();
    }

    // One line of markdown without the paragraph around it — for subtitles
    // and figure captions that sit inside markup of their own.
    public string Inline(string? md)
    {
        var html = Markdown(md);
        return html.StartsWith("<p>", StringComparison.Ordinal)
            && html.EndsWith("</p>", StringComparison.Ordinal)
            && html.IndexOf("<p>", 3, StringComparison.Ordinal) < 0
            ? html[3..^4]
            : html;
    }

    // True when the body already opens with its own top-level heading.
    // Without this the layout's <h1> and the document's own `# Title` both
    // render and the page says its name twice — which is also what a crawler
    // reads. The content owns its heading structure; the layout only supplies
    // one when the content didn't.
    public static bool HasLeadingHeading(string? body, string contentType)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        return contentType switch
        {
            "markdown" => Markdig.Markdown.Parse(body, Pipeline).FirstOrDefault() is HeadingBlock { Level: 1 },
            "html" => LeadingH1Regex().IsMatch(body),
            _ => false,
        };
    }

    // Root-relative links to a page of this site ("/data-model", "/") are
    // rewritten under the mount, so content can link the way it always has
    // and the site still works served from /website. Anything else keeps its
    // target, except schemes that can run code, which are dropped.
    public string Href(string href)
    {
        var h = href.Trim();
        if (h.StartsWith('/') && !h.StartsWith("//", StringComparison.Ordinal))
        {
            var cut = h.IndexOfAny(['?', '#']);
            var path = cut < 0 ? h : h[..cut];
            var suffix = cut < 0 ? "" : h[cut..];
            var trimmed = path.Trim('/');
            if (trimmed.Length == 0) return HomeUrl + suffix;
            if (_pagePaths.Contains(trimmed)) return PageUrl(trimmed) + suffix;
            return h;
        }
        var scheme = SchemeRegex().Match(h);
        if (!scheme.Success) return h;
        return scheme.Groups[1].Value.ToLowerInvariant() is "http" or "https" or "mailto" or "tel" ? h : "#";
    }

    private static bool IsExternal(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^[a-z0-9-]+$")]
    private static partial Regex SectionIdRegex();

    [GeneratedRegex(@"^\s*<h1[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingH1Regex();

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9+.\-]*):")]
    private static partial Regex SchemeRegex();

    // ```mermaid fences become <pre class="mermaid"> holding the escaped
    // source, which site.js hands to the engine. The prose around a diagram is
    // still in the markup, which is what crawlers need.
    private sealed class DiagramAwareCodeBlockRenderer : CodeBlockRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock obj)
        {
            if (obj is FencedCodeBlock fenced
                && string.Equals(fenced.Info, "mermaid", StringComparison.OrdinalIgnoreCase))
            {
                renderer.EnsureLine();
                renderer.Write("<pre class=\"mermaid\">");
                renderer.WriteLeafRawLines(fenced, true, true);
                renderer.WriteLine("</pre>");
                return;
            }
            base.Write(renderer, obj);
        }
    }
}
