using System.Collections.Generic;
using System.Linq;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.QueryGrammar;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.SqlAdapter;

// How the search grammar behaves when it is carrying AUTHORIZATION, not just
// user intent. Two distinct mechanisms meet in `Query.Search`:
//
//   1. RBAC-adjacent PREDICATES a caller can write — @roles / @groups /
//      @query_policies / @owner_shortname. These are ordinary filters over
//      permission-bearing columns; they narrow, they never grant.
//
//   2. The row-level ACL that dmart INJECTS — QueryService.MergeFilterFieldsValues
//      appends each matching permission's `filter_fields_values` clause to the
//      caller's own search string, then the whole string is parsed as one
//      expression. Because the injection is textual, the caller's search and
//      the permission clause share a grammar — and boolean precedence decides
//      whether the permission clause actually constrains the caller.
//
// SearchExpressionParserTests pins the emission of (1) in isolation; the tests
// here pin the COMPOSITION in (2), which is where an authorization bug would
// live. QuerySearchPermissionsTests exercises the same ground against a real
// database with real roles.
public class SearchPermissionCompositionTests
{
    private static int Occurrences(string haystack, string needle)
        => System.Text.RegularExpressions.Regex
            .Matches(haystack, System.Text.RegularExpressions.Regex.Escape(needle)).Count;

    // Whether the emitted clause splits into ALTERNATIVES at its outermost
    // level — the only structural question that matters for authorization: an
    // outermost OR means some rows can satisfy one operand while ignoring the
    // other, so a permission clause sitting in just one operand does not
    // constrain the page.
    //
    // A naive `Contains(") OR (")` can't answer this — value alternation
    // (`@k:a|b`) and the jsonb containment shape both emit nested ORs several
    // levels down. So peel the single wrapper EmitJoin/EmitLeaf always adds
    // and scan at depth 0. All parens in emitted SQL are balanced (values are
    // bound parameters, never inlined literals), so depth counting is exact.
    private static (string Peeled, int OrIndex) TopLevelOr(string sql)
    {
        var peeled = Peel(sql);
        var depth = 0;
        for (var i = 0; i < peeled.Length; i++)
        {
            if (peeled[i] == '(') depth++;
            else if (peeled[i] == ')') depth--;
            else if (depth == 0 && peeled.AsSpan(i).StartsWith(" OR ")) return (peeled, i);
        }
        return (peeled, -1);
    }

    // Strips one enclosing paren pair when it wraps the ENTIRE string.
    private static string Peel(string sql)
    {
        if (sql.Length < 2 || sql[0] != '(') return sql;
        var depth = 0;
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '(') depth++;
            else if (sql[i] == ')' && --depth == 0)
                return i == sql.Length - 1 ? sql[1..^1] : sql;
        }
        return sql;
    }

    private static Dictionary<string, object> Perms(params (string Key, string Ffv)[] entries)
    {
        var result = new Dictionary<string, object>();
        foreach (var (key, ffv) in entries)
            result[key] = new Dictionary<string, object>
            {
                ["allowed_actions"] = new List<string> { "query", "view" },
                ["conditions"] = new List<string>(),
                ["restricted_fields"] = new List<string>(),
                ["allowed_fields_values"] = new Dictionary<string, object>(),
                ["filter_fields_values"] = ffv,
            };
        return result;
    }

    private static Query Q(string? search) => new()
    {
        Type = QueryType.Subpath,
        SpaceName = "acme",
        Subpath = "/users",
        Search = search,
    };

    // Runs a caller's search through the FFV merge and then through the
    // parser — the exact pipeline a /managed/query request follows.
    private static string MergedSql(string? callerSearch, string ffv = "@payload.body.dept:sales")
    {
        var merged = QueryService.MergeFilterFieldsValues(
            Q(callerSearch),
            new List<string> { "acme:users:content:true:*" },
            Perms(("acme:users:content", ffv)));
        return string.Join(" ", SearchExpressionParser.Parse(merged.Search ?? "", 0).Clauses);
    }

    // ── (1) Permission-bearing predicates ─────────────────────────────────

    [Fact]
    public void Roles_Predicate_Filters_Rows_It_Does_Not_Grant_Anything()
    {
        // `@roles:super_admin` is a filter over the roles column of the rows
        // being listed — it is NOT a claim about the caller. It must emit a
        // plain containment predicate with no reference to the actor.
        var combined = string.Join(" ", SearchExpressionParser.Parse("@roles:super_admin", 0).Clauses);

        combined.ShouldContain("roles @>");
        combined.ShouldNotContain("owner_shortname");
        combined.ShouldNotContain("acl");
        combined.ShouldNotContain("query_policies");
    }

    [Fact]
    public void Query_Policies_Predicate_Cannot_Reach_Outside_The_Unnest()
    {
        // Searching @query_policies inspects the row's stored policy strings.
        // The value is bound, never interpolated, so a caller cannot inject a
        // second predicate through it.
        var parsed = SearchExpressionParser.Parse(
            "@query_policies:acme:users:content:true:*", 0);

        parsed.Clauses.ShouldHaveSingleItem();
        parsed.Clauses[0].ShouldStartWith("(EXISTS (SELECT 1 FROM unnest(query_policies)");
        parsed.Parameters.ShouldHaveSingleItem();
        parsed.Parameters[0].Value.ShouldBe("acme:users:content:true:%");
    }

    [Fact]
    public void Owner_Predicate_Is_A_Bound_Parameter_Not_Interpolated_Sql()
    {
        // The classic injection probe. Everything after the colon is one
        // token and lands in a parameter verbatim; no fragment of it reaches
        // the SQL text.
        var parsed = SearchExpressionParser.Parse("@owner_shortname:alice'--", 0);

        parsed.Clauses.ShouldHaveSingleItem();
        parsed.Clauses[0].ShouldContain("owner_shortname::text = @s_0");
        parsed.Clauses[0].ShouldNotContain("alice");   // no fragment reaches the SQL text
        parsed.Parameters.ShouldHaveSingleItem();
        parsed.Parameters[0].Value.ShouldBe("alice'--");
    }

    [Fact]
    public void Free_Text_Term_Cannot_Introduce_A_Field_Selector()
    {
        // A bare word never becomes a selector no matter what it contains — it
        // fans out over the five text columns as ONE bound ILIKE pattern.
        // (Whitespace would split this into several terms, so the probe is a
        // single token.)
        var injected = SearchExpressionParser.Parse("%'--", 0);

        injected.Clauses.ShouldHaveSingleItem();
        injected.Clauses[0].ShouldContain("shortname ILIKE @s_0");
        injected.Clauses[0].ShouldNotContain("--");
        injected.Parameters.ShouldHaveSingleItem();
        injected.Parameters[0].Value.ShouldBe("%%'--%");
        Occurrences(injected.Clauses[0], "@s_0").ShouldBe(5);   // five columns, one param
    }

    // ── (2) filter_fields_values composition ──────────────────────────────

    [Fact]
    public void Ffv_With_No_Caller_Search_Ands_The_Whole_Permission_Clause()
    {
        var sql = MergedSql(null);

        sql.ShouldContain("space_name::text = @s_0");
        sql.ShouldContain("subpath::text = @s_1");
        sql.ShouldContain("resource_type::text = @s_2");
        sql.ShouldContain("payload::jsonb @>");          // the FFV body
        TopLevelOr(sql).OrIndex.ShouldBe(-1);            // never an alternative
    }

    [Fact]
    public void Ffv_Ands_Onto_A_Plain_Caller_Search()
    {
        // The common case: caller filters, permission narrows further. Every
        // top-level join must be AND so neither side can be satisfied alone.
        var sql = MergedSql("@payload.body.k:v");

        sql.ShouldContain(" AND (space_name::text = ");
        sql.ShouldContain(" AND subpath::text = ");
        sql.ShouldContain(" AND resource_type::text = ");
        TopLevelOr(sql).OrIndex.ShouldBe(-1);
    }

    [Fact]
    public void Ffv_Survives_A_Caller_Search_That_Is_A_Paren_Group()
    {
        // `(A) B` is AND since 2026-06-20, so a parenthesised caller search
        // still has the permission clause AND'd onto it.
        var sql = MergedSql("(@payload.body.k:v)");

        sql.ShouldContain(" AND ");
        sql.ShouldContain("space_name::text");
        // The caller's group is the left operand of an AND, not an OR branch.
        var andIdx = sql.IndexOf(" AND ", System.StringComparison.Ordinal);
        sql.IndexOf("space_name::text", System.StringComparison.Ordinal)
            .ShouldBeGreaterThan(andIdx);
    }

    [Fact]
    public void Ffv_Applies_To_Every_Branch_Of_A_Caller_Alternation()
    {
        // Value-level alternation (`|`) stays INSIDE one selector, so the
        // permission clause still AND's onto the whole thing — unlike the
        // `or` keyword (see the known-gap test below).
        var sql = MergedSql("@payload.body.k:v|w");

        sql.ShouldContain(" AND (space_name::text = ");
        Occurrences(sql, " OR ").ShouldBeGreaterThan(0);   // the alternation itself
        TopLevelOr(sql).OrIndex.ShouldBe(-1);              // but no top-level split
    }

    [Fact]
    public void Ffv_Body_Is_Deduped_But_Still_Present_Once()
    {
        // DedupeSearchTokens drops repeated tokens. A caller who pre-types
        // the permission's own clause must not cause it to be dropped from
        // the merged expression.
        var sql = MergedSql("@payload.body.dept:sales");

        sql.ShouldContain("payload::jsonb @>");
        sql.ShouldContain("space_name::text = ");
    }

    [Fact]
    public void Or_Keyword_In_Caller_Search_No_Longer_Escapes_The_Ffv_Clause()
    {
        // REGRESSION GUARD — was a characterization test for a High-severity
        // gap, inverted once MergeFilterFieldsValues began parenthesising the
        // caller's search.
        //
        // The permission clause is appended as bare tokens, and AND binds
        // tighter than OR, so concatenating raw let a caller-supplied `or`
        // split the expression and strand the permission clause on the RIGHT
        // branch only:
        //
        //     (k=v)  OR  (k=w AND space=acme AND … AND dept=sales)
        //      ^^^^^ reachable without satisfying the permission clause
        //
        // Wrapping the caller's search makes the OR a nested operand instead:
        //
        //     ((k=v) OR (k=w))  AND  (space=acme AND … AND dept=sales)
        var sql = MergedSql("@payload.body.k:v or @payload.body.k:w");
        var (peeled, orIdx) = TopLevelOr(sql);

        // No top-level split: the OR is inside the left operand of an AND.
        orIdx.ShouldBe(-1);
        // The permission clause is present exactly once, governing everything.
        sql.ShouldContain("space_name::text");
        peeled.ShouldContain(" AND (space_name::text = ");
    }

    [Fact]
    public void Stray_Close_Paren_No_Longer_Splits_The_Ffv_Clause()
    {
        // REGRESSION GUARD — the half-fix detector. Wrapping the caller's
        // search is not enough on its own: a stray ')' closes the wrapper
        // early, so `(@k:v) or @k:w)` puts the `or` back at top level and the
        // permission clause back on one branch. BalanceParens drops the
        // unmatched closer first — the parser discarded it as noise anyway —
        // so the group survives and the AND holds.
        var sql = MergedSql("@payload.body.k:v) or @payload.body.k:w");

        TopLevelOr(sql).OrIndex.ShouldBe(-1);
        sql.ShouldContain(" AND (space_name::text = ");
    }

    [Fact]
    public void Unclosed_Open_Paren_Does_Not_Swallow_The_Ffv_Clause()
    {
        // The mirror case: an unterminated group would otherwise extend to the
        // end of the expression and take the permission tokens inside it,
        // where a caller `or` could still reach around them.
        var sql = MergedSql("(@payload.body.k:v or @payload.body.k:w");

        TopLevelOr(sql).OrIndex.ShouldBe(-1);
        sql.ShouldContain(" AND (space_name::text = ");
    }

    [Fact]
    public void BalanceParens_Drops_Stray_Closers_And_Completes_Open_Groups()
    {
        QueryService.BalanceParens("@k:v) or @k:w").ShouldBe("@k:v or @k:w");
        QueryService.BalanceParens("(@k:v or @k:w").ShouldBe("(@k:v or @k:w)");
        QueryService.BalanceParens("(@a:1) (@b:2)").ShouldBe("(@a:1) (@b:2)");
        QueryService.BalanceParens(")))").ShouldBe("");
    }

    [Fact]
    public void Ffv_From_Multiple_Permissions_Ors_The_Keys_But_Ands_The_Bodies()
    {
        // Two grants over the same subpath: the space/subpath/resource_type
        // triples alternate (`a|b`), while each distinct FFV body is AND'd —
        // holding two grants must not let either body be skipped.
        var merged = QueryService.MergeFilterFieldsValues(
            Q(null),
            new List<string> { "acme:users:content:*", "acme:users:folder:*" },
            Perms(("acme:users:content", "@payload.body.dept:sales"),
                  ("acme:users:folder", "@payload.body.region:emea")));

        merged.Search.ShouldNotBeNull();
        merged.Search.ShouldContain("@resource_type:content|folder");
        merged.Search.ShouldContain("@payload.body.dept:sales");
        merged.Search.ShouldContain("@payload.body.region:emea");

        var sql = string.Join(" ", SearchExpressionParser.Parse(merged.Search!, 0).Clauses);
        TopLevelOr(sql).OrIndex.ShouldBe(-1);   // one AND-run, no top-level split
    }

    [Fact]
    public void No_Matching_Permission_Leaves_The_Caller_Search_Untouched()
    {
        // FFV only narrows where a grant actually reaches. When no permission
        // key matches the resolved policies the search must pass through
        // unchanged — widening here would be a bug in the other direction
        // (the caller would be handed rows their grant never covered), but
        // the gate for that is the ACL clause, not this merge.
        var merged = QueryService.MergeFilterFieldsValues(
            Q("@payload.body.k:v"),
            new List<string> { "other_space:users:content:*" },
            Perms(("acme:users:content", "@payload.body.dept:sales")));

        merged.Search.ShouldBe("@payload.body.k:v");
    }

    [Fact]
    public void Permission_With_Empty_Ffv_Adds_No_Restriction()
    {
        var merged = QueryService.MergeFilterFieldsValues(
            Q("@payload.body.k:v"),
            new List<string> { "acme:users:content:*" },
            Perms(("acme:users:content", "")));

        merged.Search.ShouldBe("@payload.body.k:v");
    }

    [Fact]
    public void Ffv_Clause_Emits_A_Canonical_Leading_Slash_On_Subpath()
    {
        // Policies carry the subpath slash-stripped; permission rows usually
        // carry it with a slash. The merge re-normalises so the emitted
        // @subpath token always has exactly one leading slash — otherwise the
        // equality never matches and the restriction silently no-ops.
        var merged = QueryService.MergeFilterFieldsValues(
            Q(null),
            new List<string> { "acme:users:content:*" },
            Perms(("acme:/users:content", "@payload.body.dept:sales")));

        merged.Search.ShouldNotBeNull();
        merged.Search.ShouldContain("@subpath:/users");
        merged.Search.ShouldNotContain("@subpath://users");
    }

    [Fact]
    public void Over_Length_Merged_Expression_Fails_Closed_Not_Open()
    {
        // The merge happens BEFORE parsing, so a caller can push the combined
        // string past MaxExpressionLength. If the parser dropped the whole
        // expression the permission clause would vanish with it and the page
        // would widen; FALSE is the only safe answer.
        var huge = "@payload.body.k:" + new string('v', SearchExpressionParser.MaxExpressionLength);
        var sql = MergedSql(huge);

        sql.ShouldBe("FALSE");
    }
}
