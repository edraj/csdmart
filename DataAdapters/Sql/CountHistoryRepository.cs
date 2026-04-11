using Npgsql;

namespace Dmart.DataAdapters.Sql;

// dmart populates count_history as an analytical snapshot of entry counts per space.
// We maintain it from the C# side too so dmart Python's reporting endpoints stay
// consistent regardless of which port wrote the entries.
public sealed class CountHistoryRepository(Db db)
{
    public async Task RecordSnapshotAsync(string spaceName, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);

        long count;
        await using (var countCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM entries WHERE space_name = $1", conn))
        {
            countCmd.Parameters.Add(new() { Value = spaceName });
            count = (long)(await countCmd.ExecuteScalarAsync(ct) ?? 0L);
        }

        await using var insertCmd = new NpgsqlCommand("""
            INSERT INTO count_history (spacename, entries_count, recorded_at)
            VALUES ($1, $2, NOW())
            """, conn);
        insertCmd.Parameters.Add(new() { Value = spaceName });
        insertCmd.Parameters.Add(new() { Value = count });
        await insertCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordSnapshotForAllSpacesAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        // Bulk insert: a row per distinct space.
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO count_history (spacename, entries_count, recorded_at)
            SELECT space_name, COUNT(*), NOW()
            FROM entries
            GROUP BY space_name
            """, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
