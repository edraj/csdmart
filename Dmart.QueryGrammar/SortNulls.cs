namespace Dmart.QueryGrammar;

/// <summary>
/// Explicit NULL placement for every emitted sort key. The two engines disagree
/// by default — PostgreSQL treats NULL as the largest value (DESC → NULLs first,
/// ASC → NULLs last) while SQLite treats it as the smallest (the exact
/// opposite on both directions) — so a `sort_by` on any nullable column or
/// JSON path paged differently per backend. PostgreSQL's defaults are pinned,
/// which leaves existing PostgreSQL deployments unchanged and brings SQLite
/// (3.30+ accepts NULLS FIRST/LAST) into line.
/// </summary>
public static class SortNulls
{
    public static string For(string direction)
        => string.Equals(direction, "ASC", StringComparison.OrdinalIgnoreCase) ? "NULLS LAST" : "NULLS FIRST";
}
