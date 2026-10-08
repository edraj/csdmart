using System.Net;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Dmart.Services.Website;
using Dmart.Utils;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// `dmart website build` end to end: content in the database → the anonymous
// read → files on disk → WebsiteMiddleware serving them. The ACL half is the
// point: the build must publish exactly what the world permission lets an
// anonymous reader see — nothing while the space is private, and never an
// inactive (draft) entry.
[Collection(AnonymousWorldCollection.Name)]
public sealed class WebsiteBuildTests : IClassFixture<DmartFactory>, IDisposable
{
    private readonly DmartFactory _factory;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dmart-website-{Guid.NewGuid():N}");
    private readonly string _templateDir = Path.Combine(Path.GetTempPath(), $"dmart-website-tpl-{Guid.NewGuid():N}");

    public WebsiteBuildTests(DmartFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(_templateDir);
        File.WriteAllText(Path.Combine(_templateDir, "layout.html"),
            "<!doctype html><html><head>{{head}}<link rel=\"stylesheet\" href=\"{{assets}}/site.css?v={{version}}\"></head>"
            + "<body class=\"{{body_class}}\"><nav>{{header_links}}</nav>{{sidebar}}<main>{{main}}</main></body></html>");
        File.WriteAllText(Path.Combine(_templateDir, "site.css"), "body{color:red}");
    }

    public void Dispose()
    {
        foreach (var d in (string[])[_root, _templateDir])
            try { Directory.Delete(d, recursive: true); } catch { }
    }

    private static Entry Content(string space, string subpath, string shortname, ContentType type,
        JsonElement body, bool active = true, string? slug = null, string? title = null, string? schema = null) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = space,
        Subpath = subpath,
        Slug = slug,
        Displayname = title is null ? null : new Translation(En: title),
        OwnerShortname = "dmart",
        ResourceType = ResourceType.Content,
        IsActive = active,
        Payload = new Payload { ContentType = type, SchemaShortname = schema, Body = body },
        CreatedAt = TimeUtils.Now(),
        UpdatedAt = TimeUtils.Now(),
    };

    private static JsonElement Str(string s) => JsonSerializer.SerializeToElement(s);
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [FactIfPg]
    public async Task Build_Publishes_Only_What_Anonymous_Can_Read_And_The_Server_Serves_It()
    {
        _factory.CreateClient();   // boots AdminBootstrap: dmart, anonymous, world
        var space = $"itest_web_{Guid.NewGuid():N}"[..22];

        var seeded = new[]
        {
            Content(space, "/pages", "getting_started", ContentType.Markdown,
                Str("# Getting started\n\nSee [the model](/data-model) and [home](/).\n\n| a | b |\n|---|---|\n| 1 | 2 |"),
                slug: "getting-started", title: "Getting started"),
            Content(space, "/pages", "data_model", ContentType.Markdown,
                Str("## Entries\n\nbody"), slug: "data-model", title: "Data model"),
            Content(space, "/pages", "draft", ContentType.Markdown,
                Str("# Not yet"), active: false, slug: "draft", title: "Draft"),
            Content(space, "/site", "config", ContentType.Json, Json("""
                {"brand": "Acme", "title_suffix": " | Acme",
                 "header_links": [{"label": "Docs", "href": "/getting-started", "docs": true}],
                 "nav": [{"title": "Start", "items": [{"page": "getting-started"}, {"page": "draft"}]}]}
                """), schema: "site_config"),
            Content(space, "/site", "home", ContentType.Json, Json("""
                {"hero": {"title_lines": ["ACME"], "subtitle": "A **site**."},
                 "sections": [{"kind": "prose", "title": "Hello", "body": "World."}]}
                """), title: "Acme home", schema: "landing_page"),
        };

        using var host = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Dmart:WebsiteDir"] = _root })));
        // Everything through THIS host's services: it has its own authz cache,
        // and a grant invalidated on another host's cache would never reach
        // the QueryService the build reads through.
        var access = host.Services.GetRequiredService<AccessRepository>();
        var entries = host.Services.GetRequiredService<EntryRepository>();
        var priorWorld = await access.GetPermissionAsync(WorldPermissionFixture.Shortname);
        var template = WebsiteTemplate.LoadDirectory(_templateDir)!;
        var options = new WebsiteBuildOptions(space, _root, "/website", "https://acme.test", template);
        var builder = new WebsiteBuilder(host.Services.GetRequiredService<QueryService>());
        var client = host.CreateClient();

        try
        {
            foreach (var e in seeded) await entries.UpsertAsync(e);

            // Not built yet: a plain 404, not the API's JSON envelope.
            var before = await client.GetAsync("/website/");
            before.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await before.Content.ReadAsStringAsync()).ShouldBeEmpty();

            // Private space: the world permission does not reach it, so the
            // build has nothing to publish and must refuse rather than write
            // an empty site.
            await WorldPermissionFixture.UpsertAsync(access, priorWorld,
                subpaths: new() { ["some_other_space"] = new() { "__all_subpaths__" } },
                actions: new() { "view", "query" },
                resourceTypes: new() { "content", "folder" },
                conditions: new() { "is_active" });
            await access.InvalidateAllCachesAsync();
            var refused = await Should.ThrowAsync<WebsiteBuildException>(() => builder.BuildAsync(options));
            refused.Message.ShouldContain("anonymous");
            File.Exists(Path.Combine(_root, WebsiteBuilder.CurrentFile)).ShouldBeFalse();

            // Public: grant the space to the world permission, as an operator would.
            await WorldPermissionFixture.UpsertAsync(access, priorWorld,
                subpaths: new() { [space] = new() { "__all_subpaths__" } },
                actions: new() { "view", "query" },
                resourceTypes: new() { "content", "folder" },
                conditions: new() { "is_active" });
            await access.InvalidateAllCachesAsync();

            var result = await builder.BuildAsync(options);
            result.Pages.ShouldBe(2, "the inactive draft is not readable by anonymous, so it is not published");
            result.Home.ShouldBeTrue();
            result.Files.ShouldContain("getting-started/index.html");
            result.Files.ShouldContain("data-model/index.html");
            result.Files.ShouldNotContain("draft/index.html");
            result.Files.ShouldContain("sitemap.xml");
            result.Warnings.ShouldContain(w => w.Contains("'draft'", StringComparison.Ordinal));

            var page = await client.GetAsync("/website/getting-started");
            page.StatusCode.ShouldBe(HttpStatusCode.OK);
            page.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
            var csp = string.Join(";", page.Headers.GetValues("Content-Security-Policy"));
            csp.ShouldContain("script-src 'self' https://cdn.jsdelivr.net");
            csp.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src", StringComparison.Ordinal))
                .ShouldNotContain("unsafe-inline", Case.Sensitive, customMessage: "inline script must not run on the site");
            page.Headers.CacheControl!.NoCache.ShouldBeTrue();
            page.Headers.CacheControl.NoStore.ShouldBeFalse();
            var html = await page.Content.ReadAsStringAsync();
            html.ShouldContain("<title>Getting started | Acme</title>");
            html.ShouldContain("<link rel=\"canonical\" href=\"https://acme.test/website/getting-started\">");
            html.ShouldContain("<a href=\"/website/data-model\">the model</a>");
            html.ShouldContain("<a href=\"/website/\">home</a>");
            html.ShouldContain("<div class=\"table-wrap\"><table>");
            html.ShouldContain("class=\"page-doc\"");
            html.ShouldNotContain("Draft", Case.Sensitive, customMessage: "a nav item for an unpublished page is left out");

            // Trailing slash, the home page, an asset, and a miss.
            (await client.GetAsync("/website/getting-started/")).StatusCode.ShouldBe(HttpStatusCode.OK);
            var home = await (await client.GetAsync("/website")).Content.ReadAsStringAsync();
            home.ShouldContain("class=\"page-home\"");
            home.ShouldContain("A <strong>site</strong>.");
            var css = await client.GetAsync("/website/_site/site.css");
            css.StatusCode.ShouldBe(HttpStatusCode.OK);
            css.Content.Headers.ContentType!.MediaType.ShouldBe("text/css");
            var missing = await client.GetAsync("/website/draft");
            missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await missing.Content.ReadAsStringAsync()).ShouldContain("Page not found");

            // Revalidation: the same ETag comes back as 304.
            var etag = page.Headers.ETag!.Tag;
            using var conditional = new HttpRequestMessage(HttpMethod.Get, "/website/getting-started");
            conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
            (await client.SendAsync(conditional)).StatusCode.ShouldBe(HttpStatusCode.NotModified);

            // A rebuild goes live on the next request, with no restart.
            await entries.UpsertAsync(seeded[1] with { Payload = seeded[1].Payload! with { Body = Str("## Entries\n\nrevised body") } });
            await builder.BuildAsync(options);
            (await client.GetStringAsync("/website/data-model")).ShouldContain("revised body");
            Directory.GetDirectories(Path.Combine(_root, WebsiteBuilder.BuildsDir)).Length.ShouldBeLessThanOrEqualTo(2);
        }
        finally
        {
            foreach (var e in seeded)
                try { await entries.DeleteAsync(space, e.Subpath, e.Shortname, ResourceType.Content); } catch { }
            try { await access.DeletePermissionAsync(WorldPermissionFixture.Shortname); } catch { }
            if (priorWorld is not null) await access.UpsertPermissionAsync(priorWorld);
            await access.InvalidateAllCachesAsync();
        }
    }
}
