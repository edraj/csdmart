namespace Dmart.QueryGrammar;

/// <summary>
/// Finds the longest literal prefix shared by a set of LIKE patterns, so a
/// dialect can emit it as a cheap guard in front of a long OR-chain.
/// </summary>
/// <remarks>
/// <para>
/// Row-level authorization expands a user's query policies into one LIKE per
/// policy, OR'd together, evaluated per array element per row. In production a
/// single user carries ~30 of them, all of the shape
/// <c>space:subpath:resource_type:%</c> — identical but for the resource-type
/// segment. Every element that matches none of them still pays all 30 string
/// comparisons, and that is the common case: most rows belong to someone else.
/// </para>
/// <para>
/// The fix is a redundant guard. Because each pattern begins with the same
/// literal text, anything matching any of them necessarily matches
/// <c>prefix%</c> — so <c>qp LIKE 'prefix%' AND (p1 OR ... OR p30)</c> is
/// logically identical to the bare OR-chain, and lets a non-matching element
/// be rejected on one comparison instead of thirty. Measured on 200k rows with
/// 30 patterns: 180 ms to 69 ms when nothing matches, unchanged when
/// everything does.
/// </para>
/// <para>
/// The prefix has to be LITERAL, which is the whole subtlety. A pattern's
/// prefix ends at the first unescaped <c>%</c> or <c>_</c>, and a <c>\</c>
/// escape binds the character after it — so the scan works in units of
/// "one escape pair or one plain character" and never splits a pair. The text
/// returned is the pattern SOURCE for those units, already escaped, so it can
/// be concatenated with <c>%</c> and bound as a pattern directly.
/// </para>
/// </remarks>
internal static class LikePatternPrefix
{
    /// <summary>
    /// The longest literal prefix common to every pattern, in pattern source
    /// form (escapes intact), or <c>null</c> when there is no useful one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns null for fewer than two patterns — a guard in front of a single
    /// test is pure overhead — and whenever the shared prefix is empty, which
    /// is what happens if any pattern starts with a wildcard.
    /// </para>
    /// <para>
    /// Also returns null when the guard this would produce is already one of
    /// the patterns. That happens when a user holds a broad policy alongside
    /// narrower ones (<c>sp:/%</c> next to <c>sp:/a\_b:100\%\\x</c>): the
    /// broad pattern matches everything the guard would, so testing it first
    /// only adds work. The OR-chain is already as cheap as this transform can
    /// make it. (Dropping the narrow patterns outright would be cheaper still,
    /// and is equally provable, but removing tests from an access-control
    /// predicate is a bigger change than adding a redundant one — left alone
    /// deliberately.)
    /// </para>
    /// </remarks>
    public static string? Common(IReadOnlyList<string> patterns)
    {
        if (patterns.Count < 2) return null;

        var first = Units(patterns[0]);
        if (first.Count == 0) return null;

        var shared = first.Count;
        for (var i = 1; i < patterns.Count; i++)
        {
            var next = Units(patterns[i]);
            var limit = shared < next.Count ? shared : next.Count;
            var common = 0;
            while (common < limit && first[common] == next[common]) common++;
            shared = common;
            if (shared == 0) return null;
        }

        var guard = string.Concat(first.Take(shared)) + "%";
        return patterns.Contains(guard, StringComparer.Ordinal) ? null : guard;
    }

    /// <summary>
    /// Splits the literal head of a LIKE pattern into comparable units: an
    /// escape pair (<c>\x</c>) counts as one unit and keeps its source text, so
    /// two patterns only ever agree on whole pairs. Stops at the first
    /// unescaped wildcard, and at a trailing lone backslash — which is not a
    /// valid escape, so nothing after it can be treated as literal.
    /// </summary>
    private static List<string> Units(string pattern)
    {
        var units = new List<string>();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length) break;
                units.Add(pattern.Substring(i, 2));
                i++;
                continue;
            }
            if (c is '%' or '_') break;
            units.Add(c.ToString());
        }
        return units;
    }
}
