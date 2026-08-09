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

    // /info/me is authenticated, not anonymous: the 401 IS how a caller learns
    // it has no session. It used to answer 200 with authenticated:false, which
    // made it the one unauthenticated route inside a group that is otherwise
    // super_admin-only.
    [Fact]
    public async Task Me_Without_Auth_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/info/me");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The load-bearing half of the exemption: a role-less user — the privilege
    // level of a fresh self-registration — must get their own identity back,
    // while the sibling routes tested below still refuse them. That pair is
    // what proves AllowAuthenticated narrows the group's rule for exactly one
    // route and nothing else.
    [FactIfPg]
    public async Task Me_As_NonAdmin_Returns_Own_Shortname()
    {
        var user = await _factory.CreateLoggedInUserAsync(roles: new());
        try
        {
            var resp = await user.Client.GetAsync("/info/me");
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("status").GetString().ShouldBe("success");
            var attrs = doc.RootElement.GetProperty("attributes");
            attrs.GetProperty("shortname").GetString().ShouldBe(user.Shortname);

            // Python's /info/me returns {shortname} and nothing else
            // (api/info/router.py:51). The old `authenticated` flag went with
            // the anonymous branch that gave it meaning — here it could only
            // ever read true.
            attrs.TryGetProperty("authenticated", out _).ShouldBeFalse(
                "the authenticated flag is meaningless on an authenticated-only route");
        }
        finally { await user.Cleanup(); }
    }

    [Fact]
    public async Task Settings_Without_Auth_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/info/settings");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Root_Returns_Server_Identifier()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("dmart");
    }

    [Fact]
    public async Task CXB_Config_Json_Returns_Valid_Json_Or_404()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/cxb/config.json");
        // Either serves config.json or 404 (no CXB built)
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            // Should be valid JSON with at least one key
            var doc = JsonDocument.Parse(body);
            doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        }
        else
        {
            resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [FactIfPg]
    public async Task Settings_With_Auth_Returns_Listening_Port()
    {
        // Per-test user with super_admin role — see DmartFactory.CreateLoggedInUserAsync.
        var (client, _, _, _) = await _factory.CreateLoggedInUserAsync();

        var resp = await client.GetAsync("/info/settings");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        // Should have records with settings attributes
        doc.RootElement.GetProperty("status").GetString().ShouldBe("success");
    }

    [FactIfPg]
    public async Task Manifest_As_NonAdmin_Returns_401()
    {
        // Authenticated but role-less — the privilege level of a fresh
        // self-registration. The whole /info group is gated to super_admin
        // (GlobalAdminFilter), so mere authentication isn't enough.
        var user = await _factory.CreateLoggedInUserAsync(roles: new());
        try
        {
            var resp = await user.Client.GetAsync("/info/manifest");
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally { await user.Cleanup(); }
    }

    [FactIfPg]
    public async Task Settings_As_NonAdmin_Returns_401()
    {
        var user = await _factory.CreateLoggedInUserAsync(roles: new());
        try
        {
            var resp = await user.Client.GetAsync("/info/settings");
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally { await user.Cleanup(); }
    }
}
