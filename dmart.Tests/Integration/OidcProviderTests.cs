using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Tests.Infrastructure;
using Dmart.Utils;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// dmart as an OpenID Connect provider (docs/oidc-provider.md), driven the way a
// relying party drives it: a browser through /oidc/authorize, the RP's back
// channel to /oidc/token and /oidc/userinfo. ID tokens are verified
// independently, from the published JWKS.
public sealed partial class OidcProviderTests(OidcProviderTests.Fixture fx) : IClassFixture<OidcProviderTests.Fixture>
{
    private const string Issuer = "http://localhost";
    private const string Password = "Oidc12345";
    private const string RpSecret = "rp-secret-0123456789";
    private const string RpCallback = "https://rp.test/cb";
    private const string SpaCallback = "http://127.0.0.1:9999/cb";

    public sealed class Fixture : IAsyncLifetime
    {
        private readonly DmartFactory _factory = new();
        private readonly string _dir = Directory.CreateTempSubdirectory("oidc-").FullName;
        public WebApplicationFactory<Program> Host { get; private set; } = null!;
        private static readonly string Suffix = Guid.NewGuid().ToString("N")[..6];
        public string Alice { get; } = "oidcalice_" + Suffix;   // holds `matrix`
        public string Bob { get; } = "oidcbob_" + Suffix;       // does not
        public string AliceMailbox => $"{Alice}@hosted.test";

        public async Task InitializeAsync()
        {
            await ((IAsyncLifetime)_factory).InitializeAsync();
            if (!DmartFactory.HasPg) return;
            var clients = Path.Combine(_dir, "clients.json");
            await File.WriteAllTextAsync(clients, $$"""
                {"clients": [
                  {"client_id": "rp", "client_secret": "{{RpSecret}}", "name": "Test RP",
                   "redirect_uris": ["{{RpCallback}}"], "services": ["matrix"]},
                  {"client_id": "spa", "name": "Public app", "redirect_uris": ["{{SpaCallback}}"]}
                ]}
                """);
            Host = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Dmart:OidcIssuer"] = Issuer,
                    ["Dmart:OidcSigningKeyFile"] = Path.Combine(_dir, "signing.pem"),
                    ["Dmart:OidcClientsFile"] = clients,
                })));
            _ = Host.CreateClient();

            var users = Host.Services.GetRequiredService<UserRepository>();
            var hash = await Host.Services.GetRequiredService<PasswordHasher>().HashAsync(Password);
            foreach (var (name, services) in new[] { (Alice, new List<string> { "matrix" }), (Bob, new List<string>()) })
                await users.UpsertAsync(new User
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = name, SpaceName = "management", Subpath = "/users",
                    OwnerShortname = name, IsActive = true, Password = hash, Type = UserType.Web, Language = Language.En,
                    Displayname = new Translation(En: "Alice Example"), Groups = ["staff"], Roles = new(),
                    Mailbox = name == Alice ? AliceMailbox : null, Services = services,
                    CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
                });
        }

        public HttpClient Browser() => Host.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri(Issuer),
        });

        public async Task DisposeAsync()
        {
            if (Host is not null)
            {
                foreach (var u in new[] { Alice, Bob }) await TestUserCleanup.DeleteUserAndOwnedAsync(Host.Services, u);
                await Host.DisposeAsync();
            }
            await ((IAsyncLifetime)_factory).DisposeAsync();
            Directory.Delete(_dir, recursive: true);
        }
    }

    // ---- a relying party, in miniature ----

    private static string AuthorizeUrl(string client, string redirect, string state, string nonce,
        string? challenge = null, string? extra = null)
        => $"/oidc/authorize?response_type=code&client_id={client}&redirect_uri={Uri.EscapeDataString(redirect)}"
           + $"&scope=openid%20profile%20email%20groups&state={state}&nonce={nonce}"
           + (challenge is null ? "" : $"&code_challenge={challenge}&code_challenge_method=S256") + (extra ?? "");

    // Submits the sign-in form a GET to /oidc/authorize rendered.
    private static async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string authorizeUrl, string user, string password)
    {
        var form = await browser.GetAsync(authorizeUrl);
        form.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await form.Content.ReadAsStringAsync();
        var fields = HiddenField().Matches(html).ToDictionary(m => m.Groups[1].Value, m => WebUtility.HtmlDecode(m.Groups[2].Value));
        fields["username"] = user;
        fields["password"] = password;
        return await browser.PostAsync("/oidc/authorize", new FormUrlEncodedContent(fields));
    }

    [GeneratedRegex("<input type=\"hidden\" name=\"([a-z_]+)\" value=\"([^\"]*)\">")]
    private static partial Regex HiddenField();

    private static Dictionary<string, string> Query(Uri location)
        => location.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    private async Task<JsonElement> TokenAsync(string code, string redirect, string client, string? secret, string? verifier = null)
    {
        using var backChannel = fx.Host.CreateClient();
        var body = new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect };
        if (verifier is not null) body["code_verifier"] = verifier;
        if (secret is null) body["client_id"] = client;   // a public client names itself in the body
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oidc/token") { Content = new FormUrlEncodedContent(body) };
        if (secret is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{client}:{secret}")));
        var response = await backChannel.SendAsync(request);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        if (!response.IsSuccessStatusCode) return json;   // carries `error`
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        return json;
    }

    // Verifies a JWT against the published JWKS, the way an RP library does.
    private async Task<JsonElement> VerifiedClaimsAsync(string jwt)
    {
        using var http = fx.Host.CreateClient();
        var jwks = JsonDocument.Parse(await http.GetStringAsync("/oidc/jwks")).RootElement.GetProperty("keys")[0];
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = B64(jwks.GetProperty("n").GetString()!), Exponent = B64(jwks.GetProperty("e").GetString()!) });
        var parts = jwt.Split('.');
        var header = JsonDocument.Parse(B64(parts[0])).RootElement;
        header.GetProperty("alg").GetString().ShouldBe("RS256");
        header.GetProperty("kid").GetString().ShouldBe(jwks.GetProperty("kid").GetString());
        rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), B64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .ShouldBeTrue("the ID token's signature must verify against the JWKS");
        return JsonDocument.Parse(B64(parts[1])).RootElement.Clone();
    }

    private static byte[] B64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String((s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s });
    }

    private static string Challenge(string verifier)
        => Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ---- the tests ----

    [FactIfPg]
    public async Task Discovery_Names_The_Endpoints_Under_The_Issuer()
    {
        using var http = fx.Host.CreateClient();
        var d = JsonDocument.Parse(await http.GetStringAsync("/.well-known/openid-configuration")).RootElement;
        d.GetProperty("issuer").GetString().ShouldBe(Issuer);
        d.GetProperty("authorization_endpoint").GetString().ShouldBe(Issuer + "/oidc/authorize");
        d.GetProperty("token_endpoint").GetString().ShouldBe(Issuer + "/oidc/token");
        d.GetProperty("jwks_uri").GetString().ShouldBe(Issuer + "/oidc/jwks");
        d.GetProperty("id_token_signing_alg_values_supported").EnumerateArray().Select(x => x.GetString()).ShouldBe(new[] { "RS256" });
    }

    [FactIfPg]
    public async Task A_Confidential_Client_Gets_A_Verifiable_Id_Token_Userinfo_And_Single_Sign_On()
    {
        using var browser = fx.Browser();
        var signIn = await SignInAsync(browser, AuthorizeUrl("rp", RpCallback, "st4te", "n0nce"), fx.AliceMailbox, Password);
        signIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var back = Query(signIn.Headers.Location!);
        signIn.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(RpCallback);
        back["state"].ShouldBe("st4te");
        back["iss"].ShouldBe(Issuer);

        var tokens = await TokenAsync(back["code"], RpCallback, "rp", RpSecret);
        var id = await VerifiedClaimsAsync(tokens.GetProperty("id_token").GetString()!);
        id.GetProperty("iss").GetString().ShouldBe(Issuer);
        id.GetProperty("aud").GetString().ShouldBe("rp");
        id.GetProperty("nonce").GetString().ShouldBe("n0nce");
        id.GetProperty("preferred_username").GetString().ShouldBe(fx.Alice);
        id.GetProperty("email").GetString().ShouldBe(fx.AliceMailbox);
        id.GetProperty("email_verified").GetBoolean().ShouldBeTrue();
        id.GetProperty("groups").EnumerateArray().Select(g => g.GetString()).ShouldBe(new[] { "staff" });
        var sub = id.GetProperty("sub").GetString();
        (await fx.Host.Services.GetRequiredService<UserRepository>().GetShortnameByUuidAsync(sub!)).ShouldBe(fx.Alice);

        // userinfo answers the access token with the same subject.
        using var rp = fx.Host.CreateClient();
        rp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        var info = JsonDocument.Parse(await rp.GetStringAsync("/oidc/userinfo")).RootElement;
        info.GetProperty("sub").GetString().ShouldBe(sub);
        info.GetProperty("email").GetString().ShouldBe(fx.AliceMailbox);
        // ...and that token opens nothing else: it is not a dmart session.
        (await rp.GetAsync("/user/profile")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A code is spent once.
        (await TokenAsync(back["code"], RpCallback, "rp", RpSecret)).GetProperty("error").GetString().ShouldBe("invalid_grant");

        // The browser is now signed in to dmart: the next sign-in skips the form...
        var sso = await browser.GetAsync(AuthorizeUrl("rp", RpCallback, "again", "n2"));
        sso.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Query(sso.Headers.Location!).ShouldContainKey("code");
        // ...unless the client asks for a fresh sign-in.
        (await browser.GetAsync(AuthorizeUrl("rp", RpCallback, "fresh", "n3", extra: "&prompt=login")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [FactIfPg]
    public async Task A_User_Without_The_Clients_Service_Is_Refused()
    {
        using var browser = fx.Browser();
        var signIn = await SignInAsync(browser, AuthorizeUrl("rp", RpCallback, "s", "n"), fx.Bob, Password);
        signIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Query(signIn.Headers.Location!)["error"].ShouldBe("access_denied");
    }

    [FactIfPg]
    public async Task A_Public_Client_Must_Use_Pkce_And_Prove_The_Verifier()
    {
        using var browser = fx.Browser();
        var noPkce = await browser.GetAsync(AuthorizeUrl("spa", SpaCallback, "s", "n"));
        noPkce.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Query(noPkce.Headers.Location!)["error"].ShouldBe("invalid_request");

        const string verifier = "a-verifier-that-is-long-enough-to-satisfy-rfc-7636-0123456789";
        var signIn = await SignInAsync(browser, AuthorizeUrl("spa", SpaCallback, "s", "n", Challenge(verifier)), fx.Alice, Password);
        var code = Query(signIn.Headers.Location!)["code"];
        (await TokenAsync(code, SpaCallback, "spa", null, "not-the-verifier-not-the-verifier-not-the-verifier"))
            .GetProperty("error").GetString().ShouldBe("invalid_grant");

        // The wrong verifier spent the code; a fresh one with the right verifier works.
        var again = await browser.GetAsync(AuthorizeUrl("spa", SpaCallback, "s", "n", Challenge(verifier)));
        var tokens = await TokenAsync(Query(again.Headers.Location!)["code"], SpaCallback, "spa", null, verifier);
        (await VerifiedClaimsAsync(tokens.GetProperty("id_token").GetString()!)).GetProperty("aud").GetString().ShouldBe("spa");
    }

    [FactIfPg]
    public async Task Requests_The_Provider_Cannot_Trust_Are_Not_Redirected()
    {
        using var browser = fx.Browser();
        // An unregistered return address gets a page, never a redirect with a code or an error.
        var bad = await browser.GetAsync(AuthorizeUrl("rp", "https://evil.test/cb", "s", "n"));
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        bad.Headers.Location.ShouldBeNull();
        (await browser.GetAsync(AuthorizeUrl("nobody", RpCallback, "s", "n"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // No session and prompt=none: the client hears login_required.
        var silent = await browser.GetAsync(AuthorizeUrl("rp", RpCallback, "s", "n", extra: "&prompt=none"));
        Query(silent.Headers.Location!)["error"].ShouldBe("login_required");
    }

    [FactIfPg]
    public async Task The_Sign_In_Form_Refuses_A_Post_It_Did_Not_Render_And_A_Wrong_Secret()
    {
        // Login CSRF: credentials posted from elsewhere carry no form cookie.
        using var browser = fx.Browser();
        var forged = await browser.PostAsync("/oidc/authorize", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = "rp", ["redirect_uri"] = RpCallback, ["scope"] = "openid",
            ["state"] = "s", ["form_token"] = "made-up", ["username"] = fx.Alice, ["password"] = Password,
        }));
        forged.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await forged.Content.ReadAsStringAsync()).ShouldContain("The form expired");

        var signIn = await SignInAsync(browser, AuthorizeUrl("rp", RpCallback, "s", "n"), fx.Alice, Password);
        (await TokenAsync(Query(signIn.Headers.Location!)["code"], RpCallback, "rp", "wrong-secret-0123456789"))
            .GetProperty("error").GetString().ShouldBe("invalid_client");
    }
}
