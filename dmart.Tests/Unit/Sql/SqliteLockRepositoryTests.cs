using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.QueryGrammar;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// LockRepository is the one place the two backends need different strategies
// rather than different spellings: PostgreSQL distinguishes an insert from an
// update with `RETURNING (xmax = 0)`, and SQLite has no system column to do
// that with. The SQLite path purges, reads the incumbent, then inserts or
// updates.
//
// The Acquired/Extended distinction is user-visible — LockService records it as
// "lock" vs "extend" in history — so these assert the outcome, not just that a
// row landed.
public sealed class SqliteLockRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dmart-lock-{Guid.NewGuid():N}.db");
    private LockRepository _repo = null!;
    private SqliteConnectionFactory _factory = null!;

    private const int LockPeriod = 300;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(
            Options.Create(new DmartSettings { SqlitePath = _dbPath }));
        await new SqliteSchemaInitializer(_factory, NullLogger<SqliteSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        _repo = new LockRepository(_factory, SqliteSqlDialect.Instance);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task FirstLock_IsAcquired_SameOwnerAgain_IsExtended()
    {
        (await _repo.TryLockAsync("sp", "/a", "doc", "alice", LockPeriod))
            .ShouldBe(LockOutcome.Acquired);

        // A second call by the same owner refreshes rather than re-acquiring.
        // This is the distinction xmax gave PostgreSQL for free.
        (await _repo.TryLockAsync("sp", "/a", "doc", "alice", LockPeriod))
            .ShouldBe(LockOutcome.Extended);
    }

    [Fact]
    public async Task OtherOwner_IsDenied_AndDoesNotStealTheLock()
    {
        (await _repo.TryLockAsync("sp", "/a", "doc", "alice", LockPeriod))
            .ShouldBe(LockOutcome.Acquired);
        (await _repo.TryLockAsync("sp", "/a", "doc", "bob", LockPeriod))
            .ShouldBe(LockOutcome.Denied);

        // The denial must leave alice's lock intact — a rolled-back transaction
        // that still wrote would hand the lock to bob.
        (await _repo.GetLockerAsync("sp", "/a", "doc", LockPeriod)).ShouldBe("alice");
    }

    [Fact]
    public async Task ExpiredLock_IsPurged_SoAnotherOwnerAcquires()
    {
        (await _repo.TryLockAsync("sp", "/a", "doc", "alice", LockPeriod))
            .ShouldBe(LockOutcome.Acquired);

        // lockPeriodSeconds = 0 makes every existing row stale, which is how the
        // purge step is exercised without sleeping.
        (await _repo.TryLockAsync("sp", "/a", "doc", "bob", lockPeriodSeconds: 0))
            .ShouldBe(LockOutcome.Acquired);
        (await _repo.GetLockerAsync("sp", "/a", "doc", LockPeriod)).ShouldBe("bob");
    }

    [Fact]
    public async Task GetLocker_IgnoresExpiredLocks()
    {
        await _repo.TryLockAsync("sp", "/a", "doc", "alice", LockPeriod);
        (await _repo.GetLockerAsync("sp", "/a", "doc", LockPeriod)).ShouldBe("alice");
        // Zero period means the row is already past expiry.
        (await _repo.GetLockerAsync("sp", "/a", "doc", lockPeriodSeconds: 0)).ShouldBeNull();
    }

    [Fact]
    public async Task Unlock_OnlySucceedsForTheOwner()
    {
        await _repo.TryLockAsync("sp", "/a", "doc", "alice", LockPeriod);

        (await _repo.UnlockAsync("sp", "/a", "doc", "bob")).ShouldBeFalse();
        (await _repo.GetLockerAsync("sp", "/a", "doc", LockPeriod)).ShouldBe("alice");

        (await _repo.UnlockAsync("sp", "/a", "doc", "alice")).ShouldBeTrue();
        (await _repo.GetLockerAsync("sp", "/a", "doc", LockPeriod)).ShouldBeNull();
    }

    [Fact]
    public async Task GetActiveLockOwners_BatchesAcrossSubpaths()
    {
        await _repo.TryLockAsync("sp", "/a", "one", "alice", LockPeriod);
        await _repo.TryLockAsync("sp", "/b", "two", "bob", LockPeriod);
        // An expired lock must not appear.
        await _repo.TryLockAsync("sp", "/c", "three", "carol", LockPeriod);

        var owners = await _repo.GetActiveLockOwnersAsync("sp", new[]
        {
            ("/a", "one"), ("/b", "two"),
        }, LockPeriod);

        owners[("/a", "one")].ShouldBe("alice");
        owners[("/b", "two")].ShouldBe("bob");
        // Exercises the dialect's IN-list expansion: PostgreSQL binds one array
        // parameter here, SQLite one parameter per value.
        owners.ShouldNotContainKey(("/c", "three"));
    }

    [Fact]
    public async Task ConcurrentAcquire_ExactlyOneWins()
    {
        // Two owners racing for the same lock. Whatever the interleaving, one
        // must get Acquired and the other Denied — never both Acquired, which
        // is what a read-then-write window without the write lock would allow.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i =>
                _repo.TryLockAsync("sp", "/race", "doc", $"owner{i}", LockPeriod)));

        results.Count(r => r == LockOutcome.Acquired).ShouldBe(1);
        results.Count(r => r == LockOutcome.Denied).ShouldBe(7);
    }
}
