using Dmart.Api;
using Dmart.Api.Managed;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Public;

public static class EntryHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/entry/{resource_type}/{space}/{**rest}",
            async (string resource_type, string space, string rest,
                   bool? retrieve_json_payload,
                   bool? retrieve_attachments,
                   EntryService svc, AttachmentRepository attachmentRepo,
                   PermissionService perms,
                   CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt)) return Results.BadRequest();
                var (subpath, shortname) = RouteParts.SplitSubpathAndShortname(rest);
                if (string.IsNullOrEmpty(shortname)) return Results.BadRequest();
                var entry = await svc.GetAsync(new Locator(rt, space, subpath, shortname), actor: null, ct);
                if (entry is null) return Results.NotFound();

                // Shared with the managed route rather than duplicated: this
                // handler used to carry a byte-for-byte copy of the list →
                // per-attachment CanReadAsync → group-by loop, and the only
                // difference was the actor. A world-readable parent still says
                // nothing about its children — comment/json attachments carry
                // their own ACL — and `actor: null` is what makes the gate here
                // the anonymous one, because /public is anonymous by definition
                // (like svc.GetAsync above).
                var attNode = await Dmart.Api.Managed.EntryHandler.BuildAttachmentsAsync(
                    space, subpath, shortname, retrieve_attachments == true,
                    attachmentRepo, perms, actor: null, ct);

                var node = EntryToJsonNode.Convert(entry, retrieve_json_payload == true);
                node["attachments"] = attNode;
                return Results.Content(node.ToJsonString(DmartJsonContext.Default.Options), "application/json");
            });

        g.MapGet("/byuuid/{uuid}", async (string uuid, EntryService svc, CancellationToken ct) =>
        {
            if (!Guid.TryParse(uuid, out var u)) return Results.BadRequest();
            var entry = await svc.GetByUuidAsync(u, actor: null, ct);
            return entry is null ? Results.NotFound() : Results.Json(entry, DmartJsonContext.Default.Entry);
        });

        g.MapGet("/byslug/{slug}", async (string slug, EntryService svc, CancellationToken ct) =>
        {
            var entry = await svc.GetBySlugAsync(slug, actor: null, ct);
            return entry is null ? Results.NotFound() : Results.Json(entry, DmartJsonContext.Default.Entry);
        });
    }
}
