using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// EXECUTES every aggregation reducer and checks the value it computes.
//
// This exists because the reducer vocabulary shipped broken on SQLite and
// nothing caught it: the emitted-SQL golden pinned the PostgreSQL text, and no
// test ever ran a reducer against a live database on either driver. `sum`,
// `avg`, `group_concat`, `stddev`, `quantile`, `first_value` and
// `random_sample` all returned a 500. A test that asserted only "status ==
// success" on `count` would have stayed green through all of it.
//
// So: assert the NUMBER, on both drivers, for everything both can compute —
// and assert the refusal, by shape, for the four SQLite cannot.
public class AggregationReducerTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public AggregationReducerTests(DmartFactory factory) => _factory = factory;

    // Reducers every backend computes. Values are asserted against the fixture
    // below: amounts 10, 20, 30, 30 in group "g".
    [FactIfPg]
    public async Task Portable_Reducers_Compute_The_Right_Values()
    {
        await WithFixtureAsync(async (query, space) =>
        {
            (await ReduceAsync(query, space, "count")).ShouldBe(4d);
            (await ReduceAsync(query, space, "count_distinct")).ShouldBe(3d);
            (await ReduceAsync(query, space, "sum")).ShouldBe(90d);
            (await ReduceAsync(query, space, "avg")).ShouldBe(22.5d);
            (await ReduceAsync(query, space, "min")).ShouldBe(10d);
            (await ReduceAsync(query, space, "max")).ShouldBe(30d);

            // Order within the group is unspecified on both engines, so compare
            // as a multiset rather than pinning an order neither guarantees.
            var concat = await ReduceRawAsync(query, space, "group_concat");
            concat.ShouldNotBeNull();
            concat!.ToString()!.Split(',').OrderBy(x => x, StringComparer.Ordinal)
                .ShouldBe(new[] { "10", "20", "30", "30" });
        });
    }

    // The four with no SQLite equivalent. On PostgreSQL they must WORK; on
    // SQLite they must fail as a REQUEST error naming the reducer — never a
    // 500, and never a success with the column quietly missing, which is the
    // silent degradation this whole tier is not allowed to have.
    [FactIfPg]
    public async Task Postgres_Only_Reducers_Work_Or_Are_Refused_By_Name()
    {
        await WithFixtureAsync(async (query, space) =>
        {
            foreach (var name in new[] { "stddev", "quantile", "first_value", "random_sample" })
            {
                var resp = await RunAsync(query, space, name);
                if (DmartFactory.UseSqlite)
                {
                    resp.Status.ShouldBe(Status.Failed, $"{name} should be refused on SQLite");
                    resp.Error!.Type.ShouldBe(ErrorTypes.Request,
                        $"{name} must be a request error, not a db/500");
                    resp.Error.Message.ShouldContain(name,
                        customMessage: "the refusal must name the reducer the caller asked for");
                }
                else
                {
                    resp.Status.ShouldBe(Status.Success,
                        $"{name} is supported on PostgreSQL: {resp.Error?.Message}");
                    Attr(resp, name).ShouldNotBeNull($"{name} produced no value");
                }
            }
        });
    }

    // A reducer name dmart does not know is SKIPPED, not refused — the SELECT
    // item is simply not emitted. That predates the dialect seam and is part of
    // the PostgreSQL contract, so the refusal path above must not swallow it.
    // Pinned here because the two look identical from the outside until you
    // check the status code.
    [FactIfPg]
    public async Task Unknown_Reducer_Name_Is_Skipped_Not_Refused()
    {
        await WithFixtureAsync(async (query, space) =>
        {
            var resp = await RunAsync(query, space, "no_such_reducer");
            resp.Status.ShouldBe(Status.Success,
                "an unrecognized reducer name has always been ignored, on both backends");
            Attr(resp, "no_such_reducer").ShouldBeNull();
        });
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private async Task WithFixtureAsync(Func<QueryService, string, Task> body)
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();
        var query = sp.GetRequiredService<QueryService>();

        var space = "agg_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/",
            IsActive = true, OwnerShortname = "dmart",
        });
        try
        {
            // 10, 20, 30, 30 — a duplicate so count and count_distinct differ,
            // which is what catches a DISTINCT that was dropped in translation.
            var i = 0;
            foreach (var amount in new[] { 10, 20, 30, 30 })
            {
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = $"a{i++}",
                    SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
                    IsActive = true, OwnerShortname = "dmart",
                    Payload = new Payload
                    {
                        ContentType = ContentType.Json,
                        Body = System.Text.Json.JsonDocument
                            .Parse($$"""{"amount": {{amount}}, "grp": "g"}""").RootElement.Clone(),
                    },
                });
            }
            await body(query, space);
        }
        finally { try { await spaces.DeleteAsync(space); } catch { } }
    }

    private static Task<Response> RunAsync(QueryService query, string space, string reducer)
        => query.ExecuteAsync(new Query
        {
            Type = QueryType.Aggregation,
            SpaceName = space,
            Subpath = "/",
            Limit = 10,
            AggregationData = new RedisAggregate
            {
                GroupBy = new() { "@payload.body.grp" },
                Reducers = new() { new RedisReducer
                {
                    ReducerName = reducer, Alias = reducer,
                    Args = new() { "@payload.body.amount" },
                } },
            },
        }, actor: "dmart");

    private static object? Attr(Response resp, string alias)
        => resp.Records is { Count: > 0 } r && r[0].Attributes.TryGetValue(alias, out var v) ? v : null;

    private static async Task<object?> ReduceRawAsync(QueryService query, string space, string reducer)
    {
        var resp = await RunAsync(query, space, reducer);
        resp.Status.ShouldBe(Status.Success, $"{reducer}: {resp.Error?.Message}");
        return Attr(resp, reducer);
    }

    // Numeric reducers come back as whatever CLR type the provider chose —
    // long from PostgreSQL's numeric, double from SQLite's REAL — so compare
    // as double rather than pinning a type the two engines do not share.
    private static async Task<double> ReduceAsync(QueryService query, string space, string reducer)
    {
        var raw = await ReduceRawAsync(query, space, reducer);
        raw.ShouldNotBeNull($"{reducer} produced no value");
        return Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
    }
}
