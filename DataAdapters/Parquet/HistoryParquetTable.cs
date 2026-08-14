using Dmart.Models.Core;
using Dmart.Models.Json;

namespace Dmart.DataAdapters.Parquet;

// The audit trail. Nine columns, the smallest table in the export and the one
// with the strictest requirement: it must come back EXACTLY as it went out.
//
// Every other table describes current state, which a later export would
// legitimately correct. History describes past events. A restored row with a
// regenerated uuid or a re-stamped timestamp is not a restored audit trail —
// it is a record of the restore, which is worse than having no history at all
// because it looks authentic.
//
// Hive-partitioned by space_name like entries and attachments, so space_name is
// absent as a column and travels in the manifest.
internal static class HistoryParquetTable
{
    public static IReadOnlyList<ParquetFileWriter.ColumnSpec> Schema { get; } =
    [
        Pq.Required("uuid"),
        Pq.Required("subpath"),
        Pq.Required("shortname"),
        Pq.Timestamp("timestamp"),
        Pq.Optional("owner_shortname"),
        // NOT NULL in dmart's schema with a "{}" default, so these are required
        // columns carrying an empty object rather than optional ones carrying
        // null — writing null would restore as a NOT NULL violation.
        Pq.Required("request_headers"),
        Pq.Required("diff"),
        Pq.Optional("last_checksum_history"),
    ];

    public static IReadOnlyList<ParquetFileWriter.ColumnPage> BuildPages(IReadOnlyList<HistoryRow> rows) =>
    [
        Pq.Str(rows, h => h.Uuid),
        Pq.Str(rows, h => h.Subpath),
        Pq.Str(rows, h => h.Shortname),
        Pq.Ts(rows, h => h.Timestamp),
        Pq.NullableStr(rows, h => h.OwnerShortname),
        Pq.Str(rows, h => h.RequestHeaders is null
            ? "{}"
            : Pq.JsonAlways(h.RequestHeaders, DmartJsonContext.Default.DictionaryStringObject)),
        Pq.Str(rows, h => h.Diff is null
            ? "{}"
            : Pq.JsonAlways(h.Diff, DmartJsonContext.Default.DictionaryStringObject)),
        Pq.NullableStr(rows, h => h.LastChecksumHistory),
    ];

    /// <param name="spaceName">From the manifest — the Hive partition key.</param>
    public static List<HistoryRow> FromTable(ParquetFileReader.ParquetTable t, string spaceName)
    {
        var count = (int)t.RowCount;
        var uuid = Pq.Strings(t, "uuid");
        var subpath = Pq.Strings(t, "subpath");
        var shortname = Pq.Strings(t, "shortname");
        var timestamp = t.Column("timestamp").AsTimestamps();
        var owner = Pq.Strings(t, "owner_shortname");
        var headers = Pq.Strings(t, "request_headers");
        var diff = Pq.Strings(t, "diff");
        var checksum = Pq.Strings(t, "last_checksum_history");

        var result = new List<HistoryRow>(count);
        for (var i = 0; i < count; i++)
            result.Add(new HistoryRow
            {
                Uuid = uuid[i] ?? "",
                SpaceName = spaceName,
                Subpath = subpath[i] ?? "/",
                Shortname = shortname[i] ?? "",
                Timestamp = Pq.ToLocalNaive(timestamp[i]),
                OwnerShortname = owner[i],
                RequestHeaders = Pq.FromJson(headers[i], DmartJsonContext.Default.DictionaryStringObject),
                Diff = Pq.FromJson(diff[i], DmartJsonContext.Default.DictionaryStringObject),
                LastChecksumHistory = checksum[i],
            });
        return result;
    }
}
