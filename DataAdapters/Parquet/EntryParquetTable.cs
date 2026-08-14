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
//   * ARRAY columns are JSON strings too, NOT native Parquet lists. §4.2 still
//     says native `list<string>`; §2.2 supersedes it, because native lists
//     require repetition levels and dropping those is what kept the encoder
//     small enough to hand-write. The doc is corrected in this commit.
//   * TIMESTAMPS are TIMESTAMP_MICROS in UTC, converted back to the local-naive
//     form the DB and the rest of dmart use.
internal static class EntryParquetTable
{
    // Column order is the file's schema order and must stay stable: a reader
    // matches by name, but changing the order changes every file we produce.
    public static IReadOnlyList<ParquetFileWriter.ColumnSpec> Schema { get; } =
    [
        Required("shortname"),
        // space_name is DELIBERATELY absent: it is the Hive partition key in
        // the directory name (entries/space_name=<s>/), and a Hive partition
        // column lives in the path, not in the file. Storing both makes every
        // reader that infers partitions — DuckDB, Spark, pyarrow — fail with
        // "Field space_name has incompatible types: string vs dictionary",
        // which is exactly the compatibility §4.1 is asking for. The value is
        // carried by the manifest and restored on read.
        Required("subpath"),
        Required("uuid"),
        new("is_active", ParquetType.Boolean, null),
        Optional("slug"),
        Optional("displayname"),
        Optional("description"),
        Required("tags"),                       // JSON array, never null — see below
        Timestamp("created_at"),
        Timestamp("updated_at"),
        Required("owner_shortname"),
        Optional("owner_group_shortname"),
        Optional("acl"),
        Optional("payload"),
        Optional("relationships"),
        Optional("last_checksum_history"),
        Required("resource_type"),
        Optional("state"),
        new("is_open", ParquetType.Boolean, null, Optional: true),
        Optional("reporter"),
        Optional("workflow_shortname"),
        Optional("collaborators"),
        Optional("resolution_reason"),
        Optional("query_policies"),
    ];

    private static ParquetFileWriter.ColumnSpec Required(string name) =>
        new(name, ParquetType.ByteArray, ConvertedType.Utf8);

    private static ParquetFileWriter.ColumnSpec Optional(string name) =>
        new(name, ParquetType.ByteArray, ConvertedType.Utf8, Optional: true);

    private static ParquetFileWriter.ColumnSpec Timestamp(string name) =>
        new(name, ParquetType.Int64, ConvertedType.TimestampMicros);

    // ---- write ----

    /// <summary>Builds one row group's worth of pages, in <see cref="Schema"/> order.</summary>
    public static IReadOnlyList<ParquetFileWriter.ColumnPage> BuildPages(IReadOnlyList<Entry> rows) =>
    [
        Str(rows, e => e.Shortname),
        Str(rows, e => e.Subpath),
        Str(rows, e => e.Uuid),
        Bool(rows, e => e.IsActive),
        NullableStr(rows, e => e.Slug),
        NullableStr(rows, e => Json(e.Displayname, DmartJsonContext.Default.Translation)),
        NullableStr(rows, e => Json(e.Description, DmartJsonContext.Default.Translation)),
        // Tags is non-nullable in the model and defaults to an empty list, so it
        // is written as a required "[]" rather than a null. Making it optional
        // would collapse "no tags" and "unknown" into the same value on restore.
        Str(rows, e => JsonSerializer.Serialize(e.Tags, DmartJsonContext.Default.ListString)),
        Ts(rows, e => e.CreatedAt),
        Ts(rows, e => e.UpdatedAt),
        Str(rows, e => e.OwnerShortname),
        NullableStr(rows, e => e.OwnerGroupShortname),
        NullableStr(rows, e => Json(e.Acl, DmartJsonContext.Default.ListAclEntry)),
        NullableStr(rows, e => Json(e.Payload, DmartJsonContext.Default.Payload)),
        NullableStr(rows, e => Json(e.Relationships, DmartJsonContext.Default.ListDictionaryStringObject)),
        NullableStr(rows, e => e.LastChecksumHistory),
        // The same text the SQL column holds, so a Parquet export and a SQL
        // dump name resource types identically.
        Str(rows, e => JsonbHelpers.EnumMember(e.ResourceType)),
        NullableStr(rows, e => e.State),
        NullableBool(rows, e => e.IsOpen),
        NullableStr(rows, e => Json(e.Reporter, DmartJsonContext.Default.Reporter)),
        NullableStr(rows, e => e.WorkflowShortname),
        NullableStr(rows, e => Json(e.Collaborators, DmartJsonContext.Default.DictionaryStringString)),
        NullableStr(rows, e => e.ResolutionReason),
        NullableStr(rows, e => Json(e.QueryPolicies, DmartJsonContext.Default.ListString)),
    ];

    private static string? Json<T>(T? value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
        => value is null ? null : JsonSerializer.Serialize(value, info);

    private static ParquetFileWriter.ColumnPage Str(IReadOnlyList<Entry> rows, Func<Entry, string> f) =>
        new(ParquetFileWriter.PlainByteArray([.. rows.Select(f)]), null);

    private static ParquetFileWriter.ColumnPage NullableStr(IReadOnlyList<Entry> rows, Func<Entry, string?> f)
    {
        var present = new List<string>(rows.Count);
        var levels = new List<int>(rows.Count);
        foreach (var r in rows)
        {
            var v = f(r);
            levels.Add(v is null ? 0 : 1);
            if (v is not null) present.Add(v);
        }
        return new(ParquetFileWriter.PlainByteArray(present), levels);
    }

    private static ParquetFileWriter.ColumnPage Bool(IReadOnlyList<Entry> rows, Func<Entry, bool> f) =>
        new(ParquetFileWriter.PlainBoolean([.. rows.Select(f)]), null);

    private static ParquetFileWriter.ColumnPage NullableBool(IReadOnlyList<Entry> rows, Func<Entry, bool?> f)
    {
        var present = new List<bool>(rows.Count);
        var levels = new List<int>(rows.Count);
        foreach (var r in rows)
        {
            var v = f(r);
            levels.Add(v is null ? 0 : 1);
            if (v is not null) present.Add(v.Value);
        }
        return new(ParquetFileWriter.PlainBoolean(present), levels);
    }

    private static ParquetFileWriter.ColumnPage Ts(IReadOnlyList<Entry> rows, Func<Entry, DateTime> f) =>
        new(ParquetFileWriter.PlainTimestampMicros([.. rows.Select(f)]), null);

    // ---- read ----

    /// <summary>Rebuilds entries from a table produced by <see cref="BuildPages"/>.</summary>
    /// <param name="spaceName">
    /// The partition this file belongs to. Not stored in the file — see the
    /// schema comment — so the caller supplies it from the manifest.
    /// </param>
    public static List<Entry> FromTable(ParquetFileReader.ParquetTable table, string spaceName)
    {
        var count = (int)table.RowCount;
        var shortname = Strings(table, "shortname");
        var subpath = Strings(table, "subpath");
        var uuid = Strings(table, "uuid");
        var isActive = Bools(table, "is_active");
        var slug = Strings(table, "slug");
        var displayname = Strings(table, "displayname");
        var description = Strings(table, "description");
        var tags = Strings(table, "tags");
        var createdAt = table.Column("created_at").AsTimestamps();
        var updatedAt = table.Column("updated_at").AsTimestamps();
        var owner = Strings(table, "owner_shortname");
        var ownerGroup = Strings(table, "owner_group_shortname");
        var acl = Strings(table, "acl");
        var payload = Strings(table, "payload");
        var relationships = Strings(table, "relationships");
        var checksum = Strings(table, "last_checksum_history");
        var resourceType = Strings(table, "resource_type");
        var state = Strings(table, "state");
        var isOpen = Bools(table, "is_open");
        var reporter = Strings(table, "reporter");
        var workflow = Strings(table, "workflow_shortname");
        var collaborators = Strings(table, "collaborators");
        var resolution = Strings(table, "resolution_reason");
        var queryPolicies = Strings(table, "query_policies");

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
                Displayname = FromJson(displayname[i], DmartJsonContext.Default.Translation),
                Description = FromJson(description[i], DmartJsonContext.Default.Translation),
                Tags = FromJson(tags[i], DmartJsonContext.Default.ListString) ?? [],
                CreatedAt = ToLocalNaive(createdAt[i]),
                UpdatedAt = ToLocalNaive(updatedAt[i]),
                OwnerShortname = owner[i] ?? "",
                OwnerGroupShortname = ownerGroup[i],
                Acl = FromJson(acl[i], DmartJsonContext.Default.ListAclEntry),
                Payload = FromJson(payload[i], DmartJsonContext.Default.Payload),
                Relationships = FromJson(relationships[i], DmartJsonContext.Default.ListDictionaryStringObject),
                LastChecksumHistory = checksum[i],
                ResourceType = JsonbHelpers.ParseEnumMember<ResourceType>(resourceType[i] ?? "content"),
                State = state[i],
                IsOpen = isOpen[i],
                Reporter = FromJson(reporter[i], DmartJsonContext.Default.Reporter),
                WorkflowShortname = workflow[i],
                Collaborators = FromJson(collaborators[i], DmartJsonContext.Default.DictionaryStringString),
                ResolutionReason = resolution[i],
                QueryPolicies = FromJson(queryPolicies[i], DmartJsonContext.Default.ListString),
            });
        }
        return result;
    }

    private static string?[] Strings(ParquetFileReader.ParquetTable t, string name) =>
        t.Column(name).StringValues
        ?? throw new InvalidDataException($"column '{name}' is not a string column");

    private static bool?[] Bools(ParquetFileReader.ParquetTable t, string name) =>
        t.Column(name).BooleanValues
        ?? throw new InvalidDataException($"column '{name}' is not a boolean column");

    private static T? FromJson<T>(string? json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
        => json is null ? null : JsonSerializer.Deserialize(json, info);

    // The file stores UTC instants; dmart stores local-naive DateTimes (a
    // timestamp-without-tz column, matching Python's naive datetimes). Handing
    // back a UTC-kind value would round-trip the INSTANT correctly and still
    // change the value every consumer reads, so it is converted back.
    private static DateTime ToLocalNaive(DateTime? utc) =>
        utc is null
            ? default
            : DateTime.SpecifyKind(utc.Value.ToLocalTime(), DateTimeKind.Unspecified);
}
