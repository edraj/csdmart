using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pure-function parity tests for InvitationService — the localized SMS template
// (now sourced from LanguageLoader, the single source of truth) and the
// activation email body. These are the strings recipients actually see, so
// silent drift here is high-cost.
//
// Cross-references:
//   * dmart/languages/{english,arabic,kurdish}.json -> "invitation_message" / "reset_message"
//   * dmart/utils/templates/activation.html.j2
//   * dmart/utils/generate_email.py::generate_subject("activation")
public class InvitationServiceParityTests
{
    // The loader reads the JSON files embedded into Dmart's main assembly via
    // `<EmbeddedResource Include="languages/*.json" .../>` — same content the
    // running server consumes, so these tests exercise the production path.
    private static readonly LanguageLoader Languages = MakeLoader();

    private static LanguageLoader MakeLoader()
    {
        var l = new LanguageLoader(NullLogger<LanguageLoader>.Instance);
        l.Load();
        return l;
    }

    [Fact]
    public void InvitationMessage_English_HasLinkToken_AndContainsExpectedText()
    {
        var msg = Languages.Get(Language.En, "invitation_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("48 hours");
    }

    [Fact]
    public void InvitationMessage_Arabic_IsArabicScript_AndHasLinkToken()
    {
        var msg = Languages.Get(Language.Ar, "invitation_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("تهانينا");
    }

    [Fact]
    public void InvitationMessage_Kurdish_IsKurdishScript_AndHasLinkToken()
    {
        var msg = Languages.Get(Language.Ku, "invitation_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("بەستەرەوە");
    }

    // Languages without a Python entry (Fr, Tr) must fall through to English
    // rather than KeyError. The C# behaviour is more permissive than Python's
    // dict access — the test pins the chosen tradeoff.
    [Theory]
    [InlineData(Language.Fr)]
    [InlineData(Language.Tr)]
    public void InvitationMessage_UnmappedLanguages_FallBack_To_English(Language lang)
    {
        Languages.Get(lang, "invitation_message")
            .ShouldBe(Languages.Get(Language.En, "invitation_message"));
    }

    [Fact]
    public void ResetMessage_English_HasLinkToken_AndMentionsResetWording()
    {
        var msg = Languages.Get(Language.En, "reset_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("password reset");
    }

    [Fact]
    public void ResetMessage_Arabic_IsArabicScript_AndHasLinkToken()
    {
        var msg = Languages.Get(Language.Ar, "reset_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("كلمة المرور");
    }

    [Fact]
    public void ResetMessage_Kurdish_IsKurdishScript_AndHasLinkToken()
    {
        var msg = Languages.Get(Language.Ku, "reset_message").ShouldNotBeNull();
        msg.ShouldContain("{link}");
        msg.ShouldContain("وشەی نهێنی");
    }

    [Theory]
    [InlineData(Language.Fr)]
    [InlineData(Language.Tr)]
    public void ResetMessage_UnmappedLanguages_FallBack_To_English(Language lang)
    {
        Languages.Get(lang, "reset_message")
            .ShouldBe(Languages.Get(Language.En, "reset_message"));
    }

    [Fact]
    public void Get_UnknownKey_ReturnsNull()
    {
        Languages.Get(Language.En, "this_key_does_not_exist").ShouldBeNull();
    }

    [Fact]
    public void ActivationEmailSubject_Matches_PythonGenerateSubject()
    {
        // Python parity: utils/generate_email.py::generate_subject("activation")
        InvitationService.ActivationEmailSubject.ShouldBe("Welcome to our Platform!");
    }

    [Fact]
    public void ActivationEmailBody_ContainsAllPythonTemplateFields()
    {
        var user = NewUser(shortname: "alice", email: "alice@example.com",
            msisdn: "+96512345678", displayname: new Translation(En: "Alice Smith"));
        var html = InvitationService.ActivationEmailBody(user, "https://app/managed/s/abc123");

        html.ShouldContain("Hi Alice Smith");
        html.ShouldContain("MSISDN: +96512345678");
        html.ShouldContain("Username: alice");
        html.ShouldContain("https://app/managed/s/abc123");
        html.ShouldContain("Welcome, we're happy to see you on board!");
    }

    [Fact]
    public void ActivationEmailBody_FallsBack_To_Shortname_When_DisplayNameMissing()
    {
        var user = NewUser(shortname: "bob", email: "bob@example.com");
        var html = InvitationService.ActivationEmailBody(user, "https://app/managed/s/x");
        html.ShouldContain("Hi bob");
    }

    [Fact]
    public void ActivationEmailBody_HtmlEncodes_HostileShortname_AndLink()
    {
        // A malicious shortname or a tampered link must not break out of the
        // <a href> attribute or inject a tag into the body. We assert on the
        // structural escapes (< > ") because once those three characters are
        // encoded, "onerror=" / "alert(1)" as plain substrings are inert.
        var user = NewUser(shortname: "<script>alert('xss')</script>",
            displayname: new Translation(En: "<img src=x onerror=alert(1)>"));
        var html = InvitationService.ActivationEmailBody(user,
            "https://app/?x=\"><script>alert(1)</script>");

        // No raw injectable tags from user input survive into the body.
        html.ShouldNotContain("<script>alert");
        html.ShouldNotContain("<img src=x");
        html.ShouldNotContain("\"><script>");

        // The encoded forms are present — recipients still see something.
        html.ShouldContain("&lt;script&gt;");
        html.ShouldContain("&lt;img");
        html.ShouldContain("&quot;&gt;&lt;script&gt;");
    }

    [Fact]
    public void ActivationEmailBody_HandlesNullMsisdn_WithoutCrashing()
    {
        var user = NewUser(shortname: "carol", email: "carol@example.com", msisdn: null);
        var html = InvitationService.ActivationEmailBody(user, "https://app/managed/s/x");
        html.ShouldContain("MSISDN: ");        // empty value still rendered
        html.ShouldContain("Username: carol");
    }

    private static User NewUser(string shortname, string? email = null, string? msisdn = null,
        Translation? displayname = null) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/users",
        OwnerShortname = shortname,
        Email = email,
        Msisdn = msisdn,
        Displayname = displayname,
        Type = UserType.Web,
        Language = Language.En,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
