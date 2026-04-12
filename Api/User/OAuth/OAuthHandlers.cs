using Dmart.Models.Api;

namespace Dmart.Api.User.OAuth;

public static class OAuthHandlers
{
    public static void Map(RouteGroupBuilder g)
    {
        // Web OAuth callbacks — Python implements full flows for each.
        // These require OAuth client libraries + provider API credentials.
        g.MapGet("/google/callback", (string? code) => Response.Fail("not_implemented", "google oauth pending"));
        g.MapGet("/facebook/callback", (string? code) => Response.Fail("not_implemented", "facebook oauth pending"));
        g.MapGet("/apple/callback", (string? code) => Response.Fail("not_implemented", "apple oauth pending"));

        // Mobile OAuth — Python validates ID tokens directly from the client.
        // These require provider-specific token validation endpoints:
        //   Google: https://oauth2.googleapis.com/tokeninfo
        //   Facebook: graph.facebook.com/debug_token
        //   Apple: JWKS endpoint for RS256 JWT validation
        g.MapPost("/google/mobile-login", () => Response.Fail("not_implemented", "google mobile login pending"));
        g.MapPost("/facebook/mobile-login", () => Response.Fail("not_implemented", "facebook mobile login pending"));
        g.MapPost("/apple/mobile-login", () => Response.Fail("not_implemented", "apple mobile login pending"));
    }
}
