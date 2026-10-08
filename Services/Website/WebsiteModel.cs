using System.Text.Json;

namespace Dmart.Services.Website;

// The website space's content, read off the same wire shape /public/query
// returns. Parsing by hand from JsonElement rather than through a serializer
// context keeps it AOT-safe without adding a dozen types to DmartJsonContext,
// and lets a missing or mistyped field degrade to a default instead of failing
// the whole build — the schemas in the website space are what enforce shape.

// One renderable page under /pages.
public sealed record WebsitePage(
    string Shortname,
    string Path,
    string Title,
    string Description,
    string ContentType,
    string Body,
    string? UpdatedAt)
{
    public static WebsitePage? FromRecord(JsonElement record)
    {
        var shortname = record.Str("shortname");
        if (string.IsNullOrEmpty(shortname)) return null;
        var a = record.Obj("attributes");
        var payload = a?.Obj("payload");
        // Shortnames cannot contain hyphens (the API enforces
        // ^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{1,64}$), so a hyphenated URL can only come
        // from slug. That makes slug load-bearing for any site with existing
        // URLs to preserve, not a nicety.
        var slug = a?.Str("slug")?.Trim();
        return new WebsitePage(
            shortname,
            string.IsNullOrEmpty(slug) ? shortname : slug,
            a?.Obj("displayname").Lang() ?? shortname,
            a?.Obj("description").Lang() ?? "",
            payload?.Str("content_type") ?? "",
            payload?.Str("body") ?? "",
            a?.Str("updated_at"));
    }
}

public sealed record SiteLink(string Label, string Href, bool Docs);
public sealed record NavItem(string Page, string? Label);
public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);

// /site/config — brand, header links, the docs sidebar and the footer.
public sealed record SiteConfig(
    string Brand,
    string TitleSuffix,
    string? BaseUrl,
    IReadOnlyList<SiteLink> HeaderLinks,
    IReadOnlyList<NavGroup> Nav,
    string Footer)
{
    // A site without a config entry still builds: no sidebar, so every page
    // renders standalone, and a header that links home.
    public static SiteConfig Default { get; } =
        new("DMART", " — DMART", null, [], [], "");

    public static SiteConfig FromBody(JsonElement body) => new(
        body.Str("brand") ?? Default.Brand,
        body.Str("title_suffix") ?? Default.TitleSuffix,
        NullIfBlank(body.Str("base_url")),
        body.Arr("header_links")
            .Select(l => new SiteLink(l.Str("label") ?? "", l.Str("href") ?? "", l.Bool("docs")))
            .Where(l => l.Label.Length > 0 && l.Href.Length > 0)
            .ToList(),
        body.Arr("nav")
            .Select(g => new NavGroup(
                g.Str("title") ?? "",
                g.Arr("items")
                    .Select(i => new NavItem(i.Str("page") ?? "", NullIfBlank(i.Str("label"))))
                    .Where(i => i.Page.Length > 0)
                    .ToList()))
            .ToList(),
        body.Str("footer") ?? "");

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public sealed record LandingAction(string Label, string Href, string Style);
public sealed record LandingItem(string Title, string Body, string? Href, string? Icon);
public sealed record LandingStat(string Value, string Label);
public sealed record LandingSection(
    string? Id,
    string Kind,
    string Title,
    string? Accent,
    string Body,
    // explainer: the template figure that plays the story; items are its scenes.
    string? Figure,
    IReadOnlyList<LandingItem> Items,
    IReadOnlyList<LandingAction> Actions);

// /site/home — the landing page. Its layout lives in the template; this is
// the text that fills it.
public sealed record LandingPage(
    string Title,
    string Description,
    string? UpdatedAt,
    string? Badge,
    IReadOnlyList<string> TitleLines,
    string Subtitle,
    IReadOnlyList<LandingAction> Actions,
    string? Figure,
    IReadOnlyList<LandingStat> Stats,
    IReadOnlyList<LandingSection> Sections)
{
    public static LandingPage FromRecord(JsonElement record)
    {
        var a = record.Obj("attributes");
        var body = a?.Obj("payload")?.Obj("body");
        var hero = body?.Obj("hero");
        var shortname = record.Str("shortname") ?? "home";
        return new LandingPage(
            a?.Obj("displayname").Lang() ?? shortname,
            a?.Obj("description").Lang() ?? "",
            a?.Str("updated_at"),
            hero?.Str("badge"),
            hero?.Arr("title_lines").Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToList() ?? [],
            hero?.Str("subtitle") ?? "",
            ReadActions(hero),
            hero?.Str("figure"),
            body?.Arr("stats")
                .Select(s => new LandingStat(s.Str("value") ?? "", s.Str("label") ?? ""))
                .ToList() ?? [],
            body?.Arr("sections")
                .Select(s => new LandingSection(
                    s.Str("id"),
                    s.Str("kind") ?? "prose",
                    s.Str("title") ?? "",
                    s.Str("accent"),
                    s.Str("body") ?? "",
                    s.Str("figure"),
                    s.Arr("items")
                        .Select(i => new LandingItem(i.Str("title") ?? "", i.Str("body") ?? "", i.Str("href"), i.Str("icon")))
                        .ToList(),
                    ReadActions(s)))
                .ToList() ?? []);
    }

    internal static List<LandingAction> ReadActions(JsonElement? owner) =>
        owner?.Arr("actions")
            .Select(x => new LandingAction(x.Str("label") ?? "", x.Str("href") ?? "", x.Str("style") ?? "primary"))
            .Where(x => x.Label.Length > 0 && x.Href.Length > 0)
            .ToList() ?? [];
}

internal static class WebsiteJson
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.True;

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Object
            ? v
            : null;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object
        && e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    // First language-keyed value, falling back across the usual suspects.
    public static string? Lang(this JsonElement? dict)
    {
        if (dict is not { ValueKind: JsonValueKind.Object } d) return null;
        foreach (var key in (string[])["en", "ar", "ku"])
            if (d.Str(key) is { Length: > 0 } s) return s;
        foreach (var p in d.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s)
                return s;
        return null;
    }
}
