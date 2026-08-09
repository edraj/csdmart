using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// EntryRepository carries the densest PostgreSQL-specific SQL in the codebase.
// These exercise the four constructs that could not be translated token-for-token:
//
//   * SELECT ... FOR UPDATE + RETURNING (xmax = 0)  -> derived from the
//     in-transaction read
//   * relationships @> probe::jsonb                 -> a json_each walk
//   * substring(x FROM n)                           -> substr(x, n)
//   * UPDATE ... FROM (VALUES ...) AS v(cols)       -> WITH v(cols) AS (VALUES ...)
//
// The move tests matter most: the bulk descendant update builds its SQL and its
// parameter list separately, so an off-by-one in placeholder numbering would
// still execute and silently write the wrong rows.
public sealed class SqliteEntryRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dmart-entry-{Guid.NewGuid():N}.db");
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
            VALUES ('00000000-0000-0000-0000-0000000000aa', 'owner', 'management', '/users', 'owner',
                    '["management:/users:*"]')
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    private static Entry NewEntry(string shortname, string subpath, ResourceType type = ResourceType.Content)
        => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "sp",
            Subpath = subpath,
            OwnerShortname = "owner",
            ResourceType = type,
            IsActive = true,
        };

    [Fact]
    public async Task UpsertAndGet_RoundTrips()
    {
        var e = NewEntry("doc", "/a");
        await _repo.UpsertAsync(e);

        var got = await _repo.GetAsync("sp", "/a", "doc");
        got.ShouldNotBeNull();
        got!.Shortname.ShouldBe("doc");
        got.OwnerShortname.ShouldBe("owner");
        got.IsActive.ShouldBeTrue();
        // query_policies is a JSON array on SQLite; a round trip proves both
        // the write conversion and the read parse.
        got.QueryPolicies.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task UpsertWithPrior_ReportsInsertedThenUpdated()
    {
        var e = NewEntry("doc", "/a");

        // First call: no incumbent, so this is an insert. PostgreSQL learns
        // that from xmax; SQLite derives it from the in-transaction read.
        var (prior1, inserted1) = await _repo.UpsertWithPriorAsync(e);
        prior1.ShouldBeNull();
        inserted1.ShouldBeTrue();

        // Second call: the incumbent is returned and this is an update.
        var (prior2, inserted2) = await _repo.UpsertWithPriorAsync(e with { Slug = "changed" });
        prior2.ShouldNotBeNull();
        inserted2.ShouldBeFalse();
        (await _repo.GetAsync("sp", "/a", "doc"))!.Slug.ShouldBe("changed");
    }

    [Fact]
    public async Task FindFirstReferencer_MatchesRelationshipTarget()
    {
        await _repo.UpsertAsync(NewEntry("target", "/a"));

        var referencer = NewEntry("referencer", "/b") with
        {
            // Relationships are loose JSON dictionaries, matching the probe
            // shape FindFirstReferencerAsync serializes.
            Relationships = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["related_to"] = new Dictionary<string, object>
                    {
                        ["type"] = "content",
                        ["space_name"] = "sp",
                        ["subpath"] = "/a",
                        ["shortname"] = "target",
                    },
                },
            },
        };
        await _repo.UpsertAsync(referencer);
        await _repo.UpsertAsync(NewEntry("unrelated", "/b"));

        // The PostgreSQL form is `relationships @> probe::jsonb`; SQLite walks
        // the array with json_each and compares the four fields.
        var hit = await _repo.FindFirstReferencerAsync(
            "sp", "/a", "target", ResourceType.Content, null, null, null);
        hit.ShouldNotBeNull();
        hit!.Value.Shortname.ShouldBe("referencer");

        // A non-referenced target must find nothing.
        var miss = await _repo.FindFirstReferencerAsync(
            "sp", "/b", "unrelated", ResourceType.Content, null, null, null);
        miss.ShouldBeNull();
    }

    [Fact]
    public async Task FindFirstReferencer_HonoursTheExclusion()
    {
        var self = NewEntry("selfref", "/a") with
        {
            Relationships = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["related_to"] = new Dictionary<string, object>
                    {
                        ["type"] = "content",
                        ["space_name"] = "sp",
                        ["subpath"] = "/a",
                        ["shortname"] = "selfref",
                    },
                },
            },
        };
        await _repo.UpsertAsync(self);

        // Excluding the only referencer must yield no match — this is what
        // stops a self-reference from blocking its own delete.
        var excluded = await _repo.FindFirstReferencerAsync(
            "sp", "/a", "selfref", ResourceType.Content, "sp", "/a", "selfref");
        excluded.ShouldBeNull();
    }

    [Fact]
    public async Task Move_RelocatesRootAndRewritesDescendantSubpaths()
    {
        // A folder with descendants two levels deep. The descendant rewrite is
        // the substr()/CTE path, and it runs per page.
        await _repo.UpsertAsync(NewEntry("folder", "/", ResourceType.Folder));
        await _repo.UpsertAsync(NewEntry("child", "/folder"));
        await _repo.UpsertAsync(NewEntry("grandchild", "/folder/child"));
        // A sibling that must NOT move.
        await _repo.UpsertAsync(NewEntry("bystander", "/elsewhere"));

        var source = (await _repo.GetAsync("sp", "/", "folder"))!;
        var moved = await _repo.MoveAsync(source, new Dmart.Models.Core.Locator(
            type: ResourceType.Folder, spaceName: "sp", shortname: "folder", subpath: "/archive"));

        moved.ShouldBeGreaterThan(0);

        // Root landed at the destination.
        (await _repo.GetAsync("sp", "/archive", "folder")).ShouldNotBeNull();
        (await _repo.GetAsync("sp", "/", "folder")).ShouldBeNull();

        // Descendants had their prefix translated, not their names. An
        // off-by-one in the bulk statement's placeholder numbering would show
        // up here as a wrong or missing subpath.
        (await _repo.GetAsync("sp", "/archive/folder", "child")).ShouldNotBeNull();
        (await _repo.GetAsync("sp", "/archive/folder/child", "grandchild")).ShouldNotBeNull();

        // And the unrelated entry is untouched.
        (await _repo.GetAsync("sp", "/elsewhere", "bystander")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Move_RegeneratesDescendantQueryPolicies()
    {
        await _repo.UpsertAsync(NewEntry("folder", "/", ResourceType.Folder));
        await _repo.UpsertAsync(NewEntry("child", "/folder"));

        var source = (await _repo.GetAsync("sp", "/", "folder"))!;
        await _repo.MoveAsync(source, new Dmart.Models.Core.Locator(
            type: ResourceType.Folder, spaceName: "sp", shortname: "folder", subpath: "/archive"));

        // Policies are position-derived, so a moved row whose policies were not
        // regenerated becomes invisible to the ACL filter at its new path.
        // Assert against what the generator produces at the destination rather
        // than against an assumed string shape.
        var child = await _repo.GetAsync("sp", "/archive/folder", "child");
        child.ShouldNotBeNull();
        var expected = Dmart.Utils.QueryPolicies.Generate(child!);
        child!.QueryPolicies.ShouldBe(expected, ignoreOrder: true);

        // And they must actually have moved: the pre-move policies differ.
        var atOldPath = Dmart.Utils.QueryPolicies.Generate(
            child with { Subpath = "/folder" });
        child.QueryPolicies.OrderBy(x => x, StringComparer.Ordinal).ShouldNotBe(
            atOldPath.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task CountAndDelete_Work()
    {
        await _repo.UpsertAsync(NewEntry("a", "/x"));
        await _repo.UpsertAsync(NewEntry("b", "/x/deep"));

        // Hierarchical count: exact subpath plus descendants.
        (await _repo.CountAsync("sp", "/x")).ShouldBe(2);

        (await _repo.DeleteAsync("sp", "/x", "a", ResourceType.Content)).ShouldBeTrue();
        (await _repo.GetAsync("sp", "/x", "a")).ShouldBeNull();
        (await _repo.CountAsync("sp", "/x")).ShouldBe(1);
    }
}
