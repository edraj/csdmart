using Dmart.Models.Api;

namespace Dmart.Api.Info;

public static class MeHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/me", (HttpContext http) => Response.Ok(attributes: new()
        {
            ["shortname"] = http.User.Identity?.Name ?? "anonymous",
            ["authenticated"] = http.User.Identity?.IsAuthenticated ?? false,
        }));
}
