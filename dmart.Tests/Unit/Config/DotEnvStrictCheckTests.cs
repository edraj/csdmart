using Dmart.Config;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Pins the strict config.env validation: any key that isn't a DmartSettings
// property (and isn't one of the small BACKEND_ENV / DMART_ENV passthroughs)
// must be reported as an error. This catches typos and stale keys from
// renamed/removed settings that would otherwise silently use defaults.
public class DotEnvStrictCheckTests
{
    [Fact]
    public void Known_Keys_Pass()
    {
        var raw = new Dictionary<string, string>
        {
            ["DATABASE_HOST"] = "localhost",
            ["JWT_SECRET"] = "x",
            ["LISTENING_PORT"] = "8282",
        };
        DotEnvStrictCheck.ValidateKeys("/tmp/config.env", raw).ShouldBeEmpty();
    }

    // config.env.sample is what an operator copies, and dmart calls
    // Environment.Exit(1) on an unrecognised key (Program.cs). So a stale or
    // misspelled key in the sample does not degrade gracefully — it hands the
    // operator a file that refuses to boot. Nothing checked the sample against
    // the settings surface until this test; adding a setting to DmartSettings
    // and forgetting the sample, or renaming one and leaving the sample behind,
    // were both silent.
    [Fact]
    public void ConfigEnvSample_ContainsOnlyKnownKeys()
    {
        var sample = FindRepoFile("config.env.sample");
        var raw = DotEnv.Parse(sample);

        raw.ShouldNotBeEmpty("config.env.sample parsed to zero keys — the parser " +
                             "or the file moved, and this test would pass vacuously");
        DotEnvStrictCheck.ValidateKeys(sample, raw).ShouldBeEmpty();
    }

    // The packaged default config is seeded to /etc/dmart/config.env on first
    // install, so the same reasoning applies with more force: a bad key there
    // breaks a fresh install rather than a copy-paste.
    [Fact]
    public void PackagedConfig_ContainsOnlyKnownKeys()
    {
        var packaged = FindRepoFile(Path.Combine("dist", "config.env.packaged"));
        var raw = DotEnv.Parse(packaged);

        raw.ShouldNotBeEmpty("dist/config.env.packaged parsed to zero keys");
        DotEnvStrictCheck.ValidateKeys(packaged, raw).ShouldBeEmpty();
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new FileNotFoundException(
            $"could not locate {relative} above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void Typo_Is_Flagged()
    {
        var raw = new Dictionary<string, string> { ["DATABAE_HOST"] = "x" };
        var errors = DotEnvStrictCheck.ValidateKeys("/tmp/config.env", raw);
        errors.Count.ShouldBe(1);
        errors[0].ShouldContain("DATABAE_HOST");
        errors[0].ShouldContain("/tmp/config.env");
    }

    [Fact]
    public void Removed_Setting_Is_Flagged()
    {
        // REDIS_CONNECTION, OTP_TOKEN_TTL, etc. were deliberately removed —
        // a config.env still carrying them is a mistake worth surfacing.
        var raw = new Dictionary<string, string>
        {
            ["APP_NAME"] = "dmart",                 // removed
            ["USERS_SUBPATH"] = "users",            // removed
            ["ENABLE_SQL_BACKEND"] = "true",        // removed
        };
        var errors = DotEnvStrictCheck.ValidateKeys("/tmp/config.env", raw);
        errors.Count.ShouldBe(3);
    }

    [Fact]
    public void Passthrough_Keys_Are_Allowed()
    {
        // BACKEND_ENV / DMART_ENV select WHICH config.env to load — they may
        // legitimately appear inside one of those files without mapping to a
        // DmartSettings property.
        var raw = new Dictionary<string, string>
        {
            ["BACKEND_ENV"] = "/etc/dmart/config.env",
            ["DMART_ENV"] = "/etc/dmart/config.env",
        };
        DotEnvStrictCheck.ValidateKeys("/tmp/config.env", raw).ShouldBeEmpty();
    }

    [Fact]
    public void Pascal_To_Snake_Roundtrips_With_ToConfigurationKey()
    {
        // Inverse of DotEnv.ToConfigurationKey — the UPPER_SNAKE form we
        // expect in config.env must map back to the same Dmart:Xxx path.
        foreach (var pascal in new[] { "DatabaseHost", "JwtSecret", "MaxFailedLoginAttempts", "AllowedCorsOrigins" })
        {
            var snake = DotEnvStrictCheck.PascalToUpperSnake(pascal);
            DotEnv.ToConfigurationKey(snake).ShouldBe($"Dmart:{pascal}");
        }
    }
}
