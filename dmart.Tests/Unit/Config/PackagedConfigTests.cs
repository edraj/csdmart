using Dmart.Config;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// dist/config.env.packaged is what RPM, deb and apk seed /etc/dmart/config.env
// from. It is a SECOND config file alongside config.env.sample, with a
// different job — the sample documents every key, this one is the short set a
// packaged install needs — and a second file is a second thing that can drift.
//
// Drift here is not cosmetic. dmart refuses to boot on an unknown config key,
// so a stale line in this file breaks every packaged install at once, on
// upgrade, after the operator has already restarted the service.
public class PackagedConfigTests
{
    private static string PackagedPath()
    {
        // Walk up from the test binary to the repo root; the file is not copied
        // to the output directory and should not be, since the packagers read
        // it from the source tree.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "dist", "config.env.packaged");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("dist/config.env.packaged not found from " + AppContext.BaseDirectory);
    }

    private static Dictionary<string, string> Load()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(PackagedPath()))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var eq = t.IndexOf('=');
            if (eq <= 0) continue;
            values[t[..eq].Trim()] = t[(eq + 1)..].Trim().Trim('"');
        }
        return values;
    }

    [Fact]
    public void Every_Key_Is_One_Dmart_Actually_Accepts()
    {
        // Same gate Program.cs applies to a real config.env at startup.
        var errors = DotEnvStrictCheck.ValidateKeys(PackagedPath(), Load());
        errors.ShouldBeEmpty(string.Join("; ", errors));
    }

    [Fact]
    public void It_Selects_Sqlite_With_An_Absolute_Path_Under_The_Service_State_Dir()
    {
        var v = Load();

        // Pinned explicitly rather than left to inference. This file is edited
        // by hand and lives for years: an operator who fills in the PostgreSQL
        // block must also flip this line, and that is the point — a connection
        // edit alone should never move a deployment between backends.
        v["DATABASE_DRIVER"].ShouldBe("sqlite");

        // Absolute, not relative. SqlitePath resolves against the process
        // working directory, which for a systemd unit is not where an operator
        // would look, and "dmart.db" landing somewhere unexpected is exactly
        // the failure this whole area keeps producing.
        v["SQLITE_PATH"].ShouldBe("/var/lib/dmart/dmart.db");
        v["SPACES_FOLDER"].ShouldBe("/var/lib/dmart/spaces");
    }

    [Fact]
    public void No_PostgreSQL_Connection_Is_Left_Active()
    {
        var v = Load();

        // The PostgreSQL block must ship COMMENTED. An active DATABASE_PASSWORD
        // (which is what config.env.sample carries) would make
        // Db.HasExplicitPostgresConfig true, so a later edit removing the
        // DATABASE_DRIVER line would silently resolve back to PostgreSQL.
        foreach (var key in new[] { "DATABASE_HOST", "DATABASE_NAME", "DATABASE_PASSWORD",
                                    "DATABASE_USERNAME", "POSTGRES_CONNECTION" })
            v.ContainsKey(key).ShouldBeFalse($"{key} must ship commented out, not set");
    }

    [Fact]
    public void Jwt_Secret_Is_Still_A_Placeholder_The_Validator_Rejects()
    {
        // Deliberate: a packaged install must NOT start until the operator sets
        // a real secret. Shipping a working default would mean every dmart box
        // on the internet signing tokens with the same publicly known key.
        var s = new DmartSettings { JwtSecret = Load()["JWT_SECRET"] };
        var result = new DmartSettingsValidator().Validate(null, s);

        result.Failed.ShouldBeTrue("the shipped JWT_SECRET must fail validation");
        result.FailureMessage!.ShouldContain("JwtSecret");
    }
}
