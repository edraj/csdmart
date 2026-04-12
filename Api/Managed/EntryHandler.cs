using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

public static class EntryHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Catchall {**rest} captures multi-segment subpaths AND the trailing shortname.
        // Mirrors dmart Python's `/entry/{resource_type}/{space}/{subpath:path}/{shortname}`.
        // Python's db.load() dispatches to different tables based on class_type:
        //   Space → spaces table, User → users table, Role → roles, Permission → permissions
        //   everything else → entries table
        g.MapGet("/entry/{resource_type}/{space}/{**rest}",
            async (string resource_type, string space, string rest,
                   EntryService svc,
                   DataAdapters.Sql.SpaceRepository spaces,
                   DataAdapters.Sql.UserRepository users,
                   DataAdapters.Sql.AccessRepository access,
                   HttpContext http, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Results.BadRequest();
                var (subpath, shortname) = RouteParts.SplitSubpathAndShortname(rest);
                if (string.IsNullOrEmpty(shortname)) return Results.BadRequest();

                // Route to the correct table based on resource_type.
                object? result = rt switch
                {
                    ResourceType.Space => await spaces.GetAsync(shortname, ct),
                    ResourceType.User => await users.GetByShortnameAsync(shortname, ct),
                    ResourceType.Role => await access.GetRoleAsync(shortname, ct),
                    ResourceType.Permission => await access.GetPermissionAsync(shortname, ct),
                    _ => await svc.GetAsync(new Locator(rt, space, subpath, shortname),
                             http.User.Identity?.Name, ct),
                };

                if (result is null) return Results.NotFound();

                // Serialize through the appropriate source-gen type info.
                return result switch
                {
                    Models.Core.Space s => Results.Json(s, DmartJsonContext.Default.Space),
                    Models.Core.User u => Results.Json(u, DmartJsonContext.Default.User),
                    Models.Core.Role r => Results.Json(r, DmartJsonContext.Default.Role),
                    Models.Core.Permission p => Results.Json(p, DmartJsonContext.Default.Permission),
                    Entry e => Results.Json(e, DmartJsonContext.Default.Entry),
                    _ => Results.NotFound(),
                };
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
