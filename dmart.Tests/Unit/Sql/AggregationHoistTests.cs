using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.QueryGrammar;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// PostgreSQL's ->> erases the JSON value's type, so `min`/`max` over a jsonb
// path have to consult the -> form as well: a type guard and a value, in each
// of two aggregates, is four walks down the same path per row. The dialect
// hoists the walk into a CROSS JOIN LATERAL so it happens once.
//
// What these pin is the SHAPE of that rewrite. The values it produces are
// pinned by AggregationMinMaxTests against a live PostgreSQL; the concern here
// is that the hoist is emitted only where it pays, exactly once per distinct
// path, and never on a backend that cannot parse it.
public class AggregationHoistTests
{
    private static Query Aggregate(params RedisReducer[] reducers) => new()
    {
        Type = QueryType.Aggregation,
        SpaceName = "s",
        Subpath = "/",
        AggregationData = new RedisAggregate { Reducers = reducers.ToList() },
    };

    private static RedisReducer R(string name, string alias, string arg) =>
        new() { ReducerName = name, Alias = alias, Args = new() { arg } };

    private static string Sql(ISqlDialect dialect, Query q) =>
        QueryHelper.BuildAggregationSql("entries", q, dialect, null, null)
            .ShouldNotBeNull().Sql;

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // The whole point: the jsonb path is walked once per row, not four times.
    [Fact]
    public void Min_Walks_The_Json_Path_Exactly_Once()
    {
        var sql = Sql(PostgresSqlDialect.Instance,
            Aggregate(R("min", "lo", "@payload.body.amount")));

        Count(sql, "payload::jsonb->'body'->'amount'").ShouldBe(1);
        sql.ShouldContain("CROSS JOIN LATERAL");
    }

    // Two reducers over the SAME path share one lateral. Emitting a second
    // would re-walk the path and defeat the exercise.
    [Fact]
    public void Min_And_Max_Over_One_Field_Share_A_Single_Lateral()
    {
        var sql = Sql(PostgresSqlDialect.Instance, Aggregate(
            R("min", "lo", "@payload.body.amount"),
            R("max", "hi", "@payload.body.amount")));

        Count(sql, "CROSS JOIN LATERAL").ShouldBe(1);
        Count(sql, "payload::jsonb->'body'->'amount'").ShouldBe(1);
    }

    // Distinct paths cannot share one, and their aliases must not collide.
    [Fact]
    public void Distinct_Fields_Get_Distinct_Laterals()
    {
        var sql = Sql(PostgresSqlDialect.Instance, Aggregate(
            R("min", "lo", "@payload.body.amount"),
            R("max", "hi", "@payload.body.score")));

        Count(sql, "CROSS JOIN LATERAL").ShouldBe(2);
        sql.ShouldContain("hoist0");
        sql.ShouldContain("hoist1");
    }

    // A reducer that mentions its field once has nothing to save, so a lateral
    // would be a join added for no reason.
    [Theory]
    [InlineData("sum")]
    [InlineData("avg")]
    [InlineData("count")]
    [InlineData("stddev")]
    [InlineData("quantile")]
    public void Non_Ordering_Reducers_Are_Not_Hoisted(string reducer)
    {
        var sql = Sql(PostgresSqlDialect.Instance,
            Aggregate(R(reducer, "r", "@payload.body.amount")));

        sql.ShouldNotContain("CROSS JOIN LATERAL");
        sql.ShouldNotContain("hoist0");
    }

    // A bare column is natively typed — there is no JSON extraction to hoist,
    // and MIN(updated_at) must stay exactly that.
    [Fact]
    public void Plain_Columns_Are_Not_Hoisted()
    {
        var sql = Sql(PostgresSqlDialect.Instance, Aggregate(R("min", "lo", "@updated_at")));

        sql.ShouldNotContain("CROSS JOIN LATERAL");
        sql.ShouldContain("MIN(updated_at)");
    }

    // SQLite has no LATERAL, and needs none: its ->> carries the value's own
    // type, so min/max are a single mention already. Emitting the PostgreSQL
    // rewrite here would be a syntax error on a backend that cannot parse it.
    [Fact]
    public void Sqlite_Emits_No_Lateral_And_Keeps_The_Plain_Aggregate()
    {
        var sql = Sql(SqliteSqlDialect.Instance, Aggregate(
            R("min", "lo", "@payload.body.amount"),
            R("max", "hi", "@payload.body.amount")));

        sql.ShouldNotContain("LATERAL");
        sql.ShouldNotContain("jsonb_typeof");
        sql.ShouldNotContain("hoist0");
    }

    // The hoist sits between the table and WHERE. Anywhere else is either a
    // syntax error or a predicate the ACL filter no longer constrains.
    [Fact]
    public void The_Lateral_Sits_Between_The_Table_And_The_Where_Clause()
    {
        var sql = Sql(PostgresSqlDialect.Instance,
            Aggregate(R("min", "lo", "@payload.body.amount")));

        var table = sql.IndexOf("FROM entries", System.StringComparison.Ordinal);
        var lateral = sql.IndexOf("CROSS JOIN LATERAL", System.StringComparison.Ordinal);
        var where = sql.IndexOf("WHERE", System.StringComparison.Ordinal);

        table.ShouldBeGreaterThanOrEqualTo(0);
        lateral.ShouldBeGreaterThan(table);
        where.ShouldBeGreaterThan(lateral);
    }
}
