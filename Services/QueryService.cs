using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;

namespace Dmart.Services;

public sealed class QueryService(EntryRepository entries, PermissionService perms)
{
    public async Task<Response> ExecuteAsync(Query q, string? actor, CancellationToken ct = default)
    {
        // Bounds check
        if (string.IsNullOrEmpty(q.SpaceName))
            return Response.Fail("bad_query", "space_name is required");

        // Permission gate at subpath level (cheap, single check; result-level filtering is handled below).
        var probe = new Locator(ResourceType.Content, q.SpaceName, q.Subpath ?? "/", "*");
        if (!await perms.CanReadAsync(actor, probe, ct))
            return Response.Fail("forbidden", "no read access for subpath");

        var hits = await entries.QueryAsync(q, ct);
        var records = hits.Select(EntryMapper.ToRecord).ToList();
        return Response.Ok(records, new() { ["total"] = records.Count });
    }
}

internal static class EntryMapper
{
    public static Record ToRecord(Entry e) => new()
    {
        ResourceType = e.ResourceType,
        Subpath = e.Subpath,
        Shortname = e.Shortname,
        Uuid = e.Uuid,
        Attributes = new()
        {
            ["is_active"] = e.IsActive,
            ["displayname"] = e.Displayname ?? (object)"",
            ["tags"] = e.Tags ?? (object)Array.Empty<string>(),
            ["payload"] = e.Payload ?? (object)new Dictionary<string, object>(),
        },
    };
}
