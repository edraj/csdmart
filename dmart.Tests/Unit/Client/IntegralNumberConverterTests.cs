using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dmart.Client;
using Dmart.Client.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Client;

// dmart validates payload bodies with JSON Schema, where "type": "integer"
// means "a number with a zero fractional part" — 10240.0 IS an integer there,
// and dmart stores and returns it happily. System.Text.Json disagrees: mapping
// that same value onto an `int` throws
//   JsonException: The JSON value could not be converted to System.Int32.
//
// So a caller whose model matches the schema still cannot read the entry, and
// the client ends up STRICTER than the server it talks to. These converters
// close exactly that gap and nothing wider: a value with a real fraction is
// still refused, because the schema would refuse it too.
public class IntegralNumberConverterTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new IntegralInt32Converter(), new IntegralInt64Converter() },
    };

    private sealed class Grant
    {
        [JsonPropertyName("data")] public int Data { get; set; }
        [JsonPropertyName("text")] public int Text { get; set; }
        [JsonPropertyName("big")] public long Big { get; set; }
        [JsonPropertyName("opt")] public int? Opt { get; set; }
    }

    private static Grant Read(string json) => JsonSerializer.Deserialize<Grant>(json, Options)!;

    [Theory]
    // The reported payload: grant.data written with a trailing .0 while its
    // siblings are plain integers — the signature of a producer that modelled
    // one field as a float.
    [InlineData("10240.0", 10240)]
    [InlineData("10240", 10240)]
    [InlineData("0.0", 0)]
    [InlineData("-7.0", -7)]
    [InlineData("1.0E4", 10000)]
    [InlineData("2147483647.0", int.MaxValue)]
    [InlineData("-2147483648.0", int.MinValue)]
    public void Integral_Values_Read_As_Int(string literal, int expected)
        => Read($"{{\"data\":{literal}}}").Data.ShouldBe(expected);

    [Theory]
    [InlineData("10240.5")]      // a real fraction — the schema refuses it too
    [InlineData("0.1")]
    [InlineData("3000000000.0")] // integral, but past int
    [InlineData("1e30")]         // past decimal entirely
    [InlineData("\"10240\"")]    // a string is not a number; stay strict
    [InlineData("true")]
    public void Non_Integral_Or_Out_Of_Range_Is_Still_Refused(string literal)
        => Should.Throw<JsonException>(() => Read($"{{\"data\":{literal}}}"));

    // Past 2^53 the value must survive intact, which rules out routing through
    // double on the way in.
    [Theory]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("9007199254740993.0", 9007199254740993L)]
    [InlineData("-9007199254740993.0", -9007199254740993L)]
    public void Long_Keeps_Full_Precision(string literal, long expected)
        => Read($"{{\"big\":{literal}}}").Big.ShouldBe(expected);

    [Fact]
    public void Nullable_Ints_Still_Work()
    {
        Read("{\"opt\":null}").Opt.ShouldBeNull();
        Read("{\"opt\":10240.0}").Opt.ShouldBe(10240);
        Read("{}").Opt.ShouldBeNull();
    }

    // Reading loosely must not make writing loose: a round trip has to put a
    // plain integer back on the wire, or the .0 would propagate.
    [Fact]
    public void Writing_Emits_A_Plain_Integer()
    {
        var json = JsonSerializer.Serialize(Read("{\"data\":10240.0,\"big\":7.0}"), Options);
        json.ShouldContain("\"data\":10240");
        json.ShouldNotContain("10240.0");
        json.ShouldContain("\"big\":7");
    }

    // The whole point: the exact body from the reported entry maps cleanly.
    [Fact]
    public void The_Reported_Booster_Payload_Maps()
    {
        const string body =
            "{\"type\":\"Data\",\"grant\":{\"data\":10240.0,\"text\":0,\"voice\":0},"
            + "\"price\":12750,\"duration\":{\"period\":\"Daily\",\"interval\":30},"
            + "\"externalId\":\"DATA_BOOSTER_10GB\",\"catalogItemId\":530,\"priceThreshold\":0}";

        var grant = JsonSerializer.Deserialize<JsonElement>(body).GetProperty("grant");
        JsonSerializer.Deserialize<Grant>(grant.GetRawText(), Options)!.Data.ShouldBe(10240);
    }

    // ================================================================
    // End to end, through the API the report named.
    // ================================================================

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        public StubHandler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
    }

    // The consumer's own model. The attribute form needs no JsonSerializerOptions
    // plumbing and stays trim/AOT-safe, so it is the shape to reach for.
    private sealed class Booster
    {
        [JsonPropertyName("grant")] public GrantModel Grant { get; set; } = new();
        [JsonPropertyName("price")] [JsonConverter(typeof(IntegralInt32Converter))] public int Price { get; set; }
        [JsonPropertyName("catalogItemId")] [JsonConverter(typeof(IntegralInt32Converter))] public int CatalogItemId { get; set; }
    }

    private sealed class GrantModel
    {
        [JsonPropertyName("data")] [JsonConverter(typeof(IntegralInt32Converter))] public int Data { get; set; }
        [JsonPropertyName("text")] [JsonConverter(typeof(IntegralInt32Converter))] public int Text { get; set; }
        [JsonPropertyName("voice")] [JsonConverter(typeof(IntegralInt32Converter))] public int Voice { get; set; }
    }

    // The reported entry: grant.data stored as 10240.0, every sibling a plain
    // integer. Before the converters this threw on the FIRST field, so the
    // whole entry was unreadable rather than one property being odd.
    [Fact]
    public async Task QueryEntriesAsync_Payload_Maps_Onto_An_Int_Model()
    {
        const string wire =
            "{\"status\":\"success\",\"records\":[{\"resource_type\":\"content\"," +
            "\"shortname\":\"data_booster_10gb\",\"subpath\":\"/boosters\"," +
            "\"uuid\":\"dfffa949-5493-4da8-a1ad-ee4770aacfc1\",\"attributes\":{" +
            "\"space_name\":\"commerce\",\"is_active\":true,\"owner_shortname\":\"dmart\"," +
            "\"created_at\":\"2000-01-01T00:00:00\",\"updated_at\":\"2000-01-01T00:00:00\"," +
            "\"payload\":{\"content_type\":\"json\",\"schema_shortname\":\"booster\",\"body\":" +
            "{\"type\":\"Data\",\"grant\":{\"data\":10240.0,\"text\":0,\"voice\":0}," +
            "\"price\":12750,\"catalogItemId\":530,\"priceThreshold\":0}}}}]," +
            "\"attributes\":{\"total\":1,\"returned\":1}}";

        using var http = new HttpClient(new StubHandler(wire));
        using var client = new DmartClient("https://dmart.test", http);

        var (total, entries) = await client.QueryEntriesAsync(new Query
        { Type = QueryType.Search, SpaceName = "commerce", Subpath = "/boosters" });

        total.ShouldBe(1);
        var body = entries[0].Payload!.Body!.Value;

        // The client still hands the body over byte-exact — it never rewrites
        // what the server sent. The tolerance lives in the READ.
        body.GetProperty("grant").GetProperty("data").GetRawText().ShouldBe("10240.0");

        var booster = JsonSerializer.Deserialize<Booster>(body.GetRawText())!;
        booster.Grant.Data.ShouldBe(10240);
        booster.Grant.Text.ShouldBe(0);
        booster.Price.ShouldBe(12750);
        booster.CatalogItemId.ShouldBe(530);
    }
}
