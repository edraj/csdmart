using System.Data.Common;
using Dmart.QueryGrammar;
using Microsoft.Data.Sqlite;

namespace Dmart.DataAdapters.Sql;

// Result of a TryLockAsync call. Lets the caller distinguish a fresh lock from
// a same-owner refresh so it can record the matching history lock_type
// (Python's lock vs extend) and deny when someone else holds it.
public enum LockOutcome
{
    Denied,    // a live lock is held by another owner
    Acquired,  // a new lock row was inserted
    Extended,  // the caller's own still-valid lock was refreshed
}

// locks table — Unique base only (no Metas). Locks auto-expire after
// settings.LockPeriod seconds via a timestamp comparison at read time — we
// don't run a background sweeper, the expiry check is inline on every op.
//
// This is the one repository where the two backends need genuinely different
// strategies rather than different spellings; see TryLockAsync.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
    Justification = "Audited: CommandText is assembled from compile-time SQL, dialect-produced fragments and $N placeholders only. Every caller-supplied value is bound through DbParams, never concatenated.")]
public sealed class LockRepository(IDbConnectionFactory db, ISqlDialect dialect)
{
    // Emits the expiry comparison and binds whatever the engine needs for it.
    //
    // PostgreSQL evaluates the cutoff server-side with NOW() - interval, which
    // is deliberate: the database clock is the single authority when several
    // app hosts share it. SQLite has no interval type, and being in-process
    // there IS no separate server clock, so the cutoff is computed here from
    // the same wall-clock basis the timestamps were written with.
    private static string LiveSince(DbCommand cmd, int lockPeriodSeconds)
    {
        if (cmd is SqliteCommand)
            return DbParams.Add(cmd, TimeUtils.Now().AddSeconds(-lockPeriodSeconds));
        var p = DbParams.Add(cmd, lockPeriodSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return $"NOW() - ({p} || ' seconds')::interval";
    }

    // Tries to acquire an exclusive lock. If an existing row is older than
    // `lockPeriodSeconds`, it's evicted so the caller gets the lock. Reports
    // whether the caller acquired a fresh lock, extended their own, or was
    // denied because someone else holds it.
    public async Task<LockOutcome> TryLockAsync(
        string spaceName, string subpath, string shortname, string ownerShortname,
        int lockPeriodSeconds, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return conn is SqliteConnection sqlite
            ? await TryLockSqliteAsync(sqlite, spaceName, subpath, shortname, ownerShortname, lockPeriodSeconds, ct)
            : await TryLockPostgresAsync(conn, spaceName, subpath, shortname, ownerShortname, lockPeriodSeconds, ct);
    }

    private static async Task<LockOutcome> TryLockPostgresAsync(
        DbConnection conn, string spaceName, string subpath, string shortname,
        string ownerShortname, int lockPeriodSeconds, CancellationToken ct)
    {
        // Step 1: purge any stale lock for this (space, subpath, shortname).
        await using (var purge = conn.CreateCommand())
        {
            var sn = DbParams.Add(purge, shortname);
            var sp = DbParams.Add(purge, spaceName);
            var su = DbParams.Add(purge, subpath);
            purge.CommandText = $"""
                DELETE FROM locks
                WHERE shortname = {sn} AND space_name = {sp} AND subpath = {su}
                  AND timestamp < {LiveSince(purge, lockPeriodSeconds)}
                """;
            await purge.ExecuteNonQueryAsync(ct);
        }
        // Step 2: insert — succeeds when no live lock is left. When a live lock
        // IS left it can only belong to another caller (stale ones were purged
        // in step 1) OR to this same owner. The DO UPDATE … WHERE clause lets
        // the owner REFRESH their own still-valid lock (Python's
        // LockAction.extend). RETURNING (xmax = 0) distinguishes the insert
        // (xmax = 0) from the same-owner refresh (xmax <> 0); a lock held by
        // anyone else fails the WHERE, the conflict degrades to DO NOTHING,
        // no row is returned, and ExecuteScalar yields null → Denied.
        //
        // Kept as one atomic statement rather than converging on the SQLite
        // strategy below: under PostgreSQL's MVCC this needs no explicit
        // transaction and no read-then-write window, and it is the
        // battle-tested path.
        await using var cmd = conn.CreateCommand();
        var s1 = DbParams.Add(cmd, shortname);
        var s2 = DbParams.Add(cmd, spaceName);
        var s3 = DbParams.Add(cmd, subpath);
        var s4 = DbParams.Add(cmd, ownerShortname);
        cmd.CommandText = $"""
            INSERT INTO locks (uuid, shortname, space_name, subpath, owner_shortname, timestamp)
            VALUES (gen_random_uuid(), {s1}, {s2}, {s3}, {s4}, NOW())
            ON CONFLICT (shortname, space_name, subpath) DO UPDATE
                SET timestamp = NOW()
                WHERE locks.owner_shortname = {s4}
            RETURNING (xmax = 0) AS inserted
            """;
        var inserted = await cmd.ExecuteScalarAsync(ct);
        if (inserted is null or DBNull) return LockOutcome.Denied;
        return (bool)inserted ? LockOutcome.Acquired : LockOutcome.Extended;
    }

    // SQLite cannot express the PostgreSQL form: `xmax` is a system column with
    // no equivalent, so an upsert cannot report whether it inserted or updated,
    // and the Acquired/Extended distinction is user-visible (LockService writes
    // it to history as "lock" vs "extend"). So the decision is made explicitly:
    // purge, read the incumbent, then insert or update accordingly.
    //
    // The transaction MUST take the write lock up front (BEGIN IMMEDIATE), not
    // acquire a read lock and upgrade later. A deferred transaction reads under
    // a shared lock and only tries to upgrade at the first write; if another
    // writer committed in between, SQLite returns SQLITE_BUSY_SNAPSHOT
    // immediately and does NOT honour busy_timeout, because it cannot wait
    // there without risking deadlock. Read-then-decide-then-write is exactly
    // that pattern, so deferred would make this racy under concurrency.
    //
    // Microsoft.Data.Sqlite already begins IMMEDIATE by default — unlike raw
    // SQLite, whose default is DEFERRED — so this is a dependency on provider
    // behaviour rather than something the call opts into. Verified, and pinned
    // by SqliteTransactionSemanticsTests so a provider default change fails a
    // test instead of silently reintroducing the race.
    private static async Task<LockOutcome> TryLockSqliteAsync(
        SqliteConnection conn, string spaceName, string subpath, string shortname,
        string ownerShortname, int lockPeriodSeconds, CancellationToken ct)
    {
        return await SqliteRetry.ExecuteAsync(async token =>
        {
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, token);

            await using (var purge = conn.CreateCommand())
            {
                purge.Transaction = tx;
                var sn = DbParams.Add(purge, shortname);
                var sp = DbParams.Add(purge, spaceName);
                var su = DbParams.Add(purge, subpath);
                purge.CommandText = $"""
                    DELETE FROM locks
                    WHERE shortname = {sn} AND space_name = {sp} AND subpath = {su}
                      AND timestamp < {LiveSince(purge, lockPeriodSeconds)}
                    """;
                await purge.ExecuteNonQueryAsync(token);
            }

            string? incumbent;
            await using (var probe = conn.CreateCommand())
            {
                probe.Transaction = tx;
                var sn = DbParams.Add(probe, shortname);
                var sp = DbParams.Add(probe, spaceName);
                var su = DbParams.Add(probe, subpath);
                probe.CommandText =
                    $"SELECT owner_shortname FROM locks "
                    + $"WHERE shortname = {sn} AND space_name = {sp} AND subpath = {su}";
                var raw = await probe.ExecuteScalarAsync(token);
                incumbent = raw is null or DBNull ? null : (string)raw;
            }

            // Anything left after the purge is live. Someone else's lock denies.
            if (incumbent is not null && !string.Equals(incumbent, ownerShortname, StringComparison.Ordinal))
            {
                await tx.RollbackAsync(token);
                return LockOutcome.Denied;
            }

            await using (var write = conn.CreateCommand())
            {
                write.Transaction = tx;
                if (incumbent is null)
                {
                    var uuid = DbParams.Add(write, Guid.NewGuid());
                    var sn = DbParams.Add(write, shortname);
                    var sp = DbParams.Add(write, spaceName);
                    var su = DbParams.Add(write, subpath);
                    var ow = DbParams.Add(write, ownerShortname);
                    var ts = DbParams.Add(write, TimeUtils.Now());
                    write.CommandText =
                        "INSERT INTO locks (uuid, shortname, space_name, subpath, owner_shortname, timestamp) "
                        + $"VALUES ({uuid}, {sn}, {sp}, {su}, {ow}, {ts})";
                }
                else
                {
                    var ts = DbParams.Add(write, TimeUtils.Now());
                    var sn = DbParams.Add(write, shortname);
                    var sp = DbParams.Add(write, spaceName);
                    var su = DbParams.Add(write, subpath);
                    write.CommandText =
                        $"UPDATE locks SET timestamp = {ts} "
                        + $"WHERE shortname = {sn} AND space_name = {sp} AND subpath = {su}";
                }
                await write.ExecuteNonQueryAsync(token);
            }

            await tx.CommitAsync(token);
            return incumbent is null ? LockOutcome.Acquired : LockOutcome.Extended;
        }, ct);
    }

    // Batch variant of GetLockerAsync for the query path: returns the owner of
    // every NON-expired lock among `items`, keyed by (subpath, shortname). Keys
    // use the same subpath form the locks table stores (leading-slash, set via
    // Locator normalization on the lock endpoint) — callers must normalize their
    // record subpaths the same way before looking up. One round trip for the
    // whole page instead of one per record.
    public async Task<Dictionary<(string Subpath, string Shortname), string>> GetActiveLockOwnersAsync(
        string spaceName, IReadOnlyCollection<(string Subpath, string Shortname)> items,
        int lockPeriodSeconds, CancellationToken ct = default)
    {
        var result = new Dictionary<(string, string), string>();
        if (items.Count == 0) return result;

        var shortnames = items.Select(i => i.Shortname).Distinct().ToList();
        var subpaths = items.Select(i => i.Subpath).Distinct().ToList();

        await using var conn = await db.OpenAsync(ct);
        // shortname/subpath are matched independently so a page that mixes
        // subpaths can over-match cross pairs — harmless because we re-key on
        // the exact (subpath, shortname) tuple below.
        await using var cmd = conn.CreateCommand();
        var sp = DbParams.Add(cmd, spaceName);
        var shortnameIn = dialect.AnyOf("shortname", shortnames, (v, k) => DbParams.Add(cmd, v, k));
        var subpathIn = dialect.AnyOf("subpath", subpaths, (v, k) => DbParams.Add(cmd, v, k));
        cmd.CommandText = $"""
            SELECT subpath, shortname, owner_shortname FROM locks
            WHERE space_name = {sp}
              AND {shortnameIn}
              AND {subpathIn}
              AND timestamp >= {LiveSince(cmd, lockPeriodSeconds)}
            """;

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result[(rd.GetString(0), rd.GetString(1))] = rd.GetString(2);
        return result;
    }

    public async Task<bool> UnlockAsync(string spaceName, string subpath, string shortname, string ownerShortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sn = DbParams.Add(cmd, shortname);
        var sp = DbParams.Add(cmd, spaceName);
        var su = DbParams.Add(cmd, subpath);
        var ow = DbParams.Add(cmd, ownerShortname);
        cmd.CommandText = $"""
            DELETE FROM locks
            WHERE shortname = {sn} AND space_name = {sp} AND subpath = {su} AND owner_shortname = {ow}
            """;
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // Returns the owner if there's a non-expired lock; null if there's no
    // lock OR the existing row is past its lock_period.
    public async Task<string?> GetLockerAsync(
        string spaceName, string subpath, string shortname,
        int lockPeriodSeconds, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sn = DbParams.Add(cmd, shortname);
        var sp = DbParams.Add(cmd, spaceName);
        var su = DbParams.Add(cmd, subpath);
        cmd.CommandText = $"""
            SELECT owner_shortname FROM locks
            WHERE shortname = {sn} AND space_name = {sp} AND subpath = {su}
              AND timestamp >= {LiveSince(cmd, lockPeriodSeconds)}
            """;
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is null or DBNull ? null : (string)raw;
    }
}
