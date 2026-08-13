using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The premise of the whole storage design is that the SQL store is a
// REBUILDABLE INDEX over the flat files under SPACES_FOLDER. These tests pin
// that premise directly, on whichever driver the run is configured for.
//
// They assert on CONTENT, not just counts. The failure mode of a rebuild is a
// silently incomplete index: it looks like working software and quietly
// returns fewer rows. A test that only counted would pass against a walker
// that skipped an entire resource type.
public class ReindexFromFlatFilesTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ReindexFromFlatFilesTests(DmartFactory factory) => _factory = factory;

    // Reindexing the same tree twice must land the same rows. This is what
    // makes a rebuild trustworthy: an operator has to be able to re-run it
    // after an interruption without reasoning about what the first run got to.
    [FactIfImportSupported]
    public async Task Reindex_Is_Idempotent_Across_Two_Runs()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var (space, src) = BuildTree(entryCount: 5);
        try
        {
            var first = await ReindexAsync(io, src);
            first.Status.ShouldBe(Status.Success, $"first pass failed: {first.Error?.Message}");

            var afterFirst = await ShortnamesAsync(entries, space);
            afterFirst.Count.ShouldBe(5);

            // Second pass over an unchanged tree.
            var second = await ReindexAsync(io, src);
            second.Status.ShouldBe(Status.Success, $"second pass failed: {second.Error?.Message}");

            var afterSecond = await ShortnamesAsync(entries, space);
            afterSecond.ShouldBe(afterFirst, "a second reindex must not add, drop or duplicate rows");
        }
        finally { await CleanupAsync(spaces, space, src); }
    }

    // Reindexing onto a store that already holds part of the tree, with one
    // flat file changed since. The changed one must be UPDATED in place, not
    // duplicated — the unique (shortname, space_name, subpath) index is what
    // catches a rebuild that inserts instead of upserting.
    [FactIfImportSupported]
    public async Task Reindex_Over_Partial_Store_Updates_Changed_Entry_Without_Duplicating()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var (space, src) = BuildTree(entryCount: 3);
        try
        {
            (await ReindexAsync(io, src)).Status.ShouldBe(Status.Success);

            // Rewrite one meta with a new displayname, then reindex again with
            // replace semantics (preserveExisting: false).
            var metaPath = Path.Combine(src, space, ".dm", "e1", "meta.content.json");
            var original = await File.ReadAllTextAsync(metaPath);
            await File.WriteAllTextAsync(metaPath,
                original.Replace("\"e1\"", "\"e1\"", StringComparison.Ordinal)
                        .TrimEnd().TrimEnd('}')
                        + ",\"displayname\":{\"en\":\"rebuilt\"}}");

            (await ReindexAsync(io, src)).Status.ShouldBe(Status.Success);

            var all = await ShortnamesAsync(entries, space);
            all.Count.ShouldBe(3, "a changed meta must update its row, not add a second one");

            var changed = await entries.GetAsync(space, "/", "e1", ResourceType.Content);
            changed.ShouldNotBeNull();
            changed!.Displayname?.En.ShouldBe("rebuilt", "the changed meta must be reflected in the index");
        }
        finally { await CleanupAsync(spaces, space, src); }
    }

    // A walker that silently skips a resource type is the likely bug, and row
    // counts on `entries` alone would never show it. Assert each table.
    [FactIfImportSupported]
    public async Task Reindex_Populates_Every_Table()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var attachments = sp.GetRequiredService<AttachmentRepository>();
        var users = sp.GetRequiredService<UserRepository>();
        var access = sp.GetRequiredService<AccessRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var stamp = Guid.NewGuid().ToString("N")[..6];
        var space = "rix_all_" + stamp;
        var src = Path.Combine(Path.GetTempPath(), $"dmart-rix-{Guid.NewGuid():N}");
        var user = "rixu_" + stamp;
        var role = "rixr_" + stamp;
        var perm = "rixp_" + stamp;
        try
        {
            WriteSpace(src, space);
            WriteEntry(src, space, "e1");
            WriteAttachment(src, space, "e1", "a1");
            WriteManagement(src, space, "users", "meta.user.json", user,
                $",\"email\":\"{user}@example.com\",\"password\":\"x\"");
            WriteManagement(src, space, "roles", "meta.role.json", role, ",\"permissions\":[]");
            WriteManagement(src, space, "permissions", "meta.permission.json", perm,
                ",\"subpaths\":{\"" + space + "\":[\"/\"]},\"resource_types\":[\"content\"],\"actions\":[\"view\"]");
            WriteHistory(src, space, "e1");

            var resp = await ReindexAsync(io, src);
            resp.Status.ShouldBe(Status.Success, $"reindex failed: {resp.Error?.Message}");

            (await spaces.GetAsync(space)).ShouldNotBeNull("spaces table not populated");
            (await entries.GetAsync(space, "/", "e1", ResourceType.Content)).ShouldNotBeNull("entries table not populated");
            (await attachments.ListForParentAsync(space, "/", "e1")).Count
                .ShouldBeGreaterThan(0, "attachments table not populated");
            (await users.GetByShortnameAsync(user)).ShouldNotBeNull("users table not populated");
            (await access.GetRoleAsync(role)).ShouldNotBeNull("roles table not populated");
            (await access.GetPermissionAsync(perm)).ShouldNotBeNull("permissions table not populated");
            ((int)resp.Attributes!["histories_inserted"]!)
                .ShouldBeGreaterThan(0, "histories table not populated");
        }
        finally
        {
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await users.DeleteAsync(user); } catch { }
            await CleanupAsync(spaces, space, src);
        }
    }

    // The wildcard index is maintained by triggers on SQLite (FTS5) and by a
    // GIN/trigram index on PostgreSQL. Neither is written by the import code,
    // so a rebuild that lands rows but leaves the index empty would pass every
    // assertion above and still return nothing for a wildcard search. Query
    // through the search path to prove the index actually got populated.
    [FactIfImportSupported]
    public async Task Wildcard_Search_Finds_Reindexed_Entries()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var stamp = Guid.NewGuid().ToString("N")[..6];
        var space = "rix_fts_" + stamp;
        var src = Path.Combine(Path.GetTempPath(), $"dmart-rix-{Guid.NewGuid():N}");
        // A distinctive token so the match cannot come from unrelated rows.
        var needle = "zqxwv" + stamp;
        try
        {
            WriteSpace(src, space);
            // The needle goes in the PAYLOAD: that is the column SQLite's FTS5
            // external-content table indexes and the one PostgreSQL's trigram
            // GIN index covers, so this is what actually proves the wildcard
            // index was maintained during the rebuild.
            WriteEntry(src, space, "e1",
                extra: $",\"payload\":{{\"content_type\":\"json\",\"body\":{{\"note\":\"{needle}\"}}}}");
            WriteEntry(src, space, "e2");

            (await ReindexAsync(io, src)).Status.ShouldBe(Status.Success);

            // The repository overload, not QueryService: the ACL gate is a
            // separate concern and a freshly reindexed space has no permission
            // rows pointing at it, so going through the service would assert
            // authorization rather than the index.
            var hits = await entries.QueryAsync(new Dmart.Models.Api.Query
            {
                Type = QueryType.Search,
                SpaceName = space,
                Subpath = "/",
                // The FIELD-SCOPED wildcard form, not a bare term: this is the
                // one that emits the prefilter conjunct — FTS5 MATCH on SQLite,
                // the pg_trgm GIN lookup on PostgreSQL. A bare term would fall
                // back to a plain ILIKE over payload::text and would still pass
                // with a completely empty index.
                Search = $"@payload.body.note:*{needle}*",
                Limit = 10,
            });

            hits.Count.ShouldBe(1,
                "the wildcard index was not populated by the reindex — rows landed but are unsearchable");
            hits[0].Shortname.ShouldBe("e1");
        }
        finally { await CleanupAsync(spaces, space, src); }
    }

    // A flat file removed since the last run leaves its row behind: reindex
    // ADDS and UPDATES, it does not prune. This matches Python dmart, where
    // `create_index` is additive and removal happens through the delete API
    // (which unlinks the file and the row together). Pinned as a test because
    // the opposite assumption — "reindex makes the store match the disk" — is
    // the natural one to make, and it is wrong.
    [FactIfImportSupported]
    public async Task Reindex_Does_Not_Prune_Rows_Whose_File_Is_Gone()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var (space, src) = BuildTree(entryCount: 3);
        try
        {
            (await ReindexAsync(io, src)).Status.ShouldBe(Status.Success);
            (await ShortnamesAsync(entries, space)).Count.ShouldBe(3);

            Directory.Delete(Path.Combine(src, space, ".dm", "e2"), recursive: true);

            (await ReindexAsync(io, src)).Status.ShouldBe(Status.Success);

            var after = await ShortnamesAsync(entries, space);
            after.Count.ShouldBe(3, "reindex is additive — a deleted file does not remove its row");
            after.ShouldContain("e2");
        }
        finally { await CleanupAsync(spaces, space, src); }
    }

    // The PostgreSQL-only load options are refused with a reason rather than
    // ignored. Ignoring them would give the operator a slower (or differently
    // durable) import than the command line asked for, with no way to tell.
    [FactIfImportSupported]
    public async Task Postgres_Only_Load_Options_Are_Refused_On_Sqlite()
    {
        if (!DmartFactory.UseSqlite) return;   // the options are valid on PostgreSQL

        var sp = _factory.Services;
        _factory.CreateClient();
        var io = sp.GetRequiredService<ImportExportService>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var (space, src) = BuildTree(entryCount: 1);
        try
        {
            var fast = await io.ImportFolderAsync(src, actor: null,
                preserveExisting: false, fastUnsafeNoFkCheck: true, fastParallelism: 1,
                batchSize: ImportExportService.DefaultBatchSize);
            fast.Status.ShouldBe(Status.Failed);
            fast.Error!.Message.ShouldContain("--fast");

            var drop = await io.ImportFolderAsync(src, actor: null,
                preserveExisting: false, fastUnsafeNoFkCheck: false, fastParallelism: 1,
                batchSize: ImportExportService.DefaultBatchSize, dropIndexes: true);
            drop.Status.ShouldBe(Status.Failed);
            drop.Error!.Message.ShouldContain("--drop-indexes");
        }
        finally { await CleanupAsync(spaces, space, src); }
    }

    // ====================================================================
    // Helpers
    // ====================================================================

    private static Task<Response> ReindexAsync(ImportExportService io, string src)
        => io.ImportFolderAsync(src, actor: null,
            preserveExisting: false, fastUnsafeNoFkCheck: false, fastParallelism: 1,
            batchSize: ImportExportService.DefaultBatchSize);

    private static async Task<List<string>> ShortnamesAsync(EntryRepository entries, string space)
    {
        var rows = await entries.QueryAsync(new Dmart.Models.Api.Query
        {
            Type = QueryType.Subpath,
            SpaceName = space,
            Subpath = "/",
            Limit = 100,
        });
        return rows.Select(e => e.Shortname).OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private static (string Space, string Src) BuildTree(int entryCount)
    {
        var space = "rix_" + Guid.NewGuid().ToString("N")[..8];
        var src = Path.Combine(Path.GetTempPath(), $"dmart-rix-{Guid.NewGuid():N}");
        WriteSpace(src, space);
        for (var i = 1; i <= entryCount; i++) WriteEntry(src, space, $"e{i}");
        return (space, src);
    }

    private static void WriteSpace(string src, string space)
    {
        var dm = Path.Combine(src, space, ".dm");
        Directory.CreateDirectory(dm);
        File.WriteAllText(Path.Combine(dm, "meta.space.json"),
            $$"""{"uuid":"{{Guid.NewGuid()}}","shortname":"{{space}}","is_active":true,"owner_shortname":"dmart","languages":["english"]}""");
    }

    private static void WriteEntry(string src, string space, string shortname, string extra = "")
    {
        var dir = Path.Combine(src, space, ".dm", shortname);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.content.json"),
            $$"""{"uuid":"{{Guid.NewGuid()}}","shortname":"{{shortname}}","is_active":true,"owner_shortname":"dmart"{{extra}}}""");
    }

    private static void WriteAttachment(string src, string space, string parent, string shortname)
    {
        var dir = Path.Combine(src, space, ".dm", parent, "attachments.comment");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"meta.{shortname}.json"),
            $$"""{"uuid":"{{Guid.NewGuid()}}","shortname":"{{shortname}}","is_active":true,"owner_shortname":"dmart","body":"hi"}""");
    }

    private static void WriteManagement(
        string src, string space, string folder, string metaName, string shortname, string extra)
    {
        var dir = Path.Combine(src, space, folder, ".dm", shortname);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, metaName),
            $$"""{"uuid":"{{Guid.NewGuid()}}","shortname":"{{shortname}}","is_active":true,"owner_shortname":"dmart"{{extra}}}""");
    }

    private static void WriteHistory(string src, string space, string shortname)
        => File.WriteAllText(
            Path.Combine(src, space, ".dm", shortname, "history.jsonl"),
            "{\"owner_shortname\":\"dmart\",\"diff\":{\"a\":1}}\n");

    private static async Task CleanupAsync(SpaceRepository spaces, string space, string src)
    {
        try { await spaces.DeleteAsync(space); } catch { }
        try { Directory.Delete(src, recursive: true); } catch { }
    }
}
