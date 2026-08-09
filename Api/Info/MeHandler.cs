using Dmart.Models.Api;

namespace Dmart.Api.Info;

public static class MeHandler
{
    // GET /info/me — "who am I", for a caller that already holds a token.
    //
    // Authenticated, NOT anonymous. The earlier version was AllowAnonymous so a
    // browser could probe session state without a 401 in the console; that made
    // it the one unauthenticated route inside a group now gated to super_admin,
    // which is a carve-out that ages badly. The 401 IS the answer now: a caller
    // that gets one is anonymous, and clients that want to avoid the round-trip
    // entirely can check for a stored token first (both SPAs in this repo do).
    //
    // AllowAuthenticated exempts it from the group's GlobalAdminFilter — a
    // caller asking for their own identity obviously needs no admin rights.
    // Everything else under /info stays super_admin-only; the filter fails
    // closed, so this marker is the whole of the exemption.
    //
    // Python parity, exactly: api/info/router.py:51 is JWTBearer-gated and
    // returns {"shortname": <caller>}. The old `authenticated` flag is gone
    // with the anonymous branch that gave it meaning — it could only ever read
    // true here, and upstream never had it.
    public static void Map(RouteGroupBuilder g) =>
        g.MapGet("/me", (HttpContext http) => Response.Ok(attributes: new()
        {
            ["shortname"] = http.ActorOrAnonymous(),
        }))
        .WithMetadata(new AllowAuthenticatedAttribute());
}
