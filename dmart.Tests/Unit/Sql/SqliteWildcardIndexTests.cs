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
        await new SqliteSchemaInitializer(_factory, NullLogger<SqliteSchemaInitializer>.Instance)
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

    [Fact]
    public async Task PrefilterUsesTheIndex_NotAScan()
    {
        await PutAsync("doc", "indexed marker");

        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "EXPLAIN QUERY PLAN SELECT rowid FROM entries_fts WHERE payload LIKE '%marker%'";
        await using var r = await cmd.ExecuteReaderAsync();
        var plan = "";
        while (await r.ReadAsync()) plan += r.GetString(r.FieldCount - 1);

        // fts5's trigram LIKE optimization reports itself by appending the
        // constraint to the virtual-table index string. Without it the query
        // degrades to reading every row, which is the whole thing this index
        // exists to avoid.
        plan.ShouldContain("entries_fts");
        plan.ShouldContain(":L", Case.Sensitive);
    }
}
