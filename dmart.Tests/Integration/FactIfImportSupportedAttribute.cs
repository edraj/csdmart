using Xunit;

namespace Dmart.Tests.Integration;

// Marks a fact that exercises `dmart import` — the bulk path that rebuilds the
// SQL store from the flat files under SPACES_FOLDER.
//
// That path is PostgreSQL-only: it is built on binary COPY, the SQLSTATE-driven
// reconnecting import session, and GIN index drop/restore, none of which have a
// SQLite equivalent. Its SQLite replacement is the outstanding Phase 3 item —
// see docs/sqlite-reindex-handoff.md for the design and the tests it must carry.
//
// This is the ONLY skip the SQLite matrix carries, and it is deliberately
// narrow. A skip should record a decision that something cannot work on a tier,
// not absorb conversion work that simply is not finished — every other SQLite
// failure is left failing so it stays visible.
//
// Delete this attribute when the reindex path lands; these tests should then
// run on both drivers.
public sealed class FactIfImportSupportedAttribute : FactAttribute
{
    public FactIfImportSupportedAttribute() => Skip = Reason();

    // Also covers the "no database configured" case these call sites got from
    // FactIfPg, which this attribute replaces.
    internal static string? Reason()
    {
        if (DmartFactory.UseSqlite)
            return "dmart import is PostgreSQL-only (binary COPY + import session); "
                 + "the SQLite reindex path is not implemented yet — see "
                 + "docs/sqlite-reindex-handoff.md";
        if (!DmartFactory.HasPg)
            return "PostgreSQL not configured (set DMART_TEST_PG_CONN or create a config.env)";
        return null;
    }
}

// Theory counterpart, same rule.
public sealed class TheoryIfImportSupportedAttribute : TheoryAttribute
{
    public TheoryIfImportSupportedAttribute()
        => Skip = FactIfImportSupportedAttribute.Reason();
}
