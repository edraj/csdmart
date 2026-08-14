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
