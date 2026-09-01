using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.QueryGrammar;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// One pushed INNER join, prebuilt by QueryService and consumed by
// AppendInnerSemiJoins. RightQuery carries the right side's space/subpath/
// filters/search (already merged with the actor's filter_fields_values);
// limit/offset/sort are ignored (existence only). Correlations are SQL
// expressions: LeftExpr references the OUTER row (entries.*), RightExpr the
// inner alias (r.*). Both sides are cast to text.
public sealed record InnerSemiJoinSpec
{
    public required Query RightQuery { get; init; }
    public required string Actor { get; init; }
    public required List<string>? RightQueryPolicies { get; init; }
    public required List<(string LeftExpr, string RightExpr)> Correlations { get; init; }
}

// Shared query-building logic used by every repository's QueryAsync/CountQueryAsync.
// Mirrors Python's set_sql_statement_from_query + apply_acl_and_query_policies +
// query_aggregation. Every table in dmart shares the same Metas-base columns, so
// the filter logic is identical; only the FROM clause differs.
public static class QueryHelper
{
    private static ILogger _log = NullLogger.Instance;

    // Called once at startup from Program.cs to wire structured logging.
    public static void SetLogger(ILoggerFactory factory) =>
        _log = factory.CreateLogger("Dmart.QueryHelper");

    // ====================================================================
    // WHERE CLAUSE BUILDER
    // ====================================================================

    // Binds a value into the positional arg list and returns its $N placeholder.
    // Handed to the dialect so it can bind its own parameters — necessary
    // because some constructs differ in shape, not just spelling: PostgreSQL
    // matches a list with one array parameter, SQLite with one parameter per
    // element.
    private static SqlBinder Binder(List<NpgsqlParameter> args)
        => (value, kind) =>
        {
            args.Add(PostgresDialect.CreateParameter(new SqlParam(null, value, kind)));
            return "$" + args.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        };

    public static string BuildWhereClause(Query q, List<NpgsqlParameter> args, string? tableName = null)
        => BuildWhereClause(q, args, PostgresSqlDialect.Instance, tableName);

    public static string BuildWhereClause(
        Query q, List<NpgsqlParameter> args, ISqlDialect dialect, string? tableName = null)
    {
        var bind = Binder(args);
        // Add the param FIRST, then reference its 1-based index. For a base
        // query (empty args) this is $1 — byte-identical to before. For a nested
        // reuse (EXISTS semi-join, args already populated) it continues the
        // positional sequence instead of colliding on $1.
        args.Add(new() { Value = q.SpaceName });
        var sql = new System.Text.StringBuilder($"space_name = ${args.Count} ");

        // exact_subpath=true: only entries at this exact subpath (including "/").
        // exact_subpath=false + subpath="/": no filter (return all subpaths).
        // exact_subpath=false + subpath!="/": hierarchical match (subpath + children).
        if (q.ExactSubpath)
        {
            args.Add(new() { Value = q.Subpath ?? "/" });
            sql.Append($"AND subpath = ${args.Count} ");
        }
        else if (!string.IsNullOrEmpty(q.Subpath) && q.Subpath != "/")
        {
            args.Add(new() { Value = q.Subpath });
            // SubpathScope, not a bare `LIKE $n || '/%'`: an unescaped prefix
            // reads `_` as a wildcard and pulls in one-character siblings. See
            // SubpathScope for why the ACL predicate used to hide that and no
            // longer does.
            sql.Append($"AND (subpath = ${args.Count} OR "
                     + $"{SubpathScope.DescendantLike("subpath", $"${args.Count}")}) ");
        }

        if (q.FilterTypes is { Count: > 0 })
        {
            var types = q.FilterTypes.Select(JsonbHelpers.EnumMember).ToList();
            sql.Append($"AND {dialect.AnyOf("resource_type", types, bind)} ");
        }

        if (q.FilterShortnames is { Count: > 0 })
        {
            sql.Append($"AND {dialect.AnyOf("shortname", q.FilterShortnames, bind)} ");
        }

        if (q.FilterSchemaNames is { Count: > 0 })
        {
            var effective = q.FilterSchemaNames.Where(n => n != "meta").ToList();
            if (effective.Count > 0)
            {
                sql.Append($"AND {dialect.AnyOf(dialect.SchemaShortnameExpr, effective, bind)} ");
            }
        }

        if (q.FilterTags is { Count: > 0 })
        {
            sql.Append($"AND {dialect.JsonArrayContainsAny("tags", q.FilterTags, bind)} ");
        }

        // RediSearch-style search: @field:value syntax → SQL WHERE clauses.
        if (!string.IsNullOrEmpty(q.Search))
            AppendSearchClauses(sql, q.Search, args, tableName, dialect);

        if (q.FromDate is not null)
        {
            args.Add(new() { Value = q.FromDate.Value });
            sql.Append($"AND created_at >= ${args.Count} ");
        }
        if (q.ToDate is not null)
        {
            args.Add(new() { Value = q.ToDate.Value });
            sql.Append($"AND created_at <= ${args.Count} ");
        }

        return sql.ToString();
    }

    // ====================================================================
    // SEARCH-CLAUSE DELEGATION
    // ====================================================================
    // The full RediSearch-flavoured grammar used to live inline here; it now
    // lives in Dmart.QueryGrammar.SearchExpressionParser, shared with the
    // SDK so the two cannot drift. The server uses positional $N parameters
    // so we ask the parser for that style.

    // Which dialect a connection factory will produce. The generic runners open
    // their own connection, so they resolve it here rather than taking it as a
    // parameter every repository would have to thread through.
    internal static ISqlDialect DialectFor(IDbConnectionFactory db)
        => db is SqliteConnectionFactory
            ? SqliteSqlDialect.Instance
            : PostgresSqlDialect.Instance;

    private static void AppendSearchClauses(
        System.Text.StringBuilder sql, string search, List<NpgsqlParameter> args,
        string? tableName = null, ISqlDialect? dialect = null)
    {
        var parsed = SearchExpressionParser.Parse(
            search, args.Count, PlaceholderStyle.Positional, tableName, dialect);

        // Always append params (parser may bind even when clauses end up empty —
        // e.g. an all-negative group; the SDK side does the same).
        foreach (var p in parsed.Parameters) args.Add(PostgresDialect.CreateParameter(p));

        if (parsed.Clauses.Count == 0) return;

        // The parser returns either a single AND-joined group or a
        // ("groupA" OR "groupB" ...) compound. Either way we just prefix AND
        // and join with spaces — matches the previous inline behaviour
        // verbatim. Parens were already added by the parser for the OR case.
        sql.Append("AND ");
        sql.Append(string.Join(' ', parsed.Clauses));
        sql.Append(' ');
    }

    // ====================================================================
    // SHARED SQL UTILITIES
    // ====================================================================
    // Used by the aggregation builder below (and historically by the inline
    // search parser, now extracted). Kept here because aggregation field
    // resolution still needs them.

    // Strict SQL-identifier validator. Any `field` interpolated into the SQL
    // (as a column name or cast target) MUST match — without this gate a
    // crafted token could inject arbitrary SQL. Pattern matches a valid
    // lowercase Postgres column identifier up to NAMEDATALEN.
    private static readonly Regex SafeColumnIdent = new(
        @"^[a-z][a-z0-9_]{0,63}$", RegexOptions.Compiled);

    // Escape a string for use inside a single-quoted SQL literal. Doubles any
    // apostrophes per PostgreSQL's standard escape rule. Used for JSONB-path
    // segments that can't be parameterised (they're part of the operator
    // expression, not data).
    private static string EscapeSqlLiteral(string s) => s.Replace("'", "''");

    // Converts a dotted JSONB path like "body.user.email" into
    // payload::jsonb->'body'->'user'->>'email' (last segment uses ->>).
    private static string BuildJsonbPath(string column, string dotPath, ISqlDialect dialect)
    {
        var segments = dotPath.Split('.');
        return segments.Length == 0
            ? dialect.AsText(column)
            : dialect.JsonText(column, segments);
    }

    // ====================================================================
    // SQL ACL FILTERING
    // ====================================================================
    // Mirrors Python's apply_acl_and_query_policies. Adds a WHERE clause
    // that restricts rows to those the user owns OR has ACL access to OR
    // matches a query_policy pattern. Skipped for attachments and histories
    // (matching Python) and for the spaces query type.

    public static void AppendAclFilter(
        System.Text.StringBuilder sql, List<NpgsqlParameter> args,
        string? userShortname, string tableName, List<string>? queryPolicies,
        Query? scope = null)
        => AppendAclFilter(sql, args, userShortname, tableName, queryPolicies,
            PostgresSqlDialect.Instance, scope);

    public static void AppendAclFilter(
        System.Text.StringBuilder sql, List<NpgsqlParameter> args,
        string? userShortname, string tableName, List<string>? queryPolicies,
        ISqlDialect dialect, Query? scope = null)
    {
        var bind = Binder(args);
        // Python skips ACL for attachments, histories, and spaces.
        if (tableName is "attachments" or "histories") return;

        if (string.IsNullOrEmpty(userShortname)) return;

        // Skip the predicate entirely when the actor's policies already cover
        // every row the query can reach — it would be a tautology, and on a
        // large space an expensive one. Only offered for `entries`, whose
        // non-empty query_policies CHECK constraint the other tables lack.
        if (tableName == "entries" && scope is not null
            && QueryPolicyExpansion.CoversScope(
                queryPolicies, scope.SpaceName, scope.Subpath,
                scope.FilterTypes?.Select(JsonbHelpers.EnumMember).ToList()))
            return;

        args.Add(new() { Value = userShortname });
        var userParam = args.Count;

        // Base conditions: user owns the row OR is in the ACL with 'query' action.
        var conditions = new List<string>
        {
            $"owner_shortname = ${userParam}",
            dialect.AclGrants("acl", $"${userParam}", "query"),
        };

        // Add the query_policies row test if the user has any policies.
        if (queryPolicies is { Count: > 0 })
        {
            // Policies whose wildcards can be enumerated become an exact-set
            // overlap, which GIN can serve; the rest keep the old LIKE test.
            // See QueryPolicyExpansion for why that is loss-free.
            //
            // Both branches match case-SENSITIVELY — PostgreSQL because that is
            // LIKE's (and `=`'s) default, SQLite because SqliteConnectionFactory
            // sets PRAGMA case_sensitive_like=ON. That equivalence is what keeps
            // the two backends from granting different access for the same policy.
            var expansion = QueryPolicyExpansion.Expand(queryPolicies);
            var tests = new List<string>();
            if (expansion.ExactTokens.Count > 0)
                tests.Add(dialect.ArrayOverlapAny("query_policies", expansion.ExactTokens, bind));
            if (expansion.LikePatterns.Count > 0)
            {
                var patterns = expansion.LikePatterns
                    .Select(QueryPolicyExpansion.ToLikePattern)
                    .ToList();
                tests.Add(dialect.ArrayAnyLike("query_policies", patterns, bind));
            }
            if (tests.Count > 0)
                conditions.Insert(1, tests.Count == 1 ? tests[0] : $"({string.Join(" OR ", tests)})");
        }

        sql.Append($"AND ({string.Join(" OR ", conditions)}) ");
    }

    // ====================================================================
    // INNER-JOIN SEMI-JOIN PUSHDOWN
    // ====================================================================

    // Join keys we can express in SQL AND that QueryService's in-memory
    // GetValuesFromRecord reads from strongly-typed Record fields, guaranteeing
    // the SQL text comparison matches the in-memory FormatValue comparison.
    // Deliberately conservative: owner_shortname/slug/space_name are read from
    // record.attributes in-memory (population not guaranteed) so they stay on
    // the fallback path.
    private static readonly HashSet<string> JoinMetaColumns = new(StringComparer.Ordinal)
    {
        "shortname", "subpath", "uuid", "resource_type",
    };

    // Map a scalar join key path to a SQL expression under `qualifier`
    // ("entries." for the outer row, "r." for the semi-join inner row).
    // Returns false for any path we can't safely express (→ caller falls back).
    public static bool TryJoinKeyToSql(string path, string qualifier, out string expr)
        => TryJoinKeyToSql(path, qualifier, PostgresSqlDialect.Instance, out expr);

    public static bool TryJoinKeyToSql(
        string path, string qualifier, ISqlDialect dialect, out string expr)
    {
        expr = "";
        if (string.IsNullOrEmpty(path)) return false;

        if (JoinMetaColumns.Contains(path))
        {
            expr = dialect.AsText($"({qualifier}{path})");
            return true;
        }

        // Only payload paths beyond meta columns. Mirrors in-memory
        // GetNestedFromAttributes walking attributes["payload"].body...
        if (!path.StartsWith("payload.", StringComparison.Ordinal)) return false;
        var dot = path["payload.".Length..];
        if (dot.Length == 0) return false;
        foreach (var seg in dot.Split('.'))
            if (!SafeSortSegmentRegex.IsMatch(seg)) return false;

        // qualifier+"payload" → entries.payload / r.payload; BuildJsonbPath adds ::jsonb->...->>'last'.
        expr = BuildJsonbPath($"{qualifier}payload", dot, dialect);
        return true;
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: correlation exprs are built only from TryJoinKeyToSql (whitelisted meta columns + segment-validated JSONB paths); all data values flow through $N params via BuildWhereClause/AppendAclFilter.")]
    public static void AppendInnerSemiJoins(
        System.Text.StringBuilder sql, List<NpgsqlParameter> args,
        IReadOnlyList<InnerSemiJoinSpec> specs)
        => AppendInnerSemiJoins(sql, args, specs, PostgresSqlDialect.Instance);

    public static void AppendInnerSemiJoins(
        System.Text.StringBuilder sql, List<NpgsqlParameter> args,
        IReadOnlyList<InnerSemiJoinSpec> specs, ISqlDialect dialect)
    {
        foreach (var spec in specs)
        {
            sql.Append("AND EXISTS (SELECT 1 FROM entries r WHERE ");
            // Right-side filters. Bare columns bind to the inner `r` (it shadows
            // the outer `entries`). BuildWhereClause is positional-param safe,
            // so its $N continue the shared sequence.
            sql.Append(BuildWhereClause(spec.RightQuery, args, dialect, "entries"));
            // Right-side ACL — MANDATORY. Bare owner_shortname/acl/query_policies
            // bind to `r`. Without this a base row could survive on a right row
            // the caller can't query.
            AppendAclFilter(sql, args, spec.Actor, "entries", spec.RightQueryPolicies, dialect,
                spec.RightQuery);
            foreach (var (leftExpr, rightExpr) in spec.Correlations)
                sql.Append($"AND {rightExpr} = {leftExpr} ");
            sql.Append(") ");
        }
    }

    // ====================================================================
    // ORDER + PAGING
    // ====================================================================

    // Per-table whitelists of column names accepted for bare-column sort_by.
    // JSON-path tokens (anything containing a dot) are NOT gated by this list —
    // they're handled by BuildJsonPathSortExpression, which sanitizes each path
    // segment via SafeSortSegmentRegex so a hostile wire value can't smuggle
    // arbitrary SQL. When a comma-separated sort_by lists an unknown bare
    // column AND no JSON-path token resolves, we fall back to `updated_at`.
    private static readonly HashSet<string> SharedSortColumns = new(StringComparer.Ordinal)
    {
        "shortname", "created_at", "updated_at", "displayname", "description",
        "is_active", "resource_type", "owner_shortname", "owner_group_shortname",
        "uuid", "slug", "payload_content_type"
    };
    private static readonly Dictionary<string, HashSet<string>> TableSortColumns = new(StringComparer.Ordinal)
    {
        ["entries"] = new(SharedSortColumns, StringComparer.Ordinal) { "schema_shortname", "state", "payload" },
        ["attachments"] = new(SharedSortColumns, StringComparer.Ordinal) { "schema_shortname", "payload" },
        ["users"] = new(SharedSortColumns, StringComparer.Ordinal) { "email", "msisdn", "type", "language", "payload" },
        ["spaces"] = new(SharedSortColumns, StringComparer.Ordinal) { "space_name", "subpath", "payload" },
        ["roles"] = new(SharedSortColumns, StringComparer.Ordinal),
        ["permissions"] = new(SharedSortColumns, StringComparer.Ordinal),
        ["histories"] = new(SharedSortColumns, StringComparer.Ordinal),
    };

    // Only alphanumerics and underscore allowed per path segment — matches
    // Python's adapter_helpers._sanitize_sql_part. Keeps the segment safe for
    // inlining as a JSONB key literal inside the emitted SQL.
    private static readonly Regex SafeSortSegmentRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    // Build the SQL expression for a JSON-path sort token like "payload.body.rank".
    // Mirrors Python's transform_keys_to_sql + sort CASE wrap:
    //   payload -> 'body' ->> 'rank'
    //   CASE WHEN (<expr>) ~ '^-?[0-9]+(\.[0-9]+)?$' THEN (<expr>)::float END <dir>, (<expr>) <dir>
    // The CASE makes numeric values sort numerically (1,2,10) while non-numeric
    // values still sort lexically as a tiebreaker. Returns null when any segment
    // fails validation (sanitizer rejects the whole token, we keep other tokens).
    private static string? BuildJsonPathSortExpression(string token, string direction, ISqlDialect dialect)
    {
        var parts = token.Split('.');
        foreach (var p in parts)
            if (!SafeSortSegmentRegex.IsMatch(p)) return null;

        return dialect.JsonSortKeys(parts[0], parts[1..], direction);
    }

    // Resolve a single sort token into either a bare whitelisted column or a
    // JSON-path expression. Returns null to mean "skip this token".
    private static string? ResolveSortToken(
        string rawToken, string direction, string? tableName, ISqlDialect dialect)
    {
        var token = rawToken.Trim();
        if (token.Length == 0) return null;

        // Python: sort_by.replace("attributes.", "") — strip anywhere (the dmart
        // convention is to mirror the wire envelope's attributes.* into the
        // storage columns/payload), then drop a leading '@' for consistency
        // with the search-token syntax.
        token = token.Replace("attributes.", "");
        if (token.StartsWith('@')) token = token[1..];

        // Python shortcut: "body.xxx" is sugar for "payload.body.xxx".
        if (token.StartsWith("body.", StringComparison.Ordinal)) token = "payload." + token;

        if (token.Contains('.'))
            return BuildJsonPathSortExpression(token, direction, dialect);

        var allowed = tableName is not null && TableSortColumns.TryGetValue(tableName, out var set)
            ? set
            : SharedSortColumns;
        return allowed.Contains(token) ? $"{token} {direction}" : null;
    }

    // Parse comma-separated sort_by into one ORDER BY clause body (without the
    // leading "ORDER BY "). Returns null when nothing resolves → caller falls
    // back to `updated_at DESC`.
    private static string? BuildOrderClauseBody(
        string? sortBy, SortType? sortType, string? tableName, ISqlDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(sortBy)) return null;

        var direction = sortType == SortType.Ascending ? "ASC" : "DESC";
        var pieces = new List<string>();
        foreach (var raw in sortBy.Split(','))
        {
            var expr = ResolveSortToken(raw, direction, tableName, dialect);
            if (expr is not null) pieces.Add(expr);
        }
        return pieces.Count == 0 ? null : string.Join(", ", pieces);
    }

    public static void AppendOrderAndPaging(
        System.Text.StringBuilder sql, Query q, List<NpgsqlParameter> args, string? tableName = null)
        => AppendOrderAndPaging(sql, q, args, PostgresSqlDialect.Instance, tableName);

    public static void AppendOrderAndPaging(
        System.Text.StringBuilder sql, Query q, List<NpgsqlParameter> args,
        ISqlDialect dialect, string? tableName = null, bool defaultOrder = true)
    {
        if (q.Type == QueryType.Random)
            sql.Append("ORDER BY RANDOM() ");
        else
        {
            var clause = BuildOrderClauseBody(q.SortBy, q.SortType, tableName, dialect);
            if (clause is not null)
            {
                sql.Append($"ORDER BY {clause} ");
            }
            else if (defaultOrder)
            {
                sql.Append("ORDER BY updated_at ");
                sql.Append(q.SortType == SortType.Ascending ? "ASC " : "DESC ");
            }
        }

        args.Add(new() { Value = Math.Max(1, q.Limit) });
        sql.Append($"LIMIT ${args.Count} ");
        args.Add(new() { Value = Math.Max(0, q.Offset) });
        sql.Append($"OFFSET ${args.Count}");
    }

    // ====================================================================
    // GENERIC RUN HELPERS
    // ====================================================================

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `selectAllColumns` and `tableName` are internal-only identifiers from typed repository callers; user values flow through $N parameters built by BuildWhereClause/AppendAclFilter.")]
    public static async Task<List<T>> RunQueryAsync<T>(
        IDbConnectionFactory db, string selectAllColumns, Query q,
        Func<DbDataReader, T> hydrate,
        CancellationToken ct,
        string? userShortname = null, string? tableName = null,
        List<string>? queryPolicies = null,
        IReadOnlyList<InnerSemiJoinSpec>? semiJoins = null)
    {
        var args = new List<NpgsqlParameter>();
        var dialect = DialectFor(db);
        var where = BuildWhereClause(q, args, dialect, tableName);
        var sql = new System.Text.StringBuilder($"{selectAllColumns} WHERE {where} ");

        // Apply ACL filtering if user info provided.
        if (userShortname is not null && tableName is not null)
            AppendAclFilter(sql, args, userShortname, tableName, queryPolicies, dialect, q);

        // Inject INNER-join EXISTS semi-joins (filter base by existence of a
        // matching right row) so LIMIT/OFFSET below page the post-filter set.
        if (semiJoins is { Count: > 0 })
            AppendInnerSemiJoins(sql, args, semiJoins, dialect);

        AppendOrderAndPaging(sql, q, args, dialect, tableName);

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = DbCommandFactory.ResolveDialectPlaceholders(sql.ToString(), conn);
        DbParams.BindAll(cmd, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var results = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            try { results.Add(hydrate(reader)); }
            catch (Exception ex) { _log.LogWarning(ex, "Skipped row with bad data"); }
        }
        return results;
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `tableName` is an internal-only identifier; user values flow through $N parameters.")]
    public static async Task<int> RunCountAsync(
        IDbConnectionFactory db, string tableName, Query q,
        CancellationToken ct,
        string? userShortname = null, List<string>? queryPolicies = null,
        IReadOnlyList<InnerSemiJoinSpec>? semiJoins = null)
    {
        var args = new List<NpgsqlParameter>();
        var dialect = DialectFor(db);
        var where = BuildWhereClause(q, args, dialect, tableName);
        // Bounded count: SELECT COUNT(*) FROM (SELECT 1 ... LIMIT cap+1). The
        // LIMIT stops the scan as soon as cap+1 rows qualify, so the cost is
        // O(cap) instead of O(matching rows) — which is the whole point, since
        // no index makes counting 2.59M rows cheap. Measured on 1M rows:
        // 145 ms / 58,824 buffers unbounded, 1.8 ms / 671 bounded.
        //
        // cap+1 rather than cap so the caller can tell "exactly cap" from
        // "at least cap"; QueryService keys the total_is_lower_bound flag off
        // that extra row. TotalCap = 0 keeps the plain unbounded COUNT.
        var cap = q.TotalCap;
        var sqlBuilder = cap > 0
            ? new System.Text.StringBuilder($"SELECT COUNT(*) FROM (SELECT 1 FROM {tableName} WHERE {where} ")
            : new System.Text.StringBuilder($"SELECT COUNT(*) FROM {tableName} WHERE {where} ");
        // Parity with RunQueryAsync: apply owner/ACL/query_policies predicate
        // so COUNT(*) is scoped to rows the actor can actually see. Skipped
        // for attachments/histories inside AppendAclFilter (Python parity).
        if (userShortname is not null)
            AppendAclFilter(sqlBuilder, args, userShortname, tableName, queryPolicies, dialect, q);

        if (semiJoins is { Count: > 0 })
            AppendInnerSemiJoins(sqlBuilder, args, semiJoins, dialect);

        // Appended last so the LIMIT applies to the fully-filtered set — a cap
        // placed before the ACL predicate would count rows the actor cannot see.
        if (cap > 0)
            sqlBuilder.Append("LIMIT ").Append(cap + 1).Append(") c ");

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = DbCommandFactory.ResolveDialectPlaceholders(sqlBuilder.ToString(), conn);
        DbParams.BindAll(cmd, args);
        // COUNT(*) is int64 on PostgreSQL and int64 on SQLite too, but the
        // provider may hand back a boxed int; Convert normalizes both.
        var scalar = await cmd.ExecuteScalarAsync(ct);
        return scalar is null or DBNull
            ? 0
            : Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    // ====================================================================
    // AGGREGATION QUERY BUILDER
    // ====================================================================
    // Mirrors Python's query_aggregation(). Builds:
    //   SELECT group_by_cols, FUNC(args) AS alias FROM table WHERE ... GROUP BY ...

    // Builds the aggregation statement and its bound parameters, or null when
    // the query resolves to nothing selectable (no aggregation block, or every
    // group-by/reducer was rejected by the whitelist). Split out from
    // RunAggregationAsync so the emitted text is reachable without a live
    // connection — SqlEmissionGoldenTests snapshots it, and the reducer
    // vocabulary is one of the places the SQLite dialect will have to diverge
    // (no percentile_cont / stddev / ordered ARRAY_AGG).
    public static (string Sql, List<NpgsqlParameter> Args)? BuildAggregationSql(
        string tableName, Query q,
        string? userShortname = null, List<string>? queryPolicies = null)
        => BuildAggregationSql(tableName, q, PostgresSqlDialect.Instance, userShortname, queryPolicies);

    public static (string Sql, List<NpgsqlParameter> Args)? BuildAggregationSql(
        string tableName, Query q, ISqlDialect dialect,
        string? userShortname = null, List<string>? queryPolicies = null)
    {
        if (q.AggregationData is null)
            return null;

        var args = new List<NpgsqlParameter>();
        var where = BuildWhereClause(q, args, tableName);

        var groupBy = q.AggregationData.GroupBy ?? new();
        var reducers = q.AggregationData.Reducers ?? new();

        // Build SELECT clause: group_by columns + aggregate functions
        var selectParts = new List<string>();

        // Group-by columns
        foreach (var gb in groupBy)
        {
            var raw = gb.StartsWith('@') ? gb[1..] : gb;
            var expr = ResolveFieldExpr(raw, dialect);
            if (expr is null) continue;
            selectParts.Add($"{expr} AS {SanitizeAlias(gb)}");
        }

        // Aggregate functions (reducers)
        foreach (var reducer in reducers)
        {
            var alias = !string.IsNullOrEmpty(reducer.Alias) ? SanitizeAlias(reducer.Alias) : SanitizeAlias(reducer.ReducerName);
            var expr = BuildReducerExpression(reducer, dialect);
            if (expr is null) continue;
            selectParts.Add($"{expr} AS {alias}");
        }

        if (selectParts.Count == 0) return null;

        var sql = new System.Text.StringBuilder(
            $"SELECT {string.Join(", ", selectParts)} FROM {tableName} WHERE {where} ");

        if (userShortname is not null)
            AppendAclFilter(sql, args, userShortname, tableName, queryPolicies, dialect, q);

        // GROUP BY
        if (groupBy.Count > 0)
        {
            var gbExprs = groupBy
                .Select(gb => gb.StartsWith('@') ? gb[1..] : gb)
                .Select(x => ResolveFieldExpr(x, dialect))
                .Where(e => e is not null)
                .ToList();
            if (gbExprs.Count > 0)
                sql.Append($"GROUP BY {string.Join(", ", gbExprs)} ");
        }

        // ORDER + LIMIT.
        //
        // defaultOrder: false — an aggregation SELECTs only the group-by
        // expressions and the aggregates, so the usual `ORDER BY updated_at`
        // fallback names a column that is neither grouped nor aggregated.
        // PostgreSQL rejects that outright (42803), which meant every
        // aggregation query WITHOUT an explicit sort_by returned a 500; SQLite
        // accepts it and silently picks an arbitrary row's value to sort on.
        // Neither is what the caller wanted. An explicit sort_by is still
        // honoured, and is still the caller's job to keep group-compatible.
        AppendOrderAndPaging(sql, q, args, dialect, tableName, defaultOrder: false);

        return (sql.ToString(), args);
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `tableName` is internal; group-by/reducer expressions are built from a whitelisted ResolveFieldExpr/SanitizeAlias pipeline; user values flow through $N parameters.")]
    public static async Task<List<Dictionary<string, object>>> RunAggregationAsync(
        IDbConnectionFactory db, string tableName, Query q, CancellationToken ct,
        string? userShortname = null, List<string>? queryPolicies = null)
    {
        var built = BuildAggregationSql(tableName, q, DialectFor(db), userShortname, queryPolicies);
        if (built is null) return new();
        var (sql, args) = built.Value;

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = DbCommandFactory.ResolveDialectPlaceholders(sql, conn);
        DbParams.BindAll(cmd, args);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var results = new List<Dictionary<string, object>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                // Skip null columns rather than packing `null!` into a non-null
                // dictionary value — callers use TryGetValue and treat missing
                // keys the same as null.
                if (reader.IsDBNull(i)) continue;
                row[reader.GetName(i)] = reader.GetValue(i);
            }
            results.Add(row);
        }
        return results;
    }

    private static string? BuildReducerExpression(RedisReducer reducer, ISqlDialect dialect)
    {
        var reducerArgs = reducer.Args ?? new();
        var name = reducer.ReducerName.ToLowerInvariant();

        string? ResolveArg(int index)
        {
            if (reducerArgs.Count <= index) return null;
            var arg = reducerArgs[index];
            if (arg.StartsWith('@')) arg = arg[1..];
            return ResolveFieldExpr(arg, dialect);
        }

        var fieldExpr = ResolveArg(0);
        var quantile = ParseQuantile(reducerArgs);

        var expr = dialect.Reducer(name, fieldExpr, quantile);
        if (expr is not null) return expr;

        // The dialect produced nothing. Two very different reasons, and they
        // must not be conflated — see UnsupportedReducerException.
        //
        // PostgreSQL is the reference vocabulary: this port is defined against
        // it, so "does dmart know this reducer, called this way?" is exactly
        // "would the PostgreSQL dialect have emitted something?". Asking it
        // also covers the no-argument case (`sum` with no field yields null on
        // every backend, and skipping that is long-standing behaviour), and it
        // means a reducer added to PostgreSQL later starts REFUSING on SQLite
        // rather than silently vanishing from responses.
        if (dialect is not PostgresSqlDialect
            && PostgresSqlDialect.Instance.Reducer(name, fieldExpr, quantile) is not null)
        {
            throw new UnsupportedReducerException(name, dialect.Name);
        }
        return null;
    }

    private static string ParseQuantile(List<string> args)
    {
        if (args.Count < 2) return "0.5";
        return decimal.TryParse(args[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var q)
            ? Math.Clamp(q, 0m, 1m).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0.5";
    }

    // Resolves a field name (possibly dotted JSONB path) to a SQL expression.
    private static string? ResolveFieldExpr(string field, ISqlDialect dialect)
    {
        if (field.StartsWith("payload.body.", StringComparison.Ordinal))
            return BuildJsonbPath("payload", field["payload.".Length..], dialect);
        if (field.StartsWith("payload.", StringComparison.Ordinal))
            return BuildJsonbPath("payload", field["payload.".Length..], dialect);
        if (field.Contains('.'))
        {
            var dot = field.IndexOf('.');
            var col = field[..dot];
            if (!SafeColumnIdent.IsMatch(col)) return null;
            return BuildJsonbPath(col, field[(dot + 1)..], dialect);
        }
        if (!SafeColumnIdent.IsMatch(field)) return null;
        return field;
    }

    // Sanitize an alias for SQL (replace dots/at-signs with underscores).
    private static string SanitizeAlias(string s)
    {
        var result = Regex.Replace(s.Replace("@", "").Replace(".", "_"), @"[^a-zA-Z0-9_]", "_");
        if (result.Length > 0 && char.IsDigit(result[0])) result = "a_" + result;
        return result;
    }
}
