using System.Text;
using System.Text.RegularExpressions;
using Dmart.DataAdapters.Sql;
using Dmart.QueryGrammar;
using Dmart.SqlAdapter.Permissions;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// Guards the three seams the read-time ACL rewrite made load-bearing:
//
//  1. the set of tables carrying query_policies, because a table with the
//     column but missing from the CLI backfill lists is unreachable by the
//     migration and silently returns zero rows to wildcarded policies;
//  2. ISqlDialect.ArrayOverlapAny having a default body, because
//     Dmart.QueryGrammar ships as a package and a bare abstract member breaks
//     every third-party dialect at compile time;
//  3. PermissionFilter.Append keeping its five-parameter arity, because
//     optional-argument values are baked into the *caller's* IL.
public class QueryPolicyMigrationSurfaceTests
{
    // Lockstep with the `tables` array in Program.cs's fix_query_policies and
    // the --all-tables list in update_query_policies.
    private static readonly string[] ExpectedPolicyTables =
        ["entries", "users", "roles", "groups", "permissions", "spaces"];

    [Fact]
    public void Every_Table_With_QueryPolicies_Is_Covered_By_The_Backfill_Commands()
    {
        var declared = new List<string>();
        foreach (Match m in Regex.Matches(
            SqlSchema.CreateAll,
            @"CREATE TABLE(?:\s+IF NOT EXISTS)?\s+""?(?<name>\w+)""?\s*\((?<body>.*?)\n\s*\);",
            RegexOptions.Singleline))
        {
            if (m.Groups["body"].Value.Contains("query_policies", StringComparison.Ordinal))
                declared.Add(m.Groups["name"].Value);
        }

        declared.ShouldNotBeEmpty("the schema regex stopped matching CREATE TABLE blocks");

        // AppendAclFilter skips only attachments/histories, so every other
        // table carrying the column is filtered at read time and must be
        // repairable by the CLI.
        declared.OrderBy(t => t, StringComparer.Ordinal).ShouldBe(
            ExpectedPolicyTables.OrderBy(t => t, StringComparer.Ordinal),
            "a table carrying query_policies is missing from (or absent in) the "
            + "fix_query_policies / update_query_policies --all-tables lists");
    }

    // update_query_policies pages with a KEYSET cursor over
    // (shortname, space_name, subpath), resuming each batch from the last row
    // of the previous one. That is only correct if the tuple is UNIQUE per
    // table: a duplicate makes `> (a, b, c)` skip every row sharing the tuple,
    // and the migration silently under-migrates rather than failing.
    //
    // It also has to be an actual index in THAT COLUMN ORDER, not just three
    // columns that happen to be unique together. The cursor's companion
    // `ORDER BY shortname, space_name, subpath` (Program.cs) can only walk the
    // index if the index leads with the same columns in the same sequence;
    // UNIQUE (space_name, subpath, shortname) is an equally valid uniqueness
    // constraint and would send every batch back to sorting the whole table —
    // the quadratic behaviour the keyset form exists to remove. So the order is
    // asserted as written, not sorted first.
    //
    // Both schemas: update_query_policies runs against SQLite too, and the two
    // DDL files are maintained separately, so a constraint can drift in one
    // without the other noticing.
    [Theory]
    [InlineData("postgresql")]
    [InlineData("sqlite")]
    public void Every_Backfilled_Table_Has_The_Unique_Key_The_Keyset_Cursor_Needs(string backend)
    {
        var ddl = backend == "sqlite" ? SqliteSchema.CreateAll : SqlSchema.CreateAll;
        foreach (var table in ExpectedPolicyTables)
        {
            var m = Regex.Match(
                ddl,
                @"CREATE TABLE(?:\s+IF NOT EXISTS)?\s+""?" + table + @"""?\s*\((?<body>.*?)\n\s*\);",
                RegexOptions.Singleline);
            m.Success.ShouldBeTrue($"no CREATE TABLE found for '{table}' in the {backend} schema");

            var unique = Regex.Matches(m.Groups["body"].Value, @"UNIQUE\s*\(([^)]*)\)")
                .Select(u => u.Groups[1].Value
                    .Split(',')
                    .Select(c => c.Trim().Trim('"'))
                    .ToArray())
                .Any(cols => cols.SequenceEqual(new[] { "shortname", "space_name", "subpath" }));

            unique.ShouldBeTrue(
                $"{backend}: '{table}' has no UNIQUE (shortname, space_name, subpath) in that "
                + "order — update_query_policies' keyset cursor would skip rows sharing a "
                + "duplicate tuple, and its ORDER BY could not walk the index");
        }
    }

    [Fact]
    public void ArrayOverlapAny_Is_Not_Abstract_So_ThirdParty_Dialects_Still_Compile()
    {
        // A default interface method is non-abstract. Making this member
        // abstract again is a source-breaking change for every external
        // ISqlDialect implementation.
        var m = typeof(ISqlDialect).GetMethod(nameof(ISqlDialect.ArrayOverlapAny));
        m.ShouldNotBeNull();
        m.IsAbstract.ShouldBeFalse(
            "ArrayOverlapAny must keep a default implementation — Dmart.QueryGrammar is packaged");
    }

    [Theory]
    // '*' must survive: these are already-expanded exact tokens, and the
    // overriding dialects compare them by equality.
    [InlineData("a*b", @"a*b")]
    [InlineData("100%", @"100\%")]
    [InlineData("a_b", @"a\_b")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData("space:sub:content:true", "space:sub:content:true")]
    public void ToLiteralLikePattern_Escapes_Without_Widening(string token, string expected)
        => QueryPolicyExpansion.ToLiteralLikePattern(token).ShouldBe(expected);

    [Fact]
    public void ToLiteralLikePattern_Differs_From_ToLikePattern_Only_On_Star()
    {
        QueryPolicyExpansion.ToLikePattern("x:*").ShouldBe("x:%");
        QueryPolicyExpansion.ToLiteralLikePattern("x:*").ShouldBe("x:*");
    }

    [Fact]
    public void Append_Keeps_Its_FiveParameter_Arity()
    {
        // This is the signature an assembly compiled against the pre-scope
        // version emits a call to; without the overload it throws
        // MissingMethodException at run time while still compiling from source.
        var overload = typeof(PermissionFilter).GetMethod(
            nameof(PermissionFilter.Append),
            [typeof(StringBuilder), typeof(List<NpgsqlParameter>), typeof(string),
             typeof(string), typeof(List<string>)]);

        overload.ShouldNotBeNull(
            "the five-parameter Append overload is load-bearing for binary compatibility");

        var sql = new StringBuilder("space_name = @space");
        overload.Invoke(null, [sql, new List<NpgsqlParameter>(), "alice", "entries",
            new List<string> { "x:y:content:true" }]);

        // Identical to the widened form called with no scope.
        var expected = new StringBuilder("space_name = @space");
        PermissionFilter.Append(expected, [], "alice", "entries",
            new List<string> { "x:y:content:true" }, null, null, null);

        sql.ToString().ShouldBe(expected.ToString());
    }
}
