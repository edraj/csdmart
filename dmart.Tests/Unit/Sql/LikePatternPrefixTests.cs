using Dmart.QueryGrammar;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// The prefix guard is emitted into the row-level access-control predicate, so
// "is it faster" is the second question. The first is that it can never change
// which rows a user sees — which holds only if the prefix is a LITERAL one.
// Get that wrong by one character and a guard like 'sp:/a%' silently hides
// rows a policy grants.
public class LikePatternPrefixTests
{
    [Fact]
    public void Common_FindsSharedPrefix_ForTheProductionShape()
    {
        // 30 near-identical policies differing only in the resource-type
        // segment: the case this whole transform exists for.
        var patterns = new[]
        {
            "mbb:users:user:%", "mbb:users:group:%", "mbb:users:folder:%",
            "mbb:users:content:%", "mbb:users:parquet:%",
        };

        LikePatternPrefix.Common(patterns).ShouldBe("mbb:users:%");
    }

    [Fact]
    public void Common_StopsAtTheFirstWildcard()
    {
        // 'ab%cd' contributes only "ab" — everything past the wildcard is not
        // literal and must not reach the guard.
        LikePatternPrefix.Common(new[] { "ab%cd", "abxy" }).ShouldBe("ab%");
    }

    [Fact]
    public void Common_TreatsAnEscapePairAsOneUnit()
    {
        // Both start with the literal underscore 'a_'. The shared prefix must
        // include the whole escape pair, never the bare backslash: a guard of
        // 'a\%' would be the pattern "a-then-anything", far broader.
        LikePatternPrefix.Common(new[] { @"a\_b:%", @"a\_c:%" }).ShouldBe(@"a\_%");
    }

    [Fact]
    public void Common_DoesNotSplitAnEscapePairWhenOnlyOneSideEscapes()
    {
        // @"a\_x" matches a literal underscore; "a_x" matches any character.
        // They agree on "a" and nothing further — if the pair were compared
        // character-wise, they would appear to share "a_".
        LikePatternPrefix.Common(new[] { @"a\_x:%", "a_x:%" }).ShouldBe("a%");
    }

    [Fact]
    public void Common_ReturnsNull_WhenNothingIsShared()
        => LikePatternPrefix.Common(new[] { "abc:%", "xyz:%" }).ShouldBeNull();

    [Fact]
    public void Common_ReturnsNull_WhenAPatternStartsWithAWildcard()
        // A user holding '%' can see everything; there is no prefix to guard on.
        => LikePatternPrefix.Common(new[] { "%", "mbb:users:%" }).ShouldBeNull();

    [Fact]
    public void Common_ReturnsNull_ForASinglePattern()
        => LikePatternPrefix.Common(new[] { "mbb:users:%" }).ShouldBeNull();

    [Fact]
    public void Common_ReturnsNull_WhenTheGuardIsAlreadyOneOfThePatterns()
    {
        // Broad policy alongside a narrow one: 'sp:/%' already matches
        // everything the guard would, so the guard is pure overhead.
        LikePatternPrefix.Common(new[] { @"sp:/a\_b:100\%\\x", "sp:/%" }).ShouldBeNull();
    }

    [Fact]
    public void Common_IgnoresATrailingLoneBackslash()
    {
        // A dangling backslash is not a valid escape, so nothing after it —
        // and not it either — can be treated as literal.
        LikePatternPrefix.Common(new[] { @"ab\", "abc" }).ShouldBe("ab%");
    }
}
