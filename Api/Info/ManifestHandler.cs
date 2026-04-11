using Dmart.Models.Api;

namespace Dmart.Api.Info;

public static class ManifestHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/manifest", () => Response.Ok(attributes: new()
        {
            ["name"] = "dmart",
            ["version"] = "0.1.0",
            ["api"] = "v1",
        }));
}
