namespace Dmart.Models.Core;

// One tombstone — a row that was deleted, and when.
//
// `TableName` is what lets a single `deletions` table serve entries,
// attachments, histories, spaces, users, roles and permissions rather than
// needing seven of them. See docs/parquet-export-design.md §5.2.
public sealed record DeletionRow
{
    public required string TableName { get; init; }
    public required string SpaceName { get; init; }
    public required string Subpath { get; init; }
    public required string Shortname { get; init; }

    /// <summary>Empty for tables that have no resource_type column.</summary>
    public string ResourceType { get; set; } = "";

    public DateTime DeletedAt { get; init; }
}
