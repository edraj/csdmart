using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.QueryGrammar;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// Covers the last four repositories on SQLite: attachments, spaces, histories
// and the health checks. Each exercises the specific construct that had to
// diverge — an expanded IN list, a client-bound uuid/timestamp where PostgreSQL
// used gen_random_uuid()/NOW(), and the health check that is deliberately not
// implemented here.
public sealed class SqliteRemainingRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dmart-rest-{Guid.NewGuid():N}.db");
    private SqliteConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(
            Options.Create(new DmartSettings { SqlitePath = _dbPath }));
        await new SqliteSchemaInitializer(_factory, NullLogger<SqliteSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);

        await using var conn = await _factory.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO users (uuid, shortname, space_name, subpath, owner_shortname, query_policies)
            VALUES ('00000000-0000-0000-0000-0000000000bb', 'owner', 'management', '/users', 'owner',
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

    [Fact]
    public async Task Attachments_RoundTripAndBatchLookupAcrossSubpaths()
    {
        var repo = new AttachmentRepository(_factory, SqliteSqlDialect.Instance);

        async Task Add(string shortname, string subpath) =>
            await repo.UpsertAsync(new Attachment
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = shortname,
                SpaceName = "sp",
                Subpath = subpath,
                OwnerShortname = "owner",
                ResourceType = ResourceType.Media,
                IsActive = true,
            });

        // Attachments anchor at the parent's own path ("/doc1"), which is the
        // key ListForParentsAsync builds and keys its result by.
        await Add("a1", "/doc1");
        await Add("a2", "/doc2");
        await Add("a3", "/doc3");

        // The batch lookup is the site that had to change shape: PostgreSQL
        // binds one text[] and matches with = ANY, SQLite expands to an IN list.
        var byKey = await repo.ListForParentsAsync("sp", new[]
        {
            ("/", "doc1"), ("/", "doc2"),
        });

        byKey.Keys.ShouldContain("/doc1");
        byKey.Keys.ShouldContain("/doc2");
        byKey.Keys.ShouldNotContain("/doc3");
    }

    [Fact]
    public async Task Histories_WriteAndReadBack()
    {
        var repo = new HistoryRepository(_factory, SqliteSqlDialect.Instance);
        await using (var conn = await _factory.OpenAsync())
        {
            // uuid and timestamp are client-bound here; PostgreSQL used
            // gen_random_uuid() and NOW(), neither of which SQLite has.
            await repo.AppendAsync("sp", "/a", "doc", "actor", null, null, conn);
            await repo.AppendAsync("sp", "/a", "doc", "actor", null, null, conn);
        }

        var page = await repo.QueryHistoryAsync(new Dmart.Models.Api.Query
        {
            Type = QueryType.History, SpaceName = "sp", Subpath = "/a",
            FilterShortnames = new List<string> { "doc" },
        });

        // Two rows, and each carries a distinct client-generated uuid — a
        // constant or empty uuid would collide on the primary key.
        page.Count.ShouldBe(2);
        page.Select(h => h.Uuid).Distinct().Count().ShouldBe(2);
        page.ShouldAllBe(h => h.Timestamp != default);
    }

    [Fact]
    public async Task Spaces_UpsertRoundTripsAndUpdatesInPlace()
    {
        var repo = new SpaceRepository(_factory);
        var space = new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "sp",
            SpaceName = "sp",
            Subpath = "/",
            OwnerShortname = "owner",
            ResourceType = ResourceType.Space,
            IsActive = true,
        };
        await repo.UpsertAsync(space);
        (await repo.GetAsync("sp")).ShouldNotBeNull();

        // Second upsert takes the ON CONFLICT path, where updated_at now reads
        // EXCLUDED.updated_at rather than NOW().
        await repo.UpsertAsync(space with { PrimaryWebsite = "https://example.test" });
        var reloaded = await repo.GetAsync("sp");
        reloaded!.PrimaryWebsite.ShouldBe("https://example.test");
    }

    [Fact]
    public async Task HealthCheck_ReportsFolderContentCheckAsUnavailable()
    {
        var repo = new HealthCheckRepository(_factory);
        var results = await repo.RunAsync("sp", "all");

        // The folder-content query has no SQLite translation yet. It must
        // announce itself rather than silently report zero violations — an
        // operator has to be able to tell "clean" from "not run".
        results.ShouldContain(r => r.Name == "folder_content_violations_unsupported");
        results.ShouldNotContain(r => r.Name == "folder_content_violations");

        // The checks that DO run still work.
        results.ShouldContain(r => r.Name == "orphan_attachments");
        results.ShouldContain(r => r.Name == "stale_locks");
    }
}
