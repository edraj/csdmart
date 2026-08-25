using System.Text;
using Dmart.QueryGrammar;
using Npgsql;

namespace Dmart.SqlAdapter.Permissions;

// Port of Dmart's DataAdapters/Sql/QueryHelper.AppendAclFilter. Wraps a paged
// query's WHERE clause with the actor-aware visibility check:
//
//   AND ( owner_shortname = $actor
//         OR acl @> [{"user_shortname": $actor, "allowed_actions": ["query"]}]
//         OR query_policies && ARRAY[$tok1, $tok2, ...]
//   )
//
// query_policies on each row is a TEXT[] of pre-computed
// "<space>:<subpath>:<rt>:<is_active>:<owner>" strings produced when the row was
// written. The actor's BuildUserQueryPoliciesAsync output is matched against
// those. Both row tests are containment/overlap rather than per-row subplans so
// the GIN indexes on acl and query_policies can serve them; policy patterns
// whose wildcards QueryPolicyExpansion cannot enumerate fall back to the
// original unnest+LIKE, with '\' as the LIKE escape.

public static class PermissionFilter
{
    /// <summary>
    /// Appends the ACL visibility clause for a paged QUERY (list) operation.
    /// The emitted EXISTS-over-acl probes the ACL for <c>'query'</c>
    /// specifically, so this filter is ONLY valid for query/list code paths
    /// (<c>QueryAsync</c>, <c>GetChildrenAsync</c>). View / create / update /
    /// delete MUST go through <see cref="PermissionEngine.CanAsync"/> /
    /// <see cref="PermissionEngine.RequireAsync"/> — calling Append from those
    /// paths would silently use the wrong action.
    /// </summary>
    /// <remarks>
    /// New parameters are appended to <paramref name="parameters"/> using
    /// named placeholders (<c>@perm_actor</c>, <c>@perm_qp0…</c>) so the
    /// fragment can coexist with QueryAsync's named params. Mixing named and
    /// positional in the same NpgsqlCommand is forbidden by Npgsql.
    /// <paramref name="tableName"/> matches the Python skip list —
    /// <c>attachments</c> and <c>histories</c> bypass the filter entirely,
    /// mirroring the server.
    /// </remarks>
    /// <summary>
    /// Binary-compatible overload carrying the pre-scope-argument signature.
    /// </summary>
    /// <remarks>
    /// Dmart.SqlAdapter is a published package, and C# bakes optional-argument
    /// values into the *caller's* IL. An assembly compiled against the old
    /// five-parameter Append therefore emits a call to a five-parameter method
    /// and throws MissingMethodException against the widened one, even though
    /// the source still compiles. This overload keeps that call site resolving.
    /// It forwards with no scope, which disables only the tautology skip — an
    /// optimisation — so the emitted predicate is unchanged.
    /// </remarks>
    public static void Append(
        StringBuilder sql,
        List<NpgsqlParameter> parameters,
        string? actor,
        string tableName,
        List<string>? queryPolicies)
        => Append(sql, parameters, actor, tableName, queryPolicies, null, null, null);

    public static void Append(
        StringBuilder sql,
        List<NpgsqlParameter> parameters,
        string? actor,
        string tableName,
        List<string>? queryPolicies,
        string? scopeSpace = null,
        string? scopeSubpath = null,
        IReadOnlyList<string>? scopeResourceTypes = null)
    {
        if (tableName is "attachments" or "histories") return;
        if (string.IsNullOrEmpty(actor)) return;

        // Tautology skip: when the actor's policies already cover every row in
        // the queried scope the predicate can only cost a scan. Entries only —
        // see QueryPolicyExpansion.CoversScope.
        if (tableName == "entries" && scopeSpace is not null
            && QueryPolicyExpansion.CoversScope(
                queryPolicies, scopeSpace, scopeSubpath, scopeResourceTypes))
            return;

        const string ActorParam = "@perm_actor";
        parameters.Add(new NpgsqlParameter(ActorParam, actor));

        var conditions = new List<string>
        {
            $"owner_shortname = {ActorParam}",
            // jsonb containment so idx_entries_acl_gin can serve it. Same
            // rows as the old jsonb_array_elements probe: an ACL element with
            // this user_shortname whose allowed_actions include 'query'.
            $"acl @> jsonb_build_array(jsonb_build_object(" +
            $"'user_shortname', {ActorParam}, 'allowed_actions', jsonb_build_array('query')))",
        };

        if (queryPolicies is { Count: > 0 })
        {
            // Wildcards that can be enumerated become an exact-set overlap
            // (`&&`), which idx_entries_query_policies_gin can serve; anything
            // QueryPolicyExpansion won't expand keeps the old per-row LIKE.
            var expansion = QueryPolicyExpansion.Expand(queryPolicies);
            var tests = new List<string>();

            if (expansion.ExactTokens.Count > 0)
            {
                var placeholders = new List<string>(expansion.ExactTokens.Count);
                for (var i = 0; i < expansion.ExactTokens.Count; i++)
                {
                    var paramName = $"@perm_qp{i}";
                    parameters.Add(new NpgsqlParameter(paramName, expansion.ExactTokens[i]));
                    placeholders.Add(paramName);
                }
                tests.Add($"query_policies && ARRAY[{string.Join(", ", placeholders)}]::text[]");
            }

            if (expansion.LikePatterns.Count > 0)
            {
                var likeConditions = new List<string>(expansion.LikePatterns.Count);
                for (var i = 0; i < expansion.LikePatterns.Count; i++)
                {
                    var paramName = $"@perm_qplike{i}";
                    parameters.Add(new NpgsqlParameter(
                        paramName, QueryPolicyExpansion.ToLikePattern(expansion.LikePatterns[i])));
                    likeConditions.Add($"qp LIKE {paramName} ESCAPE '\\'");
                }
                tests.Add(
                    $"EXISTS (SELECT 1 FROM unnest(query_policies) AS qp WHERE {string.Join(" OR ", likeConditions)})");
            }

            if (tests.Count > 0)
                conditions.Insert(1, tests.Count == 1 ? tests[0] : $"({string.Join(" OR ", tests)})");
        }

        sql.Append(" AND (").Append(string.Join(" OR ", conditions)).Append(") ");
    }
}
