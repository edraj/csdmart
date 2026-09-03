using System.Data.Common;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// Executes the real SQLite DDL against a real database file and asserts the
// behaviours the schema is supposed to guarantee.
//
// The point is to test the schema's *semantics*, not that the string parses.
// Several of these encode decisions where SQLite's defaults differ from
// PostgreSQL's and the difference is silent: generated-column indexing,
// lexicographic timestamp ordering, partial unique indexes, and the deliberate
// absence of a query_policies CHECK on `groups`.
public sealed class SqliteSchemaTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dmart-schema-{Guid.NewGuid():N}.db");
    private SqliteConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        var settings = Options.Create(new DmartSettings { SqlitePath = _dbPath });
        _factory = new SqliteConnectionFactory(settings);
        var init = new SqliteSchemaInitializer(_factory, Options.Create(new DmartSettings { DatabaseDriver = "sqlite" }), NullLogger<SqliteSchemaInitializer>.Instance);
        await init.StartAsync(CancellationToken.None);
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

    private static async Task<string?> ScalarAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Inserts a minimal valid owner so the deferred FKs on the other tables
    // are satisfiable.
    private static async Task SeedOwnerAsync(DbConnection conn, string shortname = "owner")
        => await ExecAsync(conn, $"""
            INSERT INTO users (uuid, shortname, space_name, subpath, owner_shortname, query_policies)
            VALUES ('{Guid.NewGuid():D}', '{shortname}', 'management', '/users', '{shortname}', '["management:/users:*"]')
            """);

    [Fact]
    public async Task CreateAll_IsIdempotent()
    {
        // Re-running against an existing database must be a no-op, because the
        // initializer runs on every start.
        var init = new SqliteSchemaInitializer(_factory, Options.Create(new DmartSettings { DatabaseDriver = "sqlite" }), NullLogger<SqliteSchemaInitializer>.Instance);
        await init.StartAsync(CancellationToken.None);
        await init.StartAsync(CancellationToken.None);

        await using var conn = await _factory.OpenAsync();
        (await ScalarAsync(conn, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='entries'"))
            .ShouldBe("1");
    }

    [Fact]
    public async Task AllExpectedTablesExist()
    {
        await using var conn = await _factory.OpenAsync();
        foreach (var table in new[]
        {
            "users", "roles", "groups", "permissions", "entries", "attachments",
            "spaces", "histories", "locks", "sessions", "urlshorts", "otps",
            "userpermissionscache",
        })
        {
            (await ScalarAsync(conn,
                $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{table}'"))
                .ShouldBe("1", $"table {table} missing");
        }
    }

    [Fact]
    public async Task SchemaShortnameGeneratedColumn_IsPopulatedAndIndexed()
    {
        await using var conn = await _factory.OpenAsync();
        await SeedOwnerAsync(conn);
        await ExecAsync(conn, """
            INSERT INTO entries (uuid, shortname, space_name, subpath, owner_shortname,
                                 resource_type, payload, query_policies)
            VALUES ('11111111-1111-1111-1111-111111111111', 'e1', 'sp', '/a', 'owner',
                    'content', '{"schema_shortname":"note","body":{"t":"x"}}', '["sp:/a:content"]')
            """);

        // The generated column mirrors the JSON path...
        (await ScalarAsync(conn, "SELECT schema_shortname FROM entries WHERE shortname='e1'"))
            .ShouldBe("note");

        // ...and the planner actually uses its index. This is the whole reason
        // the column exists: SQLite cannot index a bare expression, so without
        // the generated column every schema-filtered query is a full scan.
        await using var explain = conn.CreateCommand();
        explain.CommandText =
            "EXPLAIN QUERY PLAN SELECT shortname FROM entries WHERE schema_shortname = 'note'";
        await using var r = await explain.ExecuteReaderAsync();
        var detail = "";
        while (await r.ReadAsync()) detail += r.GetString(r.FieldCount - 1);
        detail.ShouldContain("idx_entries_schema_shortname");
    }

    [Fact]
    public async Task TimestampsSortLexicographically_AcrossDefaultAndExplicitWrites()
    {
        await using var conn = await _factory.OpenAsync();
        await SeedOwnerAsync(conn);

        // One row takes the column DEFAULT (strftime, padded to 7 digits), one
        // is written by the application formatter. Both must be 27 chars, or
        // ORDER BY silently misorders them.
        await ExecAsync(conn, """
            INSERT INTO entries (uuid, shortname, space_name, subpath, owner_shortname,
                                 resource_type, query_policies)
            VALUES ('22222222-2222-2222-2222-222222222222', 'defaulted', 'sp', '/a', 'owner',
                    'content', '["sp:/a:content"]')
            """);

        var explicitStamp = SqliteValues.FromDateTime(new DateTime(2099, 12, 31, 23, 59, 59, 999));
        await ExecAsync(conn, $"""
            INSERT INTO entries (uuid, shortname, space_name, subpath, owner_shortname,
                                 resource_type, query_policies, created_at)
            VALUES ('33333333-3333-3333-3333-333333333333', 'explicit', 'sp', '/a', 'owner',
                    'content', '["sp:/a:content"]', '{explicitStamp}')
            """);

        (await ScalarAsync(conn, "SELECT length(created_at) FROM entries WHERE shortname='defaulted'"))
            .ShouldBe("27");
        (await ScalarAsync(conn, "SELECT length(created_at) FROM entries WHERE shortname='explicit'"))
            .ShouldBe("27");

        // The far-future explicit row must sort last against the just-now default.
        (await ScalarAsync(conn, "SELECT shortname FROM entries ORDER BY created_at DESC LIMIT 1"))
            .ShouldBe("explicit");
    }

    [Fact]
    public async Task DeferredForeignKeyOnOwner_IsEnforcedAtCommit()
    {
        await using var conn = await _factory.OpenAsync();
        // owner_shortname references users(shortname); no such user exists.
        var ex = await Should.ThrowAsync<SqliteException>(async () => await ExecAsync(conn, """
            INSERT INTO entries (uuid, shortname, space_name, subpath, owner_shortname,
                                 resource_type, query_policies)
            VALUES ('44444444-4444-4444-4444-444444444444', 'orphan', 'sp', '/a', 'ghost',
                    'content', '["sp:/a:content"]')
            """));
        ex.SqliteErrorCode.ShouldBe(19);   // SQLITE_CONSTRAINT
    }

    [Fact]
    public async Task QueryPoliciesCheck_RejectsEmptyOnEntries_ButNotOnGroups()
    {
        await using var conn = await _factory.OpenAsync();
        await SeedOwnerAsync(conn);

        // An empty policy array makes a row invisible to the ACL filter, so
        // entries rejects it — same guarantee as the PostgreSQL CHECK.
        await Should.ThrowAsync<SqliteException>(async () => await ExecAsync(conn, """
            INSERT INTO entries (uuid, shortname, space_name, subpath, owner_shortname,
                                 resource_type, query_policies)
            VALUES ('55555555-5555-5555-5555-555555555555', 'nopolicy', 'sp', '/a', 'owner',
                    'content', '[]')
            """));

        // groups is deliberately exempt — PostgreSQL excludes it too, so
        // constraining it here would reject writes PostgreSQL accepts.
        await ExecAsync(conn, """
            INSERT INTO groups (uuid, shortname, space_name, subpath, owner_shortname, query_policies)
            VALUES ('66666666-6666-6666-6666-666666666666', 'g1', 'management', '/groups', 'owner', '[]')
            """);
        (await ScalarAsync(conn, "SELECT count(*) FROM groups WHERE shortname='g1'")).ShouldBe("1");
    }

    [Fact]
    public async Task EmailUniqueness_IsCaseInsensitiveAndSkipsEmptyStrings()
    {
        await using var conn = await _factory.OpenAsync();
        await SeedOwnerAsync(conn);

        async Task AddUser(string shortname, string email) => await ExecAsync(conn, $"""
            INSERT INTO users (uuid, shortname, space_name, subpath, owner_shortname, email, query_policies)
            VALUES ('{Guid.NewGuid():D}', '{shortname}', 'management', '/users', 'owner', '{email}',
                    '["management:/users:*"]')
            """);

        await AddUser("u1", "Person@Example.com");
        // lower(email) expression index — a case-differing duplicate must collide.
        await Should.ThrowAsync<SqliteException>(() => AddUser("u2", "person@example.com"));

        // '' is excluded from the index, so several such rows coexist.
        await AddUser("u3", "");
        await AddUser("u4", "");
        (await ScalarAsync(conn, "SELECT count(*) FROM users WHERE email = ''")).ShouldBe("2");
    }

    [Fact]
    public async Task GlobalShortnameUniqueness_HoldsForRolesAcrossSubpaths()
    {
        await using var conn = await _factory.OpenAsync();
        await SeedOwnerAsync(conn);

        async Task AddRole(string subpath) => await ExecAsync(conn, $"""
            INSERT INTO roles (uuid, shortname, space_name, subpath, owner_shortname, query_policies)
            VALUES ('{Guid.NewGuid():D}', 'editor', 'management', '{subpath}', 'owner',
                    '["management:{subpath}:*"]')
            """);

        await AddRole("/roles");
        // Roles are fetched and deleted by shortname alone, so the same
        // shortname under a second subpath would make those lookups ambiguous.
        await Should.ThrowAsync<SqliteException>(() => AddRole("/other"));
    }

    [Fact]
    public async Task PatchColumns_AddsMissingColumnToAnOlderDatabase()
    {
        await using (var conn = await _factory.OpenAsync())
        {
            // Simulate a database created before `notes` existed.
            await ExecAsync(conn, "ALTER TABLE users DROP COLUMN notes");
            (await ScalarAsync(conn,
                "SELECT count(*) FROM pragma_table_info('users') WHERE name='notes'")).ShouldBe("0");
        }

        var init = new SqliteSchemaInitializer(_factory, Options.Create(new DmartSettings { DatabaseDriver = "sqlite" }), NullLogger<SqliteSchemaInitializer>.Instance);
        await init.StartAsync(CancellationToken.None);

        await using var after = await _factory.OpenAsync();
        (await ScalarAsync(after,
            "SELECT count(*) FROM pragma_table_info('users') WHERE name='notes'")).ShouldBe("1");
    }
}
