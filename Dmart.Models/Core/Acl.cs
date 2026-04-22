namespace Dmart.Models.Core;

// dmart's ACL entries are stored as JSONB list-of-dicts on Metas. We model the entry
// shape so it round-trips through DmartJsonContext cleanly.
//
// Python parity: dmart_plain/backend/models/core.py::ACL exposes exactly two
// fields — `user_shortname` and `allowed_actions`. `AllowedActions`
// serializes to `allowed_actions` via the snake-case naming policy; the
// legacy `Allowed` alias was dropped because it doesn't round-trip with
// Python clients. `Denied` is a C# extension kept for PermissionService's
// explicit-deny semantics (Python has no equivalent).
public sealed record AclEntry
{
    public string? UserShortname { get; init; }
    public List<string>? AllowedActions { get; init; }
    public List<string>? Denied { get; init; }
}
