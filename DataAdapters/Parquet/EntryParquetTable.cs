using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.DataAdapters.Parquet;

// Maps Entry rows to Parquet columns and back.
//
// This is where an export stops being an encoder and starts being a backup, so
// the rule it is built around is that every column must survive the round trip
// unchanged. A column that writes cleanly but reads back subtly different — a
// timestamp shifted, an empty list become null — is a corrupted restore that
// reports success.
//
// Three shape decisions, all from docs/parquet-export-design.md §2.2/§4.2:
//
//   * JSON columns stay OPAQUE STRINGS (payload, acl, relationships, and the
//     translation blobs). Lossless, and DuckDB still reads them via
//     json_extract. Exploding them into typed columns needs per-schema
//     knowledge and breaks round-tripping.
//   * ARRAY columns are JSON strings too, NOT native Parquet lists. Native
//     lists require repetition levels, and dropping those is what kept the
//     encoder small enough to hand-write.
//   * TIMESTAMPS are TIMESTAMP_MICROS in UTC, converted back to the local-naive
//     form the DB and the rest of dmart use.
internal static class EntryParquetTable
{
    // Column order is the file's schema order and must stay stable: a reader
    // matches by name, but changing the order changes every file we produce.
    public static IReadOnlyList<ParquetFileWriter.ColumnSpec> Schema { get; } =
    [
        Pq.Required("shortname"),
        // space_name is DELIBERATELY absent: it is the Hive partition key in
        // the directory name (entries/space_name=<s>/), and a Hive partition
        // column lives in the path, not in the file. Storing both makes every
        // reader that infers partitions — DuckDB, Spark, pyarrow — fail with
        // "Field space_name has incompatible types: string vs dictionary",
        // which is exactly the compatibility §4.1 is asking for. The value is
        // carried by the manifest and restored on read.
        Pq.Required("subpath"),
        Pq.Required("uuid"),
        Pq.Flag("is_active"),
        Pq.Optional("slug"),
        Pq.Optional("displayname"),
        Pq.Optional("description"),
        Pq.Required("tags"),                    // JSON array, never null
        Pq.Timestamp("created_at"),
        Pq.Timestamp("updated_at"),
        Pq.Required("owner_shortname"),
        Pq.Optional("owner_group_shortname"),
        Pq.Optional("acl"),
        Pq.Optional("payload"),
        Pq.Optional("relationships"),
        Pq.Optional("last_checksum_history"),
        Pq.Required("resource_type"),
        Pq.Optional("state"),
        Pq.OptionalFlag("is_open"),
        Pq.Optional("reporter"),
        Pq.Optional("workflow_shortname"),
        Pq.Optional("collaborators"),
        Pq.Optional("resolution_reason"),
        Pq.Optional("query_policies"),
    ];

    /// <summary>Builds one row group's worth of pages, in <see cref="Schema"/> order.</summary>
    public static IReadOnlyList<ParquetFileWriter.ColumnPage> BuildPages(IReadOnlyList<Entry> rows) =>
    [
        Pq.Str(rows, e => e.Shortname),
        Pq.Str(rows, e => e.Subpath),
        Pq.Str(rows, e => e.Uuid),
        Pq.Bool(rows, e => e.IsActive),
        Pq.NullableStr(rows, e => e.Slug),
        Pq.NullableStr(rows, e => Pq.Json(e.Displayname, DmartJsonContext.Default.Translation)),
        Pq.NullableStr(rows, e => Pq.Json(e.Description, DmartJsonContext.Default.Translation)),
        // Tags is non-nullable in the model and defaults to an empty list, so it
        // is written as a required "[]" rather than a null. Making it optional
        // would collapse "no tags" and "unknown" into the same value on restore.
        Pq.Str(rows, e => Pq.JsonAlways(e.Tags, DmartJsonContext.Default.ListString)),
        Pq.Ts(rows, e => e.CreatedAt),
        Pq.Ts(rows, e => e.UpdatedAt),
        Pq.Str(rows, e => e.OwnerShortname),
        Pq.NullableStr(rows, e => e.OwnerGroupShortname),
        Pq.NullableStr(rows, e => Pq.Json(e.Acl, DmartJsonContext.Default.ListAclEntry)),
        Pq.NullableStr(rows, e => Pq.Json(e.Payload, DmartJsonContext.Default.Payload)),
        Pq.NullableStr(rows, e => Pq.Json(e.Relationships, DmartJsonContext.Default.ListDictionaryStringObject)),
        Pq.NullableStr(rows, e => e.LastChecksumHistory),
        // The same text the SQL column holds, so a Parquet export and a SQL
        // dump name resource types identically.
        Pq.Str(rows, e => JsonbHelpers.EnumMember(e.ResourceType)),
        Pq.NullableStr(rows, e => e.State),
        Pq.NullableBool(rows, e => e.IsOpen),
        Pq.NullableStr(rows, e => Pq.Json(e.Reporter, DmartJsonContext.Default.Reporter)),
        Pq.NullableStr(rows, e => e.WorkflowShortname),
        Pq.NullableStr(rows, e => Pq.Json(e.Collaborators, DmartJsonContext.Default.DictionaryStringString)),
        Pq.NullableStr(rows, e => e.ResolutionReason),
        Pq.NullableStr(rows, e => Pq.Json(e.QueryPolicies, DmartJsonContext.Default.ListString)),
    ];

    /// <summary>Rebuilds entries from a table produced by <see cref="BuildPages"/>.</summary>
    /// <param name="spaceName">
    /// The partition this file belongs to. Not stored in the file — see the
    /// schema comment — so the caller supplies it from the manifest.
    /// </param>
    public static List<Entry> FromTable(ParquetFileReader.ParquetTable table, string spaceName)
    {
        var count = (int)table.RowCount;
        var shortname = Pq.Strings(table, "shortname");
        var subpath = Pq.Strings(table, "subpath");
        var uuid = Pq.Strings(table, "uuid");
        var isActive = Pq.Bools(table, "is_active");
        var slug = Pq.Strings(table, "slug");
        var displayname = Pq.Strings(table, "displayname");
        var description = Pq.Strings(table, "description");
        var tags = Pq.Strings(table, "tags");
        var createdAt = table.Column("created_at").AsTimestamps();
        var updatedAt = table.Column("updated_at").AsTimestamps();
        var owner = Pq.Strings(table, "owner_shortname");
        var ownerGroup = Pq.Strings(table, "owner_group_shortname");
        var acl = Pq.Strings(table, "acl");
        var payload = Pq.Strings(table, "payload");
        var relationships = Pq.Strings(table, "relationships");
        var checksum = Pq.Strings(table, "last_checksum_history");
        var resourceType = Pq.Strings(table, "resource_type");
        var state = Pq.Strings(table, "state");
        var isOpen = Pq.Bools(table, "is_open");
        var reporter = Pq.Strings(table, "reporter");
        var workflow = Pq.Strings(table, "workflow_shortname");
        var collaborators = Pq.Strings(table, "collaborators");
        var resolution = Pq.Strings(table, "resolution_reason");
        var queryPolicies = Pq.Strings(table, "query_policies");

        var result = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(new Entry
            {
                Shortname = shortname[i] ?? "",
                SpaceName = spaceName,
                Subpath = subpath[i] ?? "/",
                Uuid = uuid[i] ?? "",
                IsActive = isActive[i] ?? false,
                Slug = slug[i],
                Displayname = Pq.FromJson(displayname[i], DmartJsonContext.Default.Translation),
                Description = Pq.FromJson(description[i], DmartJsonContext.Default.Translation),
                Tags = Pq.FromJson(tags[i], DmartJsonContext.Default.ListString) ?? [],
                CreatedAt = Pq.ToLocalNaive(createdAt[i]),
                UpdatedAt = Pq.ToLocalNaive(updatedAt[i]),
                OwnerShortname = owner[i] ?? "",
                OwnerGroupShortname = ownerGroup[i],
                Acl = Pq.FromJson(acl[i], DmartJsonContext.Default.ListAclEntry),
                Payload = Pq.FromJson(payload[i], DmartJsonContext.Default.Payload),
                Relationships = Pq.FromJson(relationships[i], DmartJsonContext.Default.ListDictionaryStringObject),
                LastChecksumHistory = checksum[i],
                ResourceType = JsonbHelpers.ParseEnumMember<ResourceType>(resourceType[i] ?? "content"),
                State = state[i],
                IsOpen = isOpen[i],
                Reporter = Pq.FromJson(reporter[i], DmartJsonContext.Default.Reporter),
                WorkflowShortname = workflow[i],
                Collaborators = Pq.FromJson(collaborators[i], DmartJsonContext.Default.DictionaryStringString),
                ResolutionReason = resolution[i],
                QueryPolicies = Pq.FromJson(queryPolicies[i], DmartJsonContext.Default.ListString),
            });
        }
        return result;
    }
}
