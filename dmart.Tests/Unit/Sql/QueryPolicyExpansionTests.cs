using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using Dmart.Models.Enums;
using Dmart.QueryGrammar;
using Shouldly;
using Xunit;

namespace dmart.Tests.Unit.Sql;

// The read-time ACL filter used to test a row's query_policies with
// unnest + LIKE. It now tests them with an indexable array overlap over the
// exact strings a row can carry, and only falls back to LIKE for policy shapes
// QueryPolicyExpansion refuses to enumerate.
//
// The rewrite is a permission boundary: expanding to too few tokens denies
// access that should be granted, expanding to too many grants access that
// should be denied. The load-bearing test here is
// Expansion_Matches_Legacy_Like_Semantics, which runs both predicates over a
// matrix of real row policies and asserts they agree row by row.
public class QueryPolicyExpansionTests
{
    // ── The '*' resource-type set is a copy; keep it honest ────────────────

    [Fact]
    public void ResourceTypes_Match_The_Enum()
    {
        var fromEnum = Enum.GetValues<ResourceType>()
            .Select(rt => typeof(ResourceType).GetField(rt.ToString())!
                .GetCustomAttributes(typeof(EnumMemberAttribute), false)
                .Cast<EnumMemberAttribute>().Single().Value!)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();

        // A resource type missing from QueryPolicyExpansion.ResourceTypes is
        // invisible to every '*' policy — add it there when adding it here.
        QueryPolicyExpansion.ResourceTypes
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList()
            .ShouldBe(fromEnum);
    }

    // ── Shape-by-shape expansion ──────────────────────────────────────────

    [Fact]
    public void Concrete_Owner_Policy_Is_Its_Own_Token()
    {
        var e = QueryPolicyExpansion.Expand(new[] { "space:items:content:true:alice" });
        e.ExactTokens.ShouldBe(new[] { "space:items:content:true:alice" });
        e.LikePatterns.ShouldBeEmpty();
    }

    [Fact]
    public void Any_Owner_Policy_Maps_To_The_Owner_Unscoped_Literal()
    {
        // "{key}:true:*" is the is_active-condition shape: any owner, active rows.
        var e = QueryPolicyExpansion.Expand(new[] { "space:items:content:true:*" });
        e.ExactTokens.ShouldBe(new[] { "space:items:content:true" });
        e.LikePatterns.ShouldBeEmpty();
    }

    [Fact]
    public void Unconditioned_Policy_Covers_Both_IsActive_Values()
    {
        var e = QueryPolicyExpansion.Expand(new[] { "space:items:content:*" });
        e.ExactTokens.ShouldBe(new[] { "space:items:content:true", "space:items:content:false" });
        e.LikePatterns.ShouldBeEmpty();
    }

    [Fact]
    public void Wildcard_ResourceType_Enumerates_The_Closed_Set()
    {
        var e = QueryPolicyExpansion.Expand(new[] { "space:items:*:true:*" });

        e.LikePatterns.ShouldBeEmpty();
        e.ExactTokens.Count.ShouldBe(QueryPolicyExpansion.ResourceTypes.Count);
        e.ExactTokens.ShouldContain("space:items:content:true");
        e.ExactTokens.ShouldContain("space:items:folder:true");
        e.ExactTokens.ShouldContain("space:items:ticket:true");
    }

    [Fact]
    public void Tokens_Are_Deduplicated_Across_Policies()
    {
        var e = QueryPolicyExpansion.Expand(new[]
        {
            "space:items:content:true:*",
            "space:items:content:true:*",
            "space:items:content:*",
        });
        e.ExactTokens.ShouldBe(new[] { "space:items:content:true", "space:items:content:false" });
    }

    [Theory]
    // A wildcard anywhere but the resource-type or trailing segment is not
    // something we know how to enumerate — those keep the LIKE test rather
    // than being narrowed to a guess.
    [InlineData("*:items:content:true:alice")]
    [InlineData("space:it*ms:content:true:alice")]
    [InlineData("space:items:cont*:true:alice")]
    [InlineData("space:items:content:*:alice")]
    [InlineData("space:items:content:true:ali*")]
    // Shapes with the wrong segment count (e.g. hand-written policies).
    [InlineData("space:items:content")]
    [InlineData("space:items:content:true:alice:extra")]
    public void Unenumerable_Shapes_Fall_Back_To_Like(string policy)
    {
        var e = QueryPolicyExpansion.Expand(new[] { policy });
        e.ExactTokens.ShouldBeEmpty();
        e.LikePatterns.ShouldBe(new[] { policy });
    }

    [Fact]
    public void Mixed_Input_Splits_Between_Both_Tests()
    {
        var e = QueryPolicyExpansion.Expand(new[] { "space:items:content:true:*", "*:x:content:true:bob" });
        e.ExactTokens.ShouldBe(new[] { "space:items:content:true" });
        e.LikePatterns.ShouldBe(new[] { "*:x:content:true:bob" });
    }

    [Fact]
    public void Null_And_Empty_Expand_To_Nothing()
    {
        QueryPolicyExpansion.Expand(null).ExactTokens.ShouldBeEmpty();
        QueryPolicyExpansion.Expand(null).LikePatterns.ShouldBeEmpty();
        QueryPolicyExpansion.Expand(Array.Empty<string>()).ExactTokens.ShouldBeEmpty();
    }

    [Fact]
    public void Like_Metacharacters_Are_Escaped_Only_On_The_Fallback_Path()
    {
        // Exact matching needs no escaping: a '%' in a policy is a literal '%'.
        var e = QueryPolicyExpansion.Expand(new[] { @"space:100%_a:content:true:bob" });
        e.ExactTokens.ShouldBe(new[] { @"space:100%_a:content:true:bob" });

        QueryPolicyExpansion.ToLikePattern(@"sp:a_b:100%\x:true:*")
            .ShouldBe(@"sp:a\_b:100\%\\x:true:%");
    }

    // ── Old predicate vs new predicate, over real row policies ────────────

    [Fact]
    public void Expansion_Matches_Legacy_Like_Semantics()
    {
        var rows = RowPolicyCorpus().ToList();
        rows.ShouldNotBeEmpty();

        var checkedPairs = 0;
        foreach (var policySet in UserPolicyCorpus())
        {
            var expansion = QueryPolicyExpansion.Expand(policySet);

            foreach (var rowPolicies in rows)
            {
                var legacy = LegacyMatches(policySet, rowPolicies);
                var rewritten = RewrittenMatches(expansion, rowPolicies);

                rewritten.ShouldBe(legacy,
                    $"visibility changed for policies [{string.Join(", ", policySet)}] "
                    + $"against row [{string.Join(", ", rowPolicies)}]");
                checkedPairs++;
            }
        }

        // Guard against the corpus silently collapsing to nothing.
        checkedPairs.ShouldBeGreaterThan(1000);
    }

    [Fact]
    public void Real_Policy_Shapes_Never_Need_The_Like_Fallback()
    {
        foreach (var policySet in UserPolicyCorpus())
        {
            QueryPolicyExpansion.Expand(policySet).LikePatterns.ShouldBeEmpty(
                $"[{string.Join(", ", policySet)}] is a shape BuildUserQueryPolicies emits; "
                + "it must reach the indexable path, not the LIKE fallback.");
        }
    }

    [Fact]
    public void Generated_Row_Policies_Always_Carry_The_Owner_Unscoped_Literal()
    {
        // The "{key}:true:*" → "{key}:true" rewrite is only loss-free because
        // every row carries the owner-unscoped literal, including rows that
        // have an owner_group (which used to replace it).
        var withGroup = Dmart.Utils.QueryPolicies.Generate(
            spaceName: "space", subpath: "/items", resourceType: "content", isActive: true,
            ownerShortname: "alice", ownerGroupShortname: "editors", entryShortname: null);

        withGroup.ShouldContain("space:items:content:true");
        withGroup.ShouldContain("space:items:content:true:alice");
        withGroup.ShouldContain("space:items:content:true:editors");
    }

    // ── Scope coverage (the tautology skip) ───────────────────────────────

    [Fact]
    public void CoversScope_True_For_Unconditioned_Wildcard_Policy()
    {
        QueryPolicyExpansion.CoversScope(
            new[] { "space::*:*" }, "space", "/items", null).ShouldBeTrue();
    }

    [Fact]
    public void CoversScope_True_For_Ancestor_Subpath()
    {
        QueryPolicyExpansion.CoversScope(
            new[] { "space:items:*:*" }, "space", "/items/nested/deep", null).ShouldBeTrue();
    }

    [Theory]
    // Conditioned policies are never total: ":true:*" excludes inactive rows,
    // ":true:alice" excludes other owners.
    [InlineData("space:items:*:true:*")]
    [InlineData("space:items:*:true:alice")]
    // Wrong space.
    [InlineData("other:items:*:*")]
    // Sibling / descendant subpath — not an ancestor of the queried one.
    [InlineData("space:other:*:*")]
    [InlineData("space:items/deeper:*:*")]
    // A prefix that is not a path ancestor ("item" does not cover "items").
    [InlineData("space:item:*:*")]
    public void CoversScope_False_For_NonTotal_Policies(string policy)
    {
        QueryPolicyExpansion.CoversScope(
            new[] { policy }, "space", "/items", null).ShouldBeFalse();
    }

    [Fact]
    public void CoversScope_Needs_Every_Requested_ResourceType()
    {
        var policies = new[] { "space:items:content:*", "space:items:folder:*" };

        // Unfiltered query: only a '*' resource-type policy can be total.
        QueryPolicyExpansion.CoversScope(policies, "space", "/items", null).ShouldBeFalse();
        // Every requested type covered.
        QueryPolicyExpansion.CoversScope(
            policies, "space", "/items", new[] { "content", "folder" }).ShouldBeTrue();
        // One requested type not covered.
        QueryPolicyExpansion.CoversScope(
            policies, "space", "/items", new[] { "content", "ticket" }).ShouldBeFalse();
    }

    [Fact]
    public void CoversScope_True_When_Every_ResourceType_Is_Listed()
    {
        // The stock super_manager permission enumerates all resource types
        // rather than leaving resource_types empty, so it never produces a '*'
        // policy — the coverage check has to recognise the enumerated form.
        var policies = QueryPolicyExpansion.ResourceTypes
            .Select(rt => $"space:items:{rt}:*").ToList();

        QueryPolicyExpansion.CoversScope(policies, "space", "/items", null).ShouldBeTrue();

        // Drop one type and an unfiltered query is no longer fully covered.
        QueryPolicyExpansion.CoversScope(
            policies.Where(p => !p.Contains(":ticket:", StringComparison.Ordinal)).ToList(),
            "space", "/items", null).ShouldBeFalse();
    }

    [Fact]
    public void CoversScope_False_For_Empty_Or_Missing_Input()
    {
        QueryPolicyExpansion.CoversScope(null, "space", "/items", null).ShouldBeFalse();
        QueryPolicyExpansion.CoversScope(
            Array.Empty<string>(), "space", "/items", null).ShouldBeFalse();
        QueryPolicyExpansion.CoversScope(
            new[] { "space:items:*:*" }, null, "/items", null).ShouldBeFalse();
    }

    [Fact]
    public void CoversScope_Only_Claims_Coverage_It_Really_Has()
    {
        // The safety property: whenever the skip fires, the predicate it
        // replaced must have matched EVERY row in the queried scope. Anything
        // else is a widening.
        var claimed = 0;
        foreach (var policySet in UserPolicyCorpus().Concat(TotalPolicyCorpus()))
        {
            var expansion = QueryPolicyExpansion.Expand(policySet);

            foreach (var (space, subpath) in new[]
                     { ("space", "/"), ("space", "/items"), ("other", "/items/nested") })
            {
                if (!QueryPolicyExpansion.CoversScope(policySet, space, subpath, null)) continue;
                claimed++;

                foreach (var rowPolicies in RowsInScope(space, subpath))
                {
                    RewrittenMatches(expansion, rowPolicies).ShouldBeTrue(
                        $"skip claimed for [{string.Join(", ", policySet)}] over {space}{subpath}, "
                        + $"but the filter would NOT have matched row [{string.Join(", ", rowPolicies)}]");
                }
            }
        }

        // The corpus must actually exercise the true branch.
        claimed.ShouldBeGreaterThan(0);
    }

    // ── Corpora ───────────────────────────────────────────────────────────

    // Row-side policy arrays, produced by the real generator so the test
    // tracks it rather than a copy of it.
    private static IEnumerable<List<string>> RowPolicyCorpus()
    {
        foreach (var space in new[] { "space", "other" })
            foreach (var subpath in new[] { "/", "/items", "/items/nested", "/a/b/c" })
                foreach (var rt in new[] { "content", "folder", "ticket" })
                    foreach (var isActive in new[] { true, false })
                        foreach (var owner in new[] { "alice", "bob" })
                            foreach (var group in new string?[] { null, "editors" })
                                yield return Dmart.Utils.QueryPolicies.Generate(
                                    spaceName: space, subpath: subpath, resourceType: rt,
                                    isActive: isActive, ownerShortname: owner,
                                    ownerGroupShortname: group, entryShortname: null);
    }

    // Caller-side policy lists, in the five shapes BuildUserQueryPolicies
    // emits: own+is_active (per group), is_active only, own only, and
    // unconditioned — with the resource type either named or '*'.
    private static IEnumerable<List<string>> UserPolicyCorpus()
    {
        foreach (var space in new[] { "space", "other" })
            foreach (var subpath in new[] { "", "items", "items/nested", "a" })
                foreach (var rt in new[] { "content", "folder", "*" })
                {
                    var key = $"{space}:{subpath}:{rt}";
                    yield return new List<string> { $"{key}:true:alice", $"{key}:true:editors" };
                    yield return new List<string> { $"{key}:true:*" };
                    yield return new List<string> { $"{key}:true:alice", $"{key}:false:alice" };
                    yield return new List<string> { $"{key}:*" };
                }
    }

    // Unconditioned policy sets — the shape BuildUserQueryPolicies emits for a
    // permission with no `own` / `is_active` condition, i.e. the ones the skip
    // is meant to fire on.
    private static IEnumerable<List<string>> TotalPolicyCorpus()
    {
        foreach (var space in new[] { "space", "other" })
            foreach (var subpath in new[] { "", "items" })
                foreach (var rt in new[] { "content", "*" })
                    yield return new List<string> { $"{space}:{subpath}:{rt}:*" };
    }

    // Every row the query at (space, subpath) could return: the subtree rooted
    // there, across resource types, is_active values, owners and groups.
    private static IEnumerable<List<string>> RowsInScope(string space, string subpath)
    {
        var root = subpath.Trim('/');
        var subpaths = root.Length == 0
            ? new[] { "/", "/items", "/items/nested" }
            : new[] { "/" + root, "/" + root + "/child" };

        foreach (var sp in subpaths)
            foreach (var rt in new[] { "content", "folder", "ticket" })
                foreach (var isActive in new[] { true, false })
                    foreach (var owner in new[] { "alice", "bob" })
                        foreach (var group in new string?[] { null, "editors" })
                            yield return Dmart.Utils.QueryPolicies.Generate(
                                spaceName: space, subpath: sp, resourceType: rt,
                                isActive: isActive, ownerShortname: owner,
                                ownerGroupShortname: group, entryShortname: null);
    }

    // ── The two predicates ────────────────────────────────────────────────

    private static bool LegacyMatches(IReadOnlyList<string> policies, IReadOnlyList<string> rowPolicies)
        => policies.Any(p =>
        {
            var rx = LikeToRegex(QueryPolicyExpansion.ToLikePattern(p));
            return rowPolicies.Any(r => rx.IsMatch(r));
        });

    private static bool RewrittenMatches(
        QueryPolicyExpansion.Expansion expansion, IReadOnlyList<string> rowPolicies)
    {
        if (expansion.ExactTokens.Intersect(rowPolicies, StringComparer.Ordinal).Any()) return true;
        return expansion.LikePatterns.Any(p =>
        {
            var rx = LikeToRegex(QueryPolicyExpansion.ToLikePattern(p));
            return rowPolicies.Any(r => rx.IsMatch(r));
        });
    }

    // SQL LIKE with ESCAPE '\', case-sensitive — what both dialects emit.
    private static Regex LikeToRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length) sb.Append(Regex.Escape(pattern[++i].ToString()));
            else if (c == '%') sb.Append(".*");
            else if (c == '_') sb.Append('.');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.None);
    }
}
