using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.Config;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

public class JwtIssuerTests
{
    private static JwtIssuer NewIssuer() => new(Options.Create(new DmartSettings
    {
        JwtSecret = "test-secret-test-secret-test-secret-32",
        JwtIssuer = "dmart",
        JwtAudience = "dmart",
        JwtAccessMinutes = 5,
        JwtRefreshDays = 1,
    }));

    [Fact]
    public void IssueAccess_Returns_Three_Segments()
    {
        var token = NewIssuer().IssueAccess("alice");
        token.Split('.').Length.ShouldBe(3);
    }

    [Fact]
    public void IssueAccess_Payload_Contains_Subject_Issuer_Audience_Exp()
    {
        var token = NewIssuer().IssueAccess("alice", new[] { "super_admin" });
        var parts = token.Split('.');
        var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;
        root.GetProperty("sub").GetString().ShouldBe("alice");
        root.GetProperty("iss").GetString().ShouldBe("dmart");
        root.GetProperty("aud").GetString().ShouldBe("dmart");
        root.TryGetProperty("exp", out _).ShouldBeTrue();
        root.TryGetProperty("iat", out _).ShouldBeTrue();
        var roles = root.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).ToArray();
        roles.ShouldContain("super_admin");
    }

    [Fact]
    public void IssueRefresh_Returns_Distinct_Token()
    {
        var jwt = NewIssuer();
        var access = jwt.IssueAccess("alice");
        var refresh = jwt.IssueRefresh("alice");
        access.ShouldNotBe(refresh);
    }

    [Fact]
    public void Two_Tokens_For_Same_Subject_Have_Different_Jti()
    {
        var jwt = NewIssuer();
        var a = jwt.IssueAccess("alice");
        var b = jwt.IssueAccess("alice");
        a.ShouldNotBe(b);
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}
