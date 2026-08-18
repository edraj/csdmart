using System.Data.Common;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.QueryGrammar;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// Executes SqliteSqlDialect's output against a real SQLite database.
//
// A dialect test that only compared strings would prove nothing — the whole
// risk is that the SQL is syntactically fine and semantically wrong. These
// run the emitted WHERE clauses and assert which rows come back, with the ACL
// cases written specifically to catch the widening that SQLite's default
// case-insensitive LIKE would introduce.
public sealed class SqliteDialectExecutionTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dmart-dialect-{Guid.NewGuid():N}.db");
    private SqliteConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(
            Options.Create(new DmartSettings { SqlitePath = _dbPath }));
        await new SqliteSchemaInitializer(_factory, Options.Create(new DmartSettings { DatabaseDriver = "sqlite" }), NullLogger<SqliteSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);

        await using var conn = await _factory.OpenAsync();
        await ExecAsync(conn, """
            INSERT INTO users (uuid, shortname, space_name, subpath, owner_shortname, query_policies)
            VALUES ('00000000-0000-0000-0000-0000000000ff', 'owner', 'management', '/users', 'owner',
                    '["management:/users:*"]')
            """);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    private static async Task ExecAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task AddEntryAsync(
        string shortname, string subpath, string resourceType, string payloadJson,
        string tagsJson, string policiesJson, string aclJson = "null")
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entries (uuid, shortname, space_name, subpath, owner_shortname,
                                 resource_type, payload, tags, query_policies, acl)
            VALUES ($1, $2, 'sp', $3, 'owner', $4, $5, $6, $7, $8)
            """;
        void Add(string n, object v)
        {
            var p = cmd.CreateParameter(); p.ParameterName = n; p.Value = v; cmd.Parameters.Add(p);
        }
        Add("$1", SqliteValues.FromGuid(Guid.NewGuid()));
        Add("$2", shortname);
        Add("$3", subpath);
        Add("$4", resourceType);
        Add("$5", payloadJson);
        Add("$6", tagsJson);
        Add("$7", policiesJson);
        Add("$8", aclJson);
        await cmd.ExecuteNonQueryAsync();
    }

    // Runs a WHERE clause built by QueryHelper under the SQLite dialect and
    // returns the matching shortnames.
    private async Task<List<string>> RunAsync(string where, List<NpgsqlParameter> args)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT shortname FROM entries WHERE {where} ORDER BY shortname";
        // The grammar emits positional $N, which SQLite binds by NAME — "$1"
        // is a valid SQLite parameter name. That is what lets the same emitted
        // text serve both providers (audit §7.4).
        for (var i = 0; i < args.Count; i++)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "$" + (i + 1);
            p.Value = args[i].Value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        var result = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) result.Add(r.GetString(0));
        return result;
    }

    private static Query Q(string subpath = "/") => new()
    {
        Type = QueryType.Subpath, SpaceName = "sp", Subpath = subpath,
    };

    [Fact]
    public async Task Search_On_QueryPolicies_Dereferences_The_Element_And_Executes()
    {
        // REGRESSION GUARD. `query_policies` is the only TextArrayColumn, and
        // BuildTextArraySql used to interpolate the bare iteration alias into
        // the predicate (`WHERE elem = ?`). PostgreSQL's `unnest` yields a
        // COLUMN so that works there; SQLite's `json_each` yields a TABLE whose
        // element is `elem.value`, so every `@query_policies:...` search died at
        // execution with `no such column: elem`. String-only assertions would
        // have passed — the SQL is syntactically valid — so this runs it.
        await AddEntryAsync("a", "/x", "content", "{}", "[]", """["sp:/x:content"]""");
        await AddEntryAsync("b", "/x", "content", "{}", "[]", """["other:/y:content"]""");

        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(
            Q() with { Search = "@query_policies:sp:/x:content" },
            args, SqliteSqlDialect.Instance, "entries");

        where.ShouldContain("elem.value");
        where.ShouldNotContain("WHERE elem =");
        (await RunAsync(where, args)).ShouldBe(new[] { "a" });
    }

    [Fact]
    public async Task Search_On_QueryPolicies_Wildcard_Executes()
    {
        // The glob branch takes the ILike path, which had the same bare-alias
        // bug and the same "valid SQL, wrong column" failure mode.
        await AddEntryAsync("a", "/x", "content", "{}", "[]", """["sp:/x:content"]""");
        await AddEntryAsync("b", "/x", "content", "{}", "[]", """["other:/y:content"]""");

        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(
            Q() with { Search = "@query_policies:sp:*" },
            args, SqliteSqlDialect.Instance, "entries");

        (await RunAsync(where, args)).ShouldBe(new[] { "a" });
    }

    [Fact]
    public async Task FilterTypes_ExpandsToInList_AndMatches()
    {
        await AddEntryAsync("a", "/x", "content", "{}", "[]", """["sp:/x:content"]""");
        await AddEntryAsync("b", "/x", "folder", "{}", "[]", """["sp:/x:folder"]""");

        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(
            Q() with { FilterTypes = new() { ResourceType.Content } },
            args, SqliteSqlDialect.Instance, "entries");

        // PostgreSQL binds one array parameter; SQLite has no array type, so
        // the dialect expands the list into one parameter per value.
        where.ShouldContain("resource_type IN ($2)");
        (await RunAsync(where, args)).ShouldBe(new[] { "a" });
    }

    [Fact]
    public async Task FilterSchemaNames_UsesGeneratedColumn_AndMatches()
    {
        await AddEntryAsync("a", "/x", "content", """{"schema_shortname":"note"}""", "[]", """["sp:/x:content"]""");
        await AddEntryAsync("b", "/x", "content", """{"schema_shortname":"article"}""", "[]", """["sp:/x:content"]""");

        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(
            Q() with { FilterSchemaNames = new() { "note" } },
            args, SqliteSqlDialect.Instance, "entries");

        // Must name the generated column, not the json path — the index is
        // only selected for the column form.
        where.ShouldContain("schema_shortname IN");
        where.ShouldNotContain("->>");
        (await RunAsync(where, args)).ShouldBe(new[] { "a" });
    }

    [Fact]
    public async Task FilterTags_MatchesAnyTag_ViaJsonEach()
    {
        await AddEntryAsync("a", "/x", "content", "{}", """["red","blue"]""", """["sp:/x:content"]""");
        await AddEntryAsync("b", "/x", "content", "{}", """["green"]""", """["sp:/x:content"]""");

        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(
            Q() with { FilterTags = new() { "blue", "purple" } },
            args, SqliteSqlDialect.Instance, "entries");

        (await RunAsync(where, args)).ShouldBe(new[] { "a" });
    }

    [Fact]
    public async Task HierarchicalSubpath_MatchesChildren_CaseSensitively()
    {
        await AddEntryAsync("exact", "/posts", "content", "{}", "[]", """["sp:/posts:content"]""");
        await AddEntryAsync("child", "/posts/2026", "content", "{}", "[]", """["sp:/posts:content"]""");
        await AddEntryAsync("other", "/POSTS/2026", "content", "{}", "[]", """["sp:/posts:content"]""");

        var args = new List<NpgsqlParameter>();
        var where = QueryHelper.BuildWhereClause(Q("/posts"), args, SqliteSqlDialect.Instance, "entries");

        // '/POSTS/2026' must NOT match: PostgreSQL's LIKE is case-sensitive and
        // PRAGMA case_sensitive_like=ON makes SQLite agree.
        (await RunAsync(where, args)).ShouldBe(new[] { "child", "exact" });
    }

    // ── ACL ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AclPolicyMatch_IsCaseSensitive()
    {
        await AddEntryAsync("lower", "/users", "content", "{}", "[]", """["management:/users:content"]""");
        await AddEntryAsync("upper", "/users", "content", "{}", "[]", """["management:/USERS:content"]""");

        var sql = new System.Text.StringBuilder("1=1 ");
        var args = new List<NpgsqlParameter>();
        QueryHelper.AppendAclFilter(sql, args, "someone-else", "entries",
            new List<string> { "management:/users:*" }, SqliteSqlDialect.Instance);

        // The caller does not own either row and is in neither ACL, so only the
        // policy match can admit a row. A case-insensitive LIKE would return
        // both — access PostgreSQL denies.
        (await RunAsync(sql.ToString(), args)).ShouldBe(new[] { "lower" });
    }

    [Fact]
    public async Task AclPolicyMatch_TreatsUnderscoreLiterally()
    {
        await AddEntryAsync("literal", "/a", "content", "{}", "[]", """["sp:/a_b:content"]""");
        await AddEntryAsync("wildcard", "/a", "content", "{}", "[]", """["sp:/aXb:content"]""");

        var sql = new System.Text.StringBuilder("1=1 ");
        var args = new List<NpgsqlParameter>();
        QueryHelper.AppendAclFilter(sql, args, "someone-else", "entries",
            new List<string> { "sp:/a_b:content" }, SqliteSqlDialect.Instance);

        // '_' is a LIKE metacharacter; the escaping must make it literal, so
        // 'sp:/aXb:content' must not match. GLOB could not express this — it
        // has no ESCAPE clause.
        (await RunAsync(sql.ToString(), args)).ShouldBe(new[] { "literal" });
    }

    [Fact]
    public async Task AclPolicyWildcard_ExpandsStar()
    {
        await AddEntryAsync("in", "/a", "content", "{}", "[]", """["sp:/a:content"]""");
        await AddEntryAsync("out", "/a", "content", "{}", "[]", """["other:/z:content"]""");

        var sql = new System.Text.StringBuilder("1=1 ");
        var args = new List<NpgsqlParameter>();
        QueryHelper.AppendAclFilter(sql, args, "someone-else", "entries",
            new List<string> { "sp:/a:*" }, SqliteSqlDialect.Instance);

        (await RunAsync(sql.ToString(), args)).ShouldBe(new[] { "in" });
    }

    [Fact]
    public async Task AclGrants_AdmitsRowWhenUserHasQueryAction()
    {
        await AddEntryAsync("granted", "/a", "content", "{}", "[]", """["sp:/a:content"]""",
            """[{"user_shortname":"bob","allowed_actions":["query","update"]}]""");
        await AddEntryAsync("wrong-action", "/a", "content", "{}", "[]", """["sp:/a:content"]""",
            """[{"user_shortname":"bob","allowed_actions":["update"]}]""");
        await AddEntryAsync("wrong-user", "/a", "content", "{}", "[]", """["sp:/a:content"]""",
            """[{"user_shortname":"carol","allowed_actions":["query"]}]""");
        // A non-array acl must be tolerated, not raise.
        await AddEntryAsync("acl-object", "/a", "content", "{}", "[]", """["sp:/a:content"]""",
            """{"not":"an array"}""");

        var sql = new System.Text.StringBuilder("1=1 ");
        var args = new List<NpgsqlParameter>();
        QueryHelper.AppendAclFilter(sql, args, "bob", "entries", null, SqliteSqlDialect.Instance);

        (await RunAsync(sql.ToString(), args)).ShouldBe(new[] { "granted" });
    }

    [Fact]
    public async Task AclFilter_IsSkippedForAttachmentsAndHistories()
    {
        // Parity with PostgreSQL: Python skips ACL on these tables.
        foreach (var table in new[] { "attachments", "histories" })
        {
            var sql = new System.Text.StringBuilder();
            var args = new List<NpgsqlParameter>();
            QueryHelper.AppendAclFilter(sql, args, "bob", table,
                new List<string> { "sp:*" }, SqliteSqlDialect.Instance);
            sql.ToString().ShouldBeEmpty();
            args.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task JsonTypeIs_DistinguishesNumbersAndBooleans()
    {
        // json_type's vocabulary is finer than jsonb_typeof's: `number` splits
        // into integer/real and `boolean` into true/false. Collapsing either to
        // a single name would silently drop rows.
        await using var conn = await _factory.OpenAsync();
        var d = SqliteSqlDialect.Instance;

        async Task<string?> Test(string json, JsonKind kind)
        {
            var expr = d.JsonValue($"'{json}'", new[] { "v" });
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {d.JsonTypeIs(expr, kind)}";
            return Convert.ToString(await cmd.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        (await Test("""{"v":42}""", JsonKind.Number)).ShouldBe("1");
        (await Test("""{"v":4.2}""", JsonKind.Number)).ShouldBe("1");
        (await Test("""{"v":true}""", JsonKind.Boolean)).ShouldBe("1");
        (await Test("""{"v":false}""", JsonKind.Boolean)).ShouldBe("1");
        (await Test("""{"v":"s"}""", JsonKind.String)).ShouldBe("1");
        (await Test("""{"v":[1]}""", JsonKind.Array)).ShouldBe("1");
        (await Test("""{"v":42}""", JsonKind.String)).ShouldBe("0");
    }
}
