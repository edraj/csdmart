using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Cookie-authenticated requests are accepted only when the browser's own
// Fetch Metadata (or an explicit X-Requested-With) shows they are not a
// cross-site navigation — otherwise an attacker page could ride the victim's
// auth_token cookie. CsrfProtectCookieAuth is on by default, and this gate had
// no test in either direction: nothing in the suite ever authenticated an
// ordinary route with a VALID cookie.
public sealed class CookieCsrfGateTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public CookieCsrfGateTests(DmartFactory factory) => _factory = factory;

    // A client holding ONLY the auth_token cookie (no Authorization header).
    private async Task<HttpClient> CookieOnlyClientAsync()
    {
        var creds = await _factory.CreateTestUserAsync();
        var client = _factory.CreateClient();          // HandleCookies is on by default
        var login = await client.PostAsync("/user/login", new StringContent(
            $"{{\"shortname\":\"{creds.Shortname}\",\"password\":\"{creds.Password}\"}}",
            System.Text.Encoding.UTF8, "application/json"));
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        return client;
    }

    [FactIfPg]
    public async Task Cross_Site_Cookie_Request_Is_Refused()
    {
        var client = await CookieOnlyClientAsync();
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
        (await client.GetAsync("/user/profile")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
            "a cookie alone must not authenticate a cross-site request");
    }

    [FactIfPg]
    public async Task Same_Origin_Cookie_Request_Is_Accepted()
    {
        var client = await CookieOnlyClientAsync();
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        (await client.GetAsync("/user/profile")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [FactIfPg]
    public async Task XRequestedWith_Marks_A_Non_Navigation_Request_As_Safe()
    {
        var client = await CookieOnlyClientAsync();
        // No Fetch Metadata at all (older client) — the explicit header is the
        // documented alternative signal.
        client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        (await client.GetAsync("/user/profile")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
