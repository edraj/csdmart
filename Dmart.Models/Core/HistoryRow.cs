namespace Dmart.Models.Core;

// One row of an entry's audit trail (the dmart `histories` table). Shared by
// Dmart.Client and Dmart.SqlAdapter so QueryHistoryAsync returns the same CLR
// type regardless of backend. The HTTP client leaves LastChecksumHistory null
// (the server doesn't project it into the history query response).
public sealed record HistoryRow
{
    public required string Uuid { get; init; }
    public required string SpaceName { get; init; }
    public required string Subpath { get; init; }
    public required string Shortname { get; init; }
    public DateTime Timestamp { get; init; }
    public string? OwnerShortname { get; init; }
    public Dictionary<string, object>? RequestHeaders { get; init; }
    public Dictionary<string, object>? Diff { get; init; }
    public string? LastChecksumHistory { get; init; }
}
