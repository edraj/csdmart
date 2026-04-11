using Dmart.Api;
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
                   EntryService svc, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt)) return Results.BadRequest();
                var (subpath, shortname) = RouteParts.SplitSubpathAndShortname(rest);
                if (string.IsNullOrEmpty(shortname)) return Results.BadRequest();
                var entry = await svc.GetAsync(new Locator(rt, space, subpath, shortname), actor: null, ct);
                return entry is null ? Results.NotFound() : Results.Json(entry, DmartJsonContext.Default.Entry);
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
