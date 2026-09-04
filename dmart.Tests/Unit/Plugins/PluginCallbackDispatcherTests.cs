using System.Text.Json;
using Dmart.Plugins.Native;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

// Pins the plugin → host callback frame contract for the subprocess transport.
//
// The frame shape is a wire protocol: SDK code in any language parses these
// responses, so a change here silently breaks every deployed plugin that isn't
// rebuilt. The distinctions under test are the ones that are easy to erode —
// `ok:false` meaning "malformed frame" rather than "operation failed", and the
// int-returning ops keeping the C ABI's numeric codes so SDK helpers can be
// shared across both transports.
//
// These run with NativePluginCallbacks.Services forced to null, which is a
// real state (callbacks reaching the host before Program.cs publishes the
// provider) and keeps the cases free of any database.
public class PluginCallbackDispatcherTests : IDisposable
{
    private readonly IServiceProvider? _priorServices;

    public PluginCallbackDispatcherTests()
    {
        // Integration tests earlier in the run may have published a provider
        // into this static. Save and restore rather than assuming null — the
        // suite runs serially (see TestParallelization.cs) so this is safe.
        _priorServices = NativePluginCallbacks.Services;
        NativePluginCallbacks.SetServicesForTesting(null);
    }

    public void Dispose() => NativePluginCallbacks.SetServicesForTesting(_priorServices);

    private static JsonElement Frame(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Handle(string json)
        => Frame(PluginCallbackDispatcher.Handle(Frame(json), ambientActor: null));

    [Fact]
    public void Only_Callback_Typed_Object_Frames_Are_Callbacks()
    {
        // Backwards compatibility rests on this: a plugin written against a
        // dmart that had no callbacks sends only responses, and every one of
        // them must be read as the exchange's final answer, never as a frame.
        PluginCallbackDispatcher.IsCallback(Frame("""{"type":"callback","op":"log"}""")).ShouldBeTrue();
        PluginCallbackDispatcher.IsCallback(Frame("""{"status":"ok"}""")).ShouldBeFalse();
        PluginCallbackDispatcher.IsCallback(Frame("""{"type":"hook"}""")).ShouldBeFalse();
        PluginCallbackDispatcher.IsCallback(Frame("""{"type":123}""")).ShouldBeFalse();
        PluginCallbackDispatcher.IsCallback(Frame("""[1,2,3]""")).ShouldBeFalse();
        PluginCallbackDispatcher.IsCallback(Frame("\"callback\"")).ShouldBeFalse();
    }

    [Fact]
    public void Id_Is_Echoed_Verbatim_Whatever_Its_Json_Type()
    {
        // The host imposes no numbering scheme, so a plugin can correlate with
        // whatever it already uses. Echoing the raw token keeps a string id a
        // string and a number a number rather than normalising to one of them.
        Handle("""{"type":"callback","id":7,"op":"log","args":{"message":"x"}}""")
            .GetProperty("id").GetInt32().ShouldBe(7);

        Handle("""{"type":"callback","id":"abc","op":"log","args":{"message":"x"}}""")
            .GetProperty("id").GetString().ShouldBe("abc");

        Handle("""{"type":"callback","op":"log","args":{"message":"x"}}""")
            .GetProperty("id").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Malformed_Frames_Fail_The_Frame_Not_The_Operation()
    {
        var unknown = Handle("""{"type":"callback","id":1,"op":"nope"}""");
        unknown.GetProperty("ok").GetBoolean().ShouldBeFalse();
        unknown.GetProperty("error").GetString()!.ShouldContain("unknown callback op: nope");

        var missing = Handle("""{"type":"callback","id":1}""");
        missing.GetProperty("ok").GetBoolean().ShouldBeFalse();
        missing.GetProperty("error").GetString()!.ShouldContain("op");

        var noEntry = Handle("""{"type":"callback","id":1,"op":"save_entry","args":{}}""");
        noEntry.GetProperty("ok").GetBoolean().ShouldBeFalse();
        noEntry.GetProperty("error").GetString()!.ShouldContain("args.entry");
    }

    [Fact]
    public void Media_Callback_Reports_A_Miss_As_Null_Rather_Than_Failing_The_Frame()
    {
        // With no services the lookup can't run, which is a miss, not a
        // malformed frame. Byte round-tripping is covered against a real
        // attachment in Integration/PluginMediaCallbackTests.
        var res = Handle("""{"type":"callback","id":1,"op":"get_media_attachment","args":{}}""");
        res.GetProperty("ok").GetBoolean().ShouldBeTrue();
        res.GetProperty("result").GetProperty("media").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Failed_Operations_Are_Ok_True_With_The_Failure_Inside_Result()
    {
        // Collapsing this into ok:false would make a plugin unable to tell
        // "dmart doesn't understand me" (rebuild needed) from "the save didn't
        // work" (retry or report).
        var doc = Handle("""{"type":"callback","id":1,"op":"load_entry","args":{"space":"s","subpath":"/","shortname":"x"}}""");
        doc.GetProperty("ok").GetBoolean().ShouldBeTrue();
        doc.GetProperty("result").GetProperty("error").GetString().ShouldBe("services_not_initialized");

        var email = Handle("""{"type":"callback","id":1,"op":"send_email","args":{"to":"a@b.c"}}""");
        email.GetProperty("ok").GetBoolean().ShouldBeTrue();
        email.GetProperty("result").GetProperty("code").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void Int_Returning_Ops_Report_Their_Documented_Codes()
    {
        // 1 = rejected before doing anything (empty arg or services not ready).
        // The numbers are part of the wire contract — SDK helpers switch on
        // them, so they must not drift.
        Handle("""{"type":"callback","id":1,"op":"ws_broadcast","args":{"channel":"c","message":"m"}}""")
            .GetProperty("result").GetProperty("code").GetInt32().ShouldBe(1);

        Handle("""{"type":"callback","id":1,"op":"send_email","args":{"to":"","subject":"s"}}""")
            .GetProperty("result").GetProperty("code").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void Log_Callback_Succeeds_Even_With_No_Logger_Factory()
    {
        // EmitPluginLog no-ops when there is no factory. The callback must
        // still answer — an unanswered frame strands the plugin until the
        // per-line timeout fires.
        var res = Handle("""{"type":"callback","id":1,"op":"log","args":{"level":3,"category":"events","message":"hello"}}""");
        res.GetProperty("ok").GetBoolean().ShouldBeTrue();
        res.GetProperty("result").GetProperty("code").GetInt32().ShouldBe(0);
    }

    [Fact]
    public void Query_Args_Are_The_Query_Document_Itself()
    {
        // The C ABI callback takes the whole query JSON including any
        // "as_actor" override, and args carries exactly that — so the
        // three-tier actor resolution behaves identically on both transports.
        // With no services the query fails, but it must fail as a Response
        // envelope rather than a frame error.
        var res = Handle("""{"type":"callback","id":1,"op":"query","args":{"type":"search","space_name":"s","as_actor":"alice"}}""");
        res.GetProperty("ok").GetBoolean().ShouldBeTrue();
        res.GetProperty("result").TryGetProperty("status", out _).ShouldBeTrue();
    }

    [Fact]
    public void Ambient_Actor_Is_The_Default_When_As_Actor_Is_Absent()
    {
        // The security-relevant half of the contract: a plugin query with no
        // explicit override runs as the user that triggered the exchange, not
        // as the system. ResolveActor is the shared decision point for both
        // transports, so pin it directly.
        using var noOverride = JsonDocument.Parse("""{"type":"search"}""");
        NativePluginCallbacks.ResolveActor(noOverride.RootElement, "alice").ShouldBe("alice");

        using var impersonate = JsonDocument.Parse("""{"as_actor":"bob"}""");
        NativePluginCallbacks.ResolveActor(impersonate.RootElement, "alice").ShouldBe("bob");

        using var system = JsonDocument.Parse("""{"as_actor":null}""");
        NativePluginCallbacks.ResolveActor(system.RootElement, "alice").ShouldBeNull();
    }
}
