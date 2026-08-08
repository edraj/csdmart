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
