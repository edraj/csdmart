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
    /// True when the string-array column shares at least one element with
    /// <paramref name="values"/> (PostgreSQL <c>&amp;&amp;</c>).
    /// </summary>
    /// <remarks>
    /// The indexable form of the row-level ACL policy test. Callers get here
    /// via <see cref="QueryPolicyExpansion"/>, which turns wildcard policy
    /// patterns into the exact strings a row can carry; patterns it cannot
    /// expand stay on <see cref="ArrayAnyLike"/>. Matching is by equality, so
    /// no escaping applies and both backends compare case-sensitively.
    /// </remarks>
    string ArrayOverlapAny(string column, IReadOnlyList<string> values, SqlBinder bind)
    {
        // Default implementation so this member stays additive: Dmart.QueryGrammar
        // is a published package, and a new abstract member would break every
        // third-party ISqlDialect at compile time. Falls back to the LIKE form
        // every dialect already implements — same rows, just not indexable. A
        // backend that can serve an array overlap from an index overrides this;
        // both in-tree dialects do.
        //
        // The values are already-expanded exact tokens, so they are escaped to
        // match themselves literally. Deliberately NOT QueryPolicyExpansion
        // .ToLikePattern: that maps '*' to '%', and widening a policy token here
        // would grant access the overriding dialects refuse.
        if (values.Count == 0) return "FALSE";
        var patterns = new List<string>(values.Count);
        foreach (var v in values)
            patterns.Add(QueryPolicyExpansion.ToLiteralLikePattern(v));
        return ArrayAnyLike(column, patterns, bind);
    }

    /// <summary>
    /// True when the ACL JSON array holds an entry granting
    /// <paramref name="action"/> to the bound user.
    /// </summary>
    string AclGrants(string aclColumn, string userPlaceholder, string action);

    /// <summary>
    /// Cheap whole-document prefilter for a wildcard search, AND-ed onto the
    /// precise per-path check.
    /// </summary>
    /// <param name="targetTable">
    /// Table the surrounding WHERE applies to, when known. Only the entries
    /// table carries a wildcard index on either backend.
    /// </param>
    /// <remarks>
    /// The prefilter is allowed to over-match — the precise check that follows
    /// removes false positives — but it must never UNDER-match, or rows that
    /// should have matched disappear. PostgreSQL satisfies that trivially: its
    /// prefilter is a plain ILIKE that a pg_trgm GIN may or may not accelerate,
    /// and correctness does not depend on the index existing. SQLite's version
    /// reads an FTS5 trigram index instead, so there the index being in sync IS
    /// the correctness condition — see the triggers in SqliteSchema.
    /// </remarks>
    /// <param name="patternLiteral">
    /// The pattern's literal text, so a dialect can decline when its index
    /// cannot serve that particular pattern. Returning null omits the conjunct
    /// entirely, leaving only the precise per-path check — slower, still exact.
    /// </param>
    string? WildcardPrefilter(
        string column, string patternPlaceholder, string? targetTable, string patternLiteral);

    /// <summary>Case-INSENSITIVE pattern match (PostgreSQL <c>ILIKE</c>).</summary>
    string ILike(string lhs, string patternPlaceholder, bool negated);

    /// <summary>Renders a column as text for comparison or concatenation.</summary>
    string AsText(string expr);

    /// <summary>
    /// The ORDER BY key list for a dotted JSON path — a numeric key that is
    /// NULL for non-numeric values, followed by a textual tiebreaker, both in
    /// <paramref name="direction"/>.
    /// </summary>
    /// <remarks>
    /// A whole fragment rather than composed parts because the two backends
    /// answer "is this value a number?" in structurally different ways.
    /// PostgreSQL's jsonb ->> always yields text, so it has to regex-match the
    /// digits and cast. SQLite's ->> preserves the JSON type, so typeof() is
    /// both cheaper and stricter — and the difference is visible: a number
    /// stored as a JSON STRING ("42") sorts numerically on PostgreSQL and
    /// lexically on SQLite. Documented in docs/sqlite-backend-audit.md §9;
    /// closing it would mean reimplementing the regex, which SQLite has no
    /// built-in for.
    /// </remarks>
    string JsonSortKeys(string column, IReadOnlyList<string> path, string direction);

    /// <summary>
    /// The aggregate SELECT item for one reducer, or null when this backend
    /// cannot express it.
    /// </summary>
    /// <remarks>
    /// Null is a REFUSAL, not "unknown reducer" — the caller turns it into a
    /// request error naming the reducer. That distinction is the whole point of
    /// putting this on the dialect: an unrecognized name has always been
    /// skipped silently, and a reducer this backend genuinely cannot compute
    /// must not take the same path, or the response would come back missing a
    /// column the client asked for and nothing would say why.
    ///
    /// <paramref name="field"/> is null when the reducer was called with no
    /// argument; only the counting reducers accept that.
    /// </remarks>
    string? Reducer(string name, string? field, string quantile);

    /// <summary>
    /// As <see cref="Reducer(string, string?, string)"/>, but also handed the
    /// same field as an untyped JSON value, when there is one.
    /// </summary>
    /// <remarks>
    /// Only ordering reducers care. A backend whose JSON extraction yields text
    /// (PostgreSQL <c>-&gt;&gt;</c>) compares 9, 10 and 100 as strings and
    /// answers "10" for the minimum; one whose extraction is typed (SQLite
    /// <c>-&gt;&gt;</c>) compares them as numbers and needs no help.
    ///
    /// <paramref name="fieldJson"/> is the JSON value at the same path with its
    /// type intact (PostgreSQL <c>-&gt;</c>), which is strictly more than a
    /// "this is JSON text" flag would carry: a dialect can then tell a JSON
    /// NUMBER from a JSON STRING that merely looks numeric, and order each the
    /// way its own engine would. A flag cannot — its only recourse is to sniff
    /// the text, and a text sniff cannot tell 7 from "007".
    ///
    /// Null for a plain column, which is already natively typed and MUST be
    /// left alone — on PostgreSQL a text-oriented rewrite of
    /// <c>MIN(updated_at)</c> both changes the returned type and fails
    /// outright, since <c>timestamp ~ text</c> has no operator. Null also when
    /// the reducer took no argument.
    ///
    /// Default implementation so this member stays additive: Dmart.QueryGrammar
    /// is a published package, and a new abstract member would break every
    /// third-party ISqlDialect at compile time. The default ignores the JSON
    /// form and behaves exactly as before, which is correct for any dialect
    /// whose JSON extraction is already typed.
    /// </remarks>
    string? Reducer(string name, string? field, string quantile, string? fieldJson)
        => Reducer(name, field, quantile);

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

    /// <summary>
    /// Compares a TEXT-typed array element against a numeric parameter without
    /// letting a non-numeric element raise. Elements of a scalar array arrive
    /// as text, so a bare <see cref="ColumnAsNumber"/> over them casts EVERY
    /// element: on PostgreSQL <c>CAST('red' AS FLOAT)</c> aborts the whole
    /// query. Implementations must keep the guard and the cast in one
    /// expression that cannot be reordered — neither engine promises AND
    /// short-circuits before the cast — so a CASE, not a conjunction.
    /// </summary>
    string SafeNumberCompare(string textExpr, string sqlOp, string numParam);

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

    /// <summary>
    /// True when <see cref="JsonContains"/> of a one-element JSON ARRAY
    /// literal matches ONLY array values — PostgreSQL <c>@&gt;</c> semantics,
    /// where an object or scalar can never contain an array. Lets the parser
    /// emit a bare, index-servable containment for @tags/@roles/@groups.
    /// False for dialects whose containment emulation is looser (SQLite's
    /// json_tree walk also matches an object/scalar holding the value), which
    /// keep the original guarded emission so their behavior doesn't shift.
    /// </summary>
    bool JsonArrayContainmentIsExact { get; }

    /// <summary>
    /// Iterates a JSON array, yielding the FROM fragment plus expressions for
    /// each element as JSON and as text.
    /// </summary>
    /// <param name="jsonExpr">The array-valued expression to iterate.</param>
    /// <param name="elementPath">
    /// Path INSIDE each element to address, empty to use the element itself.
    /// </param>
    /// <remarks>
    /// Returned as a triple because the three parts must agree on the alias and
    /// on how an element is dereferenced, and the two engines disagree on both.
    /// PostgreSQL picks a different set-returning function depending on whether
    /// a sub-path is needed (jsonb_array_elements vs …_text) and dereferences
    /// the alias directly; SQLite always uses json_each and reaches the element
    /// through its `value` column. Splitting this into three members would let
    /// a caller mix an alias from one with a dereference from another.
    /// </remarks>
    (string From, string ElementJson, string ElementText) JsonArrayIterate(
        string jsonExpr, IReadOnlyList<string> elementPath);

    /// <summary>FROM-clause fragment iterating a string-array column's elements.</summary>
    /// <remarks>
    /// The alias is NOT usable as a value on its own, so always dereference it
    /// through <see cref="ArrayElementRef"/> instead of interpolating it into a
    /// predicate. PostgreSQL's <c>unnest</c> yields a column, so the bare alias
    /// happens to work there — which is exactly why the mistake survives review
    /// and only shows up on SQLite, whose <c>json_each</c> yields a TABLE and
    /// fails at execution with "no such column".
    /// </remarks>
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
