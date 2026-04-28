using System.Text.Json;
using Dmart.Plugins.Native;
using Dmart.Sdk;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Covers the three-tier actor resolution used by NativePluginCallbacks.QueryCb:
//   "as_actor" present, string  → impersonate that user
//   "as_actor" present, null    → run as system (no ACL filter)
//   "as_actor" absent           → fall back to ambient (the user that
//                                 triggered the hook / API request)
// and the SDK-side helper that injects/replaces the field.
public class QueryActorResolutionTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ResolveActor_Falls_Back_To_Ambient_When_Field_Absent()
    {
        var root = Parse("""{"space_name":"acme","subpath":"/"}""");
        NativePluginCallbacks.ResolveActor(root, ambient: "alice").ShouldBe("alice");
    }

    [Fact]
    public void ResolveActor_Falls_Back_To_Null_Ambient()
    {
        var root = Parse("""{"space_name":"acme"}""");
        NativePluginCallbacks.ResolveActor(root, ambient: null).ShouldBeNull();
    }

    [Fact]
    public void ResolveActor_Returns_Override_String()
    {
        var root = Parse("""{"as_actor":"bob","space_name":"acme"}""");
        NativePluginCallbacks.ResolveActor(root, ambient: "alice").ShouldBe("bob");
    }

    [Fact]
    public void ResolveActor_Returns_Null_For_Explicit_Json_Null()
    {
        var root = Parse("""{"as_actor":null,"space_name":"acme"}""");
        // Explicit null beats ambient — the plugin asked for system-level reads.
        NativePluginCallbacks.ResolveActor(root, ambient: "alice").ShouldBeNull();
    }

    [Fact]
    public void ResolveActor_Falls_Back_When_Root_Is_Not_Object()
    {
        var root = Parse("[1,2,3]");
        NativePluginCallbacks.ResolveActor(root, ambient: "alice").ShouldBe("alice");
    }

    // ============================================================
    // SDK helper — JSON rewrite
    // ============================================================

    [Fact]
    public void BuildQueryJsonWithActor_Inserts_Actor_When_Absent()
    {
        var rewritten = DmartSdk.BuildQueryJsonWithActor(
            """{"space_name":"acme","subpath":"/"}""", asActor: "bob");

        rewritten.ShouldNotBeNull();
        var root = Parse(rewritten!);
        root.GetProperty("as_actor").GetString().ShouldBe("bob");
        root.GetProperty("space_name").GetString().ShouldBe("acme");
        root.GetProperty("subpath").GetString().ShouldBe("/");
    }

    [Fact]
    public void BuildQueryJsonWithActor_Replaces_Existing_Actor()
    {
        var rewritten = DmartSdk.BuildQueryJsonWithActor(
            """{"as_actor":"old","space_name":"acme"}""", asActor: "bob");

        rewritten.ShouldNotBeNull();
        var root = Parse(rewritten!);
        root.GetProperty("as_actor").GetString().ShouldBe("bob");
        // Crucially, the field appears exactly once — JsonDocument silently
        // returns the last occurrence so a duplicate wouldn't be visible
        // here, but we can count enumerated properties to assert that.
        var count = 0;
        foreach (var prop in root.EnumerateObject())
            if (prop.NameEquals("as_actor")) count++;
        count.ShouldBe(1);
    }

    [Fact]
    public void BuildQueryJsonWithActor_Writes_Json_Null_When_Asked()
    {
        var rewritten = DmartSdk.BuildQueryJsonWithActor(
            """{"space_name":"acme"}""", asActor: null);

        rewritten.ShouldNotBeNull();
        var root = Parse(rewritten!);
        root.TryGetProperty("as_actor", out var asActor).ShouldBeTrue();
        // JSON null, NOT the string "null" — the host distinguishes these.
        asActor.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void BuildQueryJsonWithActor_Returns_Null_For_Non_Object_Body()
    {
        DmartSdk.BuildQueryJsonWithActor("[1,2,3]", asActor: "bob").ShouldBeNull();
    }

    [Fact]
    public void BuildQueryJsonWithActor_Round_Trip_Through_ResolveActor()
    {
        // End-to-end: rewriting on the SDK side and resolving on the host
        // side must agree. This is the real contract callers depend on.
        var rewritten = DmartSdk.BuildQueryJsonWithActor(
            """{"space_name":"acme"}""", asActor: "carol");
        rewritten.ShouldNotBeNull();

        NativePluginCallbacks.ResolveActor(Parse(rewritten!), ambient: "alice")
            .ShouldBe("carol");
    }

    [Fact]
    public void BuildQueryJsonWithActor_Round_Trip_Null_Through_ResolveActor()
    {
        var rewritten = DmartSdk.BuildQueryJsonWithActor(
            """{"space_name":"acme"}""", asActor: null);
        rewritten.ShouldNotBeNull();

        NativePluginCallbacks.ResolveActor(Parse(rewritten!), ambient: "alice")
            .ShouldBeNull();
    }
}

// PluginInvocationContext is a thin [ThreadStatic] holder, but the set/restore
// discipline that callers depend on warrants direct coverage so a future
// refactor doesn't silently break it.
public class PluginInvocationContextTests
{
    [Fact]
    public void Default_Is_Null()
    {
        // Reset first — other tests may run on the same thread and leave a value.
        PluginInvocationContext.CurrentActor = null;
        PluginInvocationContext.CurrentActor.ShouldBeNull();
    }

    [Fact]
    public void Set_Is_Visible_On_Same_Thread()
    {
        var prev = PluginInvocationContext.CurrentActor;
        try
        {
            PluginInvocationContext.CurrentActor = "alice";
            PluginInvocationContext.CurrentActor.ShouldBe("alice");
        }
        finally
        {
            PluginInvocationContext.CurrentActor = prev;
        }
    }

    [Fact]
    public void Nested_Set_Restore_Preserves_Outer_Value()
    {
        PluginInvocationContext.CurrentActor = "outer";
        try
        {
            // Simulates a nested dispatch (a hook firing while another hook
            // is mid-flight on the same thread).
            var saved = PluginInvocationContext.CurrentActor;
            try
            {
                PluginInvocationContext.CurrentActor = "inner";
                PluginInvocationContext.CurrentActor.ShouldBe("inner");
            }
            finally
            {
                PluginInvocationContext.CurrentActor = saved;
            }

            PluginInvocationContext.CurrentActor.ShouldBe("outer");
        }
        finally
        {
            PluginInvocationContext.CurrentActor = null;
        }
    }
}
