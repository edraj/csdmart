using Xunit;

namespace Dmart.Tests.Integration;

// Marks a fact that asserts PostgreSQL behaviour BY DESIGN — an EXPLAIN plan,
// a server notice, a GIN index — rather than application behaviour that happens
// to be exercised through PostgreSQL.
//
// The distinction matters. A test that merely fails on SQLite because a code
// path is unconverted must stay failing, so the work stays visible; converting
// it is the fix. A test whose whole purpose is to pin PostgreSQL's planner or
// its extension behaviour has nothing to assert on SQLite, and weakening it to
// run on both would destroy the property it exists to protect.
//
// These still run on PostgreSQL, which is the point: the assertions keep their
// full strength on the tier they describe.
public sealed class FactIfPostgresOnlyAttribute : FactAttribute
{
    public FactIfPostgresOnlyAttribute() => Skip = Reason();

    // Also covers the "no database configured" case these call sites got from
    // FactIfPg, which this attribute replaces.
    internal static string? Reason()
    {
        if (DmartFactory.UseSqlite)
            return "asserts PostgreSQL-specific behaviour (query plan, server notice "
                 + "or extension index) that has no SQLite equivalent — see "
                 + "docs/sqlite-backend-audit.md §9";
        if (!DmartFactory.HasPg)
            return "PostgreSQL not configured (set DMART_TEST_PG_CONN or create a config.env)";
        return null;
    }
}

// Theory counterpart, same rule.
public sealed class TheoryIfPostgresOnlyAttribute : TheoryAttribute
{
    public TheoryIfPostgresOnlyAttribute() => Skip = FactIfPostgresOnlyAttribute.Reason();
}
