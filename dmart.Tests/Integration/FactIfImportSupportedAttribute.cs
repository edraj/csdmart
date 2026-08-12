using Xunit;

namespace Dmart.Tests.Integration;

// Marks a fact that exercises `dmart import` — the bulk path that rebuilds the
// SQL store from the flat files under SPACES_FOLDER.
//
// It runs on BOTH drivers. PostgreSQL uses binary COPY through a reconnecting
// import session; SQLite routes to the per-row repository path (Bulk is null in
// ImportExportService). What remains driver-scoped is only the load OPTIONS —
// --fast, --drop-indexes, --fast-parallelism — which the service refuses on
// SQLite with a reason; the tests that need them carry FactIfFastImport.
//
// The attribute is kept rather than deleted so the import tests stay findable
// as a group, and so the "no database configured" gate has one home.
public sealed class FactIfImportSupportedAttribute : FactAttribute
{
    public FactIfImportSupportedAttribute() => Skip = Reason();

    // Also covers the "no database configured" case these call sites got from
    // FactIfPg, which this attribute replaces.
    internal static string? Reason()
    {
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
