using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The MCP surface (POST/GET/DELETE /mcp + the OAuth 2.1 authorization server
// and its discovery documents) exists only when DmartSettings.EnableMcp is
// true. This pins both states:
//   - enabled: discovery resolves (200), /mcp exists but demands auth (401);
//   - disabled: every route is ABSENT — an unmapped path becomes the canonical
//     INVALID_ROUTE/422, not a 200 or an auth challenge.
// The shared DmartFactory sets EnableMcp=true, so it is the enabled host; the
// disabled host is the same factory with the one key flipped (the pattern
// LegacyLockoutBackfillTests uses), so every suite-wide pin — rate limits,
// RepairLegacyLockoutsOnStart=false, the driver null-outs — is kept.
// The default VALUE of the setting is pinned in Unit/Config/SettingsTests.
public sealed class McpEnableGateTests : IClassFixture<DmartFactory>
{
    private const string AsMetadata = "/.well-known/oauth-authorization-server";
    private readonly DmartFactory _enabled;
    public McpEnableGateTests(DmartFactory enabled) => _enabled = enabled;

    [Fact]
    public async Task When_Enabled_Oauth_Discovery_Resolves_And_Mcp_Requires_Auth()
    {
        var client = _enabled.CreateClient();

        var discovery = await client.GetAsync(AsMetadata);
        discovery.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await discovery.Content.ReadAsStringAsync()).ShouldContain("registration_endpoint");

        // Route exists but is JWT-gated, so an unauthenticated call is rejected
        // with 401 — distinctly NOT the 422 an absent route would give.
        var mcp = await client.PostAsync("/mcp", Json("{}"));
        mcp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_Disabled_All_Mcp_And_Oauth_Routes_Are_Absent()
    {
        using var disabled = _enabled.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dmart:EnableMcp"] = "false",
            })));
        var client = disabled.CreateClient();

        // Unmapped routes fall through to the INVALID_ROUTE rewrite (HTTP 422),
        // proving the endpoints were never registered — not merely refused.
        foreach (var path in new[] { AsMetadata, "/.well-known/oauth-protected-resource" })
        {
            var resp = await client.GetAsync(path);
            resp.StatusCode.ShouldBe((HttpStatusCode)422, $"{path} must be absent when EnableMcp=false");
        }

        (await client.PostAsync("/oauth/register", Json("{}"))).StatusCode
            .ShouldBe((HttpStatusCode)422, "/oauth/register must be absent");
        (await client.PostAsync("/mcp", Json("{}"))).StatusCode
            .ShouldBe((HttpStatusCode)422, "/mcp must be absent when EnableMcp=false");
    }

    private static StringContent Json(string body)
        => new(body, System.Text.Encoding.UTF8, "application/json");
}
