using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Plugins.Native;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Plugins;

public class NativeApiPluginBinaryTests
{
    [Fact]
    public void TryDecodeBinary_Returns_False_For_Plain_Json_Object()
    {
        var ok = NativeApiPlugin.TryDecodeBinary(
            """{"status":"success","records":[]}""",
            out _, out var body, out _);
        ok.ShouldBeFalse();
        body.Length.ShouldBe(0);
    }

    [Fact]
    public void TryDecodeBinary_Returns_False_For_Json_Array()
    {
        NativeApiPlugin.TryDecodeBinary("[1,2,3]", out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryDecodeBinary_Returns_False_For_Empty_String()
    {
        NativeApiPlugin.TryDecodeBinary("", out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryDecodeBinary_Returns_False_For_Malformed_Json()
    {
        // Has the "binary" prefix to defeat the fast-path so we exercise the parse failure.
        NativeApiPlugin.TryDecodeBinary("""{"binary":""", out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryDecodeBinary_Decodes_Wrapped_Bytes()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var json = $$"""
        {"binary":true,"content_type":"application/pdf","body_b64":"{{Convert.ToBase64String(payload)}}","filename":"x.pdf"}
        """;

        var ok = NativeApiPlugin.TryDecodeBinary(json, out var ct, out var body, out var fn);

        ok.ShouldBeTrue();
        ct.ShouldBe("application/pdf");
        fn.ShouldBe("x.pdf");
        body.ShouldBe(payload);
    }

    [Fact]
    public void TryDecodeBinary_Defaults_ContentType_When_Omitted()
    {
        var payload = new byte[] { 9, 9 };
        var json = $$"""{"binary":true,"body_b64":"{{Convert.ToBase64String(payload)}}"}""";

        var ok = NativeApiPlugin.TryDecodeBinary(json, out var ct, out var body, out var fn);

        ok.ShouldBeTrue();
        ct.ShouldBe("application/octet-stream");
        fn.ShouldBeNull();
        body.ShouldBe(payload);
    }

    [Fact]
    public void TryDecodeBinary_Returns_False_When_Binary_Flag_Is_False()
    {
        var json = """{"binary":false,"body_b64":"AAAA"}""";
        NativeApiPlugin.TryDecodeBinary(json, out _, out var body, out _).ShouldBeFalse();
        body.Length.ShouldBe(0);
    }

    [Fact]
    public void TryDecodeBinary_Returns_False_When_Body_Is_Empty()
    {
        var json = """{"binary":true,"body_b64":""}""";
        NativeApiPlugin.TryDecodeBinary(json, out _, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryDecodeBinary_Tolerates_Leading_Whitespace()
    {
        var payload = new byte[] { 7, 7, 7 };
        var json = "   \n  " + $$"""{"binary":true,"body_b64":"{{Convert.ToBase64String(payload)}}"}""";

        var ok = NativeApiPlugin.TryDecodeBinary(json, out _, out var body, out _);

        ok.ShouldBeTrue();
        body.ShouldBe(payload);
    }

    [Fact]
    public void TryDecodeBinary_Rejects_Strings_Containing_The_Word_Binary()
    {
        // Fast-path may flag this string because the substring "binary" is present, but the
        // structural parser should reject it because there's no top-level binary:true key.
        var json = """{"status":"success","records":[{"description":"\"binary\" data"}]}""";
        NativeApiPlugin.TryDecodeBinary(json, out _, out _, out _).ShouldBeFalse();
    }
}

public class NativePluginCallbacksErrorShapeTests
{
    [Fact]
    public void BuildQueryFailJson_Has_Canonical_Failure_Shape()
    {
        var json = NativePluginCallbacks.BuildQueryFailJson(
            InternalErrorCode.INVALID_DATA, "invalid query json", ErrorTypes.Request);

        var resp = JsonSerializer.Deserialize(json, DmartJsonContext.Default.Response);

        resp.ShouldNotBeNull();
        resp!.Status.ShouldBe(Status.Failed);
        resp.Error.ShouldNotBeNull();
        resp.Error!.Code.ShouldBe(InternalErrorCode.INVALID_DATA);
        resp.Error.Type.ShouldBe(ErrorTypes.Request);
        resp.Error.Message.ShouldBe("invalid query json");
    }

    [Fact]
    public void BuildQueryFailJson_Escapes_Control_Characters_In_Message()
    {
        var json = NativePluginCallbacks.BuildQueryFailJson(
            InternalErrorCode.SOMETHING_WRONG, "boom\nwith\"quotes\\", ErrorTypes.Exception);

        // Round-trip: the deserializer must recover the exact original string, proving the
        // serializer escaped it correctly.
        var resp = JsonSerializer.Deserialize(json, DmartJsonContext.Default.Response);
        resp.ShouldNotBeNull();
        resp!.Error!.Message.ShouldBe("boom\nwith\"quotes\\");
        resp.Error.Type.ShouldBe(ErrorTypes.Exception);
    }
}
