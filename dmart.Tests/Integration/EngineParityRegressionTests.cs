using Dmart.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Behaviours that diverged between PostgreSQL and SQLite. Every case here runs
// on BOTH engines (the suite's two CI legs) and asserts one answer:
//   - aggregation with a filter crashed on SQLite (WHERE built with the PG dialect);
//   - a wildcard search over a value containing `_` returned nothing on SQLite
//     (no ESCAPE clause, so the parser's `\_` demanded a literal backslash);
//   - an ISO `T` time separator dropped the whole first day on SQLite;
//   - NULL placement under sort_by was the opposite on each engine.
public sealed class EngineParityRegressionTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public EngineParityRegressionTests(DmartFactory factory) => _factory = factory;

    private (QueryService Query, EntryRepository Entries) Services()
    {
        _factory.CreateClient();
        return (_factory.Services.GetRequiredService<QueryService>(),
                _factory.Services.GetRequiredService<EntryRepository>());
    }

    private static Task Seed(EntryRepository entries, string space, string shortname, string bodyJson)
        => entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = shortname, SpaceName = space, Subpath = "/items",
            OwnerShortname = "dmart", ResourceType = ResourceType.Content, IsActive = true,
            Payload = new Payload { ContentType = ContentType.Json, Body = JsonDocument.Parse(bodyJson).RootElement.Clone() },
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });

    [FactIfPg]
    public async Task Aggregation_With_A_Filter_Succeeds_On_Both_Engines()
    {
        var (query, entries) = Services();
        var space = $"itest_par_{Guid.NewGuid():N}"[..20];
        await Seed(entries, space, "a", "{\"grp\":\"x\"}");
        await Seed(entries, space, "b", "{\"grp\":\"x\"}");
        await Seed(entries, space, "c", "{\"grp\":\"y\"}");

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Aggregation,
            SpaceName = space,
            Subpath = "/items",
            FilterTypes = new() { ResourceType.Content },      // the trigger: a WHERE fragment
            Search = "@payload.body.grp:x",
            AggregationData = new RedisAggregate
            {
                GroupBy = new() { "@payload.body.grp" },
                Reducers = new() { new RedisReducer { ReducerName = "count", Alias = "n" } },
            },
            Limit = 10,
        }, _factory.AdminShortname);

        resp.Status.ShouldBe(Status.Success, resp.Error?.Message);
        resp.Records.ShouldNotBeNull();
        resp.Records!.Count.ShouldBe(1, "only group x survives the search filter");
    }

    [FactIfPg]
    public async Task Wildcard_Search_Over_A_Value_With_Underscore_Matches_On_Both_Engines()
    {
        var (query, entries) = Services();
        var space = $"itest_par_{Guid.NewGuid():N}"[..20];
        await Seed(entries, space, "u1", "{\"code\":\"ab_1\"}");
        await Seed(entries, space, "u2", "{\"code\":\"abX1\"}");   // `_` must NOT act as a wildcard

        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Search, SpaceName = space, Subpath = "/items",
            Search = "@payload.body.code:ab_*", Limit = 10,
        }, _factory.AdminShortname);

        resp.Status.ShouldBe(Status.Success, resp.Error?.Message);
        resp.Records!.Select(r => r.Shortname).ShouldBe(new[] { "u1" });
    }

    [FactIfPg]
    public async Task Timestamp_Bound_With_T_Separator_Keeps_The_First_Day_On_Both_Engines()
    {
        var (query, entries) = Services();
        var space = $"itest_par_{Guid.NewGuid():N}"[..20];
        await Seed(entries, space, "t1", "{\"v\":1}");

        // Rows were created just now. A range whose bounds carry a time of
        // day must be spelled with the ISO `T` (the grammar splits a range on
        // whitespace or comma, so a space inside a bound is impossible). The
        // lower bound is the start of yesterday: SQLite used to compare it as
        // text against the stored "yyyy-MM-dd HH:mm:ss" and, because 'T' sorts
        // after ' ', dropped every row of that day. PostgreSQL casts it.
        var from = TimeUtils.Now().AddDays(-1).ToString("yyyy-MM-dd'T'00:00:00");
        var to = TimeUtils.Now().AddDays(1).ToString("yyyy-MM-dd'T'00:00:00");
        var resp = await query.ExecuteAsync(new Query
        {
            Type = QueryType.Search, SpaceName = space, Subpath = "/items",
            Search = $"@created_at:[{from},{to}]", Limit = 10,
        }, _factory.AdminShortname);

        resp.Status.ShouldBe(Status.Success, resp.Error?.Message);
        resp.Records!.Select(r => r.Shortname).ShouldContain("t1");
    }

    [FactIfPg]
    public async Task Null_Sort_Keys_Place_The_Same_Way_On_Both_Engines()
    {
        var (query, entries) = Services();
        var space = $"itest_par_{Guid.NewGuid():N}"[..20];
        await Seed(entries, space, "r1", "{\"rank\":1}");
        await Seed(entries, space, "r2", "{\"rank\":2}");
        await Seed(entries, space, "norank", "{\"other\":true}");

        async Task<List<string>> Sorted(SortType dir)
        {
            var resp = await query.ExecuteAsync(new Query
            {
                Type = QueryType.Search, SpaceName = space, Subpath = "/items",
                SortBy = "payload.body.rank", SortType = dir, Limit = 10,
            }, _factory.AdminShortname);
            resp.Status.ShouldBe(Status.Success, resp.Error?.Message);
            return resp.Records!.Select(r => r.Shortname).ToList();
        }

        // PostgreSQL's defaults are the pinned contract: DESC → NULLs first,
        // ASC → NULLs last. SQLite's native defaults are the exact opposite.
        (await Sorted(SortType.Descending)).ShouldBe(new[] { "norank", "r2", "r1" });
        (await Sorted(SortType.Ascending)).ShouldBe(new[] { "r1", "r2", "norank" });
    }
}
