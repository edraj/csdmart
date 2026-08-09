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
