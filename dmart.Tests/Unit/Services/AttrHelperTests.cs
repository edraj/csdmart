using System.Text.Json;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Pins AttrHelper.ParseStringList / TryParseStringList / ExtractTags — the
// canonical string-list parser behind tags (ResourceWithPayloadHandler,
// EntryService.PatchTags, UserService self-registration) and roles/groups
// (RequestHandler.ExtractStringList). A regression here hits every one of
// those call paths at once, so the normalization policy is pinned explicitly:
//
//   * entries are trimmed; empty/whitespace-only entries are dropped —
//     uniformly across ALL input shapes (JSON arrays from the wire, CLR
//     lists from internal callers);
//   * non-string JSON array items are dropped (wire input is not coerced);
//     non-string CLR objects are ToString()-coerced (internal callers pass
//     boxed values);
//   * null / unrecognizable shapes: TryParseStringList returns null (lets
//     RequestHandler.ExtractStringList keep its "absent" contract);
//     ParseStringList returns the fallback (default empty) so a malformed
//     patch never wipes existing data.
public class AttrHelperTests
{
    private static object JsonArray(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void JsonElement_Array_Is_Trimmed_And_Empties_Dropped()
    {
        var result = AttrHelper.ParseStringList(JsonArray("""["vip", " beta ", "", "   "]"""));
        result.ShouldBe(new[] { "vip", "beta" });
    }

    [Fact]
    public void JsonElement_Array_Drops_NonString_Items()
    {
        var result = AttrHelper.ParseStringList(JsonArray("""["a", 42, true, null, {"x":1}]"""));
        result.ShouldBe(new[] { "a" });
    }

    [Fact]
    public void String_List_Is_Normalized_Not_Passed_Through()
    {
        var result = AttrHelper.ParseStringList(new List<string> { " one ", "", "two" });
        result.ShouldBe(new[] { "one", "two" });
    }

    [Fact]
    public void Object_List_Is_Coerced_Trimmed_And_Empties_Dropped()
    {
        var result = AttrHelper.ParseStringList(new List<object?> { " one ", 42, null, "" }!);
        result.ShouldBe(new[] { "one", "42" });
    }

    [Fact]
    public void Null_And_Unrecognized_Shapes_Return_Fallback()
    {
        AttrHelper.ParseStringList(null).ShouldBeEmpty();
        AttrHelper.ParseStringList(42).ShouldBeEmpty();
        AttrHelper.ParseStringList(JsonArray("\"not-an-array\"")).ShouldBeEmpty();

        var keep = new List<string> { "existing" };
        AttrHelper.ParseStringList(42, keep).ShouldBeSameAs(keep);
        AttrHelper.ParseStringList(null, keep).ShouldBeSameAs(keep);
    }

    [Fact]
    public void TryParse_Distinguishes_Unrecognized_From_Empty_Array()
    {
        // RequestHandler.ExtractStringList's merge contract rides on this:
        // null = "attribute absent/unusable" (keep existing), [] = "explicitly
        // cleared".
        AttrHelper.TryParseStringList(null).ShouldBeNull();
        AttrHelper.TryParseStringList(42).ShouldBeNull();
        AttrHelper.TryParseStringList(JsonArray("{}")).ShouldBeNull();
        AttrHelper.TryParseStringList(JsonArray("[]")).ShouldNotBeNull();
        AttrHelper.TryParseStringList(JsonArray("[]"))!.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractTags_Missing_Key_Yields_Empty()
    {
        AttrHelper.ExtractTags(new Dictionary<string, object>()).ShouldBeEmpty();
        AttrHelper.ExtractTags(new Dictionary<string, object>
        {
            ["tags"] = JsonArray("""["a", "b"]"""),
        }).ShouldBe(new[] { "a", "b" });
    }
}
