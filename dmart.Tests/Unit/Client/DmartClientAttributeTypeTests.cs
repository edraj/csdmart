using System.Net;
using System.Text;
using Dmart.Client;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Client;

// Reported as "the .NET SDK parses integers as decimal": a consumer whose model
// carries decimal/float money fields cannot get them through the SDK at all.
//
// Root cause: on net8.0+ the client routes every body through the source-generated
// DmartClientJsonContext, which only ever registered string/bool/int/long/double.
// Record.Attributes is Dictionary<string, object>, so System.Text.Json resolves each
// value by its RUNTIME type — and an unregistered runtime type throws
//   NotSupportedException: JsonTypeInfo metadata for type 'System.Decimal' was not
//   provided by TypeInfoResolver of type 'Dmart.Client.Json.DmartClientJsonContext'
// at serialize time, before the request is ever sent. The netstandard2.1 leg uses
// reflection and silently worked, so the break only shows on modern consumers.
//
// These tests pin the closed set of CLR values an attribute bag must be able to
// carry. Anything outside it (a consumer POCO) still has to be handed over as a
// JsonElement — that is inherent to staying AOT-safe.
public class DmartClientAttributeTypeTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"success\"}", Encoding.UTF8, "application/json"),
            };
        }
    }

    // Round-trips a single attribute value through a real create request and
    // returns the fragment the client actually put on the wire.
    private static async Task<string> WireValueAsync(object value)
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var client = new DmartClient("https://dmart.test", http);

        await client.RequestAsync(new Request
        {
            RequestType = RequestType.Create,
            SpaceName = "space",
            Records = new List<Dmart.Models.Api.Record>
            {
                new()
                {
                    ResourceType = ResourceType.Content,
                    Shortname = "sn",
                    Subpath = "/",
                    Attributes = new Dictionary<string, object> { ["v"] = value },
                },
            },
        });

        var body = handler.LastBody.ShouldNotBeNull();
        var start = body.IndexOf("\"v\":", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, body);
        return body[(start + 4)..];
    }

    public static TheoryData<string, object, string> ScalarCases() => new()
    {
        // name                value                       expected wire prefix
        { "int",               7,                          "7" },
        { "long",              7L,                         "7" },
        { "double",            7.5d,                       "7.5" },
        { "bool",              true,                       "true" },
        { "string",            "s",                        "\"s\"" },
        // The money type. A C# consumer modelling `amount` as decimal hits this first.
        { "decimal",           7.5m,                       "7.5" },
        { "float",             0.5f,                       "0.5" },
        { "short",             (short)7,                   "7" },
        { "ushort",            (ushort)7,                  "7" },
        { "byte",              (byte)7,                    "7" },
        { "sbyte",             (sbyte)7,                   "7" },
        { "uint",              7u,                         "7" },
        { "ulong",             7ul,                        "7" },
        { "Guid",              Guid.Empty,                 "\"00000000-0000-0000-0000-000000000000\"" },
        { "DateTimeOffset",    DateTimeOffset.UnixEpoch,   "\"1970-01-01T00:00:00+00:00\"" },
    };

    [Theory]
    [MemberData(nameof(ScalarCases))]
    public async Task Attribute_Bag_Carries_Every_Json_Scalar(string name, object value, string expected)
        => (await WireValueAsync(value)).ShouldStartWith(expected, Case.Sensitive, name);

    public static TheoryData<string, object, string> CollectionCases() => new()
    {
        { "int[]",                   new[] { 1, 2, 3 },                                   "[1,2,3]" },
        { "long[]",                  new[] { 1L, 2L },                                    "[1,2]" },
        { "double[]",                new[] { 1.5d },                                      "[1.5]" },
        { "decimal[]",               new[] { 1.5m },                                      "[1.5]" },
        { "string[]",                new[] { "a" },                                       "[\"a\"]" },
        { "bool[]",                  new[] { true },                                      "[true]" },
        { "object[]",                new object[] { 1, "a", true },                       "[1,\"a\",true]" },
        { "List<object>",            new List<object> { 1, "a" },                         "[1,\"a\"]" },
        { "List<string>",            new List<string> { "a" },                            "[\"a\"]" },
        { "List<int>",               new List<int> { 1 },                                 "[1]" },
        { "Dictionary<string,object>", new Dictionary<string, object> { ["a"] = 1 },      "{\"a\":1}" },
        { "Dictionary<string,string>", new Dictionary<string, string> { ["a"] = "b" },    "{\"a\":\"b\"}" },
    };

    [Theory]
    [MemberData(nameof(CollectionCases))]
    public async Task Attribute_Bag_Carries_Common_Collections(string name, object value, string expected)
        => (await WireValueAsync(value)).ShouldStartWith(expected, Case.Sensitive, name);

    // The reported symptom, stated as an invariant: an integral CLR value must
    // never reach the wire wearing a fractional tail, whatever numeric type the
    // caller happened to model it with. `decimal` is the one type that keeps
    // trailing-zero scale (1000.0m serializes as "1000.0"), so callers who want
    // an integer on the wire must hand over an integral type — that is the
    // decimal contract, not something the SDK can paper over.
    [Fact]
    public async Task Integral_Values_Serialize_Without_A_Fractional_Tail()
    {
        (await WireValueAsync(1000)).ShouldStartWith("1000}");
        (await WireValueAsync(1000L)).ShouldStartWith("1000}");
        (await WireValueAsync(1000.0d)).ShouldStartWith("1000}");
        (await WireValueAsync(1000m)).ShouldStartWith("1000}");
    }
}
