using System;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Dmart.Auth;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// JWT validation at the HTTP layer, negative cases. The validation parameters
// (ValidIssuer/ValidAudience, ClockSkew=0, HS256 signature) and the typed
// challenge bodies had no end-to-end test: nothing presented an expired,
// wrong-audience or re-signed token. Each token here is otherwise well-formed
// (token_use present, known subject) so the single defect is the one under test.
public sealed class JwtNegativeValidationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public JwtNegativeValidationTests(DmartFactory factory) => _factory = factory;

    private string HostSecret() =>
        _factory.Services.GetRequiredService<IOptions<DmartSettings>>().Value.JwtSecret;

    private static string Mint(string secret, string subject, long exp, string aud)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = $$"""
            {"sub":"{{subject}}","iss":"dmart","aud":"{{aud}}","iat":{{now}},"exp":{{exp}},"token_use":"access","data":{"shortname":"{{subject}}","type":"web"},"expires":{{exp}}}
            """;
        const string headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var header = JwtIssuer.Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var body = JwtIssuer.Base64UrlEncode(Encoding.UTF8.GetBytes(payload.Trim()));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = JwtIssuer.Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{header}.{body}")));
        return $"{header}.{body}.{sig}";
    }

    // Probe a RequireAuthorization route: the typed codes are produced by the
    // JWT bearer CHALLENGE, which only runs where a policy demands
    // authentication. /user/profile checks the actor itself and answers a
    // generic NOT_AUTHENTICATED, so it cannot observe the mapping.
    private async Task<(HttpStatusCode Status, int Code)> Probe(string token)
    {
        _factory.CreateClient();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.GetAsync("/info/me");
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return (resp.StatusCode, body?.Error?.Code ?? -1);
    }

    [FactIfPg]
    public async Task Expired_Token_Is_Refused_With_EXPIRED_TOKEN()
    {
        // ClockSkew is 0, so "one minute ago" must already be expired.
        var exp = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        var (status, code) = await Probe(Mint(HostSecret(), _factory.AdminShortname, exp, "dmart"));
        status.ShouldBe(HttpStatusCode.Unauthorized);
        code.ShouldBe(InternalErrorCode.EXPIRED_TOKEN);
    }

    [FactIfPg]
    public async Task Wrong_Audience_Is_Refused_With_INVALID_TOKEN()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var (status, code) = await Probe(Mint(HostSecret(), _factory.AdminShortname, exp, "someone-else"));
        status.ShouldBe(HttpStatusCode.Unauthorized);
        code.ShouldBe(InternalErrorCode.INVALID_TOKEN);
    }

    [FactIfPg]
    public async Task Token_Signed_With_Another_Secret_Is_Refused_With_INVALID_TOKEN()
    {
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var (status, code) = await Probe(Mint(new string('x', 48), _factory.AdminShortname, exp, "dmart"));
        status.ShouldBe(HttpStatusCode.Unauthorized);
        code.ShouldBe(InternalErrorCode.INVALID_TOKEN);
    }
}
