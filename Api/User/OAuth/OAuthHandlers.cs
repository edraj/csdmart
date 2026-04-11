using Dmart.Models.Api;

namespace Dmart.Api.User.OAuth;

public static class OAuthHandlers
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/google/callback", (string? code) => Response.Fail("not_implemented", "google oauth pending"));
        g.MapGet("/facebook/callback", (string? code) => Response.Fail("not_implemented", "facebook oauth pending"));
        g.MapGet("/apple/callback", (string? code) => Response.Fail("not_implemented", "apple oauth pending"));
    }
}
