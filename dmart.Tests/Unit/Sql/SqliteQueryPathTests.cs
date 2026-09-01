using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// The generic query runner (QueryHelper.RunQueryAsync) composes a statement from
// several fragments and only then meets a connection, so it is the one place a
// PostgreSQL-only construct can survive dialect routing unnoticed. These run it
// for real against SQLite.
public sealed class SqliteQueryPathTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dmart-qp-{Guid.NewGuid():N}.db");
    private SqliteConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(
            Options.Create(new DmartSettings { SqlitePath = _dbPath }));
        await new SqliteSchemaInitializer(_factory, Options.Create(new DmartSettings { DatabaseDriver = "sqlite" }), NullLogger<SqliteSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (uuid, shortname, space_name, subpath, owner_shortname, query_policies)
            VALUES ('00000000-0000-0000-0000-0000000000cc','owner','management','/users','owner',
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

    private static Query Q(string sub = "/") =>
        new() { Type = QueryType.Subpath, SpaceName = "management", Subpath = sub };

    [Fact]
    public async Task UserQuery_RunsThroughTheGenericRunner()
    {
        var repo = new UserRepository(_factory, new AuthzCacheRefresher(),
            new Dmart.Auth.SessionTokenHasher(new DmartSettings { JwtSecret = new string('k', 48) }));
        var rows = await repo.QueryAsync(Q("/users"), CancellationToken.None);
        rows.ShouldNotBeEmpty();
        rows[0].Shortname.ShouldBe("owner");
    }

    [Fact]
    public async Task UserQuery_WithActorAppliesAclAndStillRuns()
    {
        var repo = new UserRepository(_factory, new AuthzCacheRefresher(),
            new Dmart.Auth.SessionTokenHasher(new DmartSettings { JwtSecret = new string('k', 48) }));
        var rows = await repo.QueryAsync(Q("/users"), "owner",
            new List<string> { "management:/users:*" }, CancellationToken.None);
        rows.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task EntryQuery_RunsThroughTheGenericRunner()
    {
        var repo = new EntryRepository(_factory);
        var rows = await repo.QueryAsync(
            new Query { Type = QueryType.Subpath, SpaceName = "management", Subpath = "/users" },
            CancellationToken.None);
        rows.ShouldNotBeNull();
    }

    [Fact]
    public async Task AttachmentQuery_RunsThroughTheGenericRunner()
    {
        var repo = new AttachmentRepository(_factory, Dmart.QueryGrammar.SqliteSqlDialect.Instance);
        var rows = await repo.QueryAsync(
            new Query { Type = QueryType.Subpath, SpaceName = "management", Subpath = "/users" },
            CancellationToken.None);
        rows.ShouldNotBeNull();
    }

    [Fact]
    public async Task CountQuery_RunsThroughTheGenericRunner()
    {
        var repo = new UserRepository(_factory, new AuthzCacheRefresher(),
            new Dmart.Auth.SessionTokenHasher(new DmartSettings { JwtSecret = new string('k', 48) }));
        (await repo.CountQueryAsync(Q("/users"), CancellationToken.None)).ShouldBe(1);
    }

    // TotalCap bounds the pagination count so it stops scanning instead of
    // walking every matching row. The contract RunCountAsync exposes is the
    // cap+1 sentinel: at most cap+1, where cap+1 means "at least cap".
    [Theory]
    [InlineData(0, 6)]    // unlimited — exact, unchanged behaviour
    [InlineData(10, 6)]   // cap above the row count — still exact
    [InlineData(6, 6)]    // cap exactly on the row count — exact, no sentinel
    [InlineData(2, 3)]    // cap below — stops at cap+1
    [InlineData(1, 2)]
    public async Task CountQuery_HonoursTotalCap(int cap, int expected)
    {
        await SeedUsersAsync(5);   // plus the one from InitializeAsync = 6
        var repo = new UserRepository(_factory, new AuthzCacheRefresher(),
            new Dmart.Auth.SessionTokenHasher(new DmartSettings { JwtSecret = new string('k', 48) }));

        var total = await repo.CountQueryAsync(
            Q("/users") with { TotalCap = cap }, CancellationToken.None);

        total.ShouldBe(expected);
    }

    // The cap must be applied AFTER the ACL predicate. Capping the raw scan
    // first would count rows the actor cannot see, which would leak the size of
    // a collection through a pagination total.
    [Fact]
    public async Task CountQuery_AppliesTheCapAfterAclFiltering()
    {
        await SeedUsersAsync(5);
        var repo = new UserRepository(_factory, new AuthzCacheRefresher(),
            new Dmart.Auth.SessionTokenHasher(new DmartSettings { JwtSecret = new string('k', 48) }));

        // "stranger" owns nothing here and holds a policy matching nothing, so
        // every row is filtered out — a cap of 2 must still report 0, not 2.
        var total = await repo.CountQueryAsync(
            Q("/users") with { TotalCap = 2 }, "stranger",
            new List<string> { "nothing:matches:%" }, CancellationToken.None);

        total.ShouldBe(0);
    }

    // `subpath LIKE $n || '/%'` reads `_` as "any one character", so a query
    // scoped to /my_folder also returned /myXfolder and /my-folder — different
    // folders, which an actor may hold no permission on at all. The ACL
    // predicate used to drop those rows on the way out (an actor's policy IS
    // escaped before it becomes a LIKE pattern), so the over-match stayed
    // invisible until a query could legitimately skip that predicate.
    //
    // Runs against SQLite for real, which is the half of the fix a golden file
    // cannot check: `replace(...)` and `ESCAPE '\'` have to mean the same thing
    // on both engines, or the two backends answer differently.
    [Fact]
    public async Task SubpathScope_DoesNotReachOneCharacterSiblings()
    {
        await InsertUserAsync("in_scope", "/my_folder");
        await InsertUserAsync("in_scope_deep", "/my_folder/deep");
        await InsertUserAsync("wildcard_sibling", "/myXfolder");
        await InsertUserAsync("dash_sibling", "/my-folder/deep");

        var repo = new UserRepository(_factory, new AuthzCacheRefresher(),
            new Dmart.Auth.SessionTokenHasher(new DmartSettings { JwtSecret = new string('k', 48) }));
        var rows = await repo.QueryAsync(Q("/my_folder"), CancellationToken.None);

        // The folder itself and everything under it, and nothing else. The
        // positive half matters as much as the negative: over-escaping would
        // stop /my_folder from finding its own descendants.
        rows.Select(r => r.Shortname).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(new[] { "in_scope", "in_scope_deep" });
    }

    // A subpath that really does contain a LIKE metacharacter still finds its
    // own children — the escape has to make `%` literal, not drop the row.
    [Fact]
    public async Task SubpathScope_HandlesAPercentInTheSubpathItself()
    {
        await InsertUserAsync("pct", "/a%b/deep");
        await InsertUserAsync("not_pct", "/axxb/deep");

        var repo = new UserRepository(_factory, new AuthzCacheRefresher(),
            new Dmart.Auth.SessionTokenHasher(new DmartSettings { JwtSecret = new string('k', 48) }));
        var rows = await repo.QueryAsync(Q("/a%b"), CancellationToken.None);

        rows.Select(r => r.Shortname).ShouldBe(new[] { "pct" });
    }

    private async Task InsertUserAsync(string shortname, string subpath)
    {
        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (uuid, shortname, space_name, subpath, owner_shortname, query_policies)
            VALUES ($u, $s, 'management', $p, 'owner', '["management:/users:*"]')
            """;
        foreach (var (n, v) in new[]
                 { ("$u", Guid.NewGuid().ToString()), ("$s", shortname), ("$p", subpath) })
        {
            var prm = cmd.CreateParameter();
            prm.ParameterName = n;
            prm.Value = v;
            cmd.Parameters.Add(prm);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedUsersAsync(int count)
    {
        await using var conn = await _factory.OpenAsync();
        for (var i = 0; i < count; i++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO users (uuid, shortname, space_name, subpath, owner_shortname, query_policies)
                VALUES ($u, $s, 'management', '/users', 'owner', '["management:/users:*"]')
                """;
            foreach (var (n, v) in new[]
                     { ("$u", $"00000000-0000-0000-0000-0000000000{i:d2}"), ("$s", $"user{i}") })
            {
                var prm = cmd.CreateParameter();
                prm.ParameterName = n;
                prm.Value = v;
                cmd.Parameters.Add(prm);
            }
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
