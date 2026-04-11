using Npgsql;

namespace Dmart.DataAdapters.Sql;

// locks table — Unique base only (no Metas).
public sealed class LockRepository(Db db)
{
    public async Task<bool> TryLockAsync(string spaceName, string subpath, string shortname, string ownerShortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO locks (uuid, shortname, space_name, subpath, owner_shortname, timestamp)
            VALUES (gen_random_uuid(), $1, $2, $3, $4, NOW())
            ON CONFLICT (shortname, space_name, subpath) DO NOTHING
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = ownerShortname });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
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

    public async Task<string?> GetLockerAsync(string spaceName, string subpath, string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT owner_shortname FROM locks
            WHERE shortname = $1 AND space_name = $2 AND subpath = $3
            """, conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
