using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

public class DatabaseDriverTests
{
    [Theory]
    [InlineData("postgresql", DatabaseDriver.Postgresql)]
    [InlineData("postgres", DatabaseDriver.Postgresql)]      // seen in the wild
    [InlineData("PostgreSQL", DatabaseDriver.Postgresql)]    // case-insensitive
    [InlineData("  sqlite  ", DatabaseDriver.Sqlite)]        // trimmed
    [InlineData("SQLITE", DatabaseDriver.Sqlite)]
    public void TryParse_AcceptsSupportedDrivers(string input, DatabaseDriver expected)
    {
        DatabaseDriverParser.TryParse(input, out var driver).ShouldBeTrue();
        driver.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mysql")]
    [InlineData("postgresqlx")]
    [InlineData("sqlite3")]
    public void TryParse_RejectsUnknownDrivers(string? input)
    {
        // False, not a silent fallback: the caller fails startup on this.
        DatabaseDriverParser.TryParse(input, out _).ShouldBeFalse();
    }

    // ====================================================================
    // Inference — what happens when DATABASE_DRIVER is NOT set.
    //
    // These pin an UPGRADE-SAFETY property, not a preference. A bare default
    // of "sqlite" would switch any config.env written before DATABASE_DRIVER
    // existed onto a new empty database file, and it would do so silently:
    // the validator skips the PostgreSQL host/name checks in SQLite mode, and
    // /health/ready answers 200 against the empty file.
    // ====================================================================

    // A DEFAULT-VALUED settings object is the trap: DatabaseHost defaults to
    // "localhost" and DatabaseName to "dmart", so a config.env with no
    // DATABASE_* keys at all still arrives here looking fully populated. The
    // first version of this inference read those fields directly and therefore
    // resolved PostgreSQL for every deployment on earth, including empty ones —
    // and the unit tests passed, because they constructed settings with
    // explicit empty strings that no real config produces. Hence: build these
    // fixtures the way configuration binding does, from the defaults up.
    private static DmartSettings Defaults() => new();

    [Fact]
    public void Unset_Driver_Infers_Sqlite_When_Nothing_Configures_Postgres()
    {
        // Exactly what a fresh install's settings look like: untouched.
        DatabaseDriverParser.TryResolve(Defaults(), out var driver, out var inferred).ShouldBeTrue();
        driver.ShouldBe(DatabaseDriver.Sqlite, "a fresh install with no config serves on SQLite");
        inferred.ShouldBeTrue();
    }

    public static TheoryData<string, Action<DmartSettings>> PostgresShapes => new()
    {
        { "password set", s => s.DatabasePassword = "secret" },
        { "non-default host", s => s.DatabaseHost = "db.internal" },
        { "non-default name", s => s.DatabaseName = "dmart_prod" },
        { "non-default user", s => s.DatabaseUsername = "app" },
        { "non-default port", s => s.DatabasePort = 6432 },
        { "raw connection string", s => s.PostgresConnection = "Host=db;Database=dmart" },
    };

    [Theory]
    [MemberData(nameof(PostgresShapes))]
    public void Unset_Driver_Infers_Postgres_When_Anything_Points_At_It(
        string shape, Action<DmartSettings> configure)
    {
        // The upgrade-safety property: a config.env written before
        // DATABASE_DRIVER existed still names a connection, so it must keep
        // using PostgreSQL. A bare "sqlite" default would have switched these
        // onto a new empty file, passed validation (the host/name checks are
        // skipped in SQLite mode) and answered /health/ready with a 200.
        var s = Defaults();
        configure(s);

        DatabaseDriverParser.TryResolve(s, out var driver, out var inferred).ShouldBeTrue();
        driver.ShouldBe(DatabaseDriver.Postgresql, $"{shape} must keep the deployment on PostgreSQL");
        inferred.ShouldBeTrue();
    }

    [Theory]
    [InlineData("sqlite", DatabaseDriver.Sqlite)]
    [InlineData("postgresql", DatabaseDriver.Postgresql)]
    public void Explicit_Driver_Wins_Over_Inference(string value, DatabaseDriver expected)
    {
        // Explicit sqlite alongside a full PostgreSQL block is a real
        // configuration — pointing a dev box at a local file while leaving the
        // shared connection settings in place — and must be honoured.
        var s = Defaults();
        s.DatabaseDriver = value;
        s.DatabaseHost = "db.internal";   // a real PostgreSQL config alongside it

        DatabaseDriverParser.TryResolve(s, out var driver, out var inferred).ShouldBeTrue();
        driver.ShouldBe(expected);
        inferred.ShouldBeFalse();
    }

    [Fact]
    public void Explicitly_Unrecognized_Driver_Is_Still_Rejected()
    {
        // Inference must not rescue a typo: DATABASE_DRIVER=sqlit is a
        // mistake, and resolving it to anything at all would hide it.
        var s = Defaults();
        s.DatabaseDriver = "sqlit";

        DatabaseDriverParser.TryResolve(s, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Describe_Says_Whether_The_Driver_Was_Chosen_Or_Inferred()
    {
        // This string is what an operator sees in the startup log, and it is
        // the only signal that a backend was picked FOR them.
        DatabaseDriverParser.Describe(DatabaseDriver.Sqlite, inferred: true)
            .ShouldContain("inferred");
        DatabaseDriverParser.Describe(DatabaseDriver.Sqlite, inferred: false)
            .ShouldContain("DATABASE_DRIVER");
    }

    [Fact]
    public void DatabaseDriver_IsAnAcceptedConfigEnvKey()
    {
        // config.env.sample now ships DATABASE_DRIVER. DotEnvStrictCheck
        // forbids keys that don't map to a DmartSettings property, so if the
        // property and the dotenv key ever drift, every deployment using the
        // shipped sample refuses to boot on an "unknown config key" error.
        var raw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DATABASE_DRIVER"] = "postgresql",
        };
        Dmart.Config.DotEnvStrictCheck.ValidateKeys("/tmp/config.env", raw).ShouldBeEmpty();
    }
}
