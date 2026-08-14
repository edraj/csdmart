using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Dmart.DataAdapters.Sql;

// Writes tombstone rows for deletions — docs/parquet-export-design.md §5.2.
//
// Why this exists: a row deleted since the last incremental export is simply
// ABSENT, and absence is indistinguishable from unchanged. Without tombstones
// an incremental consumer drifts from source permanently and never notices,
// which is the worst shape a replication bug can take.
//
// Four rules this is built to satisfy, all of them easy to get wrong:
//
//   1. SAME TRANSACTION as the delete. A crash between the DELETE and the
//      tombstone loses the deletion forever, and invisibly. Every method here
//      takes the caller's open connection/transaction rather than opening its
//      own, so there is no way to call it correctly and still get this wrong.
//
//   2. IN CODE, NOT A TRIGGER. `dmart import --fast` sets
//      session_replication_role='replica', which bypasses triggers — so a
//      trigger-based tombstone would be silently skipped during exactly the
//      bulk operations that move the most rows.
//
//   3. CASCADES MUST TOMBSTONE EVERY DESCENDANT. Deleting a folder removes its
//      subtree and its attachments; each removed row needs one of these. This
//      is the likeliest bug, so the insert runs over the SAME PREDICATE as the
//      delete it accompanies rather than over a separately-derived row list —
//      the two cannot disagree about what was removed.
//
//   4. Retention must exceed the increment cadence. Pruning tombstones older
//      than the gap between runs silently loses deletes. Nothing here prunes;
//      that is deliberate, and the coupling is documented in §5.2.
//
// Ordering note: the INSERT ... SELECT must run BEFORE the DELETE, because it
// reads the rows the DELETE is about to remove.
internal static class Tombstones
{
    /// <summary>
    /// Records every row matching <paramref name="wherePredicate"/> in
    /// <paramref name="table"/> as deleted. Runs on the caller's connection so
    /// it joins the caller's transaction.
    /// </summary>
    /// <param name="bind">
    /// Binds the positional parameters the predicate uses — the SAME values the
    /// accompanying DELETE binds.
    /// </param>
    /// <remarks>
    /// `resource_type` is selected when the table has one and defaulted to ''
    /// otherwise, so a consumer can tell a deleted folder from a deleted
    /// content row without joining anything.
    /// </remarks>
    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: table and predicate are const literals from calling repositories; every caller-supplied value binds through DbCommand.Parameters.")]
    public static async Task<int> RecordAsync(
        DbConnection conn, DbTransaction? tx, string table, string wherePredicate,
        Action<DbCommand> bind, bool hasResourceType, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        // Enlisting explicitly rather than relying on ambient behaviour: rule 1
        // is that the tombstone shares the delete's transaction, and a command
        // that quietly ran outside it would satisfy the tests and lose
        // deletions on a rollback.
        cmd.Transaction = tx;
        bind(cmd);
        var typeExpr = hasResourceType ? "resource_type" : "''";
        cmd.CommandText = $"""
            INSERT INTO deletions (table_name, space_name, subpath, shortname, resource_type)
            SELECT '{table}', space_name, subpath, shortname, {typeExpr}
            FROM {table}
            WHERE {wherePredicate}
            """;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Records a single known row, for deletes that already have its identity.</summary>
    public static async Task RecordOneAsync(
        DbConnection conn, DbTransaction? tx, string table, string spaceName, string subpath,
        string shortname, string resourceType, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        DbParams.Add(cmd, table);
        DbParams.Add(cmd, spaceName);
        DbParams.Add(cmd, subpath);
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, resourceType);
        cmd.CommandText = """
            INSERT INTO deletions (table_name, space_name, subpath, shortname, resource_type)
            VALUES ($1, $2, $3, $4, $5)
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
