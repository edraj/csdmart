using System.Text.Json;
using Dmart.Plugins.Native;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Covers the three-tier actor resolution behind the `query` callback:
//   "as_actor" present, string  → impersonate that user
//   "as_actor" present, null    → run as system (no ACL filter)
//   "as_actor" absent           → fall back to ambient (the user that
//                                 triggered the hook / API request)
//
// This is the security-relevant half of the callback contract: absent means
// "run as the triggering user", not "run as the system", so a plugin query
// stays inside that user's permissions unless it explicitly says otherwise.
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
