using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Operator-only routes must refuse an ordinary authenticated user. Each of
// these used to carry nothing beyond RequireAuthorization():
//   - /managed/health/{type}/{space}  returned broken-entry shortnames for ANY
//     space to any logged-in user;
//   - /managed/reload-security-data   let anyone flush the permission + schema
//     caches (a cache-stampede lever);
//   - /send-message/{user}, /broadcast-to-channels, /ws-info let any user forge
//     realtime events, push into another user's socket, and list who is
//     connected and what they watch.
// They are now GlobalAdminFilter-gated. The admin case is asserted too, so the
// gate is proven to be a role floor and not an accidental lock-out.
public sealed class OperatorRouteAuthzTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OperatorRouteAuthzTests(DmartFactory factory) => _factory = factory;

    private static StringContent Json(string body)
        => new(body, Encoding.UTF8, "application/json");

    [FactIfPg]
    public async Task NonAdmin_Is_Refused_On_Every_Operator_Route()
    {
        var user = await _factory.CreateLoggedInUserAsync(roles: new());
        var c = user.Client;

        (await c.GetAsync("/managed/health/all/management")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized, "health check discloses broken-entry shortnames");
        (await c.GetAsync("/managed/reload-security-data")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized, "cache flush is an operator action");
        (await c.GetAsync("/ws-info")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized, "ws-info lists every connected user");
        (await c.PostAsync("/send-message/" + _factory.AdminShortname,
                Json("{\"type\":\"notification\",\"message\":{\"x\":1}}"))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized, "pushing into another user's socket");
        (await c.PostAsync("/broadcast-to-channels",
                Json("{\"type\":\"notification\",\"channels\":[\"a:/:__ALL__:__ALL__:__ALL__\"],\"message\":{\"x\":1}}"))).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized, "forging a realtime event to every subscriber");
    }

    [FactIfPg]
    public async Task Admin_Still_Passes_The_Gate()
    {
        var admin = await _factory.CreateLoggedInUserAsync();   // super_admin by default
        var c = admin.Client;

        (await c.GetAsync("/managed/health/all/management")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.GetAsync("/managed/reload-security-data")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await c.GetAsync("/ws-info")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
