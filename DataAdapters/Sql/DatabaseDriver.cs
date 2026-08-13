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

    /// <summary>
    /// The driver a given configuration actually selects, INFERRING one when
    /// DATABASE_DRIVER is absent. Returns false only when an explicitly set
    /// value is unrecognized.
    /// </summary>
    /// <remarks>
    /// Inference exists so a fresh install serves with no configuration at all
    /// — but it deliberately does NOT read "nothing set" as "SQLite" in every
    /// case. It reads "no PostgreSQL connection anywhere" as SQLite.
    ///
    /// The distinction is what makes an upgrade safe. A config.env written
    /// before DATABASE_DRIVER existed still names a host and a database, so it
    /// resolves to PostgreSQL exactly as it always did. A bare default of
    /// "sqlite" would instead have switched those deployments to a new empty
    /// file, passed validation (the PostgreSQL host/name checks are skipped in
    /// SQLite mode), and answered /health/ready with a 200 while serving an
    /// empty index.
    ///
    /// It also keeps a PARTIALLY configured PostgreSQL loud: a config with
    /// DatabaseName but no DatabaseHost resolves to PostgreSQL and fails
    /// validation on the missing host, rather than quietly falling back.
    /// </remarks>
    public static bool TryResolve(Dmart.Config.DmartSettings settings,
        out DatabaseDriver driver, out bool inferred)
    {
        if (!string.IsNullOrWhiteSpace(settings.DatabaseDriver))
        {
            inferred = false;
            return TryParse(settings.DatabaseDriver, out driver);
        }

        inferred = true;
        // Db.HasExplicitPostgresConfig, not a fresh reading of the settings:
        // DatabaseHost defaults to "localhost" and DatabaseName to "dmart", so
        // a config with no DATABASE_* keys at all still looks populated. That
        // exact mistake made a first version of this infer PostgreSQL for every
        // deployment, including ones with an empty config. It is also the same
        // predicate Db.IsConfigured is built on, so "which driver" and "is the
        // database configured" can never disagree.
        driver = Db.HasExplicitPostgresConfig(settings)
            ? DatabaseDriver.Postgresql
            : DatabaseDriver.Sqlite;
        return true;
    }

    /// <summary>One line for the startup log: which driver, and why.</summary>
    public static string Describe(DatabaseDriver driver, bool inferred) => inferred
        ? $"{driver.ToString().ToLowerInvariant()} (inferred — "
          + (driver == DatabaseDriver.Sqlite
              ? "no PostgreSQL connection configured)"
              : "PostgreSQL connection settings present)")
        : $"{driver.ToString().ToLowerInvariant()} (DATABASE_DRIVER)";
}
