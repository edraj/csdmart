using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Api.Qr;

public static class GenerateHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/generate/{resource_type}/{space}/{subpath}/{shortname}",
            async (string resource_type, string space, string subpath, string shortname,
                   QrService qr, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt)) return Results.BadRequest();
                var bytes = await qr.GenerateAsync(new Locator(rt, space, subpath, shortname), ct);
                return Results.File(bytes, "image/png");
            });
}
