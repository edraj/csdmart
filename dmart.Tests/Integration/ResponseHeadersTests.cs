using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// End-to-end HTTP tests for the CORS + security-header middleware. Each test
// boots a fresh WebApplicationFactory with specific Dmart:AllowedCorsOrigins
// values so we can verify every branch of Python's set_middleware_response_headers:
//
//   - empty allowlist  → fallback to same-host origin, origin not reflected
//                        unless it matches the canonical host:port
//   - non-empty match  → reflected origin + Allow-Credentials
//   - non-empty miss   → NO Access-Control-Allow-Origin header at all
//   - OPTIONS preflight → 204 with all CORS headers set
//
// Static CORS + security headers are asserted on every case to catch regressions.
public class ResponseHeadersTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ResponseHeadersTests(DmartFactory factory) => _factory = factory;

    // Helper — rebuild the factory with an override for AllowedCorsOrigins. The
    // base DmartFactory.ConfigureWebHost already seeds the common settings, so
    // we just add a second AddInMemoryCollection on top to override one key.
    private HttpClient ClientWithAllowlist(string allowedCorsOrigins)
        => _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dmart:AllowedCorsOrigins"] = allowedCorsOrigins,
                ["Dmart:ListeningHost"] = "127.0.0.1",
                ["Dmart:ListeningPort"] = "5099",
            });
        })).CreateClient();

    // ==================== 1. security + static CORS headers ====================

    [Fact]
    public async Task Root_Response_Has_Security_And_Static_Cors_Headers()
    {
        var client = ClientWithAllowlist("");
        var resp = await client.GetAsync("/");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resp.Headers.Contains("X-Content-Type-Options").ShouldBeTrue();
        resp.Headers.Contains("X-Frame-Options").ShouldBeTrue();
        resp.Headers.Contains("Referrer-Policy").ShouldBeTrue();
        resp.Headers.Contains("Permissions-Policy").ShouldBeTrue();
        // HSTS is only sent over HTTPS (RFC 6797). Test host uses HTTP.
        // resp.Headers.Contains("Strict-Transport-Security").ShouldBeTrue();
        resp.Headers.Contains("Access-Control-Allow-Methods").ShouldBeTrue();
        resp.Headers.Contains("Access-Control-Allow-Headers").ShouldBeTrue();
        resp.Headers.Contains("Access-Control-Max-Age").ShouldBeTrue();
        resp.Headers.Contains("x-server-time").ShouldBeTrue();
    }

    // ==================== 2. empty allowlist — fallback ====================

    [Fact]
    public async Task Empty_Allowlist_Falls_Back_To_SameHost_Origin()
    {
        var client = ClientWithAllowlist("");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Origin", "https://stranger.example.com");
        var resp = await client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        // The fallback writes the canonical same-host form, never the stranger.
        resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var values).ShouldBeTrue();
        var allowed = string.Join(",", values!);
        allowed.ShouldBe("http://127.0.0.1:5099");
        allowed.ShouldNotContain("stranger.example.com");
    }

    // ==================== 3. allowlist match ====================

    [Fact]
    public async Task Allowlisted_Origin_Is_Reflected_With_Credentials()
    {
        var client = ClientWithAllowlist("http://localhost:3000,https://app.example.com");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Origin", "https://app.example.com");
        var resp = await client.SendAsync(req);

        resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins).ShouldBeTrue();
        string.Join(",", origins!).ShouldBe("https://app.example.com");
        resp.Headers.TryGetValues("Access-Control-Allow-Credentials", out var creds).ShouldBeTrue();
        string.Join(",", creds!).ShouldBe("true");
    }

    [Fact]
    public async Task Allowlist_Handles_Leading_Whitespace_On_Csv_Entries()
    {
        var client = ClientWithAllowlist(" http://a.com ,  http://b.com ");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Origin", "http://b.com");
        var resp = await client.SendAsync(req);

        resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins).ShouldBeTrue();
        string.Join(",", origins!).ShouldBe("http://b.com");
    }

    // ==================== 4. allowlist miss ====================

    [Fact]
    public async Task NonAllowlisted_Origin_Gets_No_Cors_Origin_Header()
    {
        var client = ClientWithAllowlist("https://app.example.com");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Origin", "https://evil.example.com");
        var resp = await client.SendAsync(req);

        // Python emits NO Access-Control-Allow-Origin at all when allowlist
        // is non-empty and origin doesn't match — so the browser blocks.
        resp.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
        resp.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    // ==================== 5. OPTIONS preflight short-circuit ====================

    [Fact]
    public async Task Options_Preflight_Returns_204_With_Cors_Headers()
    {
        var client = ClientWithAllowlist("https://app.example.com");
        var req = new HttpRequestMessage(HttpMethod.Options, "/managed/request");
        req.Headers.Add("Origin", "https://app.example.com");
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
        var resp = await client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        resp.Headers.TryGetValues("Access-Control-Allow-Methods", out var methods).ShouldBeTrue();
        string.Join(",", methods!).ShouldContain("POST");
        resp.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins).ShouldBeTrue();
        string.Join(",", origins!).ShouldBe("https://app.example.com");
    }

    [Fact]
    public async Task Options_Preflight_Does_Not_Require_Auth()
    {
        // Preflight is unauthenticated by design — the middleware short-circuits
        // before UseAuthentication, so no JWT is required even on routes that
        // otherwise do. Using /managed/request which normally requires
        // authorization.
        var client = ClientWithAllowlist("");
        var req = new HttpRequestMessage(HttpMethod.Options, "/managed/request");
        req.Headers.Add("Origin", "http://127.0.0.1:5099");
        var resp = await client.SendAsync(req);

        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        resp.Headers.Contains("Access-Control-Allow-Origin").ShouldBeTrue();
    }

    // ==================== CSP: attachment rendering ====================
    //
    // Attachments are not rendered by pointing an <img>/<audio> at the payload
    // endpoint. The SPA fetches the bytes — so it can send the bearer token,
    // which a subresource load cannot carry — wraps them with
    // URL.createObjectURL and renders the resulting blob: URL.
    //
    // If the CSP omits blob:, the fetch still returns 200 and the blob is still
    // built; only the paint is refused. The failure is silent: images degrade to
    // their alt text and <audio> reports "Media load rejected by URL safety
    // check" with a 0:00 duration, while the network tab shows two clean 200s.
    // It reads as a broken or unauthorized download and is neither. These tests
    // exist because that is precisely the regression a human does not catch by
    // looking at the page.

    // Read the policy the middleware actually serves, rather than a copy in the
    // test that can drift away from it.
    private static string Csp()
    {
        var field = typeof(Dmart.Middleware.ResponseHeadersMiddleware).GetField(
            "ContentSecurityPolicy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field.ShouldNotBeNull("the ContentSecurityPolicy const was renamed or removed");
        return (string)field!.GetRawConstantValue()!;
    }

    // "img-src 'self' data: blob:" -> "'self' data: blob:"; "" when absent.
    private static string Directive(string name)
    {
        var match = Csp().Split(';')
            .Select(part => part.Trim())
            .FirstOrDefault(part => part == name || part.StartsWith(name + " ", StringComparison.Ordinal));
        return match is null ? "" : match.Substring(name.Length).Trim();
    }

    [Fact]
    public void Csp_Allows_Blob_For_Images()
    {
        Directive("img-src").ShouldContain("blob:",
            customMessage: "without blob: in img-src, attachment images silently render as alt text");
    }

    [Fact]
    public void Csp_Declares_MediaSrc_Allowing_Blob()
    {
        // media-src must be declared explicitly: with no media-src, <audio> and
        // <video> fall back to default-src 'self', which excludes blob: — so
        // adding blob: to img-src alone fixes images and leaves audio broken.
        Directive("media-src").ShouldContain("blob:",
            customMessage: "without media-src blob:, <audio> reports "
                + "\"Media load rejected by URL safety check\" and shows a 0:00 duration");
    }

    // blob: is safe for img/media because a blob: URL is an opaque handle
    // readable only by the document that minted it — it names no remote host and
    // widens no network reach. It is NOT safe as a script source.
    [Fact]
    public void Csp_Does_Not_Allow_Blob_For_Scripts_Or_Default()
    {
        Directive("script-src").ShouldNotContain("blob:");
        Directive("default-src").ShouldNotContain("blob:");
    }

    [Fact]
    public void Csp_Keeps_Its_Existing_Hardening()
    {
        // object-src stays 'none': the PDF/SVG branches of Media.svelte use
        // <object>, which is a materially larger XSS surface than <img>/<audio>.
        // Those should move to an iframe/viewer rather than loosen this.
        Directive("object-src").ShouldBe("'none'");
        Directive("frame-ancestors").ShouldBe("'none'");
        Directive("base-uri").ShouldBe("'self'");
        Directive("script-src").ShouldBe("'self'");
    }
}
