using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Mirrors dmart's pytests/test_status.py::test_sanity — root must be reachable.
public class StatusTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;

    public StatusTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task Root_Returns_200_With_Greeting()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/");
        resp.IsSuccessStatusCode.ShouldBeTrue();
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldBe("dmart-csharp");
    }
}
