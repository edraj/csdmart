using System.Text;

namespace Dmart.QueryGrammar;

/// <summary>
/// PostgreSQL SQL generation. Emits exactly the text csdmart emitted before the
/// dialect seam existed.
/// </summary>
/// <remarks>
/// Every method here is pinned by SqlEmissionGoldenTests. The golden is not a
/// convenience — the PostgreSQL path is the production tier, and the whole
/// point of routing it through an interface is that doing so must be invisible
/// to it. A diff in that snapshot is a bug in this file, never a golden to
/// refresh.
///
/// Some of the emitted text is redundant on its face (casting a column that is
/// already jsonb, or CAST(...) around an already-typed parameter). It is kept
/// verbatim rather than tidied: the value of a byte-identical migration is that
/// nothing has to be re-reviewed for planner or type-resolution effects.
/// </remarks>
public sealed class PostgresSqlDialect : ISqlDialect
{
    public static readonly PostgresSqlDialect Instance = new();

    public string Name => "postgresql";

    // payload::jsonb->'body'->'title'
    public string JsonValue(string column, IReadOnlyList<string> path)
    {
        var sb = new StringBuilder(column).Append("::jsonb");
        foreach (var segment in path) sb.Append("->'").Append(Escape(segment)).Append('\'');
        return sb.ToString();
    }

    // payload::jsonb->'body'->>'title' — the final hop uses ->> for text.
    public string JsonText(string column, IReadOnlyList<string> path)
    {
        if (path.Count == 0) return column + "::text";
        var sb = new StringBuilder(column).Append("::jsonb");
        for (var i = 0; i < path.Count; i++)
        {
            sb.Append(i == path.Count - 1 ? "->>'" : "->'")
              .Append(Escape(path[i])).Append('\'');
        }
        return sb.ToString();
    }

    public string JsonTypeIs(string jsonExpr, JsonKind kind)
        => $"jsonb_typeof({jsonExpr}) = '{TypeName(kind)}'";

    // IS DISTINCT FROM, not NOT(... = ...): it is NULL-safe, so a missing field
    // yields TRUE rather than NULL. This is the exact text the pre-seam parser
    // emitted, and the golden pins it.
    public string JsonTypeIsNot(string jsonExpr, JsonKind kind)
        => $"jsonb_typeof({jsonExpr}) IS DISTINCT FROM '{TypeName(kind)}'";

    private static string TypeName(JsonKind kind) => kind switch
    {
        JsonKind.String => "string",
        JsonKind.Number => "number",
        JsonKind.Boolean => "boolean",
        JsonKind.Array => "array",
        JsonKind.Object => "object",
        JsonKind.Null => "null",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public string JsonIsNullOrAbsent(string jsonExpr, bool negated)
        => negated
            ? $"({jsonExpr} IS NOT NULL AND jsonb_typeof({jsonExpr}) != 'null')"
            : $"({jsonExpr} IS NULL OR jsonb_typeof({jsonExpr}) = 'null')";

    // One array parameter, matched with = ANY($n). This is why the interface
    // hands dialects a binder rather than only rewriting text.
    public string AnyOf(string columnExpr, IReadOnlyList<string> values, SqlBinder bind)
        => $"{columnExpr} = ANY({bind(values.ToArray(), SqlValueKind.TextArray)})";

    // ?| is "does this jsonb value contain any of these top-level keys/elements".
    public string JsonArrayContainsAny(string column, IReadOnlyList<string> values, SqlBinder bind)
        => $"{column} ?| {bind(values.ToArray(), SqlValueKind.TextArray)}";

    public string ArrayAnyLike(string column, IReadOnlyList<string> patterns, SqlBinder bind)
    {
        var tests = patterns.Select(p => $"qp LIKE {bind(p, SqlValueKind.Inferred)} ESCAPE '\\'");
        return $"EXISTS (SELECT 1 FROM unnest({column}) AS qp WHERE {string.Join(" OR ", tests)})";
    }

    public string AclGrants(string aclColumn, string userPlaceholder, string action)
        => $"EXISTS (SELECT 1 FROM jsonb_array_elements(CASE WHEN jsonb_typeof({aclColumn}::jsonb) = 'array' "
         + $"THEN {aclColumn}::jsonb ELSE '[]'::jsonb END) AS elem "
         + $"WHERE elem->>'user_shortname' = {userPlaceholder} "
         + $"AND (elem->'allowed_actions') ? '{Escape(action)}')";

    // Unchanged from the pre-seam emission: a plain ILIKE over the serialized
    // document, which idx_entries_payload_trgm accelerates when present and
    // which is still correct when it is not.
    public string? WildcardPrefilter(
        string column, string patternPlaceholder, string? targetTable, string patternLiteral)
        => ILike(AsText(column), patternPlaceholder, negated: false);

    public string ILike(string lhs, string patternPlaceholder, bool negated)
        => $"{lhs} {(negated ? "NOT ILIKE" : "ILIKE")} {patternPlaceholder}";

    public string AsText(string expr) => $"{expr}::text";

    public string AsNumber(string expr) => $"({expr})::float";

    public string NumberParam(string placeholder) => $"CAST({placeholder} AS float)";

    public string ColumnAsNumber(string column) => $"CAST({column} AS FLOAT)";

    public string AsBoolean(string expr) => $"({expr})::boolean";

    public string ColumnAsBoolean(string column) => $"CAST({column} AS BOOLEAN)";

    // Backed by the jsonb_path_ops GIN indexes in SqlSchema.
    public string JsonParam(string placeholder) => $"CAST({placeholder} AS jsonb)";

    public string JsonContains(string jsonExpr, string placeholder)
        => $"{jsonExpr} @> {placeholder}";

    // Two different set-returning functions on purpose, matching the pre-seam
    // SQL: the _text variant yields SQL text directly (so a bare element
    // compares without a cast), while the plain variant keeps elements as jsonb
    // so a sub-path can be walked into them.
    public (string From, string ElementJson, string ElementText) JsonArrayIterate(
        string jsonExpr, IReadOnlyList<string> elementPath)
    {
        if (elementPath.Count == 0)
            return ($"jsonb_array_elements_text({jsonExpr}) AS e", "e::jsonb", "e");

        var lead = string.Concat(elementPath.Take(elementPath.Count - 1)
                                            .Select(p => $"->'{Escape(p)}'"));
        var last = Escape(elementPath[^1]);
        return ($"jsonb_array_elements({jsonExpr}) AS x",
                $"x{lead}->'{last}'",
                $"x{lead}->>'{last}'");
    }

    public string ArrayElements(string column, string alias) => $"unnest({column}) AS {alias}";

    public string ArrayElementRef(string alias) => alias;

    // array_length returns NULL for an empty array, hence the COALESCE.
    public string ArrayLength(string column) => $"COALESCE(array_length({column}, 1), 0)";

    public string TimestampFrom(string placeholder, bool epochMillis)
        => epochMillis ? $"to_timestamp({placeholder}::float8 / 1000.0)" : $"{placeholder}::timestamptz";

    // Parenthesized to match the pre-seam emission exactly. idx_entries_schema_shortname
    // is an expression index over this same text.
    public string SchemaShortnameExpr => "(payload->>'schema_shortname')";

    // Single-quote doubling for identifiers spliced into JSON path literals.
    // Callers already validate path segments against a whitelist regex; this is
    // defence in depth, not the primary control.
    private static string Escape(string s) => s.Replace("'", "''", StringComparison.Ordinal);
}
