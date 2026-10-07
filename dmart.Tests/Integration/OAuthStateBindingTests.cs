using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Login-CSRF on the social-SSO web flow. The Google/Facebook GET callbacks
// used to exchange any `code` they were handed with no `state` binding, so a
// victim could be navigated to the callback carrying an attacker's code and be
// silently logged into the attacker's account. The flow now starts at
// /user/{provider}/login, which mints a nonce into a short-lived cookie, and
// the callback refuses a missing or mismatched `state` BEFORE any provider
// call — so none of this touches the network.
public sealed class OAuthStateBindingTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OAuthStateBindingTests(DmartFactory factory) => _factory = factory;

    private WebApplicationFactory<Program> Configured() =>
        _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dmart:GoogleClientId"] = "test-client-id",
                ["Dmart:GoogleOauthCallback"] = "http://localhost/user/google/callback",
            })));

    [FactIfPg]
    public async Task Callback_Without_State_Is_Refused_Before_Any_Exchange()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/user/google/callback?code=attacker-code");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Error!.Message.ShouldContain("state");
    }

    [FactIfPg]
    public async Task Login_Start_Mints_State_And_Callback_Refuses_A_Mismatch()
    {
        using var host = Configured();
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await client.GetAsync("/user/google/login");
        start.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = start.Headers.Location!.ToString();
        location.ShouldStartWith("https://accounts.google.com/o/oauth2/v2/auth");
        location.ShouldContain("client_id=test-client-id");
        location.ShouldContain("state=");
        start.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeTrue();
        cookies!.Any(c => c.StartsWith("oauth_state=", System.StringComparison.Ordinal)).ShouldBeTrue();

        // The cookie now rides along automatically; a callback carrying a state
        // that does NOT match it is rejected — the attacker cannot know the
        // victim's nonce, and the check runs before the provider is contacted.
        var resp = await client.GetAsync("/user/google/callback?code=attacker-code&state=not-the-nonce");
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Error!.Message.ShouldContain("state");
    }
}
