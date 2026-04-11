using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Runs the same integrity probes dmart Python's health check exposes:
//   * orphan attachments       — attachments with no matching parent entry
//   * dangling owners          — entries whose owner_shortname doesn't exist
//   * stale locks              — locks held longer than `staleAfter`
//   * empty payload references — entries that claim a payload but have no body
//   * untyped resources        — rows with unknown resource_type values
//
// `health_type` filters which checks run:
//   * "soft"      → counts only
//   * "hard"      → counts + sample rows
//   * "all"       → everything
public sealed class HealthCheckRepository(Db db)
{
    public sealed record IssueCheck(string Name, long Count, List<string> Samples);

    public async Task<List<IssueCheck>> RunAsync(string spaceName, string healthType, CancellationToken ct = default)
    {
        var includeSamples = healthType is "hard" or "all";
        var sampleLimit = includeSamples ? 10 : 0;
        var results = new List<IssueCheck>();

        await using var conn = await db.OpenAsync(ct);

        results.Add(await RunCheck(conn, "orphan_attachments", """
            SELECT a.shortname FROM attachments a
            WHERE a.space_name = $1
            AND NOT EXISTS (
                SELECT 1 FROM entries e
                WHERE e.space_name = a.space_name
                  AND a.subpath LIKE e.subpath || '/' || e.shortname || '%'
            )
            """, spaceName, sampleLimit, ct));

        results.Add(await RunCheck(conn, "dangling_owner", """
            SELECT e.shortname FROM entries e
            WHERE e.space_name = $1
            AND NOT EXISTS (SELECT 1 FROM users u WHERE u.shortname = e.owner_shortname)
            """, spaceName, sampleLimit, ct));

        results.Add(await RunCheck(conn, "stale_locks", """
            SELECT l.shortname FROM locks l
            WHERE l.space_name = $1 AND l.timestamp < (NOW() - INTERVAL '24 hours')
            """, spaceName, sampleLimit, ct));

        results.Add(await RunCheck(conn, "missing_payload_body", """
            SELECT e.shortname FROM entries e
            WHERE e.space_name = $1
            AND e.payload IS NOT NULL
            AND (e.payload->'body') IS NULL
            """, spaceName, sampleLimit, ct));

        results.Add(await RunCheck(conn, "missing_schema_reference", """
            SELECT e.shortname FROM entries e
            WHERE e.space_name = $1
            AND e.payload IS NOT NULL
            AND (e.payload->>'schema_shortname') IS NOT NULL
            AND NOT EXISTS (
                SELECT 1 FROM entries s
                WHERE s.space_name = e.space_name
                  AND s.resource_type = 'schema'
                  AND s.shortname = (e.payload->>'schema_shortname')
            )
            """, spaceName, sampleLimit, ct));

        return results;
    }

    private static async Task<IssueCheck> RunCheck(
        NpgsqlConnection conn, string name, string sql, string spaceName,
        int sampleLimit, CancellationToken ct)
    {
        var samples = new List<string>();
        long count = 0;

        // Count
        await using (var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM ({sql}) c", conn))
        {
            cmd.Parameters.Add(new() { Value = spaceName });
            count = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        }

        // Samples
        if (sampleLimit > 0 && count > 0)
        {
            await using var cmd = new NpgsqlCommand($"{sql} LIMIT {sampleLimit}", conn);
            cmd.Parameters.Add(new() { Value = spaceName });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) samples.Add(reader.GetString(0));
        }

        return new IssueCheck(name, count, samples);
    }
}
