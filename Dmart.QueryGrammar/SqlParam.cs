namespace Dmart.QueryGrammar;

/// <summary>
/// How a bound value should be typed by the provider.
/// </summary>
/// <remarks>
/// Provider-neutral stand-in for the small subset of <c>NpgsqlDbType</c> the
/// query grammar actually needs. Only the kinds the grammar binds today are
/// listed; the set grows as other call sites move off provider-specific
/// parameter construction.
/// </remarks>
public enum SqlValueKind
{
    /// <summary>
    /// No explicit type — the provider infers one from the CLR value.
    /// </summary>
    /// <remarks>
    /// Distinct from naming a type on purpose. Npgsql infers a different
    /// (and sometimes more permissive) type than an explicit tag would set,
    /// so collapsing this into <see cref="Text"/> would change how PostgreSQL
    /// casts the value even though the SQL text is unchanged.
    /// </remarks>
    Inferred,

    /// <summary>JSON document — PostgreSQL <c>jsonb</c>.</summary>
    Json,

    /// <summary>Boolean.</summary>
    Boolean,

    /// <summary>
    /// An array of strings, bound as a single value — PostgreSQL <c>text[]</c>.
    /// </summary>
    /// <remarks>
    /// PostgreSQL-only by nature. SQLite has no array type, so
    /// <see cref="SqliteSqlDialect"/> never binds this kind: it expands a list
    /// into one parameter per element instead. A SQLite parameter factory that
    /// receives it should treat that as a bug rather than serializing the array
    /// to a string.
    /// </remarks>
    TextArray,

    /// <summary>
    /// A string-to-string map — PostgreSQL <c>hstore</c>.
    /// </summary>
    /// <remarks>
    /// Used only by the OTP row's value column. PostgreSQL stores it as hstore
    /// and Npgsql round-trips it as an IDictionary; SQLite has no such type, so
    /// the same map is stored as a JSON object in TEXT and read back with the
    /// json1 functions.
    /// </remarks>
    KeyValueMap,
}

/// <summary>
/// A value bound by the query grammar, described without reference to any
/// database provider.
/// </summary>
/// <param name="Name">
/// Placeholder name for named-style emission (<c>@s_0</c>), or <c>null</c> for
/// positional <c>$N</c> emission where the provider binds by position.
/// </param>
/// <param name="Value">The value to bind. Never <c>null</c> — absent values are <see cref="DBNull"/>.</param>
/// <param name="Kind">How the provider should type the value.</param>
/// <remarks>
/// This is what lets <c>Dmart.QueryGrammar</c> carry no database package
/// reference at all. The grammar builds SQL text and describes its parameters;
/// the caller's dialect materializes them into whatever concrete
/// <c>DbParameter</c> its provider needs.
/// </remarks>
public readonly record struct SqlParam(string? Name, object Value, SqlValueKind Kind);
