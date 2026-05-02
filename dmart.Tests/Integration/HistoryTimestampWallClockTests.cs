using System.Net;
using System.Net.Http.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the wall-clock contract on the histories table — same as
// TimestampWallClockTests does for entries. History rows used to land via
// Postgres NOW() which produced a UTC timestamp on a UTC-configured DB,
// while every other table got TimeUtils.Now() (DateTime.Now, local
// wall-clock). That meant a "history of an entry" projection showed the
// audit row hours apart from the entry's own created_at on a non-UTC
// server. The fix replaces NOW() with the C#-side TimeUtils.Now() so all
// tables share the same wall-clock convention.
public sealed class HistoryTimestampWallClockTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public HistoryTimestampWallClockTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task History_Timestamp_Matches_Entry_CreatedAt_TimeZone()
    {
        // Create an entry, then trigger history via an Update. Both rows
        // should carry timestamps from the SAME wall clock (no timezone
        // offset between them).
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"hwc_{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        await CreateContent(client, space, subpath, shortname);
        await UpdateContent(client, space, subpath, shortname);

        try
        {
            var db = _factory.Services.GetRequiredService<Db>();
            await using var conn = await db.OpenAsync();

            // Read entry.updated_at directly from the entries table.
            await using var entryCmd = new Npgsql.NpgsqlCommand(
                "SELECT updated_at FROM entries WHERE shortname = $1 AND space_name = $2 AND subpath = $3",
                conn);
            entryCmd.Parameters.Add(new() { Value = shortname });
            entryCmd.Parameters.Add(new() { Value = space });
            entryCmd.Parameters.Add(new() { Value = subpath });
            var entryUpdatedAt = (DateTime)(await entryCmd.ExecuteScalarAsync())!;

            // Read history.timestamp for the most recent audit row.
            await using var histCmd = new Npgsql.NpgsqlCommand(
                "SELECT timestamp FROM histories WHERE shortname = $1 AND space_name = $2 AND subpath = $3 ORDER BY timestamp DESC LIMIT 1",
                conn);
            histCmd.Parameters.Add(new() { Value = shortname });
            histCmd.Parameters.Add(new() { Value = space });
            histCmd.Parameters.Add(new() { Value = subpath });
            var historyTimestamp = (DateTime)(await histCmd.ExecuteScalarAsync())!;

            // The two stamps must be within the same minute. If history is
            // UTC and entry is local, the gap on a non-UTC server is hours
            // — far outside this window.
            var diff = (entryUpdatedAt - historyTimestamp).Duration();
            diff.TotalMinutes.ShouldBeLessThan(1,
                $"history.timestamp={historyTimestamp:O} should match entry.updated_at={entryUpdatedAt:O} " +
                $"to within a minute. A multi-hour gap means UTC vs local timezone drift.");
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task History_Timestamp_Within_Seconds_Of_Local_Now()
    {
        // Trigger a history append, then assert the row's timestamp is
        // inside the [before, after] envelope of DateTime.Now (local
        // wall-clock). UTC drift would put the value hours outside.
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"hsnow_{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        await CreateContent(client, space, subpath, shortname);

        var before = DateTime.Now;
        await UpdateContent(client, space, subpath, shortname);
        var after = DateTime.Now;

        try
        {
            var db = _factory.Services.GetRequiredService<Db>();
            await using var conn = await db.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT timestamp FROM histories WHERE shortname = $1 AND space_name = $2 AND subpath = $3 ORDER BY timestamp DESC LIMIT 1",
                conn);
            cmd.Parameters.Add(new() { Value = shortname });
            cmd.Parameters.Add(new() { Value = space });
            cmd.Parameters.Add(new() { Value = subpath });
            var historyTimestamp = (DateTime)(await cmd.ExecuteScalarAsync())!;

            historyTimestamp.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-5),
                $"history timestamp {historyTimestamp:O} is more than 5s before {before:O} — likely UTC drift");
            historyTimestamp.ShouldBeLessThanOrEqualTo(after.AddSeconds(5),
                $"history timestamp {historyTimestamp:O} is more than 5s after {after:O} — likely UTC drift");
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task Direct_HistoryRepo_AppendAsync_Stores_Local_TimeUtils_Now()
    {
        // Service-layer parity check: bypass the HTTP path and call the
        // repository directly. Confirms the fix is at the repo, not just at
        // a higher layer.
        _factory.CreateClient();
        var histories = _factory.Services.GetRequiredService<HistoryRepository>();

        var space = "test";
        var subpath = "/itest";
        var shortname = $"hdirect_{Guid.NewGuid():N}".Substring(0, 16);

        var before = DateTime.Now;
        await histories.AppendAsync(space, subpath, shortname, "dmart",
            requestHeaders: null, diff: new() { ["test"] = "value" });
        var after = DateTime.Now;

        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT timestamp FROM histories WHERE shortname = $1 AND space_name = $2 AND subpath = $3 ORDER BY timestamp DESC LIMIT 1",
            conn);
        cmd.Parameters.Add(new() { Value = shortname });
        cmd.Parameters.Add(new() { Value = space });
        cmd.Parameters.Add(new() { Value = subpath });
        var historyTimestamp = (DateTime)(await cmd.ExecuteScalarAsync())!;

        historyTimestamp.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-5));
        historyTimestamp.ShouldBeLessThanOrEqualTo(after.AddSeconds(5));

        // Cleanup: delete the history row we just made.
        await using var del = new Npgsql.NpgsqlCommand(
            "DELETE FROM histories WHERE shortname = $1 AND space_name = $2 AND subpath = $3",
            conn);
        del.Parameters.Add(new() { Value = shortname });
        del.Parameters.Add(new() { Value = space });
        del.Parameters.Add(new() { Value = subpath });
        await del.ExecuteNonQueryAsync();
    }

    // -- helpers --

    private static async Task CreateContent(HttpClient client, string space, string subpath, string shortname)
    {
        var req = new Request
        {
            RequestType = RequestType.Create,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = shortname,
                    Attributes = new() { ["displayname"] = "history wall-clock probe" },
                },
            },
        };
        var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Create failed: {resp.StatusCode}\n{body}");
        }
    }

    private static async Task UpdateContent(HttpClient client, string space, string subpath, string shortname)
    {
        // An update triggers a history row append.
        var req = new Request
        {
            RequestType = RequestType.Update,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = shortname,
                    Attributes = new() { ["displayname"] = "renamed to trigger history" },
                },
            },
        };
        var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Update failed: {resp.StatusCode}\n{body}");
        }
    }

    private static async Task DeleteContent(HttpClient client, string space, string subpath, string shortname)
    {
        try
        {
            var req = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Content,
                        Subpath = subpath,
                        Shortname = shortname,
                    },
                },
            };
            await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        }
        catch { /* best-effort cleanup */ }
    }
}
