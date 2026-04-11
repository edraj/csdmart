using Dmart.Config;
using Dmart.Models.Api;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Info;

public static class SettingsHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/settings", (IOptions<DmartSettings> opts) => Response.Ok(attributes: new()
        {
            ["default_language"] = opts.Value.DefaultLanguage,
            ["spaces_root"] = opts.Value.SpacesRoot,
        }));
}
