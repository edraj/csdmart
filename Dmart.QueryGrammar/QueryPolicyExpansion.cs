namespace Dmart.QueryGrammar;

/// <summary>
/// Rewrites a caller's <c>query_policies</c> patterns into the exact policy
/// strings a row can carry, so the row test can be an indexable array-overlap
/// instead of a per-row LIKE.
/// </summary>
/// <remarks>
/// The read filter used to emit
/// <c>EXISTS (SELECT 1 FROM unnest(query_policies) qp WHERE qp LIKE $1)</c>.
/// unnest + LIKE is a per-row subplan, so <c>idx_entries_query_policies_gin</c>
/// could never be used and every authenticated list query full-scanned. On a
/// 2.1M-row table a selective policy set measured 1700 ms that way and 3.5 ms
/// as <c>query_policies &amp;&amp; ARRAY[...]</c>.
///
/// The rewrite is possible because '*' only ever reaches a policy string in two
/// shapes, both enumerable (see PermissionService/PermissionEngine
/// BuildUserQueryPolicies):
///
///   1. the resource-type segment, when a permission names no resource_types.
///      Expands over the closed <see cref="ResourceTypes"/> set.
///   2. a TRAILING segment: <c>{key}:true:*</c> ("is_active, any owner") or
///      <c>{key}:*</c> ("no conditions"). Rows carry an owner-agnostic literal
///      at every subpath level, so these map onto exact literals:
///        <c>{key}:true:*</c> → <c>{key}:true</c>
///        <c>{key}:*</c>      → <c>{key}:true</c>, <c>{key}:false</c>
///
/// Anything that does not fit those shapes is NOT guessed at — it is returned
/// in <see cref="Expansion.LikePatterns"/> and still matched with the old LIKE
/// test. Narrowing a policy silently would deny access that should be granted;
/// falling back only costs the scan we used to pay unconditionally.
///
/// Case 2 depends on the row always carrying the owner-unscoped literal.
/// Utils/QueryPolicies.Generate emits it, but rows written before that was
/// unconditional (it used to be replaced by an owner_group-scoped literal when
/// the row had an owner_group) need `dmart update_query_policies` to be
/// rewritten. Until they are, such rows fall back to matching through their
/// owner-scoped literal only.
/// </remarks>
public static class QueryPolicyExpansion
{
    /// <summary>
    /// Every resource_type wire value, i.e. the closed set a '*' in the
    /// resource-type segment stands for.
    /// </summary>
    /// <remarks>
    /// Mirrors Dmart.Models.Enums.ResourceType's EnumMember values. This
    /// project deliberately has no dependency on Dmart.Models (it is the shared
    /// SQL-text layer), so the list is duplicated here and pinned by
    /// QueryPolicyExpansionTests.ResourceTypes_Match_The_Enum — a new resource
    /// type that is not added here would be invisible to '*' policies.
    /// </remarks>
    public static readonly IReadOnlyList<string> ResourceTypes = new[]
    {
        "user", "group", "folder", "schema", "content", "log", "acl", "comment",
        "media", "data_asset", "locator", "relationship", "alteration", "history",
        "space", "permission", "role", "ticket", "json", "lock", "post",
        "reaction", "reply", "share", "plugin_wrapper", "notification",
        "csv", "jsonl", "sqlite", "parquet",
    };

    /// <summary>
    /// Split of a policy list into exactly-matchable tokens and the leftovers
    /// that still need LIKE.
    /// </summary>
    public sealed record Expansion(IReadOnlyList<string> ExactTokens, IReadOnlyList<string> LikePatterns);

    /// <summary>
    /// Ceiling on how many exact tokens one expansion may produce before the
    /// remaining policies are left to LIKE.
    /// </summary>
    /// <remarks>
    /// Expanding trades one LIKE pattern for N exact tokens, and N exact tokens
    /// are N bind parameters. That is a good trade while N is small — the
    /// overlap test is GIN-servable where the LIKE scan is not — and a bad one
    /// once the parameter list starts driving the plan time it was meant to
    /// save. A wildcard resource type alone enumerates all
    /// <see cref="ResourceTypes"/> (doubled for the four-segment shape, which
    /// spans both is_active values), so a single `space:subpath:*:*` is already
    /// 60 tokens; an actor inheriting one such policy per group multiplies that
    /// by their group count, and a user in 50 groups would emit 3000 bind
    /// parameters. Still under PostgreSQL's 65535 cap, but well past the point
    /// where the expansion pays for itself.
    ///
    /// Falling back is always SAFE, never merely cheaper: a policy left on the
    /// LIKE path matches exactly the rows it always did. The cap only decides
    /// which of two correct spellings each policy gets.
    /// </remarks>
    public const int MaxExactTokens = 256;

    /// <summary>
    /// Expands <paramref name="policies"/>. The union of the two returned sets
    /// matches exactly the rows the original LIKE patterns matched.
    /// </summary>
    public static Expansion Expand(IReadOnlyList<string>? policies)
    {
        var exact = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var like = new List<string>();
        if (policies is null) return new Expansion(exact, like);

        foreach (var policy in policies)
        {
            // Budget checked BEFORE the policy is expanded, and against its
            // whole token count, so a policy is never split across the two
            // forms — half a policy on each path would still be correct, but
            // the two paths would then disagree about which rows each covers,
            // which is not a property worth having to reason about. Counting
            // pre-dedup can retire the budget slightly early; that only ever
            // moves a policy to the (correct, slower) LIKE path.
            if (exact.Count < MaxExactTokens
                && TryExpand(policy, out var tokens)
                && exact.Count + tokens.Count <= MaxExactTokens)
            {
                foreach (var t in tokens)
                    if (seen.Add(t)) exact.Add(t);
            }
            else
            {
                like.Add(policy);
            }
        }

        return new Expansion(exact, like);
    }

    // A policy is space:subpath:resource_type:is_active[:owner]. Only the
    // resource-type segment and the trailing segment may hold a '*'; a '*'
    // anywhere else (or a shape with a different segment count) is left to LIKE.
    private static bool TryExpand(string policy, out List<string> tokens)
    {
        tokens = new List<string>();
        var segs = policy.Split(':');

        // 4 segments = "{space}:{subpath}:{rt}:{tail}", 5 adds the owner.
        if (segs.Length is not (4 or 5)) return false;

        var space = segs[0];
        var subpath = segs[1];
        var rtSegment = segs[2];
        if (space.Contains('*', StringComparison.Ordinal)) return false;
        if (subpath.Contains('*', StringComparison.Ordinal)) return false;

        // Resource type: exactly "*" enumerates; a partial wildcard does not.
        List<string> resourceTypes;
        if (rtSegment == "*") resourceTypes = new List<string>(ResourceTypes);
        else if (rtSegment.Contains('*', StringComparison.Ordinal)) return false;
        else resourceTypes = new List<string> { rtSegment };

        // is_active + owner.
        List<string> isActiveValues;
        string? owner;
        if (segs.Length == 4)
        {
            // "{key}:*" — no conditions: any is_active, any owner. The
            // owner-agnostic literal exists for both is_active values.
            if (segs[3] != "*") return false;
            isActiveValues = new List<string> { "true", "false" };
            owner = null;
        }
        else
        {
            var isActive = segs[3];
            if (isActive is not ("true" or "false")) return false;
            isActiveValues = new List<string> { isActive };

            var ownerSegment = segs[4];
            // "*" means any owner → match the row's owner-unscoped literal.
            if (ownerSegment == "*") owner = null;
            else if (ownerSegment.Contains('*', StringComparison.Ordinal)) return false;
            else owner = ownerSegment;
        }

        foreach (var rt in resourceTypes)
            foreach (var isActive in isActiveValues)
                tokens.Add(owner is null
                    ? $"{space}:{subpath}:{rt}:{isActive}"
                    : $"{space}:{subpath}:{rt}:{isActive}:{owner}");

        return true;
    }

    /// <summary>
    /// True when the caller's policies make the visibility predicate a
    /// tautology for every row the query can reach, so the filter can be
    /// dropped instead of evaluated per row.
    /// </summary>
    /// <remarks>
    /// A policy with no conditions — the 4-segment "{space}:{subpath}:{rt}:*"
    /// shape — grants query access to that space/subpath subtree for that
    /// resource type regardless of is_active or owner. When one of those
    /// covers the queried scope, every row the WHERE clause can return would
    /// pass the ACL predicate, so emitting it only costs a scan: on a 2M-row
    /// space the predicate turns an 83 ms index-only count into a 350 ms
    /// parallel seq scan, and parallel scans collapse under concurrency.
    ///
    /// Dropping the predicate is not a widening. query_policies is a
    /// materialization of the same role→permission decision CanAsync makes;
    /// when the actor holds an unconditioned permission over the subtree, that
    /// decision is "yes" for every row in it. A row whose stored array drifted
    /// (a direct-SQL edit, a move that skipped the write path) is exactly the
    /// row the materialization gets wrong, and answering from the permission
    /// rather than from the stale array is the more correct of the two.
    ///
    /// Deliberately conservative in three ways, because a false positive here
    /// grants access:
    ///   * only "entries" callers use it (the caller passes the scope) — other
    ///     tables lack the non-empty query_policies CHECK entries has;
    ///   * only exact space matches and genuine ancestor subpaths count;
    ///   * a query with no resource-type filter needs either a '*' policy or
    ///     one policy per resource type; a filtered one needs every requested
    ///     type covered.
    /// Anything unrecognised returns false and keeps the filter.
    /// </remarks>
    /// <param name="policies">The actor's resolved policy list.</param>
    /// <param name="spaceName">Space the query is restricted to.</param>
    /// <param name="subpath">Subpath the query is rooted at (subtree included).</param>
    /// <param name="filterResourceTypes">
    /// Resource-type wire values the query restricts to, or null/empty for "any".
    /// </param>
    public static bool CoversScope(
        IReadOnlyList<string>? policies,
        string? spaceName,
        string? subpath,
        IReadOnlyList<string>? filterResourceTypes)
    {
        if (policies is null || policies.Count == 0) return false;
        if (string.IsNullOrEmpty(spaceName)) return false;

        var scopeSubpath = (subpath ?? string.Empty).Trim('/');

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in policies)
        {
            var segs = policy.Split(':');
            // Only the unconditioned shape is total: 5-segment policies carry
            // an is_active and/or owner condition, so they are not.
            if (segs.Length != 4 || segs[3] != "*") continue;
            if (!string.Equals(segs[0], spaceName, StringComparison.Ordinal)) continue;

            var policySubpath = segs[1].Trim('/');
            var isAncestor =
                policySubpath.Length == 0
                || string.Equals(policySubpath, scopeSubpath, StringComparison.Ordinal)
                || scopeSubpath.StartsWith(policySubpath + "/", StringComparison.Ordinal);
            if (!isAncestor) continue;

            covered.Add(segs[2]);
        }

        if (covered.Count == 0) return false;
        if (covered.Contains("*")) return true;

        // A query that names its resource types is covered when every named
        // type is.
        if (filterResourceTypes is { Count: > 0 })
            return filterResourceTypes.All(rt => covered.Contains(rt));

        // Unfiltered query: a row can be of any type, so every type must be
        // covered. Enumerating them is not a formality — the stock
        // super_manager permission lists all of them explicitly instead of
        // leaving resource_types empty, so requiring a literal '*' policy here
        // would miss the single most common total-access role.
        return ResourceTypes.All(rt => covered.Contains(rt));
    }

    /// <summary>
    /// Escapes a policy string for use as a LIKE pattern: backslash first, then
    /// the LIKE metacharacters, then dmart's '*' becomes '%'.
    /// </summary>
    /// <remarks>
    /// Order matters — escaping '\' after the others would double-escape the
    /// backslashes they introduce. Both dialects consume the result with
    /// ESCAPE '\' and match case-sensitively.
    /// </remarks>
    public static string ToLikePattern(string policy)
        => policy
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("*", "%", StringComparison.Ordinal);

    /// <summary>
    /// Escapes an already-expanded token so a LIKE test matches it literally.
    /// </summary>
    /// <remarks>
    /// The sibling of <see cref="ToLikePattern"/>, and deliberately missing its
    /// final line: '*' stays '*'. These strings are the exact policies a row can
    /// carry, produced by <see cref="Expand"/>, and are consumed by
    /// ISqlDialect.ArrayOverlapAny, whose contract is equality. Mapping '*' to
    /// '%' here would let a token widen back into a wildcard and grant access
    /// the overriding dialects' equality test refuses.
    /// </remarks>
    public static string ToLiteralLikePattern(string token)
        => token
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
