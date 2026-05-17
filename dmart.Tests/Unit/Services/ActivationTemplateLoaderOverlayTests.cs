using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins the ActivationTemplateLoader operator-overlay: a Scriban template at
// ~/.dmart/ActivationEmailContent.txt fully replaces the embedded default.
// Mirrors LanguageLoaderOverlayTests in mechanism (HOME redirection during
// the test to avoid touching the dev machine's real ~/.dmart/), but the
// override semantics are different — this is a single-file replace, not the
// per-key merge that the language overlay does.
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
    public void Override_File_Replaces_Embedded_Template()
    {
        // The override is a fully custom Scriban template; the embedded one
        // is ignored. Both `name` and `link` variables resolve.
        File.WriteAllText(
            Path.Combine(_tmpHome, ".dmart", "ActivationEmailContent.txt"),
            "Hello {{ name }} — activate at {{ link }}.");

        var loader = new ActivationTemplateLoader(NullLogger<ActivationTemplateLoader>.Instance);
        loader.Load();

        var user = NewUser("alice", displayname: new Translation(En: "Alice"));
        loader.RenderBody(user, "https://app/x")
            .ShouldBe("Hello Alice — activate at https://app/x.");
    }

    [Fact]
    public void Without_Override_File_Embedded_Default_Is_Used()
    {
        // No ~/.dmart/ActivationEmailContent.txt — loader falls through to
        // the embedded resource (templates/ActivationEmailContent.txt).
        // That template renders the "Hi {{ name }}" greeting from the
        // current activation HTML, so we assert on a stable substring.
        var loader = new ActivationTemplateLoader(NullLogger<ActivationTemplateLoader>.Instance);
        loader.Load();

        var user = NewUser("bob", displayname: new Translation(En: "Bob"));
        var html = loader.RenderBody(user, "https://app/x");
        html.ShouldContain("Hi Bob");
        html.ShouldContain("https://app/x");
    }

    [Fact]
    public void Override_With_Parse_Error_Falls_Back_To_Empty_String_Not_Crash()
    {
        // A malformed override shouldn't take the server down at startup.
        // Load() logs and leaves no compiled template; RenderBody returns
        // empty so the SMTP send completes with an empty body (visible to
        // ops via the warn log) rather than throwing.
        //
        // `{% for %}` (no iteration variable, no `in`, no `endfor`) is a hard
        // Scriban parse error — confirmed by Template.Parse setting
        // HasErrors=true. Scriban is otherwise quite permissive and treats
        // many "looks malformed" inputs as plain text, so picking a template
        // we know triggers the parser is important here.
        File.WriteAllText(
            Path.Combine(_tmpHome, ".dmart", "ActivationEmailContent.txt"),
            "{{ for }}");

        var loader = new ActivationTemplateLoader(NullLogger<ActivationTemplateLoader>.Instance);
        loader.Load();

        var user = NewUser("carol");
        loader.RenderBody(user, "https://app/x").ShouldBe(string.Empty);
    }

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
