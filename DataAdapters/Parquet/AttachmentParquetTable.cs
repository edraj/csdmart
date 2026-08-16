using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.DataAdapters.Parquet;

// Attachment metadata. The media BYTES are deliberately not here.
//
// §4.3: raw media does not belong in a Parquet row group. It is incompressible,
// it destroys row-group size predictability, and it defeats column projection —
// a query for "which attachments changed" would drag every blob off disk. So
// each blob is written once to `blobs/<sha256[0:2]>/<sha256>` and the row keeps
// `media_sha256` and `media_size` instead.
//
// Two payoffs beyond file size. Within one export the same file attached to
// twenty entries is stored ONCE. Across exports, an attachment whose metadata
// changed but whose bytes did not ships zero blob bytes — which for a daily
// pipeline over a multi-GB store matters far more than the metadata win,
// because media is where the gigabytes are.
internal static class AttachmentParquetTable
{
    /// <summary>Metadata plus the content address of its blob, if it has one.</summary>
    internal sealed record Row(Attachment Attachment, string? MediaSha256, long MediaSize);

    // Like entries, this table IS Hive-partitioned by space_name
    // (attachments/space_name=<s>/), so space_name is absent as a column — a
    // partition key stored in both places makes every partition-inferring
    // reader fail to merge the two.
    public static IReadOnlyList<ParquetFileWriter.ColumnSpec> Schema { get; } =
    [
        Pq.Required("shortname"),
        Pq.Required("subpath"),
        Pq.Required("uuid"),
        Pq.Flag("is_active"),
        Pq.Optional("slug"),
        Pq.Optional("displayname"),
        Pq.Optional("description"),
        Pq.Required("tags"),
        Pq.Timestamp("created_at"),
        Pq.Timestamp("updated_at"),
        Pq.Required("owner_shortname"),
        Pq.Optional("owner_group_shortname"),
        Pq.Optional("acl"),
        Pq.Optional("payload"),
        Pq.Optional("relationships"),
        Pq.Optional("last_checksum_history"),
        Pq.Required("resource_type"),
        Pq.Optional("author_locator"),
        // Null when the attachment carries no media. That is a real
        // distinction: an attachment row with no blob is not the same as one
        // whose blob is a zero-byte file.
        Pq.Optional("media_sha256"),
        Pq.OptionalInt("media_size"),
        Pq.Optional("body"),
        Pq.Optional("state"),
    ];

    public static IReadOnlyList<ParquetFileWriter.ColumnPage> BuildPages(IReadOnlyList<Row> rows) =>
    [
        Pq.Str(rows, r => r.Attachment.Shortname),
        Pq.Str(rows, r => r.Attachment.Subpath),
        Pq.Str(rows, r => r.Attachment.Uuid),
        Pq.Bool(rows, r => r.Attachment.IsActive),
        Pq.NullableStr(rows, r => r.Attachment.Slug),
        Pq.NullableStr(rows, r => Pq.Json(r.Attachment.Displayname, DmartJsonContext.Default.Translation)),
        Pq.NullableStr(rows, r => Pq.Json(r.Attachment.Description, DmartJsonContext.Default.Translation)),
        Pq.Str(rows, r => Pq.JsonAlways(r.Attachment.Tags, DmartJsonContext.Default.ListString)),
        Pq.Ts(rows, r => r.Attachment.CreatedAt),
        Pq.Ts(rows, r => r.Attachment.UpdatedAt),
        Pq.Str(rows, r => r.Attachment.OwnerShortname),
        Pq.NullableStr(rows, r => r.Attachment.OwnerGroupShortname),
        Pq.NullableStr(rows, r => Pq.Json(r.Attachment.Acl, DmartJsonContext.Default.ListAclEntry)),
        Pq.NullableStr(rows, r => Pq.Json(r.Attachment.Payload, DmartJsonContext.Default.Payload)),
        Pq.NullableStr(rows, r => Pq.Json(r.Attachment.Relationships, DmartJsonContext.Default.ListDictionaryStringObject)),
        Pq.NullableStr(rows, r => r.Attachment.LastChecksumHistory),
        Pq.Str(rows, r => JsonbHelpers.EnumMember(r.Attachment.ResourceType)),
        Pq.NullableStr(rows, r => Pq.Json(r.Attachment.AuthorLocator, DmartJsonContext.Default.Locator)),
        Pq.NullableStr(rows, r => r.MediaSha256),
        Pq.NullableInt(rows, r => r.MediaSha256 is null ? null : (int)r.MediaSize),
        Pq.NullableStr(rows, r => r.Attachment.Body),
        Pq.NullableStr(rows, r => r.Attachment.State),
    ];

    /// <summary>A streamed row plus the blob hash the caller computed for it.</summary>
    internal sealed record RawRow(
        Dmart.DataAdapters.Sql.AttachmentExportRow Row, string? MediaSha256);

    /// <summary>
    /// Same columns from the raw export rows — JSON stays the string the
    /// database returned instead of an object parsed and serialised back.
    /// </summary>
    /// <remarks>
    /// author_locator is written as null, matching the Attachment overload:
    /// the column does not exist in the table, so the paged reader never
    /// populates it either.
    /// </remarks>
    public static IReadOnlyList<ParquetFileWriter.ColumnPage> BuildPages(
        IReadOnlyList<RawRow> rows) =>
    [
        Pq.Str(rows, r => r.Row.Shortname),
        Pq.Str(rows, r => r.Row.Subpath),
        Pq.Str(rows, r => r.Row.Uuid),
        Pq.Bool(rows, r => r.Row.IsActive),
        Pq.NullableStr(rows, r => r.Row.Slug),
        Pq.NullableStr(rows, r => r.Row.Displayname),
        Pq.NullableStr(rows, r => r.Row.Description),
        Pq.Str(rows, r => r.Row.Tags ?? "[]"),
        Pq.Ts(rows, r => r.Row.CreatedAt),
        Pq.Ts(rows, r => r.Row.UpdatedAt),
        Pq.Str(rows, r => r.Row.OwnerShortname),
        Pq.NullableStr(rows, r => r.Row.OwnerGroupShortname),
        Pq.NullableStr(rows, r => r.Row.Acl),
        Pq.NullableStr(rows, r => r.Row.Payload),
        Pq.NullableStr(rows, r => r.Row.Relationships),
        Pq.NullableStr(rows, r => r.Row.LastChecksumHistory),
        Pq.Str(rows, r => r.Row.ResourceType),
        Pq.NullableStr(rows, _ => null),
        Pq.NullableStr(rows, r => r.MediaSha256),
        Pq.NullableInt(rows, r => r.MediaSha256 is null ? null : (int)r.Row.MediaSize),
        Pq.NullableStr(rows, r => r.Row.Body),
        Pq.NullableStr(rows, r => r.Row.State),
    ];

    /// <param name="spaceName">From the manifest — the Hive partition key.</param>
    /// <remarks>
    /// Attachment.Media is left NULL here. The caller rehydrates it from the
    /// blob store, because this class maps columns and knows nothing about
    /// where blobs live.
    /// </remarks>
    public static List<Row> FromTable(ParquetFileReader.ParquetTable t, string spaceName)
    {
        var count = (int)t.RowCount;
        var shortname = Pq.Strings(t, "shortname");
        var subpath = Pq.Strings(t, "subpath");
        var uuid = Pq.Strings(t, "uuid");
        var isActive = Pq.Bools(t, "is_active");
        var slug = Pq.Strings(t, "slug");
        var displayname = Pq.Strings(t, "displayname");
        var description = Pq.Strings(t, "description");
        var tags = Pq.Strings(t, "tags");
        var createdAt = t.Column("created_at").AsTimestamps();
        var updatedAt = t.Column("updated_at").AsTimestamps();
        var owner = Pq.Strings(t, "owner_shortname");
        var ownerGroup = Pq.Strings(t, "owner_group_shortname");
        var acl = Pq.Strings(t, "acl");
        var payload = Pq.Strings(t, "payload");
        var relationships = Pq.Strings(t, "relationships");
        var checksum = Pq.Strings(t, "last_checksum_history");
        var resourceType = Pq.Strings(t, "resource_type");
        var authorLocator = Pq.Strings(t, "author_locator");
        var sha = Pq.Strings(t, "media_sha256");
        var size = Pq.Longs(t, "media_size");
        var body = Pq.Strings(t, "body");
        var state = Pq.Strings(t, "state");

        var result = new List<Row>(count);
        for (var i = 0; i < count; i++)
            result.Add(new Row(
                new Attachment
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
                    ResourceType = JsonbHelpers.ParseEnumMember<ResourceType>(resourceType[i] ?? "media"),
                    AuthorLocator = Pq.FromJson(authorLocator[i], DmartJsonContext.Default.Locator),
                    Body = body[i],
                    State = state[i],
                },
                sha[i],
                size[i] ?? 0));
        return result;
    }
}
