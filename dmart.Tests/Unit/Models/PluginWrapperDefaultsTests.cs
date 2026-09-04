using System.Text.Json;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Models;

// A plugin's config.json is hand-written by an operator and routinely omits the
// optional fields, so what those fields default to on DESERIALIZATION — not on
// `new PluginWrapper()` — is what actually reaches PluginManager.
//
// Those two disagreed. With any init-only property, the source-generated
// deserializer abandons the parameterless constructor for
// `ObjectWithParameterizedConstructorCreator`, which assigns every such
// property from an args array and passes `default(T)` for whatever the JSON
// left out. The initialisers ran and were then overwritten, so `ordinal` came
// back 0 instead of 9999 and `concurrent` came back false instead of true —
// the latter silently turning every after-hook from fire-and-forget into an
// awaited call, for every plugin that did not spell the field out.
//
// `new PluginWrapper()` was correct throughout, which is why this went
// unnoticed: any test that built the object in C# saw the documented values.
// These cases go through JSON precisely because that is the path that broke.
public class PluginWrapperDefaultsTests
{
    private static PluginWrapper Parse(string json)
        => JsonSerializer.Deserialize(json, DmartJsonContext.Default.PluginWrapper)!;

    // The shape the SDK's own sample ships: no ordinal, no concurrent,
    // no dependencies.
    private const string Minimal = """
        {"shortname":"sample_hook","is_active":true,"type":"hook","listen_time":"after"}
        """;

    [Fact]
    public void Omitted_Fields_Deserialize_To_Their_Documented_Defaults()
    {
        var w = Parse(Minimal);

        w.Ordinal.ShouldBe(9999);      // documented "lower = first, default 9999"
        w.Concurrent.ShouldBeTrue();   // documented "true = fire-and-forget (default)"
        w.Dependencies.ShouldNotBeNull();
        w.Dependencies.ShouldBeEmpty();
    }

    [Fact]
    public void Deserialized_Defaults_Match_A_Freshly_Constructed_Wrapper()
    {
        // The invariant behind the bug, stated directly: parsing a config that
        // mentions none of the optional fields must give the same object as
        // constructing one and setting only what the config did mention.
        var parsed = Parse(Minimal);
        var built = new PluginWrapper
        {
            Shortname = "sample_hook",
            IsActive = true,
            Type = PluginType.Hook,
            ListenTime = EventListenTime.After,
        };

        parsed.Ordinal.ShouldBe(built.Ordinal);
        parsed.Concurrent.ShouldBe(built.Concurrent);
        parsed.Dependencies.ShouldBe(built.Dependencies);
    }

    [Fact]
    public void Explicit_Values_Still_Win_Over_The_Defaults()
    {
        // The other half: making the defaults stick must not make them sticky.
        var w = Parse("""
            {"shortname":"p","is_active":true,"type":"hook",
             "ordinal":5,"concurrent":false,"dependencies":["other_plugin"]}
            """);

        w.Ordinal.ShouldBe(5);
        w.Concurrent.ShouldBeFalse();
        w.Dependencies.ShouldBe(new List<string> { "other_plugin" });
    }

    [Fact]
    public void Every_Shipped_After_Hook_States_Its_Concurrency_Explicitly()
    {
        // Fixing the defaults changed what an OMITTED `concurrent` means: every
        // shipped hook had been running awaited, and would silently have become
        // fire-and-forget. The shipped configs now say `false` outright, so
        // runtime behaviour did not move when the bug was fixed.
        //
        // This guards the next config as much as the current ones: a hook added
        // without the field would inherit fire-and-forget, which may well be
        // right — but it should be a decision someone made, not one they
        // inherited from a bug fix.
        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(pluginsDir)) return; // not copied into this output

        var missing = new List<string>();
        foreach (var cfg in Directory.EnumerateFiles(pluginsDir, "config.json", SearchOption.AllDirectories))
        {
            var w = JsonSerializer.Deserialize(File.ReadAllText(cfg), DmartJsonContext.Default.PluginWrapper);
            if (w is null || w.Type != PluginType.Hook || w.ListenTime != EventListenTime.After) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (!doc.RootElement.TryGetProperty("concurrent", out _))
                missing.Add(Path.GetFileName(Path.GetDirectoryName(cfg))!);
        }

        missing.ShouldBeEmpty(
            $"these after-hook plugins do not state `concurrent` (see plugins/<name>/config.json): {string.Join(", ", missing)}");
    }

    [Fact]
    public void Fields_The_Config_Does_Mention_Are_Unaffected()
    {
        var w = Parse(Minimal);
        w.Shortname.ShouldBe("sample_hook");
        w.IsActive.ShouldBeTrue();
        w.Type.ShouldBe(PluginType.Hook);
        w.ListenTime.ShouldBe(EventListenTime.After);
    }
}
