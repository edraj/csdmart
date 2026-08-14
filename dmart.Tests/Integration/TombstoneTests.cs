using System.Data.Common;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Tombstones — docs/parquet-export-design.md §5.2.
//
// A deleted row is simply ABSENT, and absence is indistinguishable from
// unchanged. Without these an incremental consumer drifts from source
// permanently and never notices, which is why every one of these tests asserts
// on what was RECORDED rather than on what was removed: the removal is easy and
// the recording is the part that silently fails.
public class TombstoneTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public TombstoneTests(DmartFactory factory) => _factory = factory;

    private sealed record Tomb(string Table, string Space, string Subpath, string Shortname, string Type);

    private async Task<List<Tomb>> TombstonesForAsync(string space)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        DbParams.Add(cmd, space);
        cmd.CommandText = """
            SELECT table_name, space_name, subpath, shortname, resource_type
            FROM deletions WHERE space_name = $1 ORDER BY id
            """;
        var rows = new List<Tomb>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new Tomb(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return rows;
    }

    [FactIfPg]
    public async Task Deleting_An_Entry_Records_A_Tombstone()
    {
        await WithSpaceAsync(async space =>
        {
            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            await AddEntryAsync(space, "/", "doc1", ResourceType.Content);

            (await entries.DeleteAsync(space, "/", "doc1", ResourceType.Content)).ShouldBeTrue();

            var tombs = await TombstonesForAsync(space);
            tombs.Count.ShouldBe(1);
            tombs[0].ShouldBe(new Tomb("entries", space, "/", "doc1", "content"));
        });
    }

    // The resource type is recorded so a consumer can tell a deleted folder
    // from a deleted content row without joining anything — by which point the
    // row it would join to is gone.
    [FactIfPg]
    public async Task The_Tombstone_Carries_The_Resource_Type()
    {
        await WithSpaceAsync(async space =>
        {
            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            await AddEntryAsync(space, "/", "tkt", ResourceType.Ticket);
            await entries.DeleteAsync(space, "/", "tkt", ResourceType.Ticket);

            (await TombstonesForAsync(space)).Single().Type.ShouldBe("ticket");
        });
    }

    // §5.2 calls this the likeliest bug, so it gets the most specific test:
    // deleting a folder must tombstone the folder, every descendant entry at
    // every depth, and every attachment in the subtree.
    [FactIfPg]
    public async Task A_Folder_Cascade_Tombstones_Every_Descendant()
    {
        await WithSpaceAsync(async space =>
        {
            var entries = _factory.Services.GetRequiredService<EntryRepository>();

            await AddEntryAsync(space, "/", "docs", ResourceType.Folder);
            await AddEntryAsync(space, "/docs", "child1", ResourceType.Content);
            await AddEntryAsync(space, "/docs", "child2", ResourceType.Content);
            await AddEntryAsync(space, "/docs/nested", "deep", ResourceType.Content);
            // A sibling whose name PREFIXES the folder's path — it must not be
            // swept up. "/docs_old" starts with "/docs" as a string.
            await AddEntryAsync(space, "/", "docs_old", ResourceType.Folder);
            await AddEntryAsync(space, "/docs_old", "survivor", ResourceType.Content);

            await AddAttachmentAsync(space, "/docs/child1", "att1");
            await AddAttachmentAsync(space, "/docs_old/survivor", "att_survivor");

            await entries.DeleteFolderTreeWithDependentsAsync(space, "/", "docs");

            var tombs = await TombstonesForAsync(space);

            var deletedEntries = tombs.Where(t => t.Table == "entries")
                .Select(t => $"{t.Subpath}/{t.Shortname}").OrderBy(x => x, StringComparer.Ordinal).ToList();
            deletedEntries.ShouldBe(
                ["//docs", "/docs/child1", "/docs/child2", "/docs/nested/deep"],
                "the folder and every descendant at every depth must be tombstoned");

            tombs.Where(t => t.Table == "attachments").Select(t => t.Shortname)
                 .ShouldBe(["att1"], "subtree attachments must be tombstoned too");

            // The prefix-sibling must be untouched in BOTH directions.
            tombs.ShouldNotContain(t => t.Shortname == "survivor");
            tombs.ShouldNotContain(t => t.Shortname == "docs_old");
            tombs.ShouldNotContain(t => t.Shortname == "att_survivor");
        });
    }

    // A dryrun projects what a delete WOULD remove. It must not record
    // tombstones, or a consumer would replicate deletions that never happened.
    [FactIfPg]
    public async Task A_Dryrun_Records_Nothing()
    {
        await WithSpaceAsync(async space =>
        {
            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            await AddEntryAsync(space, "/", "docs", ResourceType.Folder);
            await AddEntryAsync(space, "/docs", "child", ResourceType.Content);

            var report = await entries.DeleteFolderTreeWithDependentsAsync(space, "/", "docs", dryRun: true);

            report.Entries.ShouldBe(2, "the dryrun still projects the real count");
            (await TombstonesForAsync(space)).ShouldBeEmpty("a dryrun deletes nothing, so it records nothing");
        });
    }

    [FactIfPg]
    public async Task Deleting_An_Attachment_Records_A_Tombstone()
    {
        await WithSpaceAsync(async space =>
        {
            var repo = _factory.Services.GetRequiredService<AttachmentRepository>();
            await AddEntryAsync(space, "/", "doc1", ResourceType.Content);
            await AddAttachmentAsync(space, "/doc1", "att1");

            var att = await repo.GetAsync(space, "/doc1", "att1");
            await repo.DeleteAsync(Guid.Parse(att!.Uuid));

            var tombs = await TombstonesForAsync(space);
            tombs.Count.ShouldBe(1);
            tombs[0].Table.ShouldBe("attachments");
            tombs[0].Shortname.ShouldBe("att1");
        });
    }

    // Deleting a row that does not exist must record nothing. A tombstone for a
    // row that was never there would make a consumer delete a live row it holds.
    [FactIfPg]
    public async Task Deleting_A_Missing_Row_Records_Nothing()
    {
        await WithSpaceAsync(async space =>
        {
            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            (await entries.DeleteAsync(space, "/", "never-existed", ResourceType.Content)).ShouldBeFalse();
            (await TombstonesForAsync(space)).ShouldBeEmpty();
        });
    }

    // Deleting a space removes an entire space's replicated content in one
    // statement. Without tombstones a consumer keeps every row of it forever,
    // with nothing to reconcile against.
    [FactIfPg]
    public async Task Deleting_A_Space_Tombstones_Its_Contents()
    {
        var space = "tomb_" + Guid.NewGuid().ToString("N")[..8];
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        _factory.CreateClient();
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });

        try
        {
            await AddEntryAsync(space, "/", "doc1", ResourceType.Content);
            await AddEntryAsync(space, "/", "doc2", ResourceType.Content);
            await AddAttachmentAsync(space, "/doc1", "att1");

            await spaces.DeleteAsync(space);

            var tombs = await TombstonesForAsync(space);
            tombs.Where(t => t.Table == "entries").Select(t => t.Shortname)
                 .OrderBy(x => x, StringComparer.Ordinal).ShouldBe(["doc1", "doc2"]);
            tombs.ShouldContain(t => t.Table == "attachments" && t.Shortname == "att1");
            tombs.ShouldContain(t => t.Table == "spaces" && t.Shortname == space);
        }
        finally { await CleanupAsync(space); }
    }

    // The least obvious path: the caller asked to delete a USER, and content
    // disappears as a side effect. A consumer that never learns those rows went
    // keeps them forever.
    [FactIfPg]
    public async Task Force_Deleting_A_User_Tombstones_The_Content_They_Owned()
    {
        await WithSpaceAsync(async space =>
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            var owner = "tombowner" + Guid.NewGuid().ToString("N")[..6];
            await users.UpsertAsync(new User
            {
                Uuid = Guid.NewGuid().ToString(), Shortname = owner,
                SpaceName = "management", Subpath = "/users",
                IsActive = true, OwnerShortname = "dmart",
            });

            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            await entries.UpsertAsync(new Entry
            {
                Uuid = Guid.NewGuid().ToString(), Shortname = "owned",
                SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
                IsActive = true, OwnerShortname = owner,
            });

            await users.ForceDeleteAsync(owner);

            (await TombstonesForAsync(space))
                .ShouldContain(t => t.Table == "entries" && t.Shortname == "owned",
                    "content removed as a side effect of a user delete must still be recorded");
        });
    }

    // ====================================================================

    private async Task AddEntryAsync(string space, string subpath, string shortname, ResourceType type)
    {
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = shortname,
            SpaceName = space, Subpath = subpath, ResourceType = type,
            IsActive = true, OwnerShortname = "dmart",
        });
    }

    private async Task AddAttachmentAsync(string space, string parentPath, string shortname)
    {
        var repo = _factory.Services.GetRequiredService<AttachmentRepository>();
        await repo.UpsertAsync(new Attachment
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = shortname,
            SpaceName = space, Subpath = parentPath, ResourceType = ResourceType.Media,
            IsActive = true, OwnerShortname = "dmart",
        });
    }

    private async Task WithSpaceAsync(Func<string, Task> body)
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var space = "tomb_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/", IsActive = true, OwnerShortname = "dmart",
        });
        try { await body(space); }
        finally { await CleanupAsync(space); }
    }

    private async Task CleanupAsync(string space)
    {
        try
        {
            var sp = _factory.Services;
            await sp.GetRequiredService<SpaceRepository>().DeleteAsync(space);
            var db = sp.GetRequiredService<IDbConnectionFactory>();
            await using var conn = await db.OpenAsync();
            await using var cmd = conn.CreateCommand();
            DbParams.Add(cmd, space);
            cmd.CommandText = "DELETE FROM deletions WHERE space_name = $1";
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best effort */ }
    }
}
