using Npgsql;

namespace Dmart.DataAdapters.Sql;

// locks table — Unique base only (no Metas). Locks auto-expire after
// settings.LockPeriod seconds via a timestamp comparison at read time — we
// don't run a background sweeper, the expiry check is inline on every op.
public sealed class LockRepository(Db db)
{
    // Tries to acquire an exclusive lock. If an existing row is older than
    // `lockPeriodSeconds`, it's evicted as part of the INSERT so the caller
    // gets the lock. Returns true if the caller now holds the lock.
    public async Task<bool> TryLockAsync(
        string spaceName, string subpath, string shortname, string ownerShortname,
        int lockPeriodSeconds, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        // Step 1: purge any stale lock for this (space, subpath, shortname).
        await using (var purge = new NpgsqlCommand("""
            DELETE FROM locks
            WHERE shortname = $1 AND space_name = $2 AND subpath = $3
              AND timestamp < NOW() - ($4 || ' seconds')::interval
            """, conn))
        {
            purge.Parameters.Add(new() { Value = shortname });
            purge.Parameters.Add(new() { Value = spaceName });
            purge.Parameters.Add(new() { Value = subpath });
            purge.Parameters.Add(new() { Value = lockPeriodSeconds.ToString() });
            await purge.ExecuteNonQueryAsync(ct);
        }
        // Step 2: insert — succeeds when no live lock is left. When a live lock
        // IS left it can only belong to another caller (stale ones were purged
        // in step 1) OR to this same owner. The DO UPDATE … WHERE clause lets
        // the owner REFRESH their own still-valid lock (Python's
        // LockAction.extend): it bumps the timestamp and reports success. A lock
        // held by anyone else fails the WHERE, degrades to DO NOTHING, and the
        // 0-row result tells the caller they don't hold the lock.
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO locks (uuid, shortname, space_name, subpath, owner_shortname, timestamp)
            VALUES (gen_random_uuid(), $1, $2, $3, $4, NOW())
            ON CONFLICT (shortname, space_name, subpath) DO UPDATE
                SET timestamp = NOW()
                WHERE locks.owner_shortname = $4
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = ownerShortname });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
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

        var shortnames = items.Select(i => i.Shortname).Distinct().ToArray();
        var subpaths = items.Select(i => i.Subpath).Distinct().ToArray();

        await using var conn = await db.OpenAsync(ct);
        // shortname/subpath are matched independently (ANY/ANY) so a page that
        // mixes subpaths can over-match cross pairs — harmless because we re-key
        // on the exact (subpath, shortname) tuple below.
        await using var cmd = new NpgsqlCommand("""
            SELECT subpath, shortname, owner_shortname FROM locks
            WHERE space_name = $1
              AND shortname = ANY($2)
              AND subpath = ANY($3)
              AND timestamp >= NOW() - ($4 || ' seconds')::interval
            """, conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = shortnames });
        cmd.Parameters.Add(new() { Value = subpaths });
        cmd.Parameters.Add(new() { Value = lockPeriodSeconds.ToString() });

        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            result[(rd.GetString(0), rd.GetString(1))] = rd.GetString(2);
        return result;
    }

    public async Task<bool> UnlockAsync(string spaceName, string subpath, string shortname, string ownerShortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM locks
            WHERE shortname = $1 AND space_name = $2 AND subpath = $3 AND owner_shortname = $4
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = ownerShortname });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // Returns the owner if there's a non-expired lock; null if there's no
    // lock OR the existing row is past its lock_period.
    public async Task<string?> GetLockerAsync(
        string spaceName, string subpath, string shortname,
        int lockPeriodSeconds, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT owner_shortname FROM locks
            WHERE shortname = $1 AND space_name = $2 AND subpath = $3
              AND timestamp >= NOW() - ($4 || ' seconds')::interval
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = lockPeriodSeconds.ToString() });
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
