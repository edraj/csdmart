using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.SqlAdapter.Permissions;

namespace Dmart.SqlAdapter.Helpers;

// Row-side query_policies generation — the PRODUCER half of the pattern
// matching that PermissionFilter/PermissionEngine consume. Port of the
// server's Utils/QueryPolicies.cs (itself a port of Python's
// query_policies_helper.py::generate_query_policies); keep the pattern
// grammar in sync with both.
//
// Every entry write MUST populate entries.query_policies: the column carries
// a non-empty CHECK constraint (entries_query_policies_nonempty, see the
// server's SqlSchema.cs), and a row without patterns is invisible to every
// ACL-filtered query except via ownership/explicit ACL. Regenerated
// deterministically on each upsert — caller-supplied values are ignored,
// matching the server-side repositories.
internal static class QueryPoliciesHelper
{
    public static List<string> Generate(Entry e) => Generate(
        spaceName: e.SpaceName,
        subpath: Locator.NormalizeSubpath(e.Subpath),
        resourceType: JsonbHelpers.EnumMember(e.ResourceType),
        isActive: e.IsActive,
        ownerShortname: e.OwnerShortname,
        ownerGroupShortname: e.OwnerGroupShortname,
        // Folders also emit patterns that include the folder's own shortname
        // as a subpath segment, mirroring Python.
        entryShortname: e.ResourceType == ResourceType.Folder ? e.Shortname : null);

    private static List<string> Generate(
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
            // Owner-unscoped (or owner_group-scoped when a group is set).
            policies.Add(ownerGroupShortname is null
                ? $"{spaceName}:{stripped}:{resourceType}:{isActiveLiteral}"
                : $"{spaceName}:{stripped}:{resourceType}:{isActiveLiteral}:{ownerGroupShortname}");

            // Global form — replace a middle segment with __all_subpaths__ so
            // permissions matching the subtree (without naming the specific
            // leaf) still grant. Only meaningful past the root segment.
            var segs = fullSubpath.Split('/');
            if (segs.Length > 1)
            {
                var head = string.Join('/', segs.Take(1));
                var magicPath = $"{head}/{PermissionEngine.AllSubpathsMw}";
                if (segs.Length > 2)
                    magicPath += "/" + string.Join('/', segs.Skip(2));
                policies.Add($"{spaceName}:{magicPath.Trim('/')}:{resourceType}:{isActiveLiteral}");
            }

            fullSubpath = fullSubpath == "/" ? "" : fullSubpath + "/";
        }
        return policies;
    }
}
