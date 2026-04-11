namespace Dmart.Api;

// Helpers for routes that mirror dmart's `{subpath:path}/{shortname}` pattern.
// ASP.NET Core's catchall route parameter (`{**rest}`) can only sit at the very end
// of a route, so we capture everything after `{space}` as a single string and split
// the trailing segments off here.
//
// Examples:
//   /entry/content/applications/api/v1/management/update_password
//     → rest = "api/v1/management/update_password"
//     → SplitSubpathAndShortname → ("api/v1/management", "update_password")
//
//   /payload/media/applications/api/v1/management/myimage.png
//     → rest = "api/v1/management/myimage.png"
//     → SplitPayloadParts → (subpath="api/v1/management", shortname="myimage", schema=null, ext="png")
//
//   /payload/content/applications/api/v1/management/foo.user.json
//     → rest = "api/v1/management/foo.user.json"
//     → SplitPayloadParts → (subpath="api/v1/management", shortname="foo", schema="user", ext="json")
//
//   /progress-ticket/applications/api/v1/tickets/myticket/in_progress
//     → rest = "api/v1/tickets/myticket/in_progress"
//     → SplitProgressTicketParts → (subpath="api/v1/tickets", shortname="myticket", action="in_progress")
public static class RouteParts
{
    /// <summary>
    /// Splits "subpath/.../shortname" → ("subpath/...", "shortname"). When `rest`
    /// has no slash the subpath is treated as "/" and the whole string is the shortname.
    /// </summary>
    public static (string Subpath, string Shortname) SplitSubpathAndShortname(string rest)
    {
        if (string.IsNullOrEmpty(rest)) return ("/", "");
        var lastSlash = rest.LastIndexOf('/');
        if (lastSlash < 0) return ("/", rest);
        var subpath = rest[..lastSlash];
        var shortname = rest[(lastSlash + 1)..];
        return (string.IsNullOrEmpty(subpath) ? "/" : subpath, shortname);
    }

    /// <summary>
    /// Splits "subpath/.../shortname[.schema].ext" → (subpath, shortname, schema, ext).
    /// One dot in the filename → no schema; two or more dots → first piece is shortname,
    /// last piece is ext, middle pieces become schema (joined by dot).
    /// </summary>
    public static (string Subpath, string Shortname, string? Schema, string Ext)? SplitPayloadParts(string rest)
    {
        var (subpath, filename) = SplitSubpathAndShortname(rest);
        if (string.IsNullOrEmpty(filename)) return null;

        var parts = filename.Split('.');
        if (parts.Length < 2) return null;          // need at least shortname.ext
        if (parts.Length == 2) return (subpath, parts[0], null, parts[1]);

        // shortname.schema.ext (or shortname.schema.with.dots.ext — schema joined by '.')
        var shortname = parts[0];
        var ext = parts[^1];
        var schema = string.Join('.', parts[1..^1]);
        return (subpath, shortname, schema, ext);
    }

    /// <summary>
    /// Splits "subpath/.../shortname/action" → (subpath, shortname, action).
    /// </summary>
    public static (string Subpath, string Shortname, string Action)? SplitProgressTicketParts(string rest)
    {
        if (string.IsNullOrEmpty(rest)) return null;
        var lastSlash = rest.LastIndexOf('/');
        if (lastSlash < 0) return null;
        var action = rest[(lastSlash + 1)..];
        var head = rest[..lastSlash];
        var (subpath, shortname) = SplitSubpathAndShortname(head);
        if (string.IsNullOrEmpty(shortname)) return null;
        return (subpath, shortname, action);
    }
}
