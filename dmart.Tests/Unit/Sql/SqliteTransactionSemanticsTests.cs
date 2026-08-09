using System.Data;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// Pins the transaction behaviour LockRepository's SQLite path depends on.
//
// LockRepository.TryLockSqliteAsync reads the incumbent lock, decides, then
// writes. That pattern is only safe inside a transaction that holds the write
// lock from the start. A DEFERRED transaction reads under a shared lock and
// upgrades at the first write, and SQLite answers a lost upgrade race with
// SQLITE_BUSY_SNAPSHOT immediately — busy_timeout does not apply, because the
// engine cannot wait there without risking deadlock.
//
// Microsoft.Data.Sqlite happens to begin IMMEDIATE by default, unlike raw
// SQLite. That is a provider behaviour we rely on rather than request, so it
// gets a test: if a future version changed the default, the lock path would
// silently become racy under concurrency with nothing else to catch it.
public sealed class SqliteTransactionSemanticsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dmart-tx-{Guid.NewGuid():N}.db");

    private SqliteConnectionFactory NewFactory()
        => new(Options.Create(new DmartSettings { SqlitePath = _dbPath }));

    [Fact]
    public async Task BeginTransaction_TakesTheWriteLockImmediately()
    {
        var factory = NewFactory();
        await using (var setup = await factory.OpenAsync())
        {
            await using var ddl = setup.CreateCommand();
            ddl.CommandText = "CREATE TABLE probe (k INTEGER PRIMARY KEY, v TEXT)";
            await ddl.ExecuteNonQueryAsync();
        }

        await using var holder = await factory.OpenAsync();
        await using var other = await factory.OpenAsync();

        // Begin a transaction and do NOT write in it.
        await using var tx = await holder.BeginTransactionAsync(IsolationLevel.Serializable);

        // If the transaction were DEFERRED, `holder` would hold no write lock
        // yet and this would succeed — which is exactly the window that makes
        // read-then-write racy. It must fail.
        await using var write = other.CreateCommand();
        write.CommandText = "INSERT INTO probe (v) VALUES ('other')";
        var ex = await Should.ThrowAsync<SqliteException>(() => write.ExecuteNonQueryAsync());

        SqliteRetry.IsContention(ex).ShouldBeTrue(
            "a blocked writer must surface as BUSY/LOCKED so SqliteRetry replays it");

        await tx.RollbackAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { /* best effort */ }
    }
}
