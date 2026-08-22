using Dmart.DataAdapters.Sql;
using Dmart.Utils;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Dmart.Tests.Unit.Sql;

// Regression pins for @created_at:[ms ms] on SQLite: the dialect's epoch-ms
// bound expression (strftime + 'localtime' + '0000' padding) must agree with
// SqliteValues.TimestampFormat and with the naive-LOCAL wall-clock convention
// (TimeUtils.Now). Written while diagnosing the QuerySearchFeatureMatrixTests
// timestamp failure — the fixture there stamped DateTime.UtcNow, which SQLite's
// lexicographic comparison exposed on any non-UTC machine. These two facts keep
// the storage format, the bound expression, and the server binding path from
// drifting apart again.
public class SqliteTimestampRangeTests(ITestOutputHelper output)
{
    [Fact]
    public void Epoch_Ms_Bound_Comparison_Against_Stored_Format()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        var now = TimeUtils.Now();
        var stored = SqliteValues.FromDateTime(now);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var hourAgo = nowMs - 3_600_000;
        var hourAhead = nowMs + 3_600_000;

        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (created_at TEXT)";
            create.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO t VALUES ($1)";
            ins.Parameters.AddWithValue("$1", stored);
            ins.ExecuteNonQuery();
        }

        string Scalar(string sql, params (string, object)[] ps)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            return cmd.ExecuteScalar()?.ToString() ?? "<NULL>";
        }

        var lowExpr = Dmart.QueryGrammar.SqliteSqlDialect.Instance
            .TimestampFrom("$1", epochMillis: true);
        output.WriteLine($"stored           : {stored}");
        output.WriteLine($"nowMs            : {nowMs}");
        output.WriteLine($"bound expr       : {lowExpr}");
        output.WriteLine($"low evaluated    : {Scalar($"SELECT {lowExpr}", ("$1", hourAgo.ToString()))}");
        output.WriteLine($"high evaluated   : {Scalar($"SELECT {lowExpr}", ("$1", hourAhead.ToString()))}");
        output.WriteLine($"sqlite localtime : {Scalar("SELECT datetime('now','localtime')")}");
        output.WriteLine($"sqlite utc       : {Scalar("SELECT datetime('now')")}");

        var count = Scalar(
            $"SELECT count(*) FROM t WHERE created_at BETWEEN {lowExpr.Replace("$1", "$lo")} AND {lowExpr.Replace("$1", "$hi")}",
            ("$lo", hourAgo.ToString()), ("$hi", hourAhead.ToString()));
        output.WriteLine($"BETWEEN count    : {count}");

        Assert.Equal("1", count);
    }

    // Same probe, but through the REAL server layers: BuildWhereClause with
    // the SQLite dialect + DbParams.BindAll, exactly what RunQueryAsync does.
    [Fact]
    public void Server_Layer_Created_At_Range_Reproduction()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE entries (shortname TEXT, space_name TEXT, subpath TEXT, created_at TEXT)";
            create.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO entries VALUES ('it_alpha', 's', '/x', $1)";
            ins.Parameters.AddWithValue("$1", SqliteValues.FromDateTime(TimeUtils.Now()));
            ins.ExecuteNonQuery();
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var q = new Dmart.Models.Api.Query
        {
            Type = Dmart.Models.Enums.QueryType.Search,
            SpaceName = "s",
            Subpath = "/x",
            ExactSubpath = true,
            Search = $"@created_at:[{nowMs - 3_600_000} {nowMs + 3_600_000}]",
        };
        var args = new List<Npgsql.NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(
            q, args, Dmart.QueryGrammar.SqliteSqlDialect.Instance, "entries");
        output.WriteLine($"WHERE: {where}");
        for (var i = 0; i < args.Count; i++)
            output.WriteLine($"  ${i + 1} = {args[i].Value} ({args[i].Value?.GetType().Name})");

        using var cmd = conn.Command($"SELECT count(*) FROM entries WHERE {where}");
        DbParams.BindAll(cmd, args);
        var count = cmd.ExecuteScalar()?.ToString();
        output.WriteLine($"server-layer count: {count}");
        Assert.Equal("1", count);
    }
}
