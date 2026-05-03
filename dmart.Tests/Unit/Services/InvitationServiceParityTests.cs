using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pure-function parity tests for InvitationService — the localized SMS template
// and the activation email body. These are the strings recipients actually see,
// so silent drift here is high-cost.
//
// Cross-references:
//   * dmart/languages/{english,arabic,kurdish}.json -> "invitation_message"
//   * dmart/utils/templates/activation.html.j2
//   * dmart/utils/generate_email.py::generate_subject("activation")
public class InvitationServiceParityTests
{
    [Fact]
    public void InvitationMessageFor_English_HasLinkToken_AndContainsExpectedText()
    {
        var msg = InvitationService.InvitationMessageFor(Language.En);
        msg.ShouldContain("{link}");
        msg.ShouldContain("48 hours");
    }

    [Fact]
    public void InvitationMessageFor_Arabic_IsArabicScript_AndHasLinkToken()
    {
        var msg = InvitationService.InvitationMessageFor(Language.Ar);
        msg.ShouldContain("{link}");
        // Arabic letters fall in U+0600..U+06FF.
        msg.ShouldContain("تهانينا");
    }

    [Fact]
    public void InvitationMessageFor_Kurdish_IsKurdishScript_AndHasLinkToken()
    {
        var msg = InvitationService.InvitationMessageFor(Language.Ku);
        msg.ShouldContain("{link}");
        // Sample word from the Python kurdish.json["invitation_message"].
        msg.ShouldContain("بەستەرەوە");
    }

    // Languages without a Python entry (Fr, Tr) must fall through to English
    // rather than KeyError. The C# behaviour is more permissive than Python's
    // dict access — the test pins the chosen tradeoff.
    [Theory]
    [InlineData(Language.Fr)]
    [InlineData(Language.Tr)]
    public void InvitationMessageFor_UnmappedLanguages_FallBack_To_English(Language lang)
    {
        InvitationService.InvitationMessageFor(lang)
            .ShouldBe(InvitationService.InvitationMessageFor(Language.En));
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
