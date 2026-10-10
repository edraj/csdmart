using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// The FTS5 trigram index that replaces PostgreSQL's pg_trgm GIN for
// `@payload.body.x:*foo*` wildcard searches.
//
// Its sync triggers are load-bearing for CORRECTNESS, not just freshness. The
// wildcard filter ANDs this prefilter onto a precise per-path check, so a stale
// index cannot return wrong rows — but it CAN silently drop rows that should
// have matched, which no assertion on the happy path would catch. Hence the
// update and delete cases below.
public sealed class SqliteWildcardIndexTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dmart-trgm-{Guid.NewGuid():N}.db");
    private SqliteConnectionFactory _factory = null!;
    private EntryRepository _repo = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(
            Options.Create(new DmartSettings { SqlitePath = _dbPath }));
        await new SqliteSchemaInitializer(_factory, Options.Create(new DmartSettings { DatabaseDriver = "sqlite" }), NullLogger<SqliteSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        _repo = new EntryRepository(_factory);

        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (uuid, shortname, space_name, subpath, owner_shortname, query_policies)
            VALUES ('00000000-0000-0000-0000-0000000000dd','owner','management','/users','owner',
                    '["management:/users:*"]')
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var s in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + s); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private async Task PutAsync(string shortname, string title)
        => await _repo.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "sp",
            Subpath = "/a",
            OwnerShortname = "owner",
            ResourceType = ResourceType.Content,
            IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = System.Text.Json.JsonSerializer.SerializeToElement(
                    new Dictionary<string, string> { ["title"] = title }),
            },
        });

    private async Task<List<string>> SearchAsync(string expression)
    {
        var rows = await _repo.QueryAsync(new Query
        {
            Type = QueryType.Search, SpaceName = "sp", Subpath = "/a",
            Search = expression, Limit = 50,
        }, CancellationToken.None);
        return rows.Select(r => r.Shortname).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public async Task WildcardSearch_MatchesThroughTheIndex()
    {
        await PutAsync("hit", "hello world");
        await PutAsync("miss", "goodbye moon");

        // Values with spaces would be split by the tokenizer, so this uses a
        // contiguous fragment; quoting is covered by the grammar tests.
        (await SearchAsync("@payload.body.title:*llo*")).ShouldBe(new[] { "hit" });
        // Prefix and suffix forms go through the same prefilter.
        (await SearchAsync("@payload.body.title:hello*")).ShouldBe(new[] { "hit" });
        (await SearchAsync("@payload.body.title:*world")).ShouldBe(new[] { "hit" });
    }

    [Fact]
    public async Task WildcardSearch_SeesAnUpdate()
    {
        await PutAsync("doc", "before text");
        (await SearchAsync("@payload.body.title:*before*")).ShouldBe(new[] { "doc" });

        // Rewriting the row must retire the old trigrams and index the new ones.
        // Without the AFTER UPDATE trigger the first assertion below still
        // passes from the stale index and the second silently returns nothing.
        await PutAsync("doc", "after text");
        (await SearchAsync("@payload.body.title:*after*")).ShouldBe(new[] { "doc" });
        (await SearchAsync("@payload.body.title:*before*")).ShouldBeEmpty();
    }

    [Fact]
    public async Task WildcardSearch_SeesADelete()
    {
        await PutAsync("doomed", "ephemeral content");
        (await SearchAsync("@payload.body.title:*ephemeral*")).ShouldBe(new[] { "doomed" });

        (await _repo.DeleteAsync("sp", "/a", "doomed", ResourceType.Content)).ShouldBeTrue();
        (await SearchAsync("@payload.body.title:*ephemeral*")).ShouldBeEmpty();
    }

    [Fact]
    public async Task WildcardSearch_HandlesArabicAndShortPatterns()
    {
        await PutAsync("ar", "مرحبا بالعالم");
        await PutAsync("en", "plain english");

        // The trigram tokenizer indexes character trigrams, so a script with no
        // word breaks works — unicode61 would have shattered this (audit §5).
        (await SearchAsync("@payload.body.title:*رحبا*")).ShouldBe(new[] { "ar" });

        // And it goes THROUGH the index, not around it. That only holds because
        // JsonbHelpers stores JSON with literal UTF-8: with \uXXXX escapes the
        // indexed text would not contain the Arabic at all.
        await using var conn = await _factory.OpenAsync();
        await using var stored = conn.CreateCommand();
        stored.CommandText = "SELECT payload FROM entries WHERE shortname = 'ar'";
        var raw = (string)(await stored.ExecuteScalarAsync())!;
        raw.ShouldContain("مرحبا", Case.Sensitive,
            "escaped storage would make the wildcard index unable to match Arabic");
        raw.ShouldNotContain("\\u06", Case.Sensitive);

        await using var fts = conn.CreateCommand();
        fts.CommandText = "SELECT count(*) FROM entries_fts WHERE payload LIKE '%رحبا%'";
        Convert.ToInt32(await fts.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(1, "the FTS index itself must contain the Arabic text");

        // Under three characters the index cannot serve the pattern and SQLite
        // scans the FTS content instead. The result must still be correct.
        (await SearchAsync("@payload.body.title:*ai*")).ShouldBe(new[] { "en" });
    }

    [Fact]
    public async Task NegatedWildcard_KeepsRowsMissingTheField()
    {
        await PutAsync("has", "contains needle here");
        // An entry whose payload has no title at all must survive a negated
        // wildcard: absence is not a match.
        await _repo.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "untitled",
            SpaceName = "sp",
            Subpath = "/a",
            OwnerShortname = "owner",
            ResourceType = ResourceType.Content,
            IsActive = true,
        });

        (await SearchAsync("-@payload.body.title:*needle*")).ShouldBe(new[] { "untitled" });
    }

    // The SQL the search path actually emits, with its parameters bound, so
    // the plan and the scoping below are read from the real prefilter. An
    // earlier version of this test checked a hand-written `LIKE '%marker%'`,
    // which used the index, while the emitted `LIKE … ESCAPE '\'` did not:
    // every wildcard search scanned the whole FTS table and CI never saw it.
    private static (string Sql, Action<System.Data.Common.DbCommand> Bind) Emit(string expression)
    {
        var parsed = Dmart.QueryGrammar.SearchExpressionParser.Parse(
            expression, 0, Dmart.QueryGrammar.PlaceholderStyle.Positional, "entries",
            Dmart.QueryGrammar.SqliteSqlDialect.Instance);
        return (string.Join(" AND ", parsed.Clauses), cmd =>
        {
            for (var i = 0; i < parsed.Parameters.Count; i++)
                cmd.Parameters.Add(new SqliteParameter("$" + (i + 1), parsed.Parameters[i].Value));
        });
    }

    [Fact]
    public async Task PrefilterUsesTheIndex_NotAScan()
    {
        await PutAsync("doc", "indexed marker");

        var (where, bind) = Emit("@payload.body.title:*marker*");
        where.ShouldContain("entries_fts MATCH");
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"EXPLAIN QUERY PLAN SELECT shortname FROM entries WHERE {where}";
        bind(cmd);
        await using var r = await cmd.ExecuteReaderAsync();
        var plan = "";
        while (await r.ReadAsync()) plan += r.GetString(r.FieldCount - 1) + "\n";

        // A MATCH served by the trigram index reports `INDEX 0:M…`; a bare
        // `INDEX 0:` is a scan of every row, which this index exists to avoid.
        plan.ShouldContain("entries_fts VIRTUAL TABLE INDEX 0:M", Case.Sensitive);
    }

    [Fact]
    public async Task WildcardSearch_IgnoresCase()
    {
        await PutAsync("lower", "hello world");
        await PutAsync("upper", "HELLO THERE");
        await PutAsync("other", "goodbye");

        // The prefilter was a LIKE under case_sensitive_like, so it was
        // case-sensitive while the precise check folds case: `*Hello*` found
        // neither row. PostgreSQL's ILIKE finds both.
        (await SearchAsync("@payload.body.title:*Hello*")).ShouldBe(new[] { "lower", "upper" });
        (await SearchAsync("@payload.body.title:*HELLO*")).ShouldBe(new[] { "lower", "upper" });
        (await SearchAsync("@payload.body.title:*hel*wor*")).ShouldBe(new[] { "lower" });
    }

    [Fact]
    public async Task WildcardSearch_FindsCharactersStoredEscaped()
    {
        await PutAsync("rnd", "R&D lab");
        await PutAsync("cpp", "C++ notes");
        await PutAsync("tag", "a <b> tag");

        // JsonbHelpers stores < > & ' + escaped (\u0026), so the indexed text
        // never holds "R&D" and a prefilter for it hid the row. The prefilter
        // is declined for such values; the precise check reads the decoded
        // value and stays exact.
        (await SearchAsync("@payload.body.title:*R&D*")).ShouldBe(new[] { "rnd" });
        (await SearchAsync("@payload.body.title:*c++*")).ShouldBe(new[] { "cpp" });
        (await SearchAsync("@payload.body.title:*<b>*")).ShouldBe(new[] { "tag" });
    }

    [Fact]
    public async Task Prefilter_Binds_To_The_Innermost_Entries_Row()
    {
        await PutAsync("plain", "nothing to see");
        await PutAsync("needle", "has the needle");

        // A pushed-down join filters its right side inside
        // `EXISTS (SELECT 1 FROM entries r WHERE …)`. The prefilter said
        // `entries.rowid`, which bound to the OUTER row, so the right side's
        // wildcard filtered the left. Unqualified, it binds to `r`: the right
        // row matches, so EVERY outer row is kept.
        var (where, bind) = Emit("@payload.body.title:*needle*");
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT shortname FROM entries WHERE space_name = 'sp' AND EXISTS "
            + $"(SELECT 1 FROM entries r WHERE r.shortname = 'needle' AND {where}) ORDER BY shortname";
        bind(cmd);
        var names = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) names.Add(r.GetString(0));
        names.ShouldBe(new[] { "needle", "plain" });
    }
}
