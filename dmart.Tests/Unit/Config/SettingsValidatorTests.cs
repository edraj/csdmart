using Dmart.Config;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Unit tests for DmartSettingsValidator. Validation runs at startup so a
// misconfiguration (bad port, zero pool size, empty DB host) fails loudly
// rather than producing obscure runtime errors later.
public class SettingsValidatorTests
{
    private static DmartSettings Valid() => new()
    {
        // Leave all defaults in place; defaults should already pass validation.
        JwtSecret = new string('x', 32),
    };

    [Fact]
    public void Defaults_Pass()
    {
        var result = new DmartSettingsValidator().Validate(null, Valid());
        result.Succeeded.ShouldBeTrue($"{string.Join("; ", result.Failures ?? new List<string>())}");
    }

    // ---- password hashing (Argon2id) ----
    // A bad value here breaks only NEW hashes — verification reads m/t/p from
    // the stored string — which is exactly the kind of fault that must stop the
    // process rather than first appear at someone's signup.

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PasswordHashIterations_Below_One_Fails(int t)
    {
        var s = Valid();
        s.PasswordHashIterations = t;
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("PasswordHashIterations");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void PasswordHashParallelism_Out_Of_Range_Fails(int p)
    {
        var s = Valid();
        s.PasswordHashParallelism = p;
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("PasswordHashParallelism");
    }

    [Fact]
    public void PasswordHashMemoryKb_Below_The_OWASP_Floor_Fails()
    {
        var s = Valid();
        s.PasswordHashMemoryKb = 7167;
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("PasswordHashMemoryKb");
    }

    [Fact]
    public void PasswordHashMemoryKb_Below_Eight_Times_Parallelism_Fails()
    {
        // Argon2's own constraint: fewer blocks than 8*p and the lanes have
        // nothing to work on. libargon2 rejects it at hash time; this catches it at
        // boot instead of at the first password written.
        var s = Valid();
        s.PasswordHashParallelism = 64;
        s.PasswordHashMemoryKb = 256;        // below 8*64, and below the floor
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("PasswordHashMemoryKb");
        // Both rules fire for this value; assert the 8*p one specifically so the
        // test would still fail if only the floor check remained.
        r.FailureMessage!.ShouldContain("8 * PasswordHashParallelism");
    }

    [Fact]
    public void PasswordHashMemoryBudgetMb_Negative_Fails()
    {
        var s = Valid();
        s.PasswordHashMemoryBudgetMb = -1;
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("PasswordHashMemoryBudgetMb");
    }

    [Fact]
    public void PasswordHashMemoryBudgetMb_Zero_Is_Auto_And_Passes()
    {
        var s = Valid();
        s.PasswordHashMemoryBudgetMb = 0;
        new DmartSettingsValidator().Validate(null, s).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void PasswordHashQueueTimeoutSeconds_Below_One_Fails()
    {
        var s = Valid();
        s.PasswordHashQueueTimeoutSeconds = 0;
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("PasswordHashQueueTimeoutSeconds");
    }

    [Fact]
    public void A_Stronger_Server_Configuration_Passes()
    {
        // The documented "raise the cost on a big server" case must validate.
        var s = Valid();
        s.PasswordHashMemoryKb = 65_536;
        s.PasswordHashIterations = 3;
        s.PasswordHashParallelism = 4;
        s.PasswordHashMemoryBudgetMb = 512;
        new DmartSettingsValidator().Validate(null, s).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ListeningPort_Zero_Fails()
    {
        var s = Valid();
        s.ListeningPort = 0;
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("ListeningPort");
    }

    [Fact]
    public void ListeningPort_TooHigh_Fails()
    {
        var s = Valid();
        s.ListeningPort = 99999;
        new DmartSettingsValidator().Validate(null, s).Failed.ShouldBeTrue();
    }

    [Fact]
    public void DatabaseHost_Empty_Fails_When_No_ConnString()
    {
        // The property this protects is "a deployment ON PostgreSQL must name a
        // host". The driver has to be explicit for that to be the situation:
        // with everything at its defaults and an empty host, nothing points at
        // PostgreSQL at all, and the case below is what happens instead.
        var s = Valid();
        s.DatabaseDriver = "postgresql";
        s.DatabaseHost = "";
        s.PostgresConnection = null;
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("DatabaseHost");
    }

    [Fact]
    public void Empty_Host_With_No_Driver_And_No_Connection_Is_A_Fresh_Sqlite_Install()
    {
        // Behaviour change, pinned deliberately. Before driver inference this
        // failed with "DatabaseHost must be configured"; now a configuration
        // that points at no database at all resolves to SQLite and starts, which
        // is what makes `dmart serve` work on a fresh box. A config that DOES
        // name a PostgreSQL connection is unaffected — see the test above and
        // DatabaseDriverTests.Unset_Driver_Infers_Postgres_When_Anything_Points_At_It.
        var s = Valid();
        s.DatabaseHost = "";
        s.PostgresConnection = null;

        new DmartSettingsValidator().Validate(null, s).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void DatabaseHost_Empty_Passes_When_ConnString_Set()
    {
        var s = Valid();
        s.DatabaseHost = "";
        s.PostgresConnection = "Host=localhost;Database=dmart";
        new DmartSettingsValidator().Validate(null, s).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void DatabasePoolSize_Zero_Fails()
    {
        var s = Valid();
        s.DatabasePoolSize = 0;
        new DmartSettingsValidator().Validate(null, s).Failed.ShouldBeTrue();
    }

    [Fact]
    public void JwtAccessExpires_Zero_Fails()
    {
        // Zero-second access lifetime would mint tokens that expire on the
        // very next request — almost certainly a config mistake.
        var s = Valid();
        s.JwtAccessExpires = 0;
        new DmartSettingsValidator().Validate(null, s).Failed.ShouldBeTrue();
    }

    [Fact]
    public void JwtSecret_TooShort_Fails()
    {
        var s = Valid();
        s.JwtSecret = "short";
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("JwtSecret");
    }

    [Fact]
    public void JwtSecret_KnownPlaceholder_Fails()
    {
        // Long enough to clear the 32-byte floor, but a publicly-known string —
        // booting on it lets anyone forge an admin JWT, so it must be rejected.
        var s = Valid();
        s.JwtSecret = "change-me-change-me-change-me-32b";
        var r = new DmartSettingsValidator().Validate(null, s);
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("JwtSecret");
    }

    [Fact]
    public void JwtSecret_SamplePlaceholder_Fails()
    {
        var s = Valid();
        s.JwtSecret = "change-me-change-me-change-me-32b-minimum-length";
        new DmartSettingsValidator().Validate(null, s).Failed.ShouldBeTrue();
    }

    [Fact]
    public void JwtSecret_CompiledDefault_Fails()
    {
        // A DmartSettings with no operator-provided secret carries the built-in
        // placeholder default — it must not be allowed to boot.
        var r = new DmartSettingsValidator().Validate(null, new DmartSettings());
        r.Failed.ShouldBeTrue();
        r.FailureMessage!.ShouldContain("JwtSecret");
    }

    [Fact]
    public void Negative_DatabasePort_Fails()
    {
        var s = Valid();
        s.DatabasePort = -1;
        new DmartSettingsValidator().Validate(null, s).Failed.ShouldBeTrue();
    }
}
