using Microsoft.Data.Sqlite;

namespace Dmart.DataAdapters.Sql;

// Bounded retry for SQLite lock contention.
//
// This is NOT redundant with `PRAGMA busy_timeout`. That pragma makes the
// engine sleep and retry internally while acquiring a lock, which covers
// ordinary contention — but it deliberately does NOT cover two cases:
//
//   * SQLITE_BUSY_SNAPSHOT (517), returned when a DEFERRED transaction that
//     already took a read lock tries to upgrade to a write lock and another
//     writer has since committed. The engine cannot wait here without risking
//     deadlock, so it returns immediately regardless of busy_timeout. The
//     transaction is dead and only a full replay can succeed.
//   * SQLITE_BUSY at COMMIT, when a reader is still holding the WAL back.
//
// So the retry has to sit around the WHOLE transaction, not around a
// statement: replaying half of an aborted transaction would be incorrect.
//
// Deliberately separate from Db.ExecuteWithRetryOnDeadlockAsync rather than
// shared with it. That one retries PostgreSQL 40P01 with linear, unjittered
// backoff, which is right for deadlocks — the loser rolls back immediately and
// the winner is already making progress. SQLite contention is the opposite
// shape: a thundering herd on one file lock, where unjittered backoff
// resynchronizes the herd and makes collisions more likely on each round. Two
// different failure modes, two different policies.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
    Justification = "Audited: CommandText is assembled from compile-time SQL, dialect-produced fragments and $N placeholders only. Every caller-supplied value is bound through DbParams, never concatenated.")]
public static class SqliteRetry
{
    private const int MaxAttempts = 3;

    // Per-attempt base delay. Kept small because SQLite write transactions in
    // this codebase are short; the jitter matters more than the magnitude.
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Runs <paramref name="operation"/>, replaying it from scratch on SQLite
    /// lock contention. The operation MUST be a complete, self-contained
    /// transaction — it is re-executed in full.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(ct);
            }
            catch (SqliteException ex) when (IsContention(ex) && attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                // Exponential with full jitter. Random is fine here — this
                // picks a backoff delay, it is not security-sensitive.
                var ceiling = BaseDelay * Math.Pow(2, attempt - 1);
                var delay = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * ceiling.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>Void overload for transactions that return nothing.</summary>
    public static Task ExecuteAsync(
        Func<CancellationToken, Task> operation, CancellationToken ct = default)
        => ExecuteAsync<object?>(async token => { await operation(token); return null; }, ct);

    // SqliteErrorCode carries the primary result code; ExtendedErrorCode the
    // refinement. Match on the primary so every BUSY/LOCKED variant is covered
    // — including SQLITE_BUSY_SNAPSHOT (517), whose primary code is 5.
    internal static bool IsContention(SqliteException ex) => ex.SqliteErrorCode switch
    {
        5 => true,   // SQLITE_BUSY   — the file is locked by another connection
        6 => true,   // SQLITE_LOCKED — a table in this same connection is locked
        _ => false,
    };
}
