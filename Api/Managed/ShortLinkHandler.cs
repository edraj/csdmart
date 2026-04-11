using Dmart.Services;

namespace Dmart.Api.Managed;

public static class ShortLinkHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/s/{token}", async (string token, ShortLinkService svc, CancellationToken ct) =>
        {
            var url = await svc.ResolveAsync(token, ct);
            return url is null ? Results.NotFound() : Results.Redirect(url);
        });
}
