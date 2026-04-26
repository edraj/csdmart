using System.Text.Json;
using Dmart.Api.Managed;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Reproduces the user's bug: POST /managed/request with request_type=create,
// resource_type=comment, attributes.payload = {content_type, body:{...}}
// was dropping the payload — the attachment row landed with payload=NULL.
// CreateAttachmentAsync only read top-level `body`/`state`, ignoring
// attrs.payload entirely. Fixed via RequestHandler.ParsePayloadFromAttrs.
//
// Direct-invoke tests — sidestep the `not allowed to create Comment` infra
// gate that blocks a full HTTP round-trip for scratch spaces.
public sealed class AttachmentCreatePayloadTests
{
    [Fact]
    public void ParsePayloadFromAttrs_With_JsonElement_Payload_Produces_Full_Payload_Record()
    {
        var raw = """
            {"is_active":true,"payload":{"content_type":"json","body":{"state":"commented","body":"qadsdasdasd"}}}
            """;
        using var doc = JsonDocument.Parse(raw);
        var attrs = new Dictionary<string, object>();
        foreach (var prop in doc.RootElement.EnumerateObject())
            attrs[prop.Name] = prop.Value.Clone();

        var payload = RequestHandler.ParsePayloadFromAttrs(attrs);

        payload.ShouldNotBeNull("attrs.payload must be parsed, not dropped");
        payload!.ContentType.ShouldBe(ContentType.Json);
        payload.Body.ShouldNotBeNull();
        var body = payload.Body!.Value;
        body.GetProperty("state").GetString().ShouldBe("commented");
        body.GetProperty("body").GetString().ShouldBe("qadsdasdasd");
    }

    [Fact]
    public void ParsePayloadFromAttrs_With_Nested_Dictionary_Shape_Also_Works()
    {
        // In-process callers that build attrs themselves might pass a
        // Dictionary<string, object> for the payload value instead of a
        // JsonElement. The parser has to handle both shapes, otherwise
        // programmatic create paths would silently lose the payload even
        // though the HTTP path worked.
        var attrs = new Dictionary<string, object>
        {
            ["payload"] = new Dictionary<string, object>
            {
                ["content_type"] = "json",
                ["body"] = new Dictionary<string, object>
                {
                    ["state"] = "commented",
                    ["body"] = "from-dict",
                },
            },
        };
        var payload = RequestHandler.ParsePayloadFromAttrs(attrs);
        payload.ShouldNotBeNull();
        payload!.ContentType.ShouldBe(ContentType.Json);
        payload.Body!.Value.GetProperty("body").GetString().ShouldBe("from-dict");
    }

    [Fact]
    public void ParsePayloadFromAttrs_Without_Payload_Returns_Null()
    {
        var attrs = new Dictionary<string, object> { ["is_active"] = true };
        RequestHandler.ParsePayloadFromAttrs(attrs).ShouldBeNull();
    }

    [Fact]
    public void ParsePayloadFromAttrs_Legacy_SchemaShortname_At_Root()
    {
        // Back-compat: clients that set `schema_shortname` directly on attrs
        // (no `payload` block) still get a schema-only Payload.
        var attrs = new Dictionary<string, object> { ["schema_shortname"] = "request" };
        var p = RequestHandler.ParsePayloadFromAttrs(attrs);
        p.ShouldNotBeNull();
        p!.SchemaShortname.ShouldBe("request");
    }
}
