using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// `min` and `max` must order a JSON field by what the value IS, not by how it
// spells. A jsonb path resolves through PostgreSQL's ->> operator, which hands
// back text, so MIN/MAX compared lexicographically: over amounts 9, 10 and 100
// `min` returned "10" and `max` returned "9". Silent, and wrong for any data
// whose digit count varies — which is most data.
//
// AggregationReducerTests never caught it because its fixture is 10, 20, 30, 30
// — values whose text and numeric orderings happen to agree. Every case here
// uses values whose orderings DISAGREE.
//
// SQLite was already correct: its ->> hands back the JSON value's own SQL type,
// so MIN/MAX were comparing numbers as numbers. These tests are written to hold
// BOTH drivers to the same answers rather than encoding one engine's quirk.
public class AggregationMinMaxTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public AggregationMinMaxTests(DmartFactory factory) => _factory = factory;

    // Text order is "10" < "100" < "9"; numeric order is 9 < 10 < 100. Nothing
    // about the two agrees, so a lexicographic comparison cannot pass this.
    [FactIfPg]
    public async Task Numbers_Are_Compared_As_Numbers_Not_As_Text()
        => await WithFieldAsync("amount", new[] { "9", "10", "100" }, async (query, space) =>
        {
            (await ReduceNumberAsync(query, space, "min", "@payload.body.amount")).ShouldBe(9m);
            (await ReduceNumberAsync(query, space, "max", "@payload.body.amount")).ShouldBe(100m);
        });

    [FactIfPg]
    public async Task Negatives_And_Decimals_Order_Numerically()
        => await WithFieldAsync("amount", new[] { "-5", "0.5", "9.50", "-100" }, async (query, space) =>
        {
            (await ReduceNumberAsync(query, space, "min", "@payload.body.amount")).ShouldBe(-100m);
            (await ReduceNumberAsync(query, space, "max", "@payload.body.amount")).ShouldBe(9.5m);
        });

    // Past 2^53 a float sort key ties two distinct integers and picks whichever
    // it saw first, so the comparison has to be exact, not floating point.
    [FactIfPg]
    public async Task Integers_Beyond_Double_Precision_Still_Order_Exactly()
        => await WithFieldAsync("amount",
            new[] { "9007199254740993", "9007199254740992" }, async (query, space) =>
        {
            (await ReduceNumberAsync(query, space, "min", "@payload.body.amount")).ShouldBe(9007199254740992m);
            (await ReduceNumberAsync(query, space, "max", "@payload.body.amount")).ShouldBe(9007199254740993m);
        });

    // The other half of the contract: min/max are legitimately useful on text,
    // and ISO-8601 timestamps in particular RELY on lexicographic ordering.
    // A blanket numeric cast would have broken both.
    [FactIfPg]
    public async Task Text_Still_Compares_Lexicographically()
        => await WithFieldAsync("name", new[] { "n3", "n1", "n2" }, async (query, space) =>
        {
            (await ReduceAsync(query, space, "min", "@payload.body.name")).ShouldBe("n1");
            (await ReduceAsync(query, space, "max", "@payload.body.name")).ShouldBe("n3");
        });

    [FactIfPg]
    public async Task Iso_Timestamps_Still_Compare_Lexicographically()
        => await WithFieldAsync("at",
            new[] { "\"2026-01-02T00:00:00\"", "\"2025-12-31T00:00:00\"" }, async (query, space) =>
        {
            (await ReduceAsync(query, space, "min", "@payload.body.at")).ShouldBe("2025-12-31T00:00:00");
            (await ReduceAsync(query, space, "max", "@payload.body.at")).ShouldBe("2026-01-02T00:00:00");
        });

    // A plain column is already natively typed, so it must be left alone —
    // wrapping it in a text-oriented comparison would both change the returned
    // type and fail outright on PostgreSQL, where `timestamp ~ text` has no
    // operator.
    [FactIfPg]
    public async Task Plain_Columns_Are_Left_Alone()
        => await WithFieldAsync("amount", new[] { "9", "10" }, async (query, space) =>
        {
            var minShortname = await ReduceRawAsync(query, space, "min", "@shortname");
            minShortname.ShouldBe("m0");

            // Never a 500, and never silently degraded to text on a driver that
            // was returning a real timestamp.
            (await ReduceRawAsync(query, space, "max", "@updated_at")).ShouldNotBeNull();
        });

    // ====================================================================

    private async Task WithFieldAsync(string field, string[] values, Func<QueryService, string, Task> body)
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();
        var query = sp.GetRequiredService<QueryService>();

        var space = "aggm_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });
        try
        {
            var i = 0;
            foreach (var value in values)
            {
                var json = field == "name"
                    ? $"{{\"name\": \"{value}\", \"grp\": \"g\"}}"
                    : $"{{\"{field}\": {value}, \"grp\": \"g\"}}";
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = $"m{i++}",
                    SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
                    IsActive = true, OwnerShortname = "dmart",
                    Payload = new Payload
                    {
                        ContentType = ContentType.Json,
                        Body = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(),
                    },
                });
            }
            await body(query, space);
        }
        finally { try { await spaces.DeleteAsync(space); } catch { } }
    }

    // Numeric answers are compared as numbers, never as strings. The two engines
    // legitimately SPELL a number differently — PostgreSQL numeric keeps the
    // stored scale ("9.50") where SQLite REAL does not ("9.5") — and a reducer
    // test has no business pinning that. What both must agree on is WHICH value
    // won, and parsing removes the spelling from the comparison. Text answers
    // below are compared as text, because there the spelling IS the value.
    private static async Task<decimal> ReduceNumberAsync(
        QueryService query, string space, string reducer, string field)
    {
        var raw = (await ReduceRawAsync(query, space, reducer, field))
            .ShouldNotBeNull($"{reducer}({field}) produced no value");
        return decimal.Parse(
            raw.ToString()!, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    // PostgreSQL must return one static column type for an untyped JSON field
    // and picks text, while SQLite carries the value's own type. Rendering both
    // to string holds them to the same answer without pretending the CLR types
    // agree.
    private static async Task<string?> ReduceAsync(
        QueryService query, string space, string reducer, string field)
        => (await ReduceRawAsync(query, space, reducer, field))?.ToString();

    private static async Task<object?> ReduceRawAsync(
        QueryService query, string space, string reducer, string field)
    {
        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Aggregation, SpaceName = space, Subpath = "/", Limit = 10,
            AggregationData = new RedisAggregate
            {
                GroupBy = new() { "@payload.body.grp" },
                Reducers = new() { new RedisReducer
                { ReducerName = reducer, Alias = "r", Args = new() { field } } },
            },
        }, actor: "dmart");

        resp.Status.ShouldBe(Status.Success, $"{reducer}({field}): {resp.Error?.Message}");
        return resp.Records?[0].Attributes?.GetValueOrDefault("r");
    }
}
