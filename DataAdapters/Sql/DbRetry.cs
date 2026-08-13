namespace Dmart.DataAdapters.Sql;

// Retries a whole transaction on the transient contention error its backend
// produces.
//
// The two engines fail differently enough that one policy would be wrong for
// both, so this dispatches rather than unifying:
//
//   * PostgreSQL raises 40P01 (deadlock detected). The loser's transaction is
//     fully rolled back and the winner is already making progress, so a short
//     LINEAR backoff is right — see Db.ExecuteWithRetryOnDeadlockAsync.
//   * SQLite raises SQLITE_BUSY / SQLITE_LOCKED, which is a thundering herd on
//     a single file lock. Unjittered backoff resynchronizes the herd and makes
//     the next collision more likely, so SqliteRetry uses exponential backoff
//     with full jitter.
//
// In both cases the operation must be a complete transaction: it is replayed
// from the start, and replaying half of an aborted one would be incorrect.
public static class DbRetry
{
    /// <summary>
    /// True when an exception is transient write contention worth replaying the
    /// whole transaction for.
    /// </summary>
    /// <remarks>
    /// The two engines report the same situation with different vocabularies:
    /// PostgreSQL as a serialization failure (40P01 deadlock detected, 40001
    /// serialization failure), SQLite as a busy or locked database. Both mean
    /// "someone else got there first, try again"; neither means the statement
    /// was wrong.
    /// </remarks>
    public static bool IsTransientContention(Exception ex) => ex switch
    {
        Npgsql.PostgresException pg => pg.SqlState is "40P01" or "40001",
        Microsoft.Data.Sqlite.SqliteException s => SqliteRetry.IsContention(s),
        _ => false,
    };

    public static Task<T> ExecuteWithRetryAsync<T>(
        this IDbConnectionFactory db,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
        => db is Db postgres
            ? postgres.ExecuteWithRetryOnDeadlockAsync(operation, ct)
            : SqliteRetry.ExecuteAsync(operation, ct);
}
