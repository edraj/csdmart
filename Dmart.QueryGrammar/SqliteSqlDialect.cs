using System.Text;

namespace Dmart.QueryGrammar;

/// <summary>
/// SQLite SQL generation, using the json1 extension.
/// </summary>
/// <remarks>
/// Where PostgreSQL has an operator, SQLite usually has a table-valued function
/// and a correlated EXISTS. That is a real performance difference, not just a
/// spelling one: json_each walks the document per row and no index can serve
/// it, so containment and array-membership filters scan. See
/// docs/sqlite-backend-audit.md §4 for exactly which API predicates that
/// affects, and §9 for the tier's documented limits.
///
/// Two conventions this class depends on, both established by
/// SqliteConnectionFactory:
///   * PRAGMA case_sensitive_like = ON, so plain LIKE matches PostgreSQL's
///     case-sensitive semantics. Every case-INSENSITIVE site must therefore
///     lower() both sides explicitly — see ILike.
///   * JSON is stored as TEXT and array columns hold JSON arrays.
/// </remarks>
public sealed class SqliteSqlDialect : ISqlDialect
{
    public static readonly SqliteSqlDialect Instance = new();

    public string Name => "sqlite";

    // SQLite addresses JSON with a single path string rather than chained
    // operators: payload -> '$.body.title'.
    public string JsonValue(string column, IReadOnlyList<string> path)
        => path.Count == 0 ? column : $"{column} -> '{JsonPath(path)}'";

    public string JsonText(string column, IReadOnlyList<string> path)
        => path.Count == 0 ? column : $"{column} ->> '{JsonPath(path)}'";

    // json_type's vocabulary is finer than jsonb_typeof's: `number` splits into
    // integer/real and `boolean` into true/false, so those two kinds test a set
    // rather than a single value. Collapsing them to one name would silently
    // drop every integer (or every false) from a filter.
    // ->> hands back the JSON value's own SQL type, so typeof() answers the
    // "is it a number?" question directly — no regex, no cast, and integers
    // keep integer precision instead of round-tripping through float.
    public string JsonSortKeys(string column, IReadOnlyList<string> path, string direction)
    {
        var expr = path.Count == 0 ? column : JsonText(column, path);
        return $"CASE WHEN typeof({expr}) IN ('integer','real') THEN {expr} END {direction}, "
             + $"({expr}) {direction}";
    }

    // SQLite covers the counting, extremum, summing and concatenating
    // reducers. The four it does not are refused rather than approximated:
    //
    //   stddev        not in core SQLite (extension-only), and computing it
    //                 from SUM/SUM-of-squares in SQL would silently change
    //                 which of population/sample variance the caller gets.
    //   quantile      no percentile_cont, and no ordered-set aggregates.
    //   first_value   PostgreSQL uses (ARRAY_AGG(x ORDER BY updated_at DESC))[1].
    //   random_sample SQLite has neither ordered array aggregation nor array
    //                 subscripting. The bare-column-beside-max() trick would
    //                 need a second aggregate in the SELECT list, which is not
    //                 a shape one reducer expression can produce.
    //
    // CAST(x AS REAL) rather than PostgreSQL's ::numeric is the documented
    // precision degradation: SQLite has no exact decimal type, so a sum of
    // money-like values accumulates float error where PostgreSQL would not.
    public string? Reducer(string name, string? field, string quantile) => name switch
    {
        "count" or "r_count" => field is null ? "COUNT(*)" : $"COUNT({field})",
        "count_distinct" or "count_distinctish" =>
            field is null ? "COUNT(*)" : $"COUNT(DISTINCT {field})",
        "sum" or "total" => field is null ? null : $"SUM(CAST({field} AS REAL))",
        "avg" => field is null ? null : $"AVG(CAST({field} AS REAL))",
        "min" => field is null ? null : $"MIN({field})",
        "max" => field is null ? null : $"MAX({field})",
        "group_concat" or "tolist" =>
            field is null ? null : $"group_concat(CAST({field} AS TEXT), ',')",
        _ => null,
    };

    public string JsonTypeIs(string jsonExpr, JsonKind kind) => kind switch
    {
        JsonKind.String => $"json_type({jsonExpr}) = 'text'",
        JsonKind.Number => $"json_type({jsonExpr}) IN ('integer','real')",
        JsonKind.Boolean => $"json_type({jsonExpr}) IN ('true','false')",
        JsonKind.Array => $"json_type({jsonExpr}) = 'array'",
        JsonKind.Object => $"json_type({jsonExpr}) = 'object'",
        JsonKind.Null => $"json_type({jsonExpr}) = 'null'",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // SQLite's IS NOT is NULL-safe, matching PostgreSQL's IS DISTINCT FROM, so
    // a missing field yields TRUE. The kinds that map to a SET of json_type
    // answers cannot use IS NOT directly, so their absence case is spelled out.
    public string JsonTypeIsNot(string jsonExpr, JsonKind kind) => kind switch
    {
        JsonKind.Number =>
            $"(json_type({jsonExpr}) IS NULL OR json_type({jsonExpr}) NOT IN ('integer','real'))",
        JsonKind.Boolean =>
            $"(json_type({jsonExpr}) IS NULL OR json_type({jsonExpr}) NOT IN ('true','false'))",
        JsonKind.String => $"json_type({jsonExpr}) IS NOT 'text'",
        JsonKind.Array => $"json_type({jsonExpr}) IS NOT 'array'",
        JsonKind.Object => $"json_type({jsonExpr}) IS NOT 'object'",
        JsonKind.Null => $"json_type({jsonExpr}) IS NOT 'null'",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // IS NOT is NULL-safe, so an absent value does not poison the comparison.
    public string JsonIsNullOrAbsent(string jsonExpr, bool negated)
        => negated
            ? $"({jsonExpr} IS NOT NULL AND json_type({jsonExpr}) IS NOT 'null')"
            : $"({jsonExpr} IS NULL OR json_type({jsonExpr}) = 'null')";

    // No array type, so the list expands to one parameter per value.
    public string AnyOf(string columnExpr, IReadOnlyList<string> values, SqlBinder bind)
    {
        if (values.Count == 0) return "0";   // empty IN () is a syntax error
        var placeholders = values.Select(v => bind(v, SqlValueKind.Inferred));
        return $"{columnExpr} IN ({string.Join(", ", placeholders)})";
    }

    // The `?|` analogue. json_each over the stored array, matched against the
    // bound values. Guarded on json_type so a column holding an object (or
    // malformed JSON) yields no rows instead of raising.
    public string JsonArrayContainsAny(string column, IReadOnlyList<string> values, SqlBinder bind)
    {
        if (values.Count == 0) return "0";
        var placeholders = values.Select(v => bind(v, SqlValueKind.Inferred));
        return $"EXISTS (SELECT 1 FROM json_each({column}) AS je "
             + $"WHERE json_type({column}) = 'array' AND je.value IN ({string.Join(", ", placeholders)}))";
    }

    // Row-level ACL. LIKE is case-sensitive here because of
    // PRAGMA case_sensitive_like=ON, and ESCAPE behaves exactly as PostgreSQL's
    // does — which is why this is LIKE and not GLOB. GLOB is case-sensitive
    // too, but it has no ESCAPE clause and treats ? [ ] as metacharacters, so a
    // policy string containing any of them would match more than it should.
    public string ArrayAnyLike(string column, IReadOnlyList<string> patterns, SqlBinder bind)
    {
        var tests = patterns.Select(p => $"qp.value LIKE {bind(p, SqlValueKind.Inferred)} ESCAPE '\\'");
        return $"EXISTS (SELECT 1 FROM json_each({column}) AS qp WHERE {string.Join(" OR ", tests)})";
    }

    public string AclGrants(string aclColumn, string userPlaceholder, string action)
        => $"EXISTS (SELECT 1 FROM json_each(CASE WHEN json_valid({aclColumn}) "
         + $"AND json_type({aclColumn}) = 'array' THEN {aclColumn} ELSE '[]' END) AS elem "
         + $"WHERE elem.value ->> '$.user_shortname' = {userPlaceholder} "
         + $"AND EXISTS (SELECT 1 FROM json_each(elem.value, '$.allowed_actions') AS act "
         + $"WHERE act.value = '{Escape(action)}'))";

    // Reads the FTS5 trigram index (SqliteSchema.entries_fts), which is the only
    // way SQLite can serve a LIKE '%...%' from an index rather than by scanning.
    // The index covers entries only, matching PostgreSQL, where the pg_trgm GIN
    // is also declared on entries alone; any other table falls back to the plain
    // comparison, which is correct and merely slower.
    //
    // LIKE inside the subquery is deliberately NOT lowered on both sides the way
    // ILike does it: the trigram tokenizer is case-insensitive by default, and
    // wrapping the column in lower() would make the expression unindexable and
    // silently turn this back into a scan.
    public string? WildcardPrefilter(
        string column, string patternPlaceholder, string? targetTable, string patternLiteral)
    {
        // Non-ASCII is served too, because JsonbHelpers writes JSON columns with
        // an encoder that emits literal UTF-8 rather than \uXXXX escapes. That
        // is load-bearing here: with escaped storage the indexed text would
        // contain "\u0645\u0631..." and a wildcard for Arabic could never
        // match, silently ANDing the query down to nothing. If that encoder is
        // ever reverted, this must go back to declining non-ASCII patterns.
        _ = patternLiteral;

        // Only entries carries the index, matching PostgreSQL, where the
        // pg_trgm GIN is likewise declared on entries alone.
        return string.Equals(targetTable, "entries", StringComparison.Ordinal)
            ? $"entries.rowid IN (SELECT rowid FROM entries_fts WHERE {column} LIKE {patternPlaceholder})"
            : ILike(column, patternPlaceholder, negated: false);
    }

    // PRAGMA case_sensitive_like=ON makes plain LIKE case-sensitive, so an
    // ILIKE site must fold both sides itself. lower() is ASCII-only in stock
    // SQLite (no ICU), so accented Latin does not fold the way PostgreSQL's
    // ILIKE folds it — a documented tier limit (audit §9). Arabic is unaffected:
    // the script has no case.
    public string ILike(string lhs, string patternPlaceholder, bool negated)
        => $"lower({lhs}) {(negated ? "NOT LIKE" : "LIKE")} lower({patternPlaceholder})";

    // No cast needed: SQLite compares TEXT-affinity values directly, and an
    // explicit CAST would defeat index use on the column.
    public string AsText(string expr) => expr;

    public string AsNumber(string expr) => $"CAST({expr} AS REAL)";

    public string NumberParam(string placeholder) => $"CAST({placeholder} AS REAL)";

    public string ColumnAsNumber(string column) => $"CAST({column} AS REAL)";

    // SQLite's CAST never raises — a non-numeric element yields 0.0 — so the
    // guard PostgreSQL needs would only change results, not prevent an error.
    // Left as the plain cast so this dialect's emitted SQL is unchanged.
    public string SafeNumberCompare(string textExpr, string sqlOp, string numParam)
        => $"CAST({textExpr} AS REAL) {sqlOp} {numParam}";

    // A stored column is already 0/1, so it compares directly.
    public string ColumnAsBoolean(string column) => column;

    // SQLite has no boolean type. A JSON boolean read through ->> arrives as
    // the text 'true'/'false', while a column stores 0/1 — so normalize both
    // into 0/1 rather than casting, which would silently make 'true' become 0.
    public string AsBoolean(string expr)
        => $"(CASE WHEN {expr} IN ('true', 1, '1') THEN 1 WHEN {expr} IN ('false', 0, '0') THEN 0 END)";

    // The parameter is already JSON text; no cast exists or is needed.
    public string JsonParam(string placeholder) => placeholder;

    // No containment operator and no JSON index. The bound value is a JSON
    // document, so this asks whether every one of its leaves is present at the
    // same path in the target — the semantics PostgreSQL's @> gives for the
    // shapes this grammar builds (a nested object, or a one-element array).
    // Every leaf of the probe must appear at the same path in the target.
    //
    // The key comparison is skipped for ARRAY elements — an integer json_tree
    // key is an array index, and PostgreSQL's @> is order-independent over
    // arrays. Requiring the index to match would make `@payload.body.tags:x`
    // miss any entry whose tags do not happen to start with "x": wrong results,
    // silently. Object members still require their key to match, so a value
    // found under a different property is not a match.
    public string JsonContains(string jsonExpr, string placeholder)
        => $"(SELECT count(*) = 0 FROM json_tree({placeholder}) AS probe "
         + $"WHERE probe.atom IS NOT NULL "
         + $"AND NOT EXISTS (SELECT 1 FROM json_tree({jsonExpr}) AS target "
         + $"WHERE target.atom IS NOT NULL AND target.atom = probe.atom "
         + $"AND target.path IS probe.path "
         + $"AND (typeof(probe.key) = 'integer' OR target.key IS probe.key)))";

    // json_each covers both cases: its `value` column already yields the SQL
    // value for a scalar element, and a sub-path is walked from that value. So
    // unlike PostgreSQL there is no second iterator to choose between.
    public (string From, string ElementJson, string ElementText) JsonArrayIterate(
        string jsonExpr, IReadOnlyList<string> elementPath)
    {
        if (elementPath.Count == 0)
            return ($"json_each({jsonExpr}) AS e", "e.value", "e.value");

        var path = JsonPath(elementPath);
        return ($"json_each({jsonExpr}) AS x",
                $"x.value -> '{path}'",
                $"x.value ->> '{path}'");
    }

    // query_policies is a JSON array in TEXT, so json_each replaces unnest.
    public string ArrayElements(string column, string alias) => $"json_each({column}) AS {alias}";

    public string ArrayElementRef(string alias) => $"{alias}.value";

    public string ArrayLength(string column)
        => $"COALESCE(CASE WHEN json_valid({column}) AND json_type({column}) = 'array' "
         + $"THEN json_array_length({column}) END, 0)";

    // Timestamps are stored as fixed-width local wall-clock text (SqliteValues),
    // so a date string compares directly. An epoch-millis value is converted to
    // that same format so the comparison stays lexicographic.
    public string TimestampFrom(string placeholder, bool epochMillis)
        => epochMillis
            ? $"strftime('%Y-%m-%d %H:%M:%f', {placeholder} / 1000.0, 'unixepoch', 'localtime') || '0000'"
            : placeholder;

    // The VIRTUAL generated column, not the json path it derives from —
    // idx_entries_schema_shortname is only selected when the query names the
    // column. See SqliteSchema.
    public string SchemaShortnameExpr => "schema_shortname";

    // Builds '$.a.b.c'. Segments are whitelist-validated upstream; the quote
    // doubling is defence in depth.
    private static string JsonPath(IReadOnlyList<string> path)
    {
        var sb = new StringBuilder("$");
        foreach (var segment in path) sb.Append('.').Append(Escape(segment));
        return sb.ToString();
    }

    private static string Escape(string s) => s.Replace("'", "''", StringComparison.Ordinal);
}
