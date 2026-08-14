using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The Parquet export end to end: database -> files on disk -> entries back.
//
// The unit tests cover the format and the column mapping. What only a real
// export can show is whether the pieces are wired together — that the pager
// feeds the writer, that row groups flush at the boundary AND at the tail, and
// that what lands on disk is what the manifest claims.
public class ParquetExportTests : IClassFixture<DmartFactory>, IDisposable
{
    private readonly DmartFactory _factory;
    private readonly int _originalRowGroup = ParquetArchiveService.RowGroupRows;
    private readonly List<string> _dirs = [];

    public ParquetExportTests(DmartFactory factory) => _factory = factory;

    public void Dispose()
    {
        ParquetArchiveService.RowGroupRows = _originalRowGroup;
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dmart-pqx-{Guid.NewGuid():N}");
        _dirs.Add(dir);
        return dir;
    }

    [FactIfPg]
    public async Task Exports_Entries_And_Reads_Them_Back()
    {
        await WithSpaceAsync(5, async (svc, space, shortnames) =>
        {
            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

            manifest.RowsIn("entries").ShouldBe(5);
            manifest.SpaceName.ShouldBe(space);
            File.Exists(Path.Combine(dir, "manifest.json")).ShouldBeTrue();

            // Hive-style partitioning is what DuckDB and Spark expect, and it is
            // the unit of a per-space restore — see design §4.1.
            File.Exists(Path.Combine(dir, "entries", $"space_name={space}", "part-00000.parquet"))
                .ShouldBeTrue("the layout is part of the format, not an implementation detail");

            var back = ParquetArchiveService.ReadEntries(dir);
            back.Count.ShouldBe(5);
            back.Select(e => e.Shortname).OrderBy(x => x, StringComparer.Ordinal)
                .ShouldBe(shortnames.OrderBy(x => x, StringComparer.Ordinal));
            back.ShouldAllBe(e => e.SpaceName == space);
        });
    }

    // Row groups are the unit of writer memory, so the boundary logic has to
    // hold on real data: 13 rows over groups of 5 means two full groups and a
    // short tail, which is the case a writer that only flushes when full drops.
    [FactIfPg]
    public async Task Exports_Every_Row_Across_Row_Group_Boundaries()
    {
        ParquetArchiveService.RowGroupRows = 5;

        await WithSpaceAsync(13, async (svc, space, shortnames) =>
        {
            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

            manifest.RowsIn("entries").ShouldBe(13, "the tail row group must be flushed too");

            var back = ParquetArchiveService.ReadEntries(dir);
            back.Count.ShouldBe(13);
            back.Select(e => e.Shortname).OrderBy(x => x, StringComparer.Ordinal)
                .ShouldBe(shortnames.OrderBy(x => x, StringComparer.Ordinal));
        });
    }

    // The off-by-one companion: a total that lands exactly on the boundary must
    // not flush an empty tail group, which the encoder rejects outright.
    [FactIfPg]
    public async Task Exports_An_Exact_Multiple_Of_The_Row_Group_Size()
    {
        ParquetArchiveService.RowGroupRows = 4;

        await WithSpaceAsync(8, async (svc, space, _) =>
        {
            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

            manifest.RowsIn("entries").ShouldBe(8);
            ParquetArchiveService.ReadEntries(dir).Count.ShouldBe(8);
        });
    }

    // No rows still has to produce a valid, readable export. A restore must be
    // able to tell "nothing matched" from "the export failed", and an absent
    // file cannot express that difference.
    [FactIfPg]
    public async Task An_Empty_Space_Produces_A_Valid_Empty_Export()
    {
        await WithSpaceAsync(0, async (svc, space, _) =>
        {
            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

            manifest.RowsIn("entries").ShouldBe(0);
            ParquetArchiveService.ReadEntries(dir).ShouldBeEmpty();
        });
    }

    // The manifest is written separately from the files, so a disagreement
    // means one of them is wrong — a truncated copy being the likely cause, and
    // exactly what a restore must refuse rather than silently accept.
    [FactIfPg]
    public async Task A_Manifest_That_Disagrees_With_The_Files_Is_Refused()
    {
        await WithSpaceAsync(3, async (svc, space, _) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var manifestPath = Path.Combine(dir, "manifest.json");
            await File.WriteAllTextAsync(manifestPath,
                (await File.ReadAllTextAsync(manifestPath)).Replace("\"row_count\": 3", "\"row_count\": 99"));

            Should.Throw<InvalidDataException>(() => ParquetArchiveService.ReadEntries(dir))
                  .Message.ShouldContain("99");
        });
    }

    // A file written by a newer build may have a layout this one cannot see.
    // Reading it anyway and producing partial results is the failure a restore
    // can least afford.
    [FactIfPg]
    public async Task A_Future_Format_Version_Is_Refused()
    {
        await WithSpaceAsync(2, async (svc, space, _) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var manifestPath = Path.Combine(dir, "manifest.json");
            await File.WriteAllTextAsync(manifestPath,
                (await File.ReadAllTextAsync(manifestPath)).Replace("\"format_version\": 1", "\"format_version\": 99"));

            Should.Throw<NotSupportedException>(() => ParquetArchiveService.ReadEntries(dir));
        });
    }

    // The watermark is stamped BEFORE any row is read, so a later incremental
    // run overlaps this export rather than starting after it (§5.1). An import
    // upserts, so a re-shipped row is free; a missed one is silent corruption.
    [FactIfPg]
    public async Task The_Watermark_Precedes_The_Export()
    {
        await WithSpaceAsync(2, async (svc, space, _) =>
        {
            var before = DateTime.UtcNow;
            var manifest = await svc.ExportAsync(NewDir(), space, "/", actor: null);

            manifest.Watermark.ShouldBeLessThanOrEqualTo(manifest.CreatedAt);
            manifest.Watermark.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
        });
    }

    // The export must be readable by the tools the format was chosen for, not
    // only by us.
    [FactIfPg]
    public async Task PyArrow_Can_Read_The_Exported_File()
    {
        await WithSpaceAsync(4, async (svc, space, _) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);
            var file = Path.Combine(dir, "entries", $"space_name={space}", "part-00000.parquet");

            if (!Unit.Parquet.PyArrow.Available) return;   // covered elsewhere; not a failure here

            var table = Unit.Parquet.PyArrow.ReadTable(file);
            table.GetProperty("shortname").EnumerateArray().Count().ShouldBe(4);
            table.GetProperty("space_name").EnumerateArray()
                 .ShouldAllBe(x => x.GetString() == space);
        });
    }

    // Query.Limit defaults to 10, so a hand-built Query is a bounded SAMPLE,
    // not an export. That default is why the space/subpath overload above
    // exists; this pins that the explicit form still means what it says.
    [FactIfPg]
    public async Task An_Explicit_Query_Limit_Is_Still_Honoured()
    {
        await WithSpaceAsync(13, async (svc, space, _) =>
        {
            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, QueryFor(space) with { Limit = 6 }, actor: null);
            manifest.RowsIn("entries").ShouldBe(6, "an explicit limit must cap the export");
        });
    }

    // ---- histories ----

    // The audit trail must come back EXACTLY as it went out. Every other table
    // describes current state, which a later export would legitimately correct;
    // history describes past events. A row restored with a regenerated uuid or
    // a re-stamped timestamp is a record of the RESTORE, which is worse than no
    // history at all because it looks authentic.
    [FactIfPg]
    public async Task History_Restores_With_Its_Original_Uuid_And_Timestamp()
    {
        await WithSpaceAsync(1, async (svc, space, shortnames) =>
        {
            var histories = _factory.Services.GetRequiredService<HistoryRepository>();
            await histories.AppendAsync(space, "/", shortnames[0], "alice",
                new Dictionary<string, object> { ["ua"] = "test" },
                new Dictionary<string, object> { ["slug"] = "changed" });

            var before = (await histories.ListForSpacePagedAsync(space, 100, 0)).Single();

            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);
            manifest.RowsIn("histories").ShouldBe(1);

            await WipeHistoryAsync(space);
            var result = await svc.ImportAsync(dir);
            result.For("histories").Imported.ShouldBe(1);

            var after = (await histories.ListForSpacePagedAsync(space, 100, 0)).Single();
            after.Uuid.ShouldBe(before.Uuid, "a regenerated uuid is a new event, not a restored one");

            // Compared at MICROSECOND granularity, not tick-for-tick. Parquet
            // TIMESTAMP_MICROS holds 6 decimal places; .NET DateTime holds 7.
            // On PostgreSQL this is invisible — its timestamp column is already
            // microsecond, so the value was rounded before it reached the file.
            // SQLite keeps the full tick, so a round trip there drops the last
            // digit. Accepted and documented (§4.2); asserting tick equality
            // would pass on one driver and fail on the other for a difference
            // no audit trail depends on.
            //
            // A RE-STAMPED row, which is the failure this guards against, is off
            // by milliseconds at least — orders of magnitude coarser than this
            // tolerance — so the check still bites. Mutation-verified: replacing
            // row.Timestamp with TimeUtils.Now() fails this test.
            ToMicroseconds(after.Timestamp).ShouldBe(ToMicroseconds(before.Timestamp),
                "a re-stamped row records the restore, not the change");
            after.OwnerShortname.ShouldBe("alice");
            after.Shortname.ShouldBe(shortnames[0]);
            after.Diff!["slug"].ToString().ShouldBe("changed");
            after.RequestHeaders!["ua"].ToString().ShouldBe("test");
        });
    }

    // History is append-only, so re-importing must not duplicate the trail —
    // and must not need to know whether a previous run finished.
    [FactIfPg]
    public async Task Re_Importing_History_Skips_Rather_Than_Duplicating()
    {
        await WithSpaceAsync(1, async (svc, space, shortnames) =>
        {
            var histories = _factory.Services.GetRequiredService<HistoryRepository>();
            for (var i = 0; i < 3; i++)
                await histories.AppendAsync(space, "/", shortnames[0], "bob", null,
                    new Dictionary<string, object> { ["n"] = i });

            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            // Rows are still present, so every one is a no-op on conflict.
            var first = await svc.ImportAsync(dir);
            first.For("histories").Skipped.ShouldBe(3);
            first.For("histories").Imported.ShouldBe(0);

            (await histories.ListForSpacePagedAsync(space, 100, 0)).Count
                .ShouldBe(3, "the trail must not grow on re-import");
        });
    }

    // Timestamps collide freely — one request touching several resources writes
    // several rows with the same stamp — so paging orders by uuid. Ordering by
    // a non-unique column skips or repeats rows as the window advances.
    [FactIfPg]
    public async Task History_Pages_Without_Losing_Rows_To_Colliding_Timestamps()
    {
        ParquetArchiveService.HistoryPageSize = 4;
        try
        {
            await WithSpaceAsync(1, async (svc, space, shortnames) =>
            {
                var histories = _factory.Services.GetRequiredService<HistoryRepository>();
                for (var i = 0; i < 11; i++)
                    await histories.AppendAsync(space, "/", shortnames[0], "carol", null,
                        new Dictionary<string, object> { ["n"] = i });

                var dir = NewDir();
                var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

                manifest.RowsIn("histories").ShouldBe(11,
                    "11 rows over pages of 4 — none dropped, none repeated");

                await WipeHistoryAsync(space);
                (await svc.ImportAsync(dir)).For("histories").Imported.ShouldBe(11);
            });
        }
        finally { ParquetArchiveService.HistoryPageSize = 10_000; }
    }

    // Truncates to the precision Parquet TIMESTAMP_MICROS actually stores.
    private static long ToMicroseconds(DateTime t) => t.Ticks / 10;

    private async Task WipeHistoryAsync(string space)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM histories WHERE space_name = '{space}'";
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- attachments and blobs ----

    // Media bytes live in blobs/<sha[0:2]>/<sha>, not in the row group (§4.3),
    // and the row keeps the content address instead.
    [FactIfPg]
    public async Task Attachment_Media_Goes_To_The_Blob_Store_Not_The_Row_Group()
    {
        await WithSpaceAsync(1, async (svc, space, shortnames) =>
        {
            var media = System.Text.Encoding.UTF8.GetBytes("The quick brown fox jumps over the lazy dog");
            await AddAttachmentAsync(space, shortnames[0], "att1", media);

            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

            manifest.RowsIn("attachments").ShouldBe(1);
            manifest.BlobCount.ShouldBe(1);
            manifest.BlobBytes.ShouldBe(media.Length);

            // The published sha256 of that pangram — a value from outside this
            // codebase, so a wrong hashing step (encoding, casing, truncation)
            // shows up here rather than agreeing with itself.
            const string sha = "d7a8fbb307d7809469ca9abcb0082e4f8d5651e46d3cdb762d02d0bf37c9e592";
            File.Exists(Path.Combine(dir, "blobs", "d7", sha))
                .ShouldBeTrue("the blob must be stored under its own sha256");

            File.ReadAllBytes(Path.Combine(dir, "blobs", "d7", sha)).ShouldBe(media);
        });
    }

    // The dedup lever: the same bytes attached twice are stored ONCE. This is
    // what makes increments cheap, so it is worth asserting rather than
    // assuming.
    [FactIfPg]
    public async Task Identical_Media_Is_Stored_Once()
    {
        await WithSpaceAsync(2, async (svc, space, shortnames) =>
        {
            var media = System.Text.Encoding.UTF8.GetBytes("shared attachment content");
            await AddAttachmentAsync(space, shortnames[0], "a", media);
            await AddAttachmentAsync(space, shortnames[1], "b", media);

            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

            manifest.RowsIn("attachments").ShouldBe(2, "both attachment ROWS are exported");
            manifest.BlobCount.ShouldBe(1, "but the identical bytes are stored once");

            Directory.EnumerateFiles(Path.Combine(dir, "blobs"), "*", SearchOption.AllDirectories)
                     .Count().ShouldBe(1);
        });
    }

    [FactIfPg]
    public async Task Attachments_Restore_With_Their_Media_Intact()
    {
        await WithSpaceAsync(1, async (svc, space, shortnames) =>
        {
            var media = System.Text.Encoding.UTF8.GetBytes("restore me byte for byte");
            await AddAttachmentAsync(space, shortnames[0], "att1", media);

            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var repo = _factory.Services.GetRequiredService<AttachmentRepository>();
            await DeleteAttachmentAsync(space, shortnames[0], "att1");

            var result = await svc.ImportAsync(dir);
            result.For("attachments").Imported.ShouldBe(1);
            result.For("attachments").Failed.ShouldBe(0);

            var back = await repo.GetAsync(space, $"/{shortnames[0]}", "att1");
            back.ShouldNotBeNull();
            back.Media.ShouldBe(media, "media must survive the blob round trip byte for byte");
        });
    }

    // A corrupted blob must fail loudly. An attachment restored with silently
    // empty or wrong bytes is undetectable afterwards — nothing downstream
    // checks them — so this is the one place a hard failure is the safe outcome.
    [FactIfPg]
    public async Task A_Corrupted_Blob_Fails_The_Restore_Rather_Than_Restoring_Garbage()
    {
        await WithSpaceAsync(1, async (svc, space, shortnames) =>
        {
            await AddAttachmentAsync(space, shortnames[0], "att1",
                System.Text.Encoding.UTF8.GetBytes("original content"));

            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var blob = Directory.EnumerateFiles(Path.Combine(dir, "blobs"), "*", SearchOption.AllDirectories).Single();
            await File.WriteAllTextAsync(blob, "tampered");   // name no longer matches contents

            var repo = _factory.Services.GetRequiredService<AttachmentRepository>();
            await DeleteAttachmentAsync(space, shortnames[0], "att1");

            var result = await svc.ImportAsync(dir);
            result.For("attachments").Failed.ShouldBe(1, "a blob that does not hash to its name must not restore");
            result.For("attachments").Imported.ShouldBe(0);
        });
    }

    // An attachment with no media at all is a real case, and must not be
    // confused with one whose blob is missing.
    [FactIfPg]
    public async Task An_Attachment_Without_Media_Round_Trips()
    {
        await WithSpaceAsync(1, async (svc, space, shortnames) =>
        {
            await AddAttachmentAsync(space, shortnames[0], "nomedia", media: null);

            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);
            manifest.RowsIn("attachments").ShouldBe(1);
            manifest.BlobCount.ShouldBe(0, "no media means no blob");

            var repo = _factory.Services.GetRequiredService<AttachmentRepository>();
            await DeleteAttachmentAsync(space, shortnames[0], "nomedia");

            var result = await svc.ImportAsync(dir);
            result.For("attachments").Imported.ShouldBe(1);
            (await repo.GetAsync(space, $"/{shortnames[0]}", "nomedia"))!.Media.ShouldBeNull();
        });
    }

    // Deletes by uuid, which is the only key AttachmentRepository.DeleteAsync
    // accepts — so the row has to be looked up first.
    private async Task DeleteAttachmentAsync(string space, string parent, string shortname)
    {
        var repo = _factory.Services.GetRequiredService<AttachmentRepository>();
        var existing = await repo.GetAsync(space, $"/{parent}", shortname);
        if (existing is not null) await repo.DeleteAsync(Guid.Parse(existing.Uuid));
    }

    private async Task AddAttachmentAsync(string space, string parent, string shortname, byte[]? media)
    {
        var repo = _factory.Services.GetRequiredService<AttachmentRepository>();
        await repo.UpsertAsync(new Attachment
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = $"/{parent}",          // attachments hang off "<parent subpath>/<parent shortname>"
            ResourceType = ResourceType.Media,
            IsActive = true,
            OwnerShortname = "dmart",
            Media = media,
        });
    }

    // ---- global tables ----

    // Entries alone give you content in a system nobody can log into. These are
    // what make the export a backup rather than a table dump.
    [FactIfPg]
    public async Task Exports_The_Global_Tables_Alongside_Entries()
    {
        await WithSpaceAsync(2, async (svc, space, _) =>
        {
            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

            manifest.Tables.Select(t => t.Name).ShouldBe(
                ["entries", "attachments", "histories", "spaces", "users", "roles", "permissions"],
                ignoreOrder: true);

            foreach (var table in new[] { "spaces", "users", "roles", "permissions" })
                File.Exists(Path.Combine(dir, table, "part-00000.parquet"))
                    .ShouldBeTrue($"{table} must be written");

            // The space just created must be in there, or the export is not a
            // snapshot of the system it claims to describe.
            manifest.RowsIn("spaces").ShouldBeGreaterThan(0);
        });
    }

    // The password hash is the difference between a restore that recovers
    // logins and one that forces every user to reset. It is included
    // deliberately, and that makes the export directory credential material.
    [FactIfPg]
    public async Task Users_Export_Carries_The_Password_Hash()
    {
        await WithSpaceAsync(1, async (svc, space, _) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            if (!Unit.Parquet.PyArrow.Available) return;

            var table = Unit.Parquet.PyArrow.ReadTable(Path.Combine(dir, "users", "part-00000.parquet"));
            var hashes = table.GetProperty("password").EnumerateArray()
                .Where(x => x.ValueKind != System.Text.Json.JsonValueKind.Null)
                .Select(x => x.GetString()!)
                .ToList();

            hashes.ShouldNotBeEmpty("the seeded dmart user has a hash; an empty column means it was dropped");
            hashes.ShouldAllBe(h => h.StartsWith("$argon2"),
                "a hash that is not Argon2 means the wrong column was read");
        });
    }

    // Restoring users whose roles do not exist yet trips foreign keys or leaves
    // dangling references, so spaces/roles/permissions must land before users
    // and entries. This asserts the whole set restores together, which is the
    // observable consequence of getting that order right.
    [FactIfPg]
    public async Task Global_Tables_Restore_Without_Reference_Errors()
    {
        await WithSpaceAsync(2, async (svc, space, _) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var result = await svc.ImportAsync(dir, replaceExisting: true);

            result.Failed.ShouldBe(0, "a reference error would show up here as a failed row");
            result.For("users").Imported.ShouldBeGreaterThan(0, "users must restore");
            result.For("spaces").Imported.ShouldBeGreaterThan(0, "spaces must restore");
        });
    }

    // ---- restore ----

    // The claim the whole format rests on: an export can be imported. Rows are
    // deleted between export and import so the restore genuinely recreates
    // them rather than finding them already there.
    [FactIfPg]
    public async Task An_Export_Restores_Into_An_Empty_Space()
    {
        await WithSpaceAsync(6, async (svc, space, shortnames) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            foreach (var sn in shortnames)
                await entries.DeleteAsync(space, "/", sn, ResourceType.Content);

            var result = await svc.ImportAsync(dir);

            result.For("entries").Imported.ShouldBe(6);
            result.For("entries").Skipped.ShouldBe(0);
            result.Failed.ShouldBe(0);
            result.Total.ShouldBe(result.Imported + result.Skipped + result.Failed);

            // Restored from the archive, not merely reported as restored.
            foreach (var sn in shortnames)
                (await entries.GetAsync(space, "/", sn, ResourceType.Content))
                    .ShouldNotBeNull($"'{sn}' should have been restored");
        });
    }

    // A rerun must be idempotent. A restore pipeline that runs twice — after a
    // partial failure, say — must not need to know whether the first run
    // finished.
    [FactIfPg]
    public async Task Importing_Twice_Skips_Instead_Of_Duplicating()
    {
        await WithSpaceAsync(4, async (svc, space, _) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var first = await svc.ImportAsync(dir);
            first.For("entries").Skipped.ShouldBe(4, "the rows are still there, so all four are skipped");

            var second = await svc.ImportAsync(dir);
            second.For("entries").Skipped.ShouldBe(4);
            second.Failed.ShouldBe(0);
        });
    }

    // -r rewrites existing rows from the archive. Without it the archive's
    // values would be silently ignored for any row that already exists, which
    // is the wrong default for "restore this backup over the top".
    [FactIfPg]
    public async Task Replace_Rewrites_Existing_Rows_From_The_Archive()
    {
        await WithSpaceAsync(2, async (svc, space, shortnames) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            var target = shortnames[0];
            var live = (await entries.GetAsync(space, "/", target, ResourceType.Content))!;
            await entries.UpsertAsync(live with { Slug = "changed-after-export" });

            var result = await svc.ImportAsync(dir, replaceExisting: true);
            result.For("entries").Imported.ShouldBe(2);
            result.For("entries").Skipped.ShouldBe(0);

            (await entries.GetAsync(space, "/", target, ResourceType.Content))!
                .Slug.ShouldNotBe("changed-after-export", "the archive's value must win under -r");
        });
    }

    // ====================================================================

    private static Dmart.Models.Api.Query QueryFor(string space) => new()
    {
        Type = QueryType.Search,
        SpaceName = space,
        Subpath = "/",
        FilterSchemaNames = new(),
    };

    private async Task WithSpaceAsync(
        int entryCount, Func<ParquetArchiveService, string, List<string>, Task> body)
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var svc = sp.GetRequiredService<ParquetArchiveService>();
        var entries = sp.GetRequiredService<EntryRepository>();
        var spaces = sp.GetRequiredService<SpaceRepository>();

        var space = "pqx_" + Guid.NewGuid().ToString("N")[..8];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = space,
            SpaceName = space, Subpath = "/",
            IsActive = true, OwnerShortname = "dmart",
        });
        try
        {
            var shortnames = new List<string>();
            for (var i = 0; i < entryCount; i++)
            {
                var sn = $"e{i:D4}";
                shortnames.Add(sn);
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = sn,
                    SpaceName = space, Subpath = "/", ResourceType = ResourceType.Content,
                    IsActive = true, OwnerShortname = "dmart",
                    Tags = i % 2 == 0 ? ["even"] : [],
                    Slug = i % 3 == 0 ? $"slug-{i}" : null,
                });
            }
            await body(svc, space, shortnames);
        }
        finally { try { await spaces.DeleteAsync(space); } catch { } }
    }
}
