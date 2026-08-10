namespace Dmart.QueryGrammar;

/// <summary>
/// Binds a value and returns the placeholder text referring to it.
/// </summary>
/// <remarks>
/// Dialects need this because some constructs differ in SHAPE, not just in
/// token spelling. PostgreSQL matches a list with one array parameter
/// (<c>col = ANY($1)</c>); SQLite has no array type and needs one parameter per
/// element (<c>col IN ($1,$2,$3)</c>). A dialect that could only rewrite text
/// could not express that difference, so it binds its own parameters instead.
/// </remarks>
public delegate string SqlBinder(object value, SqlValueKind kind);

/// <summary>JSON value categories, normalized across the two engines.</summary>
/// <remarks>
/// The engines disagree on vocabulary, and the disagreement is not a simple
/// rename. PostgreSQL's <c>jsonb_typeof</c> answers
/// string/number/boolean/array/object/null; SQLite's <c>json_type</c> answers
/// text/integer/real/true/false/array/object/null. So PostgreSQL's single
/// `number` spans two SQLite answers, and its single `boolean` spans two more.
/// Callers name the category they mean and let the dialect emit whatever test
/// its engine needs.
/// </remarks>
public enum JsonKind
{
    String,
    Number,
    Boolean,
    Array,
    Object,
    Null,
}

/// <summary>
/// The parts of SQL generation that genuinely differ between PostgreSQL and
/// SQLite.
/// </summary>
/// <remarks>
/// Deliberately narrow. Everything the two engines spell identically — SELECT,
/// JOIN, GROUP BY, ON CONFLICT, RETURNING, comparison operators, LIMIT/OFFSET —
/// stays as plain text at the call site. A method earns its place here only
/// when a second backend actually needs it to differ.
///
/// Implementations are pure string builders with no database dependency, which
/// is why both live in this package alongside the grammar. Materializing a
/// bound value into a concrete DbParameter is a separate concern handled by the
/// caller (see PostgresDialect / JsonbHelpers.ToNpgsqlParameter).
/// </remarks>
public interface ISqlDialect
{
    /// <summary>Name used in diagnostics and test output.</summary>
    string Name { get; }

    /// <summary>
    /// Extracts a JSON value at <paramref name="path"/> as SQL text
    /// (PostgreSQL <c>-&gt;&gt;</c>).
    /// </summary>
    string JsonText(string column, IReadOnlyList<string> path);

    /// <summary>
    /// Extracts a JSON value at <paramref name="path"/> as JSON
    /// (PostgreSQL <c>-&gt;</c>).
    /// </summary>
    string JsonValue(string column, IReadOnlyList<string> path);

    /// <summary>Tests the JSON type of an expression produced by <see cref="JsonValue"/>.</summary>
    string JsonTypeIs(string jsonExpr, JsonKind kind);

    /// <summary>
    /// NULL-safe negation of <see cref="JsonTypeIs"/>: true when the value is
    /// absent OR is of some other type.
    /// </summary>
    /// <remarks>
    /// Deliberately its own method rather than NOT(JsonTypeIs(...)). Wrapping
    /// the positive test in NOT is wrong under three-valued logic: a missing
    /// field makes the type function NULL, `NULL = 'string'` is NULL, and
    /// `NOT NULL` is still NULL — which WHERE discards. A negated filter must
    /// KEEP rows where the field is absent, so the absence has to be folded in
    /// rather than negated away.
    /// </remarks>
    string JsonTypeIsNot(string jsonExpr, JsonKind kind);

    /// <summary>
    /// Tests whether a JSON value is absent or explicitly JSON null.
    /// </summary>
    /// <remarks>
    /// This is the `@field:null` filter, and it is one concept rather than two
    /// composable ones: "missing" and "present but null" must both count, so the
    /// SQL-NULL check and the JSON-type check have to be emitted together.
    /// Building it from JsonTypeIs plus a negation would lose that, and on
    /// PostgreSQL would also change the emitted text.
    /// </remarks>
    string JsonIsNullOrAbsent(string jsonExpr, bool negated);

    /// <summary>
    /// True when the column's value equals any of <paramref name="values"/>.
    /// </summary>
    string AnyOf(string columnExpr, IReadOnlyList<string> values, SqlBinder bind);

    /// <summary>
    /// True when the JSON array column contains any of <paramref name="values"/>
    /// (PostgreSQL <c>?|</c>).
    /// </summary>
    string JsonArrayContainsAny(string column, IReadOnlyList<string> values, SqlBinder bind);

    /// <summary>
    /// True when any element of the string-array column matches one of the
    /// supplied LIKE patterns. Case-SENSITIVE, and honouring backslash escapes
    /// — this is the row-level ACL policy test, where widening the match grants
    /// access the other backend would refuse.
    /// </summary>
    string ArrayAnyLike(string column, IReadOnlyList<string> patterns, SqlBinder bind);

    /// <summary>
    /// True when the ACL JSON array holds an entry granting
    /// <paramref name="action"/> to the bound user.
    /// </summary>
    string AclGrants(string aclColumn, string userPlaceholder, string action);

    /// <summary>Case-INSENSITIVE pattern match (PostgreSQL <c>ILIKE</c>).</summary>
    string ILike(string lhs, string patternPlaceholder, bool negated);

    /// <summary>Renders a column as text for comparison or concatenation.</summary>
    string AsText(string expr);

    /// <summary>Casts an extracted JSON value to a number for comparison.</summary>
    string AsNumber(string expr);

    /// <summary>Casts a bound parameter to a number for comparison.</summary>
    string NumberParam(string placeholder);

    /// <summary>Casts a whole COLUMN to a number for comparison.</summary>
    /// <remarks>
    /// Three members rather than one because PostgreSQL's pre-seam SQL spells
    /// the same cast three ways depending on the site — (x)::float on a JSON
    /// extract, CAST(x AS float) on a parameter, CAST(x AS FLOAT) on a column.
    /// They are the identical cast; keeping them distinct is purely what makes
    /// the emitted text byte-identical, and the alternative was rewriting
    /// assertions in tests that had every right to keep passing.
    /// </remarks>
    string ColumnAsNumber(string column);

    /// <summary>Casts a whole COLUMN to a boolean for comparison.</summary>
    /// <remarks>
    /// Separate from <see cref="AsBoolean"/> only because PostgreSQL spells the
    /// two sites differently in the pre-seam SQL — CAST(x AS BOOLEAN) on a
    /// column, (x)::boolean on an extracted JSON value. Both are the same cast;
    /// keeping them distinct is what lets the emitted text stay byte-identical.
    /// </remarks>
    string ColumnAsBoolean(string column);

    /// <summary>Casts an expression to a boolean for comparison.</summary>
    /// <remarks>
    /// PostgreSQL has a real boolean type. SQLite does not: it stores 0/1, and
    /// a JSON boolean extracted with -&gt;&gt; comes back as the text 'true' or
    /// 'false'. The dialect normalizes whichever form its engine produces.
    /// </remarks>
    string AsBoolean(string expr);

    /// <summary>Renders a bound JSON parameter for use in a JSON expression.</summary>
    /// <remarks>
    /// PostgreSQL wraps it in a CAST that is redundant beside an already-typed
    /// jsonb parameter, kept verbatim so the emitted text does not move.
    /// </remarks>
    string JsonParam(string placeholder);

    /// <summary>True when the JSON column contains the bound JSON document.</summary>
    /// <remarks>
    /// PostgreSQL's <c>@&gt;</c>, backed by a GIN index. SQLite has no
    /// containment operator and no JSON index, so this degrades to a per-row
    /// walk — see docs/sqlite-backend-audit.md §4.
    /// </remarks>
    string JsonContains(string jsonExpr, string placeholder);

    /// <summary>FROM-clause fragment iterating a string-array column's elements.</summary>
    string ArrayElements(string column, string alias);

    /// <summary>References an element produced by <see cref="ArrayElements"/>.</summary>
    string ArrayElementRef(string alias);

    /// <summary>Number of elements in a string-array column; 0 when absent.</summary>
    string ArrayLength(string column);

    /// <summary>
    /// Renders a bound value as a timestamp comparable against a timestamp column.
    /// </summary>
    /// <param name="epochMillis">
    /// True when the bound value is a millisecond epoch rather than a date string.
    /// </param>
    string TimestampFrom(string placeholder, bool epochMillis);

    /// <summary>
    /// Expression selecting an entry's schema shortname, index-backed on both
    /// engines.
    /// </summary>
    /// <remarks>
    /// Earns a dedicated member because the two backends reach it differently
    /// and the difference decides whether the query uses an index. PostgreSQL
    /// reads the JSON path and has an expression index over exactly that text.
    /// SQLite cannot index a bare expression, so the schema shortname is a
    /// generated column — and its index is only selected when the query names
    /// the COLUMN, not the equivalent json path. Emitting the path form on
    /// SQLite would silently turn every schema-filtered query into a scan.
    /// </remarks>
    string SchemaShortnameExpr { get; }
}
