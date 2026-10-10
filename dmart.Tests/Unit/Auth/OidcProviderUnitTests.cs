using System.Text;
using Dmart.Auth.Oidc;
using Dmart.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

public sealed class OidcProviderUnitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oidc-unit-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private OidcSigningKey Key(string file) => new(
        Options.Create(new DmartSettings { OidcSigningKeyFile = Path.Combine(_dir, file) }), NullLogger<OidcSigningKey>.Instance);

    [Fact]
    public void The_Signing_Key_Is_Created_Private_And_Keeps_Its_Id_Across_Restarts()
    {
        using var first = Key("k.pem");
        var kid = first.KeyId;
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(Path.Combine(_dir, "k.pem")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using var reloaded = Key("k.pem");
        reloaded.KeyId.ShouldBe(kid, "the key id is the key's thumbprint");
        using var other = Key("other.pem");
        other.KeyId.ShouldNotBe(kid);
    }

    [Fact]
    public void Verify_Accepts_Its_Own_Token_And_Rejects_Tampering_A_Foreign_Key_And_The_Wrong_Type()
    {
        using var key = Key("k.pem");
        var token = key.Sign(Encoding.UTF8.GetBytes("""{"sub":"u1"}"""), "at+jwt");
        using (var ok = key.Verify(token, "at+jwt")) ok.ShouldNotBeNull().RootElement.GetProperty("sub").GetString().ShouldBe("u1");

        key.Verify(token, "JWT").ShouldBeNull("an access token is not an ID token");
        var parts = token.Split('.');
        var forged = parts[0] + "." + OidcSigningKey.Base64Url(Encoding.UTF8.GetBytes("""{"sub":"admin"}""")) + "." + parts[2];
        key.Verify(forged, "at+jwt").ShouldBeNull();
        using var other = Key("other.pem");
        key.Verify(other.Sign(Encoding.UTF8.GetBytes("""{"sub":"u1"}"""), "at+jwt"), "at+jwt").ShouldBeNull();
        key.Verify("not.a.token", "at+jwt").ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"clients":[{"client_id":"a","redirect_uris":["http://evil.test/cb"]}]}""", "https URL")]
    [InlineData("""{"clients":[{"client_id":"a","redirect_uris":["https://rp.test/cb#x"]}]}""", "without a fragment")]
    [InlineData("""{"clients":[{"client_id":"a","client_secret":"short","redirect_uris":["https://rp.test/cb"]}]}""", "at least 16")]
    [InlineData("""{"clients":[{"client_id":"a","redirect_uris":["https://rp.test/cb"]},{"client_id":"a","redirect_uris":["https://rp.test/cb"]}]}""", "listed twice")]
    [InlineData("""{"clients":[{"client_id":"a"}]}""", "redirect_uris is required")]
    [InlineData("""{"clients":[{"client_id":"a b","redirect_uris":["https://rp.test/cb"]}]}""", "must be 1-128")]
    [InlineData("""{"clients":[{"client_id":"a","redirect_uris":["https://rp.test/cb"],"services":["Mail Box"]}]}""", "not a valid service")]
    public void A_Clients_File_That_Would_Leak_Codes_Or_Is_Ambiguous_Is_Refused(string json, string reason)
    {
        var path = Path.Combine(_dir, "clients.json");
        File.WriteAllText(path, json);
        Should.Throw<InvalidDataException>(() => OidcClients.Load(path)).Message.ShouldContain(reason);
    }

    [Fact]
    public void Loopback_Http_Is_Allowed_For_Native_Apps_And_Secrets_Compare_Exactly()
    {
        var path = Path.Combine(_dir, "clients.json");
        File.WriteAllText(path, """
            {"clients":[{"client_id":"native","redirect_uris":["http://127.0.0.1:8765/cb"]},
                        {"client_id":"rp","client_secret":"0123456789abcdef","redirect_uris":["https://rp.test/cb"],"services":["matrix"]}]}
            """);
        var clients = OidcClients.Load(path);
        OidcClients.IsPublic(clients["native"]).ShouldBeTrue();
        OidcClients.SecretMatches(clients["rp"], "0123456789abcdef").ShouldBeTrue();
        OidcClients.SecretMatches(clients["rp"], "0123456789abcdeF").ShouldBeFalse();
        OidcClients.SecretMatches(clients["native"], "").ShouldBeFalse("a public client has no secret to match");
        OidcClients.Admits(clients["rp"], ["mail", "matrix"]).ShouldBeTrue();
        OidcClients.Admits(clients["rp"], ["mail"]).ShouldBeFalse();
        OidcClients.Admits(clients["native"], []).ShouldBeTrue();
    }
}
