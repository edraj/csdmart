using System.Text;
using Npgsql;

namespace Dmart.SqlAdapter.Permissions;

// Port of Dmart's DataAdapters/Sql/QueryHelper.AppendAclFilter. Wraps a paged
// query's WHERE clause with the actor-aware visibility check:
//
//   AND ( owner_shortname = $actor
//         OR EXISTS (... acl_array ... allowed_actions ? 'query')
//         OR EXISTS (... unnest(query_policies) LIKE $pat1 OR LIKE $pat2 ...)
//   )
//
// query_policies on each row is a TEXT[] of pre-computed
// "<space>:<subpath>:<rt>:<is_active>:<owner>" patterns produced when the row
// was written. The actor's BuildUserQueryPoliciesAsync output is matched against
// those — '*' in the actor's pattern becomes '%' in LIKE, with '\' as the
// LIKE escape for the other metacharacters.

public static class PermissionFilter
{
    // Appends the ACL clause to an existing WHERE-builder. New parameters are
    // appended to `parameters` using named placeholders (@perm_actor, @perm_qp0…)
    // so the fragment can coexist with QueryAsync's named params. Mixing named
    // and positional in the same NpgsqlCommand is forbidden by Npgsql — and
    // QueryAsync already uses @space / @subpath / @search… everywhere else.
    //
    // `tableName` matches the Python skip list — attachments and histories
    // bypass the filter entirely, mirroring the server.
    public static void Append(
        StringBuilder sql,
        List<NpgsqlParameter> parameters,
        string? actor,
        string tableName,
        List<string>? queryPolicies)
    {
        if (tableName is "attachments" or "histories") return;
        if (string.IsNullOrEmpty(actor)) return;

        const string ActorParam = "@perm_actor";
        parameters.Add(new NpgsqlParameter(ActorParam, actor));

        var conditions = new List<string>
        {
            $"owner_shortname = {ActorParam}",
            $"EXISTS (SELECT 1 FROM jsonb_array_elements(" +
            $"CASE WHEN jsonb_typeof(acl::jsonb) = 'array' THEN acl::jsonb ELSE '[]'::jsonb END" +
            $") AS elem WHERE elem->>'user_shortname' = {ActorParam} " +
            $"AND (elem->'allowed_actions') ? 'query')",
        };

        if (queryPolicies is { Count: > 0 })
        {
            var likeConditions = new List<string>();
            for (var i = 0; i < queryPolicies.Count; i++)
            {
                // Order: escape backslash first, then LIKE metacharacters %
                // and _, finally expand dmart's '*' wildcard to LIKE's '%'.
                var pattern = queryPolicies[i]
                    .Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_")
                    .Replace("*", "%");
                var paramName = $"@perm_qp{i}";
                parameters.Add(new NpgsqlParameter(paramName, pattern));
                likeConditions.Add($"qp LIKE {paramName} ESCAPE '\\'");
            }
            if (likeConditions.Count > 0)
            {
                conditions.Insert(1,
                    $"EXISTS (SELECT 1 FROM unnest(query_policies) AS qp WHERE {string.Join(" OR ", likeConditions)})");
            }
        }

        sql.Append(" AND (").Append(string.Join(" OR ", conditions)).Append(") ");
    }
}
