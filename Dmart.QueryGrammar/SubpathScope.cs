namespace Dmart.QueryGrammar;

/// <summary>
/// Hierarchical subpath matching: a subpath together with everything beneath
/// it.
/// </summary>
/// <remarks>
/// The obvious spelling — <c>subpath LIKE $1 || '/%'</c> — is wrong, and wrong
/// in the direction that grants access. LIKE reads <c>_</c> as "any one
/// character" and <c>%</c> as "any run", so a query scoped to
/// <c>space/my_folder</c> also matched <c>space/myXfolder</c>,
/// <c>space/my-folder</c> and every other one-character sibling. Underscores in
/// a subpath are not exotic; they are the house style for multi-word folders.
///
/// It went unnoticed because the ACL predicate used to clean up after it. An
/// actor's policy is escaped on its way to a LIKE pattern (see
/// <see cref="QueryPolicyExpansion.ToLikePattern"/>, which escapes <c>_</c>),
/// so the over-matched sibling rows carried a query_policies token the actor's
/// pattern did not match and were dropped before anyone saw them. The moment a
/// query could skip the ACL predicate — because the actor's policies provably
/// cover the requested scope — the masking went with it and the siblings came
/// back. The scope predicate has to be right on its own; nothing downstream is
/// guaranteed to be there to narrow it.
///
/// Escaping happens in SQL rather than in the caller's bound value so that ONE
/// parameter can still serve both halves of the usual
/// <c>subpath = $1 OR &lt;descendants&gt;</c> pair: the equality needs the
/// subpath raw, the LIKE needs it escaped, and binding it twice would renumber
/// every positional parameter after it. The replaces run against a parameter,
/// not a column, so both engines fold them once per execution rather than per
/// row.
///
/// Both engines spell all of this identically — <c>replace</c>, <c>||</c> and
/// <c>ESCAPE</c> are common to PostgreSQL and SQLite — so this is plain text
/// rather than an <see cref="ISqlDialect"/> member.
/// </remarks>
public static class SubpathScope
{
    /// <summary>
    /// Emits a predicate matching every row strictly BENEATH the subpath bound
    /// to <paramref name="placeholder"/>. Pair it with an equality test on the
    /// same placeholder to include the subpath itself.
    /// </summary>
    /// <param name="column">The subpath column, already qualified if it needs to be.</param>
    /// <param name="placeholder">
    /// The placeholder the subpath is bound to — <c>$3</c>, <c>@subpath</c>, whatever
    /// the caller's parameter style is. It is emitted verbatim, so it must be a
    /// placeholder the caller produced, never caller data.
    /// </param>
    public static string DescendantLike(string column, string placeholder)
        => column + " LIKE " + EscapedPrefix(placeholder) + @" || '/%' ESCAPE '\'";

    /// <summary>
    /// Neutralises the LIKE metacharacters in a bound value, so it matches
    /// literally under <c>ESCAPE '\'</c>. Backslash goes first — escaping it
    /// after the others would escape the escapes.
    /// </summary>
    public static string EscapedPrefix(string placeholder)
        => "replace(replace(replace(" + placeholder + @", '\', '\\'), '%', '\%'), '_', '\_')";
}
