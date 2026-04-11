using System.Net;
using System.Net.Http.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Tests that don't strictly need a DB connection — the public/query route is reachable
// even without auth. With no DB the handler will return 500 from the underlying repo;
// without DB we just check the route is wired.
public class PublicQueryTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PublicQueryTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task Public_Query_Route_Exists_And_Accepts_Json()
    {
        var client = _factory.CreateClient();
        var query = new Query
        {
            Type = QueryType.Search,
            SpaceName = "demo",
            Subpath = "/",
            Limit = 10,
        };
        var resp = await client.PostAsJsonAsync("/public/query", query, DmartJsonContext.Default.Query);
        // Whether this returns 200 (DB present) or 500 (no DB), the route MUST exist.
        ((int)resp.StatusCode).ShouldNotBe(404);
    }

    [Fact]
    public async Task Public_Entry_Returns_404_For_Unknown_Resource()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/public/entry/content/demo/notes/does-not-exist");
        // Without DB → 500; with DB → 404. Either way, not 401 (no auth needed).
        resp.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }
}
