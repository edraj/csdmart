using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class EntryHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Mirrors dmart Python's `/entry/{resource_type}/{space}/{subpath:path}/{shortname}`.
        // Python returns {**meta.model_dump(exclude_none=True), "attachments": {...}}
        // — a flat dict with ALL model fields at the top level (state, is_open,
        // workflow_shortname, etc.), NOT nested under "attributes".
        //
        // Query parameters (matching Python):
        //   retrieve_json_payload   — include payload.body in the response (default false)
        //   retrieve_attachments    — include child attachments grouped by type (default false)
        g.MapGet("/entry/{resource_type}/{space}/{**rest}",
            async (string resource_type, string space, string rest,
                   bool? retrieve_json_payload,
                   bool? retrieve_attachments,
                   EntryService svc,
                   AttachmentRepository attachmentRepo,
                   SpaceRepository spaces,
                   UserRepository users,
                   AccessRepository access,
                   HttpContext http, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Results.BadRequest();
                var (subpath, shortname) = RouteParts.SplitSubpathAndShortname(rest);
                if (string.IsNullOrEmpty(shortname)) return Results.BadRequest();

                var actor = http.User.Identity?.Name;

                // Non-entry types: direct serialization.
                switch (rt)
                {
                    case ResourceType.Space:
                    {
                        var s = await spaces.GetAsync(shortname, ct);
                        return s is null ? Results.NotFound() : Results.Json(s, DmartJsonContext.Default.Space);
                    }
                    case ResourceType.User:
                    {
                        var u = await users.GetByShortnameAsync(shortname, ct);
                        return u is null ? Results.NotFound() : Results.Json(u, DmartJsonContext.Default.User);
                    }
                    case ResourceType.Role:
                    {
                        var r = await access.GetRoleAsync(shortname, ct);
                        return r is null ? Results.NotFound() : Results.Json(r, DmartJsonContext.Default.Role);
                    }
                    case ResourceType.Permission:
                    {
                        var p = await access.GetPermissionAsync(shortname, ct);
                        return p is null ? Results.NotFound() : Results.Json(p, DmartJsonContext.Default.Permission);
                    }
                }

                var entry = await svc.GetAsync(new Locator(rt, space, subpath, shortname), actor, ct);
                if (entry is null) return Results.NotFound();

                // Build attachments dict (Python always includes the key, even empty).
                Dictionary<string, List<Record>> attachmentsDict = new();
                if (retrieve_attachments == true)
                {
                    var children = await attachmentRepo.ListForParentAsync(space, subpath, shortname, ct);
                    if (children.Count > 0)
                    {
                        attachmentsDict = children
                            .GroupBy(a => JsonbHelpers.EnumMember(a.ResourceType))
                            .ToDictionary(
                                grp => grp.Key,
                                grp => grp.Select(a => AttachmentMapper.ToEntryRecord(a)).ToList());
                    }
                }

                // Build the flat response matching Python's meta.model_dump() + attachments.
                // Serialize Entry via source-gen (AOT-safe), then merge attachments in.
                var node = EntryToJsonNode.Convert(entry, retrieve_json_payload == true);
                var attNode = JsonSerializer.SerializeToNode(attachmentsDict, DmartJsonContext.Default.DictionaryStringListRecord);
                node["attachments"] = attNode;
                return Results.Content(node.ToJsonString(DmartJsonContext.Default.Options), "application/json");
            });

        g.MapGet("/byuuid/{uuid}", async (string uuid, EntryService svc, CancellationToken ct) =>
        {
            if (!Guid.TryParse(uuid, out var u)) return Results.BadRequest();
            var entry = await svc.GetByUuidAsync(u, ct);
            return entry is null ? Results.NotFound() : Results.Json(entry, DmartJsonContext.Default.Entry);
        });

        g.MapGet("/byslug/{slug}", async (string slug, EntryService svc, CancellationToken ct) =>
        {
            var entry = await svc.GetBySlugAsync(slug, ct);
            return entry is null ? Results.NotFound() : Results.Json(entry, DmartJsonContext.Default.Entry);
        });
    }
}

/// <summary>
/// Serializes an Entry to a JsonNode (mutable JSON DOM) using the source-gen context,
/// then strips the payload body if not requested. This avoids Dictionary&lt;string, object&gt;
/// serialization issues with AOT while producing the flat response Python returns.
/// </summary>
internal static class EntryToJsonNode
{
    public static JsonNode Convert(Entry entry, bool includePayloadBody)
    {
        // Serialize via source-gen → guaranteed correct for all nested types.
        var json = JsonSerializer.Serialize(entry, DmartJsonContext.Default.Entry);
        var node = JsonNode.Parse(json)!.AsObject();

        // Remove internal-only field that Python doesn't return.
        node.Remove("query_policies");

        // Strip payload.body if not requested.
        if (!includePayloadBody && node["payload"] is JsonObject payload)
            payload.Remove("body");

        return node;
    }
}
