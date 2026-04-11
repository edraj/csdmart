using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Mirrors dmart's pytests/test_info.py — /info/me, /info/manifest, /info/settings.
public class InfoTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public InfoTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task Manifest_Without_Auth_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/info/manifest");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_Without_Auth_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/info/me");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Settings_Without_Auth_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/info/settings");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
