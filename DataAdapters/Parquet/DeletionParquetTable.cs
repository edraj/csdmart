using Dmart.Models.Core;

namespace Dmart.DataAdapters.Parquet;

// Tombstones, carried by INCREMENTAL exports only — design §4.1, §5.2.
//
// A full export needs none: it IS the state, so anything absent from it is
// deleted by construction. An increment carries only what changed, and a
// deleted row is indistinguishable from an unchanged one without these.
//
// Not Hive-partitioned. Tombstones for a space delete name the space that no
// longer exists, and writing them under `space_name=<gone>/` would put the
// record of a deletion inside a partition whose whole point is that it is gone.
internal static class DeletionParquetTable
{
    public static IReadOnlyList<ParquetFileWriter.ColumnSpec> Schema { get; } =
    [
        Pq.Required("table_name"),
        Pq.Required("space_name"),
        Pq.Required("subpath"),
        Pq.Required("shortname"),
        Pq.Required("resource_type"),
        Pq.Timestamp("deleted_at"),
    ];

    public static IReadOnlyList<ParquetFileWriter.ColumnPage> BuildPages(IReadOnlyList<DeletionRow> rows) =>
    [
        Pq.Str(rows, d => d.TableName),
        Pq.Str(rows, d => d.SpaceName),
        Pq.Str(rows, d => d.Subpath),
        Pq.Str(rows, d => d.Shortname),
        Pq.Str(rows, d => d.ResourceType),
        Pq.Ts(rows, d => d.DeletedAt),
    ];

    public static List<DeletionRow> FromTable(ParquetFileReader.ParquetTable t)
    {
        var count = (int)t.RowCount;
        var table = Pq.Strings(t, "table_name");
        var space = Pq.Strings(t, "space_name");
        var subpath = Pq.Strings(t, "subpath");
        var shortname = Pq.Strings(t, "shortname");
        var type = Pq.Strings(t, "resource_type");
        var deletedAt = t.Column("deleted_at").AsTimestamps();

        var result = new List<DeletionRow>(count);
        for (var i = 0; i < count; i++)
            result.Add(new DeletionRow
            {
                TableName = table[i] ?? "",
                SpaceName = space[i] ?? "",
                Subpath = subpath[i] ?? "/",
                Shortname = shortname[i] ?? "",
                ResourceType = type[i] ?? "",
                DeletedAt = Pq.ToLocalNaive(deletedAt[i]),
            });
        return result;
    }
}
