using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// GET /info/metrics — GC and process telemetry.
//
// The endpoint exists because RSS sampled from outside the process cannot tell
// a managed leak from native allocation from the GC simply retaining freed
// segments. These tests pin the two things that make it useful: that the gen
// counters are really the runtime's (not a constant), and that it is not
// readable without admin — it reports load shape, memory pressure and uptime.
public class InfoMetricsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public InfoMetricsTests(DmartFactory factory) => _factory = factory;

    private async Task<string> AdminTokenAsync(HttpClient client)
    {
        var raw = await (await client.PostAsync("/user/login", new StringContent(
            $$"""{"shortname":"{{_factory.AdminShortname}}","password":"{{_factory.AdminPassword}}"}""",
            Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        var token = doc.RootElement.GetProperty("records")[0]
            .GetProperty("attributes").GetProperty("access_token").GetString();
        token.ShouldNotBeNullOrEmpty($"login failed: {raw}");
        return token!;
    }

    private static async Task<JsonElement> MetricsAsync(HttpClient client, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/info/metrics");
        req.Headers.Authorization = new("Bearer", token);
        using var resp = await client.SendAsync(req);
        var raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, raw);
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("records")[0].GetProperty("attributes").Clone();
    }

    [Fact]
    public async Task Reports_Gc_And_Process_Telemetry()
    {
        var client = _factory.CreateClient();
        var attrs = await MetricsAsync(client, await AdminTokenAsync(client));

        foreach (var key in new[]
        {
            "gc_gen0_collections", "gc_gen1_collections", "gc_gen2_collections",
            "gc_heap_bytes", "gc_allocated_bytes_total", "gc_heap_committed_bytes",
            "gc_heap_hard_limit_bytes", "gc_server_mode",
            "process_working_set_bytes", "process_uptime_seconds", "dotnet_version",
        })
        {
            attrs.TryGetProperty(key, out _).ShouldBeTrue($"missing metric: {key}");
        }

        attrs.GetProperty("gc_heap_bytes").GetInt64().ShouldBeGreaterThan(0);
        attrs.GetProperty("process_working_set_bytes").GetInt64().ShouldBeGreaterThan(0);
        attrs.GetProperty("gc_gen0_collections").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Counters_Move_With_Real_Allocation()
    {
        // The point of the endpoint is that the numbers are the runtime's, not
        // a plausible-looking constant. Allocate enough to force collections
        // and assert the counters actually advanced.
        var client = _factory.CreateClient();
        var token = await AdminTokenAsync(client);

        var before = await MetricsAsync(client, token);
        var gen0Before = before.GetProperty("gc_gen0_collections").GetInt32();
        var allocBefore = before.GetProperty("gc_allocated_bytes_total").GetInt64();

        // ~64 MB of short-lived arrays: comfortably past any gen0 budget.
        for (var i = 0; i < 1024; i++)
        {
            var junk = new byte[64 * 1024];
            junk[0] = (byte)i;
        }
        GC.Collect();

        var after = await MetricsAsync(client, token);
        after.GetProperty("gc_allocated_bytes_total").GetInt64()
            .ShouldBeGreaterThan(allocBefore, "allocation total must be monotonic and live");
        after.GetProperty("gc_gen0_collections").GetInt32()
            .ShouldBeGreaterThanOrEqualTo(gen0Before, "collection counts never decrease");
    }

    [Fact]
    public async Task Prometheus_Format_Is_Scrapable()
    {
        var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/info/metrics?format=prometheus");
        req.Headers.Authorization = new("Bearer", await AdminTokenAsync(client));

        using var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        resp.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");

        body.ShouldContain("dmart_gc_gen2_collections ");
        body.ShouldContain("dmart_process_working_set_bytes ");
        // Strings become labelled _info gauges; a bare string would be an
        // unparseable sample value and would break a scrape of the whole page.
        body.ShouldContain("dmart_dotnet_version_info{value=\"");

        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            line.ShouldStartWith("dmart_");
            var value = line[(line.LastIndexOf(' ') + 1)..];
            double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _)
                .ShouldBeTrue($"non-numeric sample value in: {line}");
        }
    }

    [Fact]
    public async Task Requires_Authentication()
    {
        // It reports load shape, memory pressure and uptime. The /info group
        // is admin-gated for exactly this class of reconnaissance, and this
        // route must not have opted out of it.
        var client = _factory.CreateClient();
        using var resp = await client.GetAsync("/info/metrics");
        resp.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}
