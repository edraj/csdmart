using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Utils;

// Port of Python dmart's backend/utils/query_policies_helper.py::generate_query_policies.
// Produces the per-row LIKE-matchable patterns stored on entries.query_policies
// (TEXT[]). Every authenticated query runs AppendAclFilter which matches the
// caller's per-user policy list (built by PermissionService.BuildUserQueryPoliciesAsync)
// against these row patterns — so entries with an empty query_policies column
// are invisible to everyone except the owner and explicit ACL grants.
//
// Patterns emitted for space=X, subpath=/a/b, resource_type=content,
// is_active=true, owner=alice, owner_group=null:
//
//   X::content:true:alice
//   X::content:true
//   X:__all_subpaths__:content:true
//   X:a:content:true:alice
//   X:a:content:true
//   X:a/__all_subpaths__:content:true
//   X:a/b:content:true:alice
//   X:a/b:content:true
//
// The patterns walk the subpath tree from "/" outward, emitting at each level:
//   - owner-scoped literal
//   - owner-unscoped literal (always)
//   - owner_group-scoped literal, when the row has an owner_group
//   - a "__all_subpaths__ at level N" global form (skipped at root)
public static class QueryPolicies
{
    public static List<string> Generate(Entry e) => Generate(
        spaceName: e.SpaceName,
        subpath: e.Subpath,
        resourceType: JsonbHelpers.EnumMember(e.ResourceType),
        isActive: e.IsActive,
        ownerShortname: e.OwnerShortname,
        ownerGroupShortname: e.OwnerGroupShortname,
        entryShortname: e.ResourceType == ResourceType.Folder ? e.Shortname : null);

    public static List<string> Generate(Attachment a) => Generate(
        spaceName: a.SpaceName,
        subpath: a.Subpath,
        resourceType: JsonbHelpers.EnumMember(a.ResourceType),
        isActive: a.IsActive,
        ownerShortname: a.OwnerShortname,
        ownerGroupShortname: a.OwnerGroupShortname,
        entryShortname: null);

    public static List<string> Generate(User u) => Generate(
        spaceName: u.SpaceName,
        subpath: u.Subpath,
        resourceType: "user",
        isActive: u.IsActive,
        ownerShortname: u.OwnerShortname,
        ownerGroupShortname: u.OwnerGroupShortname,
        entryShortname: null);

    public static List<string> Generate(Group g) => Generate(
        spaceName: g.SpaceName,
        subpath: g.Subpath,
        resourceType: "group",
        isActive: g.IsActive,
        ownerShortname: g.OwnerShortname,
        ownerGroupShortname: g.OwnerGroupShortname,
        entryShortname: null);

    public static List<string> Generate(Role r) => Generate(
        spaceName: r.SpaceName,
        subpath: r.Subpath,
        resourceType: "role",
        isActive: r.IsActive,
        ownerShortname: r.OwnerShortname,
        ownerGroupShortname: r.OwnerGroupShortname,
        entryShortname: null);

    public static List<string> Generate(Permission p) => Generate(
        spaceName: p.SpaceName,
        subpath: p.Subpath,
        resourceType: "permission",
        isActive: p.IsActive,
        ownerShortname: p.OwnerShortname,
        ownerGroupShortname: p.OwnerGroupShortname,
        entryShortname: null);

    public static List<string> Generate(Space s) => Generate(
        spaceName: s.SpaceName,
        subpath: s.Subpath,
        resourceType: "space",
        isActive: s.IsActive,
        ownerShortname: s.OwnerShortname,
        ownerGroupShortname: s.OwnerGroupShortname,
        entryShortname: null);

    public static List<string> Generate(
        string spaceName,
        string subpath,
        string resourceType,
        bool isActive,
        string ownerShortname,
        string? ownerGroupShortname,
        string? entryShortname)
    {
        var parts = new List<string> { "/" };
        parts.AddRange(subpath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries));
        // For folders, also emit patterns that include the folder's own
        // shortname as a subpath segment — mirrors Python's
        // `if resource_type == folder and entry_shortname: subpath_parts.append(entry_shortname)`.
        if (entryShortname is not null) parts.Add(entryShortname);

        var isActiveLiteral = isActive ? "true" : "false";
        var policies = new List<string>();
        var fullSubpath = "";
        foreach (var part in parts)
        {
            fullSubpath += part;
            var stripped = fullSubpath.Trim('/');

            // Literal + owner.
            policies.Add($"{spaceName}:{stripped}:{resourceType}:{isActiveLiteral}:{ownerShortname}");
            // Owner-unscoped. Emitted unconditionally — it is the token an
            // "any owner" policy ("{key}:true:*") is rewritten to by
            // QueryPolicyExpansion, so a row missing it would be invisible to
            // that policy under the indexable filter. It used to be replaced by
            // the owner_group-scoped form below when the row had a group.
            //
            // MIGRATION IS MANDATORY, and the failure mode is access LOSS, not
            // degraded matching: "{key}:true:*" expands to exactly "{key}:true"
            // and "{key}:*" to "{key}:true"/"{key}:false". Neither ever matches
            // the owner- or group-scoped literal, so a non-owner caller whose
            // permission is wildcarded sees ZERO rows for any row written
            // before this change — it does not fall back to matching through
            // the owner-scoped literal.
            //
            // Repair every affected table with:
            //     dmart update_query_policies --all-tables
            // fix_query_policies is NOT sufficient: it heals only rows whose
            // array is empty, and these rows have a stale non-empty one.
            // Rows are unreadable to wildcarded policies until that finishes,
            // so on a large `entries` table run it in the same maintenance
            // window as the deploy rather than after it.
            policies.Add($"{spaceName}:{stripped}:{resourceType}:{isActiveLiteral}");
            // Owner-group-scoped, matched by a group member's own policy list.
            if (ownerGroupShortname is not null)
                policies.Add($"{spaceName}:{stripped}:{resourceType}:{isActiveLiteral}:{ownerGroupShortname}");

            // Global form — replace a middle segment with __all_subpaths__ so
            // permissions that match the subtree (without naming the specific
            // leaf) still grant. Only meaningful when the full_subpath has
            // more than one segment.
            var segs = fullSubpath.Split('/');
            if (segs.Length > 1)
            {
                var head = string.Join('/', segs.Take(1));
                var magicPath = $"{head}/{PermissionService.AllSubpathsMw}";
                if (segs.Length > 2)
                    magicPath += "/" + string.Join('/', segs.Skip(2));
                policies.Add($"{spaceName}:{magicPath.Trim('/')}:{resourceType}:{isActiveLiteral}");
            }

            fullSubpath = fullSubpath == "/" ? "" : fullSubpath + "/";
        }
        return policies;
    }
}
