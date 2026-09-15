using Dmart.DataAdapters.Sql;
using Dmart.Utils;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// dmart stores NAIVE timestamps — `timestamp without time zone`, a bare
// wall-clock reading with no offset attached. That only works if every writer
// reads the SAME clock.
//
// Two do not, unless the session timezone is pinned: `TimeUtils.Now()` reads the
// app host's clock, while SQL `NOW()` is rendered in the database session's
// zone. With a UTC database and a non-UTC host — the common production shape —
// the same instant is stored as two wall clocks hours apart, in one column,
// with nothing to say which clock produced which value.
//
// The damage is silent: an incremental export selects `updated_at >= watermark`
// against a host-clock watermark, so a row stamped by a `NOW()` path (folder
// move, rename) lands in the past and is skipped forever.
public class DatabaseClockTests(DmartFactory factory) : IClassFixture<DmartFactory>
{
    [FactIfPg]
    public async Task Server_Now_And_Host_Now_Agree()
    {
        factory.CreateClient();
        var db = factory.Services.GetRequiredService<IDbConnectionFactory>();
        if (db is not Db) return;   // SQLite binds TimeUtils.Now() everywhere already

        await using var conn = await db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT now()::timestamp";

        var before = TimeUtils.Now();
        var serverNow = (DateTime)(await cmd.ExecuteScalarAsync())!;
        var after = TimeUtils.Now();

        // A minute of slack absorbs real clock skew between hosts. The failure
        // this guards against is a whole timezone offset — hours, not seconds.
        var drift = serverNow < before
            ? before - serverNow
            : serverNow > after ? serverNow - after : TimeSpan.Zero;

        drift.ShouldBeLessThan(TimeSpan.FromMinutes(1),
            $"NOW() returned {serverNow:o} while the host clock reads {before:o}. "
            + "Naive timestamps written by SQL NOW() and by TimeUtils.Now() would land "
            + "in the same column hours apart, and an incremental export would silently "
            + "skip every row stamped by the NOW() paths.");
    }

    // ...and the COLUMNS have to be naive too, or the binding seam is
    // defending a door that is already open. A `timestamptz` column re-attaches
    // an offset to every value written through it and hands back an instant
    // rather than the wall clock that was stored, which is precisely the shape
    // SqlSchema's TIMESTAMPTZ->TIMESTAMP migration exists to undo. This asserts
    // against the LIVE database, so it also catches a deployment whose
    // migration never ran — not just a new column declared the wrong way.
    [FactIfPg]
    public async Task No_Timestamp_Column_Carries_A_Time_Zone()
    {
        factory.CreateClient();
        var db = factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();

        if (conn is Microsoft.Data.Sqlite.SqliteConnection)
        {
            // SQLite has no timestamp type at all: dmart stores the naive wall
            // clock as fixed-width TEXT so lexicographic and chronological
            // ordering agree (SqliteValues.TimestampFormat). A timestamp column
            // declared INTEGER/REAL would be an epoch — an instant, not a wall
            // clock — and would sort correctly while reading back wrong.
            var offenders = new List<string>();
            var tables = new List<string>();
            await using (var t = conn.CreateCommand())
            {
                t.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                await using var r = await t.ExecuteReaderAsync();
                while (await r.ReadAsync()) tables.Add(r.GetString(0));
            }
            foreach (var table in tables)
            {
                await using var c = conn.CreateCommand();
                c.CommandText = $"PRAGMA table_info({table})";
                await using var r = await c.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var column = r.GetString(1);
                    var type = r.GetString(2);
                    if (!IsTimestampColumn(column)) continue;
                    if (!string.Equals(type, "TEXT", StringComparison.OrdinalIgnoreCase))
                        offenders.Add($"{table}.{column} is {type}");
                }
            }
            offenders.ShouldBeEmpty(
                "dmart stores naive wall clocks as fixed-width TEXT on SQLite; these columns would "
                + "round-trip as something else: " + string.Join(", ", offenders));
            return;
        }

        // PostgreSQL: `timestamp with time zone` must not appear at all.
        var tz = new List<string>();
        await using (var c = conn.CreateCommand())
        {
            c.CommandText = """
                SELECT table_name, column_name, data_type
                FROM information_schema.columns
                WHERE table_schema = 'public' AND data_type = 'timestamp with time zone'
                ORDER BY table_name, column_name
                """;
            await using var r = await c.ExecuteReaderAsync();
            while (await r.ReadAsync()) tz.Add($"{r.GetString(0)}.{r.GetString(1)}");
        }

        tz.ShouldBeEmpty(
            "every dmart timestamp column is `timestamp without time zone` — a naive wall clock. "
            + "These are timestamptz, so they re-attach an offset on write and return an instant on "
            + "read: " + string.Join(", ", tz)
            + ". SqlSchema's TIMESTAMPTZ->TIMESTAMP DO block converts legacy columns; a new one "
            + "declared this way needs fixing at the declaration.");
    }

    // Column names that hold a timestamp in dmart's schema.
    private static bool IsTimestampColumn(string column) =>
        column is "timestamp" or "floor_at"
        || column.EndsWith("_at", StringComparison.Ordinal)
        || column is "last_failed_login";

    // The naive model has to hold for the VALUE as bound, not just for the
    // clock each writer reads. Npgsql infers the PostgreSQL type from
    // DateTime.Kind: Kind=Utc infers `timestamptz`, which the server then
    // converts into a `timestamp without time zone` column through the session
    // TimeZone pinned above — shifting the stored wall-clock by the host's
    // offset. SQLite writes the components verbatim and never did that, so the
    // two backends disagreed about what a Kind=Utc value meant.
    //
    // PostgresDialect.CreateParameter now relabels every bound DateTime to
    // Unspecified, so all three Kinds store the SAME wall clock on BOTH
    // backends. This test is what stops that quietly regressing: without the
    // relabel, the Utc row lands an offset away on PostgreSQL and this fails on
    // any non-UTC host — which is precisely the condition CI now runs the
    // SQLite leg under.
    [FactIfPg]
    public async Task Every_DateTimeKind_Stores_The_Same_Wall_Clock()
    {
        factory.CreateClient();
        var db = factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        var sqlite = conn is Microsoft.Data.Sqlite.SqliteConnection;

        // A fixed wall-clock, so the assertion is exact rather than a tolerance.
        var wall = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var table = "kindprobe_" + Guid.NewGuid().ToString("N")[..8];

        await using (var c = conn.CreateCommand())
        {
            c.CommandText = sqlite
                ? $"CREATE TABLE {table} (k TEXT, v TEXT)"
                : $"CREATE TABLE {table} (k text, v timestamp)";
            await c.ExecuteNonQueryAsync();
        }

        try
        {
            foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Local, DateTimeKind.Utc })
            {
                await using var c = conn.CreateCommand();
                var k = DbParams.Add(c, kind.ToString());
                var v = DbParams.Add(c, DateTime.SpecifyKind(wall, kind));
                c.CommandText = $"INSERT INTO {table} (k, v) VALUES ({k}, {v})";
                await c.ExecuteNonQueryAsync();
            }

            var stored = new List<(string Kind, DateTime Value)>();
            await using (var c = conn.CreateCommand())
            {
                c.CommandText = $"SELECT k, v FROM {table} ORDER BY k";
                await using var r = await c.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    stored.Add((r.GetString(0), ReadStoredTimestamp(r.GetValue(1))));
            }

            stored.Count.ShouldBe(3);
            foreach (var (kind, value) in stored)
                value.ShouldBe(wall,
                    $"a DateTime bound with Kind={kind} stored {value:yyyy-MM-dd HH:mm:ss} instead of the "
                    + $"{wall:yyyy-MM-dd HH:mm:ss} wall clock it was given. dmart's columns are naive — the "
                    + "binding seam must not let the driver reinterpret one Kind against the session timezone.");
        }
        finally
        {
            await using var drop = conn.CreateCommand();
            drop.CommandText = $"DROP TABLE {table}";
            await drop.ExecuteNonQueryAsync();
        }
    }

    // PostgreSQL hands back a DateTime; SQLite stores the naive wall clock as
    // TEXT. Mirrors OtpRepository.ReadTimestamp.
    private static DateTime ReadStoredTimestamp(object raw) => raw switch
    {
        DateTime dt => dt,
        string s when SqliteValues.TryToDateTime(s, out var parsed) => parsed,
        _ => throw new InvalidOperationException($"unexpected timestamp value: {raw?.GetType().Name ?? "null"}"),
    };

    // The mechanism, asserted directly so a regression names its own cause.
    [FactIfPg]
    public async Task The_Session_Timezone_Matches_The_Host()
    {
        factory.CreateClient();
        var db = factory.Services.GetRequiredService<IDbConnectionFactory>();
        if (db is not Db) return;

        await using var conn = await db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT current_setting('TimeZone')";
        var sessionZone = (string)(await cmd.ExecuteScalarAsync())!;

        var hostOffset = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        var sessionOffset = TimeZoneInfo.FindSystemTimeZoneById(sessionZone)
            .GetUtcOffset(DateTime.UtcNow);

        // Compared by OFFSET, not by id: "Asia/Amman" and a link like
        // "Asia/Jerusalem" can share an offset, and the offset is what actually
        // determines the stored wall clock.
        sessionOffset.ShouldBe(hostOffset,
            $"session timezone '{sessionZone}' is {sessionOffset} but the host is {hostOffset}");
    }
}
