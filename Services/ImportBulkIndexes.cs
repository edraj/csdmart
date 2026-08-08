using Dmart.DataAdapters.Sql;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dmart.Services;

// Drops the expensive secondary indexes for the duration of a bulk import and
// rebuilds them afterwards — the classic bulk-load trade.
//
// Why it matters: `entries` carries several GIN indexes (payload, tags, acl,
// relationships, query_policies, plus a pg_trgm index over payload::text on a
// server that has run SchemaInitializer). GIN maintenance is paid per inserted
// row AND gets worse as the index grows, so insert rate decays through a long
// run. Measured on a 4M-row load: ~7,400 rows/s into an empty table falling to
// ~3,150 rows/s at 4M with GIN present, versus a FLAT ~69,000 rows/s with the
// GIN indexes dropped. Load-then-rebuild came out ~5.6x ahead end to end. The
// advantage grows with row count — at 200k rows it is only ~1.2x, because the
// one-off rebuild dominates a small table.
//
// SCOPE — deliberately narrow:
//   * GIN indexes only. The btree indexes are cheap to maintain, and one of
//     them (shortname, space_name, subpath) is the ON CONFLICT arbiter the
//     merge depends on — dropping it would change import semantics from
//     "upsert" to "insert duplicates".
//   * Never an index backing a constraint. Those cannot be dropped without
//     dropping the constraint, and the guard keeps that true if the filter
//     below ever widens.
//   * Discovered from the live catalog rather than hardcoded, and recreated
//     from the catalog's own `indexdef`, so this keeps working when the schema
//     gains or changes an index.
//
// RECOVERY: the definitions are written to the import checkpoint BEFORE the
// drop, so a hard crash (SIGKILL, power loss) leaves a durable record. The next
// `--resume` run rebuilds them; the failure path also prints the exact SQL.
internal static class ImportBulkIndexes
{
    // Tables whose bulk-insert cost the import actually cares about.
    private static readonly string[] Tables = ["entries", "attachments"];

    // Rebuild memory. GIN builds are dominated by sorting the pending entries,
    // and the default (usually 64MB) makes a multi-million-row build spill to
    // disk repeatedly. Session-local, so it cannot affect anything else.
    private const string RebuildWorkMem = "1GB";

    // Discover the indexes this import would drop. Returns (name, definition)
    // pairs; the definition is PostgreSQL's own `pg_indexes.indexdef`, which
    // round-trips exactly.
    public static async Task<List<ImportCheckpointStore.DroppedIndex>> DiscoverAsync(
        NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT i.indexname, i.indexdef
            FROM pg_indexes i
            JOIN pg_class c  ON c.relname = i.indexname
                            AND c.relnamespace = i.schemaname::regnamespace
            JOIN pg_am    am ON am.oid = c.relam
            WHERE i.schemaname = 'public'
              AND i.tablename = ANY(@tables)
              AND am.amname = 'gin'
              -- never touch an index that backs a PK/unique constraint
              AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = c.oid)
            ORDER BY i.indexname
            """;
        var found = new List<ImportCheckpointStore.DroppedIndex>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("tables", Tables);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            found.Add(new ImportCheckpointStore.DroppedIndex
            {
                Name = r.GetString(0),
                Definition = r.GetString(1),
            });
        return found;
    }

    // Drop the given indexes. Quoted identifiers throughout — index names come
    // from the catalog, but quoting keeps this correct for mixed-case or
    // otherwise awkward names rather than relying on them being tame.
    public static async Task DropAsync(
        NpgsqlConnection conn, IReadOnlyList<ImportCheckpointStore.DroppedIndex> indexes,
        ILogger log, CancellationToken ct)
    {
        foreach (var ix in indexes)
        {
            await using var cmd = new NpgsqlCommand(
                $"DROP INDEX IF EXISTS public.\"{ix.Name.Replace("\"", "\"\"")}\"", conn);
            cmd.CommandTimeout = 0;
            await cmd.ExecuteNonQueryAsync(ct);
            log.LogInformation("import: dropped index {Index} for the bulk load", ix.Name);
        }
    }

    // Rebuild them from their captured definitions. Plain CREATE INDEX, not
    // CONCURRENTLY: it is far faster, and this runs in a maintenance window by
    // construction (the indexes have been missing for the whole import, so
    // queries were already degraded). CONCURRENTLY would also leave an INVALID
    // index behind on failure, which is worse than a clean error here.
    //
    // Each statement is independent: one failure does not abandon the rest, and
    // the names that did not come back are reported so the operator knows
    // exactly what to fix.
    public static async Task<List<string>> RestoreAsync(
        NpgsqlConnection conn, IReadOnlyList<ImportCheckpointStore.DroppedIndex> indexes,
        ILogger log, CancellationToken ct)
    {
        var failed = new List<string>();
        await using (var mem = new NpgsqlCommand($"SET maintenance_work_mem = '{RebuildWorkMem}'", conn))
            await mem.ExecuteNonQueryAsync(ct);

        foreach (var ix in indexes)
        {
            try
            {
                log.LogInformation("import: rebuilding index {Index}...", ix.Name);
                await using var cmd = new NpgsqlCommand(ix.Definition, conn);
                cmd.CommandTimeout = 0;   // a multi-million-row GIN build takes minutes
                await cmd.ExecuteNonQueryAsync(ct);
                log.LogInformation("import: rebuilt index {Index}", ix.Name);
            }
            catch (Exception ex)
            {
                failed.Add(ix.Name);
                log.LogError(ex, "import: FAILED to rebuild index {Index} — run this manually:\n  {Sql}",
                    ix.Name, ix.Definition);
            }
        }
        return failed;
    }

    // The SQL an operator needs if everything goes wrong (crash between drop
    // and rebuild). Printed on the failure path and recoverable from the
    // checkpoint sidecar.
    public static string RecoverySql(IReadOnlyList<ImportCheckpointStore.DroppedIndex> indexes)
        => $"SET maintenance_work_mem = '{RebuildWorkMem}';\n"
           + string.Join("\n", indexes.Select(i => i.Definition.TrimEnd(';') + ";"));
}
