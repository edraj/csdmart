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
        // — Meta fields at root (no space_name/subpath/resource_type), attachments
        // use Record shape with attributes wrapper.
        //
        // Query parameters (matching Python):
        //   retrieve_json_payload   — include payload.body in the response (default false)
        //   retrieve_attachments    — include child attachments grouped by type (default false)
        g.MapGet("/entry/{resource_type}/{space}/{**rest}",
            async (string resource_type, string space, string rest,
                   bool? retrieve_json_payload,
                   bool? retrieve_attachments,
                   bool? retrieve_lock_status,
                   EntryService svc,
                   AttachmentRepository attachmentRepo,
                   SpaceRepository spaces,
                   UserRepository users,
                   AccessRepository access,
                   PermissionService perms,
                   LockRepository locks,
                   Microsoft.Extensions.Options.IOptions<Dmart.Config.DmartSettings> settings,
                   HttpContext http, CancellationToken ct) =>
            {
                // Python parity: every failure path returns the structured
                // {status:"failed", error:{type, code, message}} envelope. The
                // caller's Python client decodes api.Error — bare 404/400 HTML
                // breaks that contract. Not-found still uses HTTP 404 so
                // clients can branch on the status code without parsing the
                // body (matches Python api.Error bindings + existing tests).
                static IResult NotFoundMedia() => Results.Json(
                    Response.Fail(InternalErrorCode.OBJECT_NOT_FOUND,
                        "Request object is not available", ErrorTypes.Media),
                    DmartJsonContext.Default.Response, statusCode: 404);

                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Results.Json(
                        Response.Fail(InternalErrorCode.INVALID_DATA,
                            $"invalid resource_type '{resource_type}'", ErrorTypes.Request),
                        DmartJsonContext.Default.Response, statusCode: 400);
                var (subpath, shortname) = RouteParts.SplitSubpathAndShortname(rest);
                if (string.IsNullOrEmpty(shortname))
                    return Results.Json(
                        Response.Fail(InternalErrorCode.INVALID_DATA,
                            "shortname required", ErrorTypes.Request),
                        DmartJsonContext.Default.Response, statusCode: 400);

                var actor = http.Actor();

                // Non-entry types: direct serialization, plus `attachments`
                // (see JsonWithAttachments). Their attachment lookup keys off
                // the ROW's own space/subpath rather than the route's — these
                // four resolve by shortname alone, so a caller can reach a user
                // at .../management/__root__/x and the route subpath is not
                // where the row actually lives.
                switch (rt)
                {
                    case ResourceType.Space:
                    {
                        var s = await spaces.GetAsync(shortname, ct);
                        if (s is null) return NotFoundMedia();
                        var locator = new Locator(ResourceType.Space, s.SpaceName, s.Subpath, s.Shortname);
                        if (!await perms.CanReadAsync(actor, locator, PermissionService.FromSpace(s), ct))
                            return NotFoundMedia();
                        var sAttachments = await BuildAttachmentsAsync(
                            s.SpaceName, s.Subpath, s.Shortname,
                            retrieve_attachments == true, attachmentRepo, perms, actor, ct);
                        return JsonWithAttachments(
                            JsonSerializer.Serialize(s, DmartJsonContext.Default.Space), sAttachments);
                    }
                    case ResourceType.User:
                    {
                        var u = await users.GetByShortnameAsync(shortname, ct);
                        if (u is null) return NotFoundMedia();
                        var locator = new Locator(ResourceType.User, u.SpaceName, u.Subpath, u.Shortname);
                        if (!await perms.CanReadAsync(actor, locator, PermissionService.FromUser(u), ct))
                            return NotFoundMedia();
                        var uAttachments = await BuildAttachmentsAsync(
                            u.SpaceName, u.Subpath, u.Shortname,
                            retrieve_attachments == true, attachmentRepo, perms, actor, ct);
                        return JsonWithAttachments(
                            JsonSerializer.Serialize(u, DmartJsonContext.Default.User), uAttachments);
                    }
                    case ResourceType.Role:
                    {
                        var r = await access.GetRoleAsync(shortname, ct);
                        if (r is null) return NotFoundMedia();
                        var locator = new Locator(ResourceType.Role, r.SpaceName, r.Subpath, r.Shortname);
                        if (!await perms.CanReadAsync(actor, locator, PermissionService.FromRole(r), ct))
                            return NotFoundMedia();
                        var rAttachments = await BuildAttachmentsAsync(
                            r.SpaceName, r.Subpath, r.Shortname,
                            retrieve_attachments == true, attachmentRepo, perms, actor, ct);
                        return JsonWithAttachments(
                            JsonSerializer.Serialize(r, DmartJsonContext.Default.Role), rAttachments);
                    }
                    case ResourceType.Permission:
                    {
                        var p = await access.GetPermissionAsync(shortname, ct);
                        if (p is null) return NotFoundMedia();
                        var locator = new Locator(ResourceType.Permission, p.SpaceName, p.Subpath, p.Shortname);
                        if (!await perms.CanReadAsync(actor, locator, PermissionService.FromPermission(p), ct))
                            return NotFoundMedia();
                        var pAttachments = await BuildAttachmentsAsync(
                            p.SpaceName, p.Subpath, p.Shortname,
                            retrieve_attachments == true, attachmentRepo, perms, actor, ct);
                        return JsonWithAttachments(
                            JsonSerializer.Serialize(p, DmartJsonContext.Default.Permission), pAttachments);
                    }
                }

                var entry = await svc.GetAsync(new Locator(rt, space, subpath, shortname), actor, ct);
                if (entry is null) return NotFoundMedia();

                var attNode = await BuildAttachmentsAsync(space, subpath, shortname,
                    retrieve_attachments == true, attachmentRepo, perms, actor, ct);

                var node = EntryToJsonNode.Convert(entry, retrieve_json_payload == true);
                node["attachments"] = attNode;

                // retrieve_lock_status: surface the holder of any live lock as a
                // `locked` object (Python's get_entry surfacing). Subpath is
                // normalized to the leading-slash form the locks table stores.
                if (retrieve_lock_status == true)
                {
                    var holder = await locks.GetLockerAsync(
                        space, Locator.NormalizeSubpath(subpath), shortname, settings.Value.LockPeriod, ct);
                    if (holder is not null)
                        node["locked"] = new JsonObject { ["owner_shortname"] = holder };
                }

                return Results.Content(node.ToJsonString(DmartJsonContext.Default.Options), "application/json");
            });

        g.MapGet("/byuuid/{uuid}", async (string uuid, EntryService svc, HttpContext http, CancellationToken ct) =>
        {
            if (!Guid.TryParse(uuid, out var u)) return Results.BadRequest();
            var entry = await svc.GetByUuidAsync(u, http.ActorOrAnonymous(), ct);
            return entry is null ? Results.NotFound() : Results.Json(entry, DmartJsonContext.Default.Entry);
        });

        g.MapGet("/byslug/{slug}", async (string slug, EntryService svc, HttpContext http, CancellationToken ct) =>
        {
            var entry = await svc.GetBySlugAsync(slug, http.ActorOrAnonymous(), ct);
            return entry is null ? Results.NotFound() : Results.Json(entry, DmartJsonContext.Default.Entry);
        });
    }

    // Attachments for one parent, grouped by resource_type. Each attachment
    // keeps its `attributes` wrapper around the meta fields, matching Python's
    // `get_entry_attachments` shape (adapter.py:1342 —
    // `attachment["attributes"] = {...}`) and the /entry handler's
    // `return {**meta.model_dump(), "attachments": attachments}` composition at
    // router.py:1003. Spreading those attributes at the record root instead made
    // every attachment flat, so clients parsing attachment.attributes.X got
    // `undefined`.
    //
    // Not requested (or nothing readable) yields an empty object: callers always
    // set an `attachments` key and JsonStripEmptiesMiddleware drops it on the way
    // out when it is empty.
    private static async Task<JsonObject> BuildAttachmentsAsync(
        string space, string subpath, string shortname, bool retrieveAttachments,
        AttachmentRepository attachmentRepo, PermissionService perms, string? actor,
        CancellationToken ct)
    {
        var attNode = new JsonObject();
        if (!retrieveAttachments) return attNode;

        var children = await attachmentRepo.ListForParentAsync(space, subpath, shortname, ct);
        // Readable-parent does not imply readable-children: comment and json
        // attachments carry their own ACL, and ToEntryRecord emits `payload` AND
        // `body` — so an unfiltered list hands over the attachment's CONTENT, not
        // just its metadata. Same gate, same shape as
        // Api/Managed/PayloadHandler.cs:55 (which refuses the bytes) and
        // Api/Public/EntryHandler.cs (the anonymous twin).
        var visible = new List<Attachment>(children.Count);
        foreach (var a in children)
        {
            var attachmentLocator = new Locator(a.ResourceType, a.SpaceName, a.Subpath, a.Shortname);
            if (await perms.CanReadAsync(actor, attachmentLocator,
                    PermissionService.FromAttachment(a), ct))
                visible.Add(a);
        }
        foreach (var grp in visible.GroupBy(a => JsonbHelpers.EnumMember(a.ResourceType)))
        {
            var arr = new JsonArray();
            foreach (var rec in grp.Select(a => AttachmentMapper.ToEntryRecord(a)))
            {
                var recJson = JsonSerializer.Serialize(rec, DmartJsonContext.Default.Record);
                arr.Add(JsonNode.Parse(recJson));
            }
            attNode[grp.Key] = arr;
        }
        return attNode;
    }

    // Hangs `attachments` off an already-serialized non-entry row (space, user,
    // role, permission — each lives in its own table and is serialized straight
    // from its source-gen type info rather than through EntryToJsonNode).
    //
    // Python's retrieve_entry_meta has no per-resource_type branch: it loads the
    // meta for ANY resource class and always composes
    // `{**meta, "attachments": ...}`. Returning a bare Results.Json for these four
    // meant retrieve_attachments=true was silently ignored for exactly them, so
    // cxb's EntryRenderer (which reads `entry.attachments` for every type) showed
    // an empty Attachments tab on a user no matter what was attached.
    //
    // SYNCHRONOUS, and the callers await BuildAttachmentsAsync into a local first,
    // on purpose: ASP.NET Core's RequestDelegateGenerator (the AOT route-delegate
    // source generator) cannot infer a handler lambda's return type through a
    // `return await <Task<IResult>>`. It emits a bare `Task<>` / `typeof()` into
    // GeneratedRouteBuilderExtensions.g.cs and the build dies on CS7003
    // "Unexpected use of an unbound generic name", with no diagnostic pointing
    // back at this file. Keeping every `return` a direct IResult-typed call is
    // what keeps that generator working.
    private static IResult JsonWithAttachments(string rowJson, JsonObject attachments)
    {
        var node = JsonNode.Parse(rowJson)!.AsObject();
        node["attachments"] = attachments;
        return Results.Content(node.ToJsonString(DmartJsonContext.Default.Options), "application/json");
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
        // SerializeToNode skips the string encode→parse round-trip that we
        // used to run here (Serialize + JsonNode.Parse) — same output, one pass.
        var node = JsonSerializer.SerializeToNode(entry, DmartJsonContext.Default.Entry)!.AsObject();

        // Remove fields that Python's Meta.model_dump() doesn't include.
        // These live on the DB row / Locator, not on the Meta model.
        node.Remove("query_policies");
        node.Remove("space_name");
        node.Remove("subpath");
        node.Remove("resource_type");

        // Python parity (and parity with QueryService.EntryMapper.ToRecord):
        // relationships is always present in the response, defaulting to an
        // empty array. The Entry record's nullable List + the global
        // WhenWritingNull policy would drop the key, and the
        // JsonStripEmptiesMiddleware exempts "relationships" so the [] we
        // materialize here survives all the way to the wire. Clients can
        // branch on length instead of needing a "is the key even there"
        // probe.
        if (node["relationships"] is null) node["relationships"] = new JsonArray();

        // Strip payload.body if not requested.
        if (!includePayloadBody && node["payload"] is JsonObject payload)
            payload.Remove("body");

        return node;
    }
}
