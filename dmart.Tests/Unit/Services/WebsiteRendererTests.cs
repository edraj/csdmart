using System.Text.Json;
using System.Text.RegularExpressions;
using Dmart.Middleware;
using Dmart.Services.Website;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins the HTML `dmart website build` emits — the shapes site.css styles and
// the trust rules that keep markdown from becoming script — against a
// minimal template, so a design change to WebsiteTemplate/ cannot mask a
// renderer regression (and vice versa).
public sealed class WebsiteRendererTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dmart-website-tpl-{Guid.NewGuid():N}");
    private readonly WebsiteTemplate _template;

    public WebsiteRendererTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "icons"));
        Directory.CreateDirectory(Path.Combine(_dir, "figures"));
        File.WriteAllText(Path.Combine(_dir, "layout.html"),
            "<html lang=\"{{lang}}\"><head>{{head}}<link href=\"{{assets}}/site.css?v={{version}}\">{{scripts}}</head>"
            + "<body class=\"{{body_class}}\"><a href=\"{{home}}\">{{brand}}</a><nav>{{header_links}}</nav>"
            + "{{sidebar}}<main>{{main}}</main><footer>{{footer}}</footer>{{unknown}}</body></html>");
        File.WriteAllText(Path.Combine(_dir, "site.css"), "body{}");
        File.WriteAllText(Path.Combine(_dir, "og-card.png"), "png");
        File.WriteAllText(Path.Combine(_dir, "icons", "database.svg"), "<svg id=\"icon-db\"></svg>");
        File.WriteAllText(Path.Combine(_dir, "figures", "architecture.svg"), "<svg id=\"fig-arch\"></svg>");
        File.WriteAllText(Path.Combine(_dir, "figures", "explainer.svg"), "<svg id=\"fig-xp\"><g class=\"sc\"></g><g class=\"sc\"></g></svg>");
        _template = WebsiteTemplate.LoadDirectory(_dir)!;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static WebsitePage Page(string path, string body, string type = "markdown", string title = "Data model") =>
        new(path.Replace('-', '_'), path, title, "About the model", type, body, "2026-10-01T00:00:00");

    private static readonly SiteConfig Site = new(
        "DMART", " — DMART", null,
        [new SiteLink("Home", "/", false), new SiteLink("Docs", "/data-model", true), new SiteLink("GitHub", "https://github.com/edraj/csdmart", false)],
        [new NavGroup("Core", [new NavItem("data-model", null), new NavItem("folders", "Folders & Rendering"), new NavItem("missing", null)])],
        "Made with [dmart](/).");

    private WebsiteRenderer Renderer(string mount = "/website", string? baseUrl = null, params WebsitePage[] pages) =>
        new(_template, Site, pages.Length > 0 ? pages : [Page("data-model", "x"), Page("folders", "x", title: "Folders"), Page("why", "x", title: "Why")],
            mount, baseUrl);

    [Fact]
    public void Markdown_Escapes_Raw_Html_Instead_Of_Passing_It_Through()
    {
        var html = Renderer().Markdown("hi <script>alert(1)</script> <img src=x onerror=alert(1)>");
        html.ShouldNotContain("<script>");
        html.ShouldNotContain("<img");
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public void Markdown_Ignores_Generic_Attribute_Syntax()
    {
        // GenericAttributes would turn this into onclick="…" on the paragraph.
        Renderer().Markdown("text {onclick=\"alert(1)\"}").ShouldNotContain("onclick=\"alert");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox")]
    public void Markdown_Drops_Link_Targets_That_Can_Run_Code(string target)
    {
        var html = Renderer().Markdown($"[click]({target})");
        html.ShouldContain("href=\"#\"");
        html.ShouldNotContain(target.Split(':')[0] + ":", Case.Insensitive);
    }

    [Fact]
    public void Markdown_Keeps_Ordinary_Link_Targets()
    {
        var html = Renderer().Markdown("[a](https://example.com/x) [b](mailto:a@b.c) [c](#section) [d](notes.txt)");
        html.ShouldContain("href=\"https://example.com/x\"");
        html.ShouldContain("href=\"mailto:a@b.c\"");
        html.ShouldContain("href=\"#section\"");
        html.ShouldContain("href=\"notes.txt\"");
    }

    [Fact]
    public void Root_Relative_Links_To_Site_Pages_Are_Rewritten_Under_The_Mount()
    {
        var r = Renderer("/website");
        r.Href("/data-model").ShouldBe("/website/data-model");
        r.Href("/data-model#ids").ShouldBe("/website/data-model#ids");
        r.Href("/folders/").ShouldBe("/website/folders");
        r.Href("/").ShouldBe("/website/");
        // Not a page of this site — left for whatever serves it.
        r.Href("/cxb/").ShouldBe("/cxb/");
        r.Href("//cdn.example.com/x").ShouldBe("//cdn.example.com/x");
    }

    [Fact]
    public void A_Root_Mount_Leaves_Page_Links_At_The_Root()
    {
        var r = Renderer("/");
        r.Href("/data-model").ShouldBe("/data-model");
        r.Href("/").ShouldBe("/");
        r.AssetsUrl.ShouldBe("/_site");
    }

    [Fact]
    public void Tables_Are_Wrapped_For_Horizontal_Scroll()
    {
        var html = Renderer().Markdown("| a | b |\n|---|---|\n| 1 | 2 |");
        html.ShouldContain("<div class=\"table-wrap\"><table>");
        html.ShouldContain("</table></div>");
    }

    [Fact]
    public void Mermaid_Fences_Become_Escaped_Pre_Mermaid()
    {
        var html = Renderer().Markdown("```mermaid\ngraph TD\n  A-->B<script>\n```");
        html.ShouldContain("<pre class=\"mermaid\">graph TD\n  A--&gt;B&lt;script&gt;\n</pre>");
        html.ShouldNotContain("<code");
    }

    [Fact]
    public void Ordinary_Fences_Keep_Their_Language_Class()
    {
        Renderer().Markdown("```bash\necho hi\n```").ShouldContain("<pre><code class=\"language-bash\">echo hi");
    }

    [Fact]
    public void Headings_Get_Ids_For_Anchors()
    {
        Renderer().Markdown("## Query Search").ShouldContain("<h2 id=\"query-search\">");
    }

    [Theory]
    [InlineData("# Title\n\nbody", "markdown", true)]
    [InlineData("Title\n=====\n\nbody", "markdown", true)]
    [InlineData("## Sub\n\nbody", "markdown", false)]
    [InlineData("intro\n\n# Later", "markdown", false)]
    [InlineData("  <h1 class=\"x\">T</h1>", "html", true)]
    [InlineData("<h2>T</h2>", "html", false)]
    [InlineData("# not markdown", "text", false)]
    public void HasLeadingHeading(string body, string type, bool expected) =>
        WebsiteRenderer.HasLeadingHeading(body, type).ShouldBe(expected);

    [Fact]
    public void Page_Supplies_An_H1_Only_When_The_Content_Has_None()
    {
        var r = Renderer();
        var own = r.RenderPage(Page("data-model", "# Data model\n\ntext"));
        Regex.Matches(own, "<h1").Count.ShouldBe(1);
        var none = r.RenderPage(Page("data-model", "## Section\n\ntext"));
        none.ShouldContain("<h1>Data model</h1>");
    }

    [Fact]
    public void Doc_Pages_Get_The_Sidebar_And_Standalone_Pages_Do_Not()
    {
        var r = Renderer();
        var doc = r.RenderPage(Page("data-model", "x"));
        doc.ShouldContain("class=\"page-doc\"");
        doc.ShouldContain("<aside class=\"sidebar\" id=\"sidebar\">");
        doc.ShouldContain("<a href=\"/website/data-model\" aria-current=\"page\">Data model</a>");
        doc.ShouldContain("<a href=\"/website/folders\">Folders &amp; Rendering</a>");
        doc.ShouldNotContain("missing", Case.Sensitive, customMessage: "a nav item naming no published page is left out");
        // The header link flagged docs:true is current on every doc page.
        doc.ShouldContain("<a class=\"header-link\" href=\"/website/data-model\" aria-current=\"page\">Docs</a>");

        var standalone = r.RenderPage(Page("why", "x", title: "Why"));
        standalone.ShouldContain("class=\"page-standalone\"");
        standalone.ShouldNotContain("sidebar");
        standalone.ShouldNotContain("aria-current");
    }

    [Fact]
    public void Header_External_Links_Get_Noopener()
    {
        Renderer().RenderPage(Page("why", "x"))
            .ShouldContain("<a class=\"header-link\" href=\"https://github.com/edraj/csdmart\" rel=\"noopener\">GitHub</a>");
    }

    [Fact]
    public void Head_Carries_Title_Description_And_Canonical_Only_With_A_Base()
    {
        var withoutBase = Renderer().RenderPage(Page("data-model", "x"));
        withoutBase.ShouldContain("<title>Data model — DMART</title>");
        withoutBase.ShouldContain("<meta name=\"description\" content=\"About the model\">");
        withoutBase.ShouldNotContain("canonical");
        withoutBase.ShouldNotContain("og:image");

        var withBase = Renderer("/website", "https://dmart.cc/").RenderPage(Page("data-model", "x"));
        withBase.ShouldContain("<link rel=\"canonical\" href=\"https://dmart.cc/website/data-model\">");
        withBase.ShouldContain("<meta property=\"og:image\" content=\"https://dmart.cc/website/_site/og-card.png\">");
        withBase.ShouldContain("summary_large_image");
    }

    [Fact]
    public void Head_Escapes_Content_Values()
    {
        var html = Renderer().RenderPage(new WebsitePage("p", "p", "A \"quoted\" <b>title</b>", "x\" onload=\"y", "markdown", "x", null));
        html.ShouldContain("<title>A &quot;quoted&quot; &lt;b&gt;title&lt;/b&gt; — DMART</title>");
        html.ShouldContain("content=\"x&quot; onload=&quot;y\"");
    }

    [Fact]
    public void Mermaid_Script_Is_Loaded_Only_On_Pages_That_Draw()
    {
        var r = Renderer();
        r.RenderPage(Page("data-model", "```mermaid\ngraph TD\n```")).ShouldContain(WebsiteRenderer.MermaidScript);
        r.RenderPage(Page("data-model", "no diagrams")).ShouldNotContain("mermaid.min.js");
    }

    [Fact]
    public void Substituted_Content_Is_Not_Rescanned_For_Placeholders()
    {
        var html = Renderer().RenderPage(Page("data-model", "literal {{main}} and {{brand}}"));
        html.ShouldContain("literal {{main}} and {{brand}}");
        html.ShouldContain("{{unknown}}", Case.Sensitive, customMessage: "an unknown placeholder stays visible rather than vanishing");
    }

    [Fact]
    public void Html_Pages_Are_Taken_As_Written_And_Text_Pages_Escaped()
    {
        var r = Renderer();
        r.RenderPage(Page("data-model", "<h1>T</h1><p class=\"x\">y</p>", "html"))
            .ShouldContain("<h1>T</h1><p class=\"x\">y</p>");
        r.RenderPage(Page("data-model", "a <b>", "text")).ShouldContain("<pre>a &lt;b&gt;</pre>");
    }

    [Fact]
    public void Home_Renders_Every_Section_Kind()
    {
        using var doc = JsonDocument.Parse("""
        {
          "shortname": "home",
          "attributes": {
            "displayname": {"en": "DMART — Platform"},
            "description": {"en": "One binary."},
            "payload": {"content_type": "json", "body": {
              "hero": {
                "badge": "AGPL-3.0 · SELF-HOSTED",
                "title_lines": ["DATA", "MART"],
                "subtitle": "In **one binary**.",
                "actions": [
                  {"label": "Explore", "href": "/data-model", "style": "primary"},
                  {"label": "GitHub →", "href": "https://github.com/edraj/csdmart", "style": "link"},
                  {"label": "Bad", "href": "javascript:alert(1)", "style": "weird"}
                ],
                "figure": "architecture"
              },
              "stats": [{"value": "≤300M", "label": "Entries"}],
              "sections": [
                {"id": "pillars", "kind": "cards", "title": "Core Pillars", "items": [
                  {"title": "Unified", "body": "All *one*.", "href": "/folders", "icon": "database"},
                  {"title": "Plain", "body": "No link.", "icon": "nope"}
                ]},
                {"kind": "steps", "title": "How", "items": [{"title": "Define", "body": "Spaces."}, {"title": "Store"}]},
                {"kind": "figures", "title": "Runs", "body": "Lede.\n\nBody.", "items": [{"title": "106 MB", "body": "idle"}]},
                {"kind": "prose", "title": "Own it", "body": "Files."},
                {"id": "Bad Id!", "kind": "banner", "title": "Stop.", "accent": "Start.", "actions": [{"label": "Go", "href": "/"}]}
              ]
            }}
          }
        }
        """);
        var home = LandingPage.FromRecord(doc.RootElement);
        var html = Renderer().RenderHome(home);

        html.ShouldContain("class=\"page-home\"");
        html.ShouldContain("<title>DMART — Platform</title>");
        html.ShouldContain("<p class=\"hero-badge\">AGPL-3.0 · SELF-HOSTED</p>");
        html.ShouldContain("<h1 class=\"hero-title\"><span class=\"hero-title-line\">DATA</span><span class=\"hero-title-line accent\">MART</span></h1>");
        html.ShouldContain("<p class=\"hero-subtitle\">In <strong>one binary</strong>.</p>");
        html.ShouldContain("<a class=\"btn btn-primary\" href=\"/website/data-model\">Explore</a>");
        html.ShouldContain("<a class=\"btn btn-link\" href=\"https://github.com/edraj/csdmart\" rel=\"noopener\">GitHub →</a>");
        html.ShouldContain("<a class=\"btn btn-primary\" href=\"#\">Bad</a>");
        html.ShouldContain("<div class=\"hero-figure\"><svg id=\"fig-arch\"></svg></div>");
        html.ShouldContain("<li class=\"stat\"><span class=\"stat-value\">≤300M</span><span class=\"stat-label\">Entries</span></li>");

        html.ShouldContain("<section class=\"band band-cards\" id=\"pillars\">");
        html.ShouldContain("<a class=\"card\" href=\"/website/folders\"><span class=\"card-icon\"><svg id=\"icon-db\"></svg></span><h3 class=\"card-title\">Unified</h3>");
        html.ShouldContain("<span class=\"card-arrow\" aria-hidden=\"true\">→</span></a>");
        // No href → a div with no arrow; an unknown icon name → no icon.
        html.ShouldContain("<div class=\"card\"><h3 class=\"card-title\">Plain</h3><div class=\"card-body\"><p>No link.</p></div></div>");

        html.ShouldContain("<li class=\"step\"><span class=\"step-num\">01</span><div class=\"step-content\"><h3 class=\"step-title\">Define</h3>");
        html.ShouldContain("<span class=\"step-num\">02</span>");
        html.ShouldContain("<div class=\"figures-grid\"><div class=\"figures-main\"><p>Lede.</p>\n<p>Body.</p></div><dl class=\"figures\"><div class=\"figure\"><dt>106 MB</dt><dd>idle</dd></div></dl></div>");
        html.ShouldContain("<section class=\"band band-prose\">\n<h2 class=\"section-title\">Own it</h2>\n<div class=\"section-body\"><p>Files.</p></div>");
        // An id that fails the pattern is dropped rather than escaped into the markup.
        html.ShouldContain("<section class=\"band band-banner\">\n<h2 class=\"section-title\">Stop.<br><span class=\"accent\">Start.</span></h2>");
        html.ShouldContain("<a class=\"btn btn-primary\" href=\"/website/\">Go</a>");
    }

    [Fact]
    public void Explainer_Renders_Its_Figure_And_Numbered_Scene_Captions()
    {
        using var doc = JsonDocument.Parse("""
        {"shortname": "home", "attributes": {"payload": {"body": {
          "hero": {"title_lines": ["X"]},
          "sections": [
            {"id": "in-30-seconds", "kind": "explainer", "title": "In 30 seconds", "figure": "explainer", "items": [
              {"title": "Model it", "body": "A **space**."},
              {"title": "<Store>"}
            ]},
            {"kind": "explainer", "title": "No art", "figure": "missing", "items": [{"title": "Only"}]}
          ]
        }}}}
        """);
        var html = Renderer().RenderHome(LandingPage.FromRecord(doc.RootElement));

        html.ShouldContain("<section class=\"band band-explainer\" id=\"in-30-seconds\">");
        // The figure is inlined, then the play/pause control site.js reveals.
        html.ShouldContain("<div class=\"explainer\">\n<div class=\"explainer-stage\"><svg id=\"fig-xp\"><g class=\"sc\"></g><g class=\"sc\"></g></svg>"
            + "<button type=\"button\" class=\"explainer-toggle\" aria-pressed=\"false\" hidden>Pause</button></div>");
        // Scene buttons start disabled: without script they must not look clickable and do nothing.
        html.ShouldContain("<li class=\"scene\"><h3 class=\"scene-title\"><button type=\"button\" class=\"scene-jump\" disabled>"
            + "<span class=\"scene-num\">1</span>Model it</button></h3><span class=\"scene-bar\" aria-hidden=\"true\"></span>"
            + "<div class=\"scene-body\"><p>A <strong>space</strong>.</p></div></li>");
        html.ShouldContain("<span class=\"scene-num\">2</span>&lt;Store&gt;</button></h3><span class=\"scene-bar\" aria-hidden=\"true\"></span></li>");
        // An unknown figure name leaves an empty stage, not an error.
        html.ShouldContain("<div class=\"explainer-stage\"><button type=\"button\" class=\"explainer-toggle\"");
    }

    [Fact]
    public void A_Single_Title_Line_Gets_No_Accent()
    {
        using var doc = JsonDocument.Parse("""{"shortname":"home","attributes":{"payload":{"body":{"hero":{"title_lines":["DMART"]}}}}}""");
        Renderer().RenderHome(LandingPage.FromRecord(doc.RootElement))
            .ShouldContain("<h1 class=\"hero-title\"><span class=\"hero-title-line\">DMART</span></h1>");
    }

    [Fact]
    public void Sitemap_Lists_Home_And_Pages_With_Lastmod()
    {
        var r = Renderer("/", "https://dmart.cc");
        using var doc = JsonDocument.Parse("""{"shortname":"home","attributes":{"updated_at":"2026-10-08T10:00:00","payload":{"body":{"hero":{"title_lines":["X"]}}}}}""");
        var xml = r.RenderSitemap([Page("data-model", "x")], LandingPage.FromRecord(doc.RootElement));
        xml.ShouldContain("<loc>https://dmart.cc/</loc>\n    <lastmod>2026-10-08</lastmod>");
        xml.ShouldContain("<loc>https://dmart.cc/data-model</loc>\n    <lastmod>2026-10-01</lastmod>");
        r.RenderRobots().ShouldBe("User-agent: *\nAllow: /\nSitemap: https://dmart.cc/sitemap.xml\n");
    }

    [Fact]
    public void Sitemap_Without_A_Base_Is_Refused()
    {
        Should.Throw<InvalidOperationException>(() => Renderer().RenderSitemap([], null));
    }

    [Fact]
    public void Page_Reads_Slug_Then_Shortname_From_The_Wire_Record()
    {
        using var doc = JsonDocument.Parse("""
        [{"shortname":"data_model","attributes":{"slug":"data-model","displayname":{"ar":"نموذج"},"payload":{"content_type":"markdown","body":"# x"}}},
         {"shortname":"why","attributes":{"slug":"  ","payload":{"content_type":"markdown","body":"y"}}}]
        """);
        var pages = doc.RootElement.EnumerateArray().Select(WebsitePage.FromRecord).ToList();
        pages[0]!.Path.ShouldBe("data-model");
        pages[0]!.Title.ShouldBe("نموذج");
        pages[1]!.Path.ShouldBe("why");
        pages[1]!.Title.ShouldBe("why");
    }

    [Fact]
    public void Embedded_Template_Has_Every_Placeholder_And_No_Inline_Script()
    {
        var t = WebsiteTemplate.LoadEmbedded();
        t.ShouldNotBeNull("WebsiteTemplate/** must be embedded in the dmart assembly");
        foreach (var p in (string[])["{{head}}", "{{assets}}", "{{version}}", "{{scripts}}", "{{body_class}}",
                     "{{home}}", "{{brand}}", "{{header_links}}", "{{sidebar}}", "{{main}}", "{{footer}}"])
            t.Layout.ShouldContain(p);
        t.Assets.Keys.ShouldContain("site.css");
        t.Assets.Keys.ShouldContain("site.js");
        // The site is served with script-src 'self' + jsdelivr and no
        // 'unsafe-inline': an inline script or handler would simply not run.
        Regex.IsMatch(t.Layout, @"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase).ShouldBeFalse("inline <script> in layout.html");
        Regex.IsMatch(t.Layout, @"\son[a-z]+\s*=", RegexOptions.IgnoreCase).ShouldBeFalse("inline event handler in layout.html");
        t.Figure("architecture").ShouldNotBeNull();

        // Figures are inlined into the page, so the same rule holds for them.
        // The explainer tells the five-scene story of the `website` pack's home page.
        var explainer = t.Figure("explainer");
        explainer.ShouldNotBeNull();
        Regex.Matches(explainer, "<g class=\"sc[ \"]").Count.ShouldBe(5);
        Regex.IsMatch(explainer, @"<script|\son[a-z]+\s*=", RegexOptions.IgnoreCase).ShouldBeFalse("script in explainer.svg");
    }

    [Theory]
    [InlineData("", "index.html")]
    [InlineData("/", "index.html")]
    [InlineData("/data-model", "data-model/index.html")]
    [InlineData("/data-model/", "data-model/index.html")]
    [InlineData("/_site/site.css", "_site/site.css")]
    public void Locate_Maps_Extensionless_Urls_To_Index_Files(string request, string expected)
    {
        var build = Path.Combine(_dir, "build");
        foreach (var f in (string[])["index.html", "data-model/index.html", "_site/site.css"])
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(build, f))!);
            File.WriteAllText(Path.Combine(build, f), "x");
        }
        WebsiteMiddleware.Locate(build, request).ShouldBe(Path.GetFullPath(Path.Combine(build, expected)));
    }

    [Theory]
    [InlineData("/../secret.txt")]
    [InlineData("/data-model/../../secret.txt")]
    [InlineData("/.hidden")]
    [InlineData("/..%2fsecret.txt")]
    [InlineData("/a\\..\\..\\secret.txt")]
    [InlineData("//etc/passwd")]
    [InlineData("/missing")]
    public void Locate_Refuses_Anything_Outside_The_Build(string request)
    {
        var build = Path.Combine(_dir, "build2");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Combine(_dir, "secret.txt"), "s");
        WebsiteMiddleware.Locate(build, request).ShouldBeNull();
    }
}
