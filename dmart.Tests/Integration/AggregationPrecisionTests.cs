using System.Text;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Aggregation results must reach the caller with the value PostgreSQL computed.
//
// QueryService used to narrow every aggregation cell to a type the source-gen
// JSON context knew — `long -> int` and `decimal -> double`. PostgreSQL emits
// SUM/AVG over numeric as `numeric`, which Npgsql hands back as decimal, so
// every money aggregate went through binary floating point and came out
// approximate. AggregationValueTests pins the conversion itself; this pins the
// whole path, including the JSON serializer that the narrowing existed to
// appease in the first place — a missing registration surfaces here as a 500,
// not as a wrong number.
public class AggregationPrecisionTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public AggregationPrecisionTests(DmartFactory factory) => _factory = factory;

    // 0.1 + 0.2 is the canonical binary-floating-point failure: exactly 0.3 in
    // decimal, 0.30000000000000004 through double.
    [FactIfPostgresOnly]
    public async Task Sum_Of_Fractions_Is_Exact_Not_Binary_Floating_Point()
        => await WithAmountsAsync(new[] { "0.1", "0.2" }, async (query, space) =>
        {
            var sum = await ReduceAsync(query, space, "sum");
            sum.ShouldBeOfType<decimal>().ShouldBe(0.3m);
            sum.ToString().ShouldBe("0.3");
        });

    // Cents past double's 15-16 significant digits. Through double this value
    // returns as 12345678901234568 — the fractional part is simply gone.
    [FactIfPostgresOnly]
    public async Task Sum_Keeps_Cents_Beyond_Double_Precision()
        => await WithAmountsAsync(new[] { "12345678901234567.89" }, async (query, space) =>
            (await ReduceAsync(query, space, "sum"))
                .ShouldBeOfType<decimal>().ShouldBe(12345678901234567.89m));

    // AVG(numeric) arrives from PostgreSQL at scale 16 (literally
    // 22.5000000000000000). decimal keeps trailing zeros through
    // System.Text.Json where double does not, so this pins that the scale is
    // normalised away and the wire shape callers already parse is unchanged.
    [FactIfPostgresOnly]
    public async Task Avg_Keeps_Its_Established_Wire_Shape()
        => await WithAmountsAsync(new[] { "10", "20", "30", "30" }, async (query, space) =>
        {
            (await ReduceAsync(query, space, "avg")).ShouldBeOfType<decimal>()
                .ToString().ShouldBe("22.5");
            // An integral sum must stay integral — no "90.0" tail for a caller
            // mapping the field onto an integer type.
            (await ReduceAsync(query, space, "sum")).ShouldBeOfType<decimal>()
                .ToString().ShouldBe("90");
        });

    // The serializer is the half a unit test cannot reach: an unregistered
    // runtime type in the attributes bag is a 500, however correct the number.
    [FactIfPg]
    public async Task Aggregation_Survives_Json_Serialization_Over_Http()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        try
        {
            await WithAmountsAsync(new[] { "0.1", "0.2" }, async (_, space) =>
            {
                var body =
                    "{\"type\":\"aggregation\",\"space_name\":\"" + space + "\",\"subpath\":\"/\",\"limit\":10," +
                    "\"aggregation_data\":{\"group_by\":[\"@payload.body.grp\"],\"reducers\":[" +
                    "{\"reducer_name\":\"sum\",\"alias\":\"sum\",\"args\":[\"@payload.body.amount\"]}," +
                    "{\"reducer_name\":\"count\",\"alias\":\"count\",\"args\":[\"@payload.body.amount\"]}]}}";
                var resp = await admin.Client.PostAsync("/managed/query",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var raw = await resp.Content.ReadAsStringAsync();

                resp.IsSuccessStatusCode.ShouldBeTrue(raw);
                raw.ShouldContain("\"count\":2", customMessage: raw);

                if (DmartFactory.UseSqlite)
                {
                    // SQLite sums through REAL — the dialect documents that as an
                    // accepted precision degradation, so only assert the reducer
                    // ran and serialized. Exactness is PostgreSQL's to promise.
                    raw.ShouldContain("\"sum\":", customMessage: raw);
                    return;
                }

                raw.ShouldContain("\"sum\":0.3", customMessage: raw);
                raw.ShouldNotContain("0.30000000000000004");
            });
        }
        finally { await admin.Cleanup(); }
    }

    // ====================================================================

    private async Task WithAmountsAsync(string[] amounts, Func<QueryService, string, Task> body)
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();
        var query = sp.GetRequiredService<QueryService>();

        var space = "aggp_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });
        try
        {
            var i = 0;
            foreach (var amount in amounts)
            {
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = $"p{i++}",
                    SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
                    IsActive = true, OwnerShortname = "dmart",
                    Payload = new Payload
                    {
                        ContentType = ContentType.Json,
                        Body = System.Text.Json.JsonDocument
                            .Parse($"{{\"amount\": {amount}, \"grp\": \"g\"}}").RootElement.Clone(),
                    },
                });
            }
            await body(query, space);
        }
        finally { try { await spaces.DeleteAsync(space); } catch { } }
    }

    private static async Task<object> ReduceAsync(QueryService query, string space, string reducer)
    {
        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Aggregation, SpaceName = space, Subpath = "/", Limit = 10,
            AggregationData = new RedisAggregate
            {
                GroupBy = new() { "@payload.body.grp" },
                Reducers = new() { new RedisReducer
                { ReducerName = reducer, Alias = reducer, Args = new() { "@payload.body.amount" } } },
            },
        }, actor: "dmart");

        resp.Status.ShouldBe(Status.Success, $"{reducer}: {resp.Error?.Message}");
        var value = resp.Records?[0].Attributes?.GetValueOrDefault(reducer);
        return value.ShouldNotBeNull($"{reducer} produced no value");
    }
}
