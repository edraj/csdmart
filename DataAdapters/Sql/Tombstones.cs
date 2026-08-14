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
        // deleted_at is BOUND, not left to the column's NOW() default. The
        // default is evaluated by the database server in ITS timezone, while
        // everything dmart writes is host-local wall clock — so on a UTC server
        // with a +03 host the tombstones land three hours behind every other
        // timestamp, and an incremental scan keyed on deleted_at silently sees
        // none of them. Same reasoning as HistoryRepository.AppendAsync, and
        // caught here by an increment reporting 0 tombstones for a real delete.
        var stamp = DbParams.Add(cmd, Utils.TimeUtils.Now());
        var typeExpr = hasResourceType ? "resource_type" : "''";
        cmd.CommandText = $"""
            INSERT INTO deletions (table_name, space_name, subpath, shortname, resource_type, deleted_at)
            SELECT '{table}', space_name, subpath, shortname, {typeExpr}, {stamp}
            FROM {table}
            WHERE {wherePredicate}
            """;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Reads tombstones recorded at or after <paramref name="since"/> for one
    /// space, for an incremental export.
    /// </summary>
    /// <remarks>
    /// Index: idx_deletions_deleted_at. Ordered by id — the insertion order,
    /// and unique — because deleted_at collides freely: one cascade stamps
    /// every row it removes identically.
    /// </remarks>
    public static async Task<List<Models.Core.DeletionRow>> ReadSinceAsync(
        DbConnection conn, string spaceName, DateTime since, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        DbParams.Add(cmd, spaceName);
        DbParams.Add(cmd, since);
        cmd.CommandText = """
            SELECT table_name, space_name, subpath, shortname, resource_type, deleted_at
            FROM deletions
            WHERE space_name = $1 AND deleted_at >= $2
            ORDER BY id
            """;

        var rows = new List<Models.Core.DeletionRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new Models.Core.DeletionRow
            {
                TableName = r.GetString(0),
                SpaceName = r.GetString(1),
                Subpath = r.GetString(2),
                Shortname = r.GetString(3),
                ResourceType = r.GetString(4),
                DeletedAt = r.GetDateTime(5),
            });
        return rows;
    }

}
