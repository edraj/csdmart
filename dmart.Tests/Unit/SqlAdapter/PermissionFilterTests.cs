using System.Text;
using Dmart.SqlAdapter.Permissions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.SqlAdapter;

// Pure-string tests for the ACL filter — no DB. Verifies skip-list behaviour,
// no-op cases, the owner / ACL-containment shape, that enumerable policies
// reach the indexable `&&` test, and that LIKE-special chars are still escaped
// under `ESCAPE '\'` on the patterns that fall back.
public class PermissionFilterTests
{
    [Theory]
    [InlineData("attachments")]
    [InlineData("histories")]
    public void Append_Skips_Excluded_Tables(string tableName)
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, "alice", tableName, new List<string> { "*" });

        sql.ToString().ShouldBe("space_name = @space");  // unchanged
        pars.Count.ShouldBe(0);                          // no params added
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Append_Skips_When_Actor_Missing(string? actor)
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, actor, "entries", new List<string> { "*" });

        sql.ToString().ShouldBe("space_name = @space");
        pars.Count.ShouldBe(0);
    }

    [Fact]
    public void Append_With_No_Policies_Emits_Owner_And_Acl_Clauses()
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, "alice", "entries", queryPolicies: null);

        var emitted = sql.ToString();
        emitted.ShouldContain("AND (");
        emitted.ShouldContain("owner_shortname = @perm_actor");
        // Containment, not a jsonb_array_elements subplan — idx_entries_acl_gin
        // can only serve the former.
        emitted.ShouldContain("acl @> jsonb_build_array(jsonb_build_object(");
        emitted.ShouldContain("'user_shortname', @perm_actor");
        emitted.ShouldContain("'allowed_actions', jsonb_build_array('query')");
        emitted.ShouldNotContain("jsonb_array_elements");
        // One @perm_actor only; no policy params.
        pars.Count.ShouldBe(1);
        pars[0].ParameterName.ShouldBe("@perm_actor");
        pars[0].Value.ShouldBe("alice");
    }

    [Fact]
    public void Append_Emits_Array_Overlap_For_Enumerable_Policies()
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, "alice", "entries",
            new List<string> { "myspace:foo:content:true:*", "myspace:bar:content:*" });

        var emitted = sql.ToString();
        emitted.ShouldContain("query_policies && ARRAY[");
        emitted.ShouldNotContain("unnest(query_policies)");
        // "…:true:*" → one token, "…:content:*" → true + false.
        emitted.ShouldContain("@perm_qp0");
        emitted.ShouldContain("@perm_qp2");
        pars.Select(x => x.Value).ShouldBe(new object[]
        {
            "alice",
            "myspace:foo:content:true",
            "myspace:bar:content:true",
            "myspace:bar:content:false",
        });
    }

    [Fact]
    public void Append_Includes_Policy_Like_Conditions_With_Escape_Clause()
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        // A '*' in the space segment is not enumerable, so these keep the
        // original per-row LIKE test rather than being narrowed to a guess.
        PermissionFilter.Append(sql, pars, "alice", "entries",
            new List<string> { "*:foo:content:true:bob", "*:bar:content:true:bob" });

        var emitted = sql.ToString();
        emitted.ShouldContain("unnest(query_policies)");
        emitted.ShouldNotContain("query_policies && ARRAY[");
        emitted.ShouldContain("@perm_qplike0");
        emitted.ShouldContain("@perm_qplike1");
        emitted.ShouldContain("ESCAPE '\\'");
        // 1 actor + 2 policies.
        pars.Count.ShouldBe(3);
    }

    [Fact]
    public void Append_Combines_Both_Tests_When_Policies_Are_Mixed()
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, "alice", "entries",
            new List<string> { "myspace:foo:content:true:*", "*:bar:content:true:bob" });

        var emitted = sql.ToString();
        emitted.ShouldContain("query_policies && ARRAY[@perm_qp0]::text[]");
        emitted.ShouldContain("unnest(query_policies)");
        emitted.ShouldContain("@perm_qplike0");
    }

    [Fact]
    public void Append_Escapes_Like_Metacharacters_In_Policy_Patterns()
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        // Pattern that contains every metachar we care about: % and _ must
        // become \% and \_ ; * must expand to %; \ must escape itself first.
        // The partial '*' in the resource-type segment keeps this on the LIKE
        // path, which is where escaping applies.
        PermissionFilter.Append(sql, pars, "alice", "entries",
            new List<string> { @"my%space:bar_baz:*\thing:*:*" });

        // Find the policy parameter (after @perm_actor).
        var policyParam = pars.Single(p => p.ParameterName == "@perm_qplike0");
        var pattern = (string)policyParam.Value!;

        // Order: \ escaped first, then % and _, then * → %.
        pattern.ShouldBe(@"my\%space:bar\_baz:%\\thing:%:%");
    }

    [Fact]
    public void Append_With_Empty_Policy_List_Still_Emits_Owner_And_Acl_Clauses()
    {
        var sql = new StringBuilder("space_name = @space");
        var pars = new List<NpgsqlParameter>();

        PermissionFilter.Append(sql, pars, "alice", "entries", new List<string>());

        var emitted = sql.ToString();
        emitted.ShouldContain("owner_shortname = @perm_actor");
        emitted.ShouldNotContain("unnest(query_policies)");
        pars.Count.ShouldBe(1);
    }
}
