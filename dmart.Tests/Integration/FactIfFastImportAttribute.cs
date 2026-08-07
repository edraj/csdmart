using Npgsql;
using Xunit;

namespace Dmart.Tests.Integration;

// Marks a fact that needs the DB role to be able to set
// `session_replication_role = 'replica'`. That GUC is superuser-context, so
// the role must be a superuser or — on PG 15+ — hold a per-parameter grant:
//
//     GRANT SET ON PARAMETER session_replication_role TO dmart;
//
// (There is no `pg_session_replication_role` predefined role; PostgreSQL has
// never shipped one.) CI runs as the `dmart` application role, which has
// neither by default, so these tests skip there instead of producing the
// documented hard failure. An environment that grants either will run them.
//
// The probe runs ONCE per process (Lazy) so we don't pay the open+SET cost
// per test. A connection failure or any non-42501 exception is treated as
// "skip + report the reason" so a misconfigured local env doesn't silently
// hide test failures.
public sealed class FactIfFastImportAttribute : FactAttribute
{
    private static readonly Lazy<string?> _skipReason = new(Probe);

    public FactIfFastImportAttribute()
    {
        var reason = _skipReason.Value;
        if (reason is not null) Skip = reason;
    }

    private static string? Probe()
    {
        if (!DmartFactory.HasPg)
            return "PostgreSQL not configured (set DMART_TEST_PG_CONN or create a config.env)";
        try
        {
            using var conn = new NpgsqlConnection(DmartFactory.PgConn);
            conn.Open();
            using (var set = new NpgsqlCommand("SET session_replication_role = 'replica'", conn))
                set.ExecuteNonQuery();
            using (var reset = new NpgsqlCommand("SET session_replication_role = DEFAULT", conn))
                reset.ExecuteNonQuery();
            return null;
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            return "DB role lacks privilege to SET session_replication_role (42501) — grant it as a "
                   + "superuser with `GRANT SET ON PARAMETER session_replication_role TO <role>;` (PG 15+)";
        }
        catch (Exception ex)
        {
            return $"fast-import probe failed: {ex.Message}";
        }
    }
}
