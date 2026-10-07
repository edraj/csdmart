using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Dmart.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The MCP surface (POST/GET/DELETE /mcp + the OAuth 2.1 authorization server
// and its discovery documents) is mapped only when DmartSettings.EnableMcp is
// true. This pins both states:
//   - enabled: discovery resolves (200), /mcp exists but demands auth (401);
//   - disabled: every route is ABSENT — an unmapped path becomes the canonical
//     INVALID_ROUTE/422, not a 200 or an auth challenge.
// The shared DmartFactory sets EnableMcp=true, so it stands in for the enabled
// case; the disabled case gets a dedicated factory that leaves it unset (the
// production default).
public sealed class McpEnableGateTests : IClassFixture<DmartFactory>
{
    private const string AsMetadata = "/.well-known/oauth-authorization-server";
    private readonly DmartFactory _enabled;
    public McpEnableGateTests(DmartFactory enabled) => _enabled = enabled;

    [Fact]
    public void EnableMcp_Defaults_Off()
        => new DmartSettings().EnableMcp.ShouldBeFalse(
            "MCP must ship off by default — a deployment opts in with ENABLE_MCP=true");

    [Fact]
    public async Task When_Enabled_Oauth_Discovery_Resolves_And_Mcp_Requires_Auth()
    {
        var client = _enabled.CreateClient();

        var discovery = await client.GetAsync(AsMetadata);
        discovery.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await discovery.Content.ReadAsStringAsync()).ShouldContain("registration_endpoint");

        // Route exists but is JWT-gated, so an unauthenticated call is rejected
        // with 401 — distinctly NOT the 422 an absent route would give.
        var mcp = await client.PostAsync("/mcp",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        mcp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task When_Disabled_All_Mcp_And_Oauth_Routes_Are_Absent()
    {
        using var disabled = new McpDisabledFactory();
        var client = disabled.CreateClient();

        // Unmapped routes fall through to the INVALID_ROUTE rewrite (HTTP 422),
        // proving the endpoints were never registered — not merely refused.
        foreach (var path in new[]
                 {
                     AsMetadata,
                     "/.well-known/oauth-protected-resource",
                 })
        {
            var resp = await client.GetAsync(path);
            resp.StatusCode.ShouldBe((HttpStatusCode)422,
                $"{path} must be absent when EnableMcp=false");
        }

        var register = await client.PostAsync("/oauth/register",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        register.StatusCode.ShouldBe((HttpStatusCode)422, "/oauth/register must be absent");

        var mcp = await client.PostAsync("/mcp",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        mcp.StatusCode.ShouldBe((HttpStatusCode)422, "/mcp must be absent when EnableMcp=false");
    }

    // Standard test host with MCP left at its production default (off). Mirrors
    // the self-contained factory shape used by AuthRateLimitTests so the
    // driver-gating (PgConn/SQLite null-out) still applies.
    private sealed class McpDisabledFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            System.Environment.SetEnvironmentVariable("BACKEND_ENV", "/dev/null");
            builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Error));
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                var overrides = new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Dmart:JwtSecret"] = "test-secret-test-secret-test-secret-32-bytes",
                    ["Dmart:JwtIssuer"] = "dmart",
                    ["Dmart:JwtAudience"] = "dmart",
                    ["Dmart:JwtAccessExpires"] = "300",
                    ["Dmart:AdminPassword"] = "admin-password-123",
                    ["Dmart:AdminEmail"] = "admin@test.local",
                    // The knob under test: left false (production default).
                    ["Dmart:EnableMcp"] = "false",
                };
                DmartFactory.ApplyDriverOverrides(overrides);
                cfg.AddInMemoryCollection(overrides);
            });
        }
    }
}
