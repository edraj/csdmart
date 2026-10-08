using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

// JsonUtil.ParseElement replaces `JsonDocument.Parse(x).RootElement` at every
// site in the server: that chained form never disposes the document, so each
// call leaked one ArrayPool buffer to the GC (CA2026 in the .NET 11 SDK). The
// helper disposes the document before returning, so what it hands back must be
// a standalone element — usable after the document, and the pooled buffer it
// was parsed from, are gone.
public class JsonUtilTests
{
    [Fact]
    public void ParseElement_String_Returns_A_Standalone_Element()
    {
        var el = JsonUtil.ParseElement("""{"a":[1,2,{"b":"c"}],"n":null}""");

        // Force buffer churn through the pool the document rented from; a
        // non-cloned element would now read someone else's bytes (or throw).
        for (var i = 0; i < 64; i++) _ = JsonUtil.ParseElement(new string('x', 4096).Insert(0, "\"") + "\"");

        el.ValueKind.ShouldBe(JsonValueKind.Object);
        el.GetProperty("a")[2].GetProperty("b").GetString().ShouldBe("c");
        el.GetProperty("n").ValueKind.ShouldBe(JsonValueKind.Null);
        el.GetRawText().ShouldBe("""{"a":[1,2,{"b":"c"}],"n":null}""");
    }

    [Fact]
    public void ParseElement_Utf8_Returns_A_Standalone_Element()
    {
        var bytes = Encoding.UTF8.GetBytes("""[true,false,"عربي",1.5]""");
        var el = JsonUtil.ParseElement(bytes);
        Array.Clear(bytes); // the caller's buffer is not what the element reads from

        el.ValueKind.ShouldBe(JsonValueKind.Array);
        el.GetArrayLength().ShouldBe(4);
        el[2].GetString().ShouldBe("عربي");
        el[3].GetDouble().ShouldBe(1.5);
    }

    [Fact]
    public void ParseElement_Rejects_Malformed_Json_Like_JsonDocument_Does()
    {
        Should.Throw<JsonException>(() => JsonUtil.ParseElement("{not json"));
    }
}
