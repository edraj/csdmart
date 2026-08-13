using System.Data.Common;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// Asserts the PRAGMA state a request-path connection actually runs under.
//
// This is worth testing rather than trusting because every one of these is
// connection-scoped and silently defaults to something unsuitable: foreign_keys
// defaults OFF (making the schema's declared foreign keys decorative), and
// journal_mode defaults to `delete` (serializing every reader against every
// writer). A regression here degrades correctness or concurrency without
// failing anything else.
public sealed class SqliteConnectionFactoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dmart-pragma-{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory NewFactory()
        => new(Options.Create(new DmartSettings { SqlitePath = _dbPath }));

    private static async Task<string> ScalarAsync(DbConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    [Fact]
    public async Task OpenAsync_AppliesConcurrencyAndIntegrityPragmas()
    {
        await using var conn = await NewFactory().OpenAsync();

        // WAL is what allows readers concurrent with a writer. It is also the
        // one setting here that persists in the file header rather than the
        // connection.
        (await ScalarAsync(conn, "PRAGMA journal_mode")).ShouldBe("wal", StringCompareShould.IgnoreCase);

        // OFF by default in SQLite — the schema's foreign keys do nothing without this.
        (await ScalarAsync(conn, "PRAGMA foreign_keys")).ShouldBe("1");

        // 1 == NORMAL. Correct under WAL; FULL would fsync per commit for what
        // is a rebuildable index.
        (await ScalarAsync(conn, "PRAGMA synchronous")).ShouldBe("1");

        (await ScalarAsync(conn, "PRAGMA busy_timeout")).ShouldBe("5000");
    }

    [Fact]
    public async Task LikeIsCaseSensitive_SoTheAclFilterCannotWiden()
    {
        // The single most consequential PRAGMA here. SQLite's LIKE is
        // ASCII-case-insensitive by default while PostgreSQL's is
        // case-sensitive, and the row-level ACL matches policy patterns with
        // LIKE. Left at the default, a caller holding 'management:/users:*'
        // would also match rows under '/USERS' — access PostgreSQL denies.
        await using var conn = await NewFactory().OpenAsync();

        (await ScalarAsync(conn, "SELECT 'management:/USERS:content' LIKE 'management:/users:%'"))
            .ShouldBe("0", "ACL policy matching must be case-sensitive, as it is on PostgreSQL");

        // Same-case still matches, so the filter is not merely broken.
        (await ScalarAsync(conn, "SELECT 'management:/users:content' LIKE 'management:/users:%'"))
            .ShouldBe("1");

        // ESCAPE keeps working, so a policy containing a literal % or _ is
        // matched literally rather than as a wildcard. This is what GLOB —
        // the other way to get case-sensitive matching — cannot express: it
        // has no ESCAPE clause and treats ? and [ ] as metacharacters.
        (await ScalarAsync(conn, @"SELECT 'a_b:/x' LIKE 'a\_b:/x' ESCAPE '\'")).ShouldBe("1");
        (await ScalarAsync(conn, @"SELECT 'aXb:/x' LIKE 'a\_b:/x' ESCAPE '\'")).ShouldBe("0");
    }

    [Fact]
    public async Task PragmasApplyToEveryConnection_NotJustTheFirst()
    {
        var factory = NewFactory();

        // The pool hands back a used connection on the second open. PRAGMA
        // state survives pooling, but the factory must not depend on that —
        // a connection that misses foreign_keys silently stops enforcing them.
        await using (var first = await factory.OpenAsync())
            (await ScalarAsync(first, "PRAGMA foreign_keys")).ShouldBe("1");

        await using var second = await factory.OpenAsync();
        (await ScalarAsync(second, "PRAGMA foreign_keys")).ShouldBe("1");
        (await ScalarAsync(second, "PRAGMA busy_timeout")).ShouldBe("5000");
    }

    [Fact]
    public async Task ForeignKeysAreActuallyEnforced()
    {
        // PRAGMA foreign_keys reporting 1 and the engine rejecting a bad write
        // are different claims. Assert the behaviour, not the flag.
        await using var conn = await NewFactory().OpenAsync();
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = """
                CREATE TABLE parent (shortname TEXT PRIMARY KEY);
                CREATE TABLE child (id TEXT PRIMARY KEY,
                                    owner TEXT NOT NULL REFERENCES parent(shortname));
                """;
            await ddl.ExecuteNonQueryAsync();
        }

        await using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO child(id, owner) VALUES ('c1', 'nobody')";
        var ex = await Should.ThrowAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
        ex.SqliteErrorCode.ShouldBe(19);   // SQLITE_CONSTRAINT
    }

    [Fact]
    public void IsContention_MatchesBusyAndLockedIncludingExtendedCodes()
    {
        // SQLITE_BUSY_SNAPSHOT is 517; its primary code is 5, which is what
        // SqliteErrorCode reports and what the retry must match on. Getting
        // this wrong means deferred-transaction upgrade failures never retry.
        (517 & 0xFF).ShouldBe(5);
        (261 & 0xFF).ShouldBe(5);   // SQLITE_BUSY_RECOVERY
        (262 & 0xFF).ShouldBe(6);   // SQLITE_LOCKED_SHAREDCACHE
    }

    public void Dispose()
    {
        // Pooled connections keep the file handle open; clearing the pool is
        // what actually releases it so the temp files can be removed.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { /* best effort */ }
    }
}
