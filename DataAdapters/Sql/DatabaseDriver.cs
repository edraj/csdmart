namespace Dmart.DataAdapters.Sql;

// Which SQL backend stores the index. Selected by DATABASE_DRIVER, mirroring
// Python dmart's config key of the same name.
//
// The flat files under SPACES_FOLDER are the source of truth in every mode;
// this only decides how the rebuildable index is stored.
public enum DatabaseDriver
{
    /// <summary>PostgreSQL. The default and the only fully-supported tier.</summary>
    Postgresql,

    /// <summary>
    /// SQLite. A reduced tier intended for development, CI, single-node and
    /// edge deployments — deliberately not at parity with PostgreSQL.
    /// </summary>
    Sqlite,
}

public static class DatabaseDriverParser
{
    /// <summary>
    /// Parses a DATABASE_DRIVER value, case-insensitively. Returns false for
    /// anything unrecognized so the caller can fail startup with a clear
    /// message instead of silently falling back to a default — a typo'd driver
    /// silently running on PostgreSQL is exactly the kind of config drift that
    /// only surfaces in production.
    /// </summary>
    public static bool TryParse(string? value, out DatabaseDriver driver)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            // "postgres" and "postgresql" both appear in the wild; Python
            // dmart writes "postgresql". Accept both rather than making
            // operators guess.
            case "postgresql" or "postgres":
                driver = DatabaseDriver.Postgresql;
                return true;
            case "sqlite":
                driver = DatabaseDriver.Sqlite;
                return true;
            default:
                driver = DatabaseDriver.Postgresql;
                return false;
        }
    }

    /// <summary>Accepted values, for error messages.</summary>
    public static string Supported => "postgresql, sqlite";
}
