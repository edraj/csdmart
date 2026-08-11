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
}
