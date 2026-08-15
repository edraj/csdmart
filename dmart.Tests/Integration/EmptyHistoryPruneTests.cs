using Dmart.DataAdapters.Sql;
using Dmart.QueryGrammar;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// `dmart prune-empty-histories` — cleanup for rows written before 5ce715b,
// when an entry update that changed nothing still appended a history row whose
// diff was the literal `{}`.
//
// The risk this file exists to pin is deleting too much. A history table is an
// audit trail: a prune that also took real rows, or rows with a NULL diff (an
// older, different shape), would destroy evidence with no way to get it back.
// So every test here asserts what SURVIVES, not only what goes.
public class EmptyHistoryPruneTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public EmptyHistoryPruneTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Removes_Empty_Diffs_And_Keeps_Real_History()
    {
        var (histories, dbf, space) = await SeedAsync();
        try
        {
            var result = await histories.PruneEmptyDiffAsync(space);

            result.Removed.ShouldBe(2);
            result.DryRun.ShouldBeFalse();

            await using var conn = await dbf.OpenAsync();
            // The two `{}` rows are gone...
            (await CountAsync(conn, space, EmptyDiff())).ShouldBe(0);
            // ...and the row that recorded an actual change is untouched.
            (await CountAsync(conn, space, $"{EmptyDiff(raw: true)} LIKE '%displayname%'")).ShouldBe(1);
        }
        finally { await CleanupAsync(space); }
    }

    // A NULL diff is a different, older shape than the empty object this bug
    // produced. Sweeping it up would be a separate decision, so it is reported
    // and left in place.
    [FactIfPg]
    public async Task Leaves_Null_Diff_Rows_Alone_And_Reports_Them()
    {
        var (histories, dbf, space) = await SeedAsync(withNullDiff: true);
        try
        {
            var result = await histories.PruneEmptyDiffAsync(space);

            result.Removed.ShouldBe(2);
            result.NullDiffLeft.ShouldBe(1);

            await using var conn = await dbf.OpenAsync();
            (await CountAsync(conn, space, "diff IS NULL")).ShouldBe(1);
        }
        finally { await CleanupAsync(space); }
    }

    // histories is one of the seven replicated tables, so a delete that left no
    // tombstone would silently diverge every incremental consumer holding those
    // rows — the exact failure §5.2 exists to prevent.
    [FactIfPg]
    public async Task Deletions_Are_Tombstoned()
    {
        var (histories, dbf, space) = await SeedAsync();
        try
        {
            await histories.PruneEmptyDiffAsync(space);

            await using var conn = await dbf.OpenAsync();
            await using var cmd = conn.Command(
                "SELECT COUNT(*) FROM deletions WHERE space_name = $1 AND table_name = 'histories'");
            DbParams.Add(cmd, space);
            Convert.ToInt32(await cmd.ExecuteScalarAsync())
                .ShouldBe(2, "each pruned history row needs a tombstone");
        }
        finally { await CleanupAsync(space); }
    }

    [FactIfPg]
    public async Task A_Dry_Run_Counts_Without_Deleting()
    {
        var (histories, dbf, space) = await SeedAsync();
        try
        {
            var result = await histories.PruneEmptyDiffAsync(space, dryRun: true);

            result.Removed.ShouldBe(2);
            result.DryRun.ShouldBeTrue();

            await using var conn = await dbf.OpenAsync();
            (await CountAsync(conn, space, EmptyDiff())).ShouldBe(2);
            // And no tombstone either — a dry run must touch nothing at all.
            await using var t = conn.Command(
                "SELECT COUNT(*) FROM deletions WHERE space_name = $1 AND table_name = 'histories'");
            DbParams.Add(t, space);
            Convert.ToInt32(await t.ExecuteScalarAsync()).ShouldBe(0);
        }
        finally { await CleanupAsync(space); }
    }

    // Scoping must not reach past the named space, or a per-space cleanup
    // quietly becomes a global one.
    [FactIfPg]
    public async Task Scoping_To_A_Space_Leaves_Other_Spaces_Untouched()
    {
        var (histories, dbf, spaceA) = await SeedAsync();
        var (_, _, spaceB) = await SeedAsync();
        try
        {
            await histories.PruneEmptyDiffAsync(spaceA);

            await using var conn = await dbf.OpenAsync();
            (await CountAsync(conn, spaceA, EmptyDiff())).ShouldBe(0);
            (await CountAsync(conn, spaceB, EmptyDiff())).ShouldBe(2, "other spaces are not in scope");
        }
        finally { await CleanupAsync(spaceA); await CleanupAsync(spaceB); }
    }

    // ---- helpers ----

    private async Task<(HistoryRepository, IDbConnectionFactory, string)> SeedAsync(
        bool withNullDiff = false)
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var histories = sp.GetRequiredService<HistoryRepository>();
        var dbf = sp.GetRequiredService<IDbConnectionFactory>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var space = "eh_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });

        // Two empty-diff rows (what the bug wrote — AppendAsync coerces a null
        // diff to `{}`), plus one that records a real change.
        await histories.AppendAsync(space, "/", "e1", "dmart", null, null);
        await histories.AppendAsync(space, "/", "e2", "dmart", null, null);
        await histories.AppendAsync(space, "/", "e3", "dmart", null,
            new Dictionary<string, object>
            {
                ["displayname.en"] = new Dictionary<string, string> { ["old"] = "a", ["new"] = "b" },
            });

        if (withNullDiff)
        {
            await using var conn = await dbf.OpenAsync();
            await using var cmd = conn.Command(
                "INSERT INTO histories (uuid, space_name, subpath, shortname, owner_shortname, "
                + "request_headers, diff, timestamp) "
                + "VALUES ($1, $2, '/', 'e4', 'dmart', '{}', NULL, $3)");
            DbParams.Add(cmd, Guid.NewGuid());
            DbParams.Add(cmd, space);
            DbParams.Add(cmd, Dmart.Utils.TimeUtils.Now());
            await cmd.ExecuteNonQueryAsync();
        }

        return (histories, dbf, space);
    }

    // The feature runs on both backends, so the tests must too: jsonb needs
    // ::text on PostgreSQL while SQLite stores the column as TEXT already.
    private string EmptyDiff(bool raw = false)
    {
        var expr = _factory.Services.GetRequiredService<ISqlDialect>().AsText("diff");
        return raw ? expr : $"{expr} = '{{}}'";
    }

    private static async Task<int> CountAsync(
        System.Data.Common.DbConnection conn, string space, string predicate)
    {
        await using var cmd = conn.Command(
            $"SELECT COUNT(*) FROM histories WHERE space_name = $1 AND {predicate}");
        DbParams.Add(cmd, space);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task CleanupAsync(string space)
    {
        try
        {
            var dbf = _factory.Services.GetRequiredService<IDbConnectionFactory>();
            await using var conn = await dbf.OpenAsync();
            foreach (var t in new[] { "histories", "deletions" })
            {
                await using var cmd = conn.Command($"DELETE FROM {t} WHERE space_name = $1");
                DbParams.Add(cmd, space);
                await cmd.ExecuteNonQueryAsync();
            }
            await _factory.Services.GetRequiredService<SpaceRepository>().DeleteAsync(space);
        }
        catch { /* best effort */ }
    }
}
