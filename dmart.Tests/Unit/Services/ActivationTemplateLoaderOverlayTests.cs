using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins the ActivationTemplateLoader operator-overlay: Scriban templates at
// ~/.dmart/ActivationEmailContent.{html,txt} each fully replace their
// embedded counterpart independently. Either, both, or neither override
// can be present.
//
// Mirrors LanguageLoaderOverlayTests in mechanism (HOME redirection during
// the test to avoid touching the dev machine's real ~/.dmart/), but the
// override semantics are different — this is a per-format single-file
// replace, not the per-key merge that the language overlay does.
//
// Shares "dmart-home-overlay" collection with LanguageLoaderOverlayTests so
// the two env-mutating classes never race on HOME at the same time.
[Collection(HomeOverlayCollection.Name)]
public sealed class ActivationTemplateLoaderOverlayTests : IDisposable
{
    private readonly string _tmpHome = Path.Combine(
        Path.GetTempPath(),
        $"dmart-tmpltest-{Guid.NewGuid():N}");
    private readonly string? _origHome;

    public ActivationTemplateLoaderOverlayTests()
    {
        _origHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", _tmpHome);
        Directory.CreateDirectory(Path.Combine(_tmpHome, ".dmart"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _origHome);
        if (Directory.Exists(_tmpHome)) Directory.Delete(_tmpHome, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Override_Html_Only_Uses_Override_For_Html_And_Embedded_Or_AutoDerived_Text()
    {
        // Only the .html override is present. The HTML render uses the
        // override; the text render uses the embedded .txt (which ships in
        // the assembly) — proving the two formats resolve independently.
        WriteOverride("html", "<p>Hello {{ name }} — activate at {{ link }}.</p>");

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("alice", displayname: new Translation(En: "Alice"));
        var html = loader.RenderHtmlBody(user, "https://app/x");
        var text = loader.RenderTextBody(user, "https://app/x");

        html.ShouldBe("<p>Hello Alice — activate at https://app/x.</p>");
        // Text comes from the embedded plain-text template, which contains
        // a stable greeting we can assert on.
        text.ShouldContain("Hi Alice");
        text.ShouldContain("https://app/x");
        text.ShouldNotContain("<p>");
    }

    [Fact]
    public void Override_Text_Only_Uses_Override_For_Text_And_Embedded_For_Html()
    {
        WriteOverride("txt", "Hello {{ name }} — activate at {{ link }}.");

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("bob", displayname: new Translation(En: "Bob"));
        var html = loader.RenderHtmlBody(user, "https://app/x");
        var text = loader.RenderTextBody(user, "https://app/x");

        text.ShouldBe("Hello Bob — activate at https://app/x.");
        // HTML comes from the embedded HTML template, which still has the
        // <p>Hi Bob</p> greeting.
        html.ShouldContain("Hi Bob");
        html.ShouldContain("<p>");
    }

    [Fact]
    public void Override_Both_Uses_Both_Overrides()
    {
        WriteOverride("html", "<h1>Welcome, {{ name }}</h1>");
        WriteOverride("txt", "Welcome, {{ name }}.");

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("carol", displayname: new Translation(En: "Carol"));
        loader.RenderHtmlBody(user, "https://app/x").ShouldBe("<h1>Welcome, Carol</h1>");
        loader.RenderTextBody(user, "https://app/x").ShouldBe("Welcome, Carol.");
    }

    [Fact]
    public void Override_Neither_Uses_Both_Embedded_Defaults()
    {
        // No overrides — both renders go through the embedded templates.
        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("dave", displayname: new Translation(En: "Dave"));
        var html = loader.RenderHtmlBody(user, "https://app/x");
        var text = loader.RenderTextBody(user, "https://app/x");

        html.ShouldContain("Hi Dave");
        html.ShouldContain("https://app/x");
        text.ShouldContain("Hi Dave");
        text.ShouldContain("https://app/x");
        text.ShouldNotContain("<p>");
    }

    [Fact]
    public void Override_Html_With_Parse_Error_Falls_Back_To_Embedded_Html()
    {
        // A malformed HTML override shouldn't take the server down at
        // startup and shouldn't silently produce an empty HTML part. The
        // loader logs and falls through to the embedded default for HTML
        // — text resolution is unaffected.
        //
        // `{{ for }}` is a hard Scriban parse error (no iteration variable,
        // no `in`, no `endfor`).
        WriteOverride("html", "{{ for }}");

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("erin", displayname: new Translation(En: "Erin"));
        var html = loader.RenderHtmlBody(user, "https://app/x");
        var text = loader.RenderTextBody(user, "https://app/x");

        html.ShouldContain("Hi Erin");
        html.ShouldContain("<p>");
        text.ShouldContain("Hi Erin");
    }

    [Fact]
    public void Override_Text_With_Parse_Error_Falls_Back_To_Embedded_Text()
    {
        WriteOverride("txt", "{{ for }}");

        var loader = MakeLoader();
        loader.Load();

        var user = NewUser("frank", displayname: new Translation(En: "Frank"));
        var text = loader.RenderTextBody(user, "https://app/x");
        var html = loader.RenderHtmlBody(user, "https://app/x");

        // Fell back to the embedded plain-text default.
        text.ShouldContain("Hi Frank");
        text.ShouldNotContain("<p>");
        html.ShouldContain("Hi Frank");
    }

    private void WriteOverride(string ext, string content) =>
        File.WriteAllText(Path.Combine(_tmpHome, ".dmart", $"ActivationEmailContent.{ext}"), content);

    private static ActivationTemplateLoader MakeLoader() =>
        new(NullLogger<ActivationTemplateLoader>.Instance);

    private static User NewUser(string shortname, Translation? displayname = null) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/users",
        OwnerShortname = shortname,
        Displayname = displayname,
        Type = UserType.Web,
        Language = Language.En,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
