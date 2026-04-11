using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// histories table — flat (no Metas inheritance in dmart).
public sealed class HistoryRepository(Db db)
{
    public async Task AppendAsync(string spaceName, string subpath, string shortname, string? actor,
                                   Dictionary<string, object>? requestHeaders, Dictionary<string, object>? diff,
                                   CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO histories (uuid, request_headers, diff, timestamp,
                                   owner_shortname, last_checksum_history,
                                   space_name, subpath, shortname)
            VALUES (gen_random_uuid(), $1, $2, NOW(), $3, NULL, $4, $5, $6)
            """, conn);
        // request_headers and diff are NOT NULL in dmart's schema — default to {}.
        cmd.Parameters.Add(new()
        {
            Value = JsonbHelpers.ToJsonb(requestHeaders) ?? "{}",
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        cmd.Parameters.Add(new()
        {
            Value = JsonbHelpers.ToJsonb(diff) ?? "{}",
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        cmd.Parameters.Add(new() { Value = (object?)actor ?? DBNull.Value });
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<HistoryEntry>> ListAsync(string spaceName, string subpath, string shortname, int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT uuid, owner_shortname, diff, timestamp
            FROM histories
            WHERE space_name = $1 AND subpath = $2 AND shortname = $3
            ORDER BY timestamp DESC
            LIMIT $4
            """, conn);
        cmd.Parameters.Add(new() { Value = spaceName });
        cmd.Parameters.Add(new() { Value = subpath });
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = limit });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<HistoryEntry>();
        while (await reader.ReadAsync(ct))
        {
            results.Add(new HistoryEntry(
                Uuid: reader.GetGuid(0),
                Actor: reader.IsDBNull(1) ? null : reader.GetString(1),
                Diff: reader.IsDBNull(2) ? null : reader.GetString(2),
                Timestamp: reader.GetDateTime(3)));
        }
        return results;
    }
}

public sealed record HistoryEntry(Guid Uuid, string? Actor, string? Diff, DateTime Timestamp);
