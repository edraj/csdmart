namespace Dmart.ParquetAdapter;

// Canonical row shape for a dmart-terminal-folder Parquet file.
// Parquet.Net 6.x's ParquetSerializer reflects this class into the
// column schema automatically — one column per public property,
// nullable columns wherever the property type is nullable.
//
// One row per entry under the terminal folder. The shape is
// deliberately thin: instead of mapping every entries-table column
// individually (uuid, shortname, space_name, subpath, ..., 25+
// columns whose schema drifts every time dmart adds a field), we
// store the whole meta.*.json as an opaque UTF-8 string in MetaJson
// and let dmart's existing deserializer reconstitute the entry on
// unarchive. The columns we DO surface separately are the ones
// operators (and DuckDB queries) want without unpacking JSON:
//
//   * Uuid           — primary key, useful for joining across files
//   * Shortname      — file-name in the original .dm/ tree
//   * ResourceType   — content / ticket / schema / etc.
//   * Subpath        — full subpath relative to the space root
//   * CreatedAt      — for time-range queries
//   * UpdatedAt      — for time-range queries
//   * MetaJson       — full meta.*.json as a UTF-8 string
//   * BodyBase64     — inlined payload body file when small enough
//                      (json / html / md / txt / csv / jsonl),
//                      base64-encoded as a string column. NULL for
//                      binary attachments whose bytes stay on disk
//                      under a sibling attachments/ folder. Base64
//                      is used here instead of a raw byte[] column
//                      because Parquet.Net 6's reflection serializer
//                      doesn't accept byte[]? as a column type (its
//                      Striper requires T:struct for nullable spans).
//                      The encoding overhead is acceptable given the
//                      1 MB per-body cap.
//   * BodyFilename   — original filename of the body when separately
//                      stored on disk (NULL when inlined)
//
// Compression: Snappy (DuckDB default; ~30-50% size reduction on
// JSON-heavy payloads, sub-ms decompress overhead).
public sealed class EntryRow
{
    public string Uuid { get; set; } = "";
    public string Shortname { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string Subpath { get; set; } = "/";
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string MetaJson { get; set; } = "";
    public string? BodyBase64 { get; set; }
    public string? BodyFilename { get; set; }
}
