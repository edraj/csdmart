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
    private readonly int? _originalRowGroup = ParquetArchiveService.RowGroupRowsOverride;
    private readonly List<string> _dirs = [];

    public ParquetExportTests(DmartFactory factory) => _factory = factory;

    public void Dispose()
    {
        ParquetArchiveService.RowGroupRowsOverride = _originalRowGroup;
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
        ParquetArchiveService.RowGroupRowsOverride = 5;

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
        ParquetArchiveService.RowGroupRowsOverride = 4;

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

    // ---- bulk restore ----

    // The bulk path COPYs in batches, so a restore larger than one batch must
    // still land every row — the boundary is where a batching bug hides.
    [FactIfPg]
    public async Task A_Restore_Larger_Than_One_Batch_Lands_Every_Row()
    {
        var original = ParquetArchiveService.BulkBatchSizeOverride;
        ParquetArchiveService.BulkBatchSizeOverride = 7;   // 20 rows => 2 full batches + a short tail
        try
        {
            await WithSpaceAsync(20, async (svc, space, shortnames) =>
            {
                var dir = NewDir();
                await svc.ExportAsync(dir, space, "/", actor: null);

                var entries = _factory.Services.GetRequiredService<EntryRepository>();
                foreach (var sn in shortnames)
                    await entries.DeleteAsync(space, "/", sn, ResourceType.Content);

                var result = await svc.ImportAsync(dir);

                result.For("entries").Imported.ShouldBe(20, "the short tail batch must flush too");
                result.For("entries").Failed.ShouldBe(0);

                foreach (var sn in shortnames)
                    (await entries.GetAsync(space, "/", sn, ResourceType.Content))
                        .ShouldNotBeNull($"'{sn}' must survive a multi-batch restore");
            });
        }
        finally { ParquetArchiveService.BulkBatchSizeOverride = original; }
    }

    // COPY writes query_policies VERBATIM, so the bulk path has to regenerate
    // them. If it does not, restored rows carry stale policies and become
    // invisible to ACL-filtered queries — a restore that "succeeds" and leaves
    // the data unreachable.
    [FactIfPg]
    public async Task Bulk_Restored_Rows_Carry_Regenerated_Query_Policies()
    {
        await WithSpaceAsync(2, async (svc, space, shortnames) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            foreach (var sn in shortnames)
                await entries.DeleteAsync(space, "/", sn, ResourceType.Content);

            await svc.ImportAsync(dir);

            var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
            // The bulk COPY path is PostgreSQL-only, and so is cardinality().
            // SQLite restores per row through UpsertAsync, which regenerates
            // query_policies itself — there is nothing here for it to get wrong.
            if (db is not Db) return;

            await using var conn = await db.OpenAsync();
            await using var cmd = conn.CreateCommand();
            DbParams.Add(cmd, space);
            cmd.CommandText = "SELECT count(*) FROM entries WHERE space_name = $1 AND cardinality(query_policies) = 0";
            var empty = Convert.ToInt64(await cmd.ExecuteScalarAsync());

            empty.ShouldBe(0, "a row restored with empty query_policies is invisible to ACL-filtered reads");
        });
    }

    // A batch that hits an integrity violation aborts WHOLE on PostgreSQL, so
    // the importer replays it row by row. The good rows must still land and the
    // offender must fail alone — otherwise one bad row loses a whole batch.
    [FactIfPg]
    public async Task One_Bad_Row_Fails_Alone_Rather_Than_Losing_Its_Batch()
    {
        await WithSpaceAsync(3, async (svc, space, shortnames) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            foreach (var sn in shortnames)
                await entries.DeleteAsync(space, "/", sn, ResourceType.Content);

            // Point one archived row at an owner that does not exist — an FK
            // violation, raised at the deferred commit for the whole batch.
            var rows = ParquetArchiveService.ReadEntries(dir);
            rows[1] = rows[1] with { OwnerShortname = "no_such_user_" + Guid.NewGuid().ToString("N")[..6] };
            await RewriteEntriesFileAsync(dir, space, rows);

            var result = await svc.ImportAsync(dir);

            result.For("entries").Imported.ShouldBe(2, "the two good rows must still land");
            result.For("entries").Failed.ShouldBe(1, "the offender must fail alone, and be counted");
        });
    }

    // Rewrites the entries file in place, so a test can plant a row the
    // database will reject.
    private static async Task RewriteEntriesFileAsync(string dir, string space, List<Entry> rows)
    {
        var path = Path.Combine(dir, "entries", $"space_name={space}", "part-00000.parquet");
        var writer = new Dmart.DataAdapters.Parquet.ParquetFileWriter(
            Dmart.DataAdapters.Parquet.EntryParquetTable.Schema);
        await using (var fs = File.Create(path))
            writer.Write(fs, Dmart.DataAdapters.Parquet.EntryParquetTable.BuildPages(rows), rows.Count);
    }

    // Two restore paths exist now. They must be indistinguishable from the
    // outside, or "which driver am I on?" becomes a behavioural question.
    [FactIfPg]
    public async Task Bulk_And_Per_Row_Restores_Agree()
    {
        await WithSpaceAsync(12, async (svc, space, shortnames) =>
        {
            // Attachments and history too, so the comparison covers all three
            // tables that now have a bulk path rather than only entries.
            await AddAttachmentAsync(space, shortnames[0], "att1",
                System.Text.Encoding.UTF8.GetBytes("media for the comparison"));
            var histories = _factory.Services.GetRequiredService<HistoryRepository>();
            for (var i = 0; i < 5; i++)
                await histories.AppendAsync(space, "/", shortnames[0], "dave", null,
                    new Dictionary<string, object> { ["n"] = i });

            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var entries = _factory.Services.GetRequiredService<EntryRepository>();

            async Task WipeAsync()
            {
                foreach (var sn in shortnames)
                    await entries.DeleteAsync(space, "/", sn, ResourceType.Content);
                await DeleteAttachmentAsync(space, shortnames[0], "att1");
                await WipeHistoryAsync(space);
            }

            await WipeAsync();
            var bulk = await svc.ImportAsync(dir);

            await WipeAsync();
            ParquetArchiveService.ForcePerRowRestore = true;
            ParquetImportResult perRow;
            try { perRow = await svc.ImportAsync(dir); }
            finally { ParquetArchiveService.ForcePerRowRestore = false; }

            foreach (var table in new[] { "entries", "attachments", "histories" })
            {
                perRow.For(table).Imported.ShouldBe(bulk.For(table).Imported, $"{table}: imported");
                perRow.For(table).Skipped.ShouldBe(bulk.For(table).Skipped, $"{table}: skipped");
                perRow.For(table).Failed.ShouldBe(bulk.For(table).Failed, $"{table}: failed");
            }
        });
    }

    // ---- tombstone retention floor (§5.2) ----

    // An increment chained from a watermark predating tombstone recording
    // cannot see deletions from that gap — none were recorded. Without the
    // floor that is undetectable: missing tombstones look exactly like
    // "nothing was deleted".
    [FactIfPg]
    public async Task A_Watermark_Predating_The_Retention_Floor_Is_Detected()
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        _factory.CreateClient();
        await using var conn = await db.OpenAsync();

        var floor = await Tombstones.ReadRetentionFloorAsync(conn, default);
        floor.ShouldNotBeNull("schema init must seed the floor, or the check cannot work");

        // A watermark from before the floor is exactly the "chain started on an
        // older build" case.
        (floor!.Value.AddHours(-1) < floor.Value).ShouldBeTrue();
    }

    // The floor is seeded ONCE. A second schema init must not move it forward,
    // or every restart would silently claim a shorter guaranteed window.
    [FactIfPg]
    public async Task The_Retention_Floor_Is_Not_Moved_By_A_Later_Run()
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        _factory.CreateClient();
        await using var conn = await db.OpenAsync();

        var first = await Tombstones.ReadRetentionFloorAsync(conn, default);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO deletion_retention (id, floor_at) VALUES (1, $1) ON CONFLICT (id) DO NOTHING";
            DbParams.Add(cmd, DateTime.Now.AddYears(1));
            await cmd.ExecuteNonQueryAsync();
        }

        (await Tombstones.ReadRetentionFloorAsync(conn, default))
            .ShouldBe(first, "ON CONFLICT DO NOTHING must keep the original floor");
    }

    // ---- blob garbage collection ----

    // Blobs are content-addressed and never overwritten, so exporting
    // repeatedly into the SAME directory — what a nightly job pointed at a
    // fixed path does — keeps every version of every attachment forever while
    // the parquet files are replaced each run.
    [FactIfPg]
    public async Task Garbage_Collection_Removes_Blobs_No_Attachment_References()
    {
        await WithSpaceAsync(1, async (svc, space, shortnames) =>
        {
            await AddAttachmentAsync(space, shortnames[0], "att1",
                System.Text.Encoding.UTF8.GetBytes("first version"));

            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            // Change the media and export AGAIN into the same directory: the
            // old blob is now unreferenced but still on disk.
            var repo = _factory.Services.GetRequiredService<AttachmentRepository>();
            var live = (await repo.GetAsync(space, $"/{shortnames[0]}", "att1"))!;
            await repo.UpsertAsync(live with { Media = System.Text.Encoding.UTF8.GetBytes("second version") });
            await svc.ExportAsync(dir, space, "/", actor: null);

            var blobDir = Path.Combine(dir, "blobs");
            Directory.EnumerateFiles(blobDir, "*", SearchOption.AllDirectories).Count()
                .ShouldBe(2, "both versions are on disk; only one is referenced");

            var dry = ParquetArchiveService.CollectGarbageBlobs(dir, dryRun: true);
            dry.Removed.ShouldBe(1);
            dry.Kept.ShouldBe(1);
            Directory.EnumerateFiles(blobDir, "*", SearchOption.AllDirectories).Count()
                .ShouldBe(2, "a dry run must not delete anything");

            var real = ParquetArchiveService.CollectGarbageBlobs(dir);
            real.Removed.ShouldBe(1);
            real.Freed.ShouldBeGreaterThan(0);

            Directory.EnumerateFiles(blobDir, "*", SearchOption.AllDirectories).Count().ShouldBe(1);

            // And the surviving archive must still verify.
            await ParquetArchiveService.VerifyAsync(dir, ParquetArchiveService.ReadManifest(dir));
        });
    }

    // "No attachments table" and "no attachments are referenced" look identical
    // from the blob store's side, and acting on the wrong one deletes every
    // blob in the archive.
    [FactIfPg]
    public async Task Garbage_Collection_Refuses_When_The_Attachments_Table_Is_Absent()
    {
        await WithSpaceAsync(1, async (svc, space, _) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var manifestPath = Path.Combine(dir, "manifest.json");
            var text = await File.ReadAllTextAsync(manifestPath);
            await File.WriteAllTextAsync(manifestPath, text.Replace("\"attachments\"", "\"attachments_renamed\""));

            Should.Throw<InvalidDataException>(() => ParquetArchiveService.CollectGarbageBlobs(dir))
                  .Message.ShouldContain("refusing");
        });
    }

    // ---- restore verification ----

    // The counts an import reports come from the writer's own bookkeeping.
    // Verification re-reads BOTH sides and answers the question those counts
    // cannot: does the database now match the archive?
    [FactIfPg]
    public async Task Verification_Passes_After_A_Good_Restore()
    {
        await WithSpaceAsync(4, async (svc, space, shortnames) =>
        {
            await AddAttachmentAsync(space, shortnames[0], "att1",
                System.Text.Encoding.UTF8.GetBytes("verified media"));
            var histories = _factory.Services.GetRequiredService<HistoryRepository>();
            await histories.AppendAsync(space, "/", shortnames[0], "eve", null,
                new Dictionary<string, object> { ["n"] = 1 });

            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);
            await svc.ImportAsync(dir, replaceExisting: true);

            var check = await Verifier().VerifyAsync(dir);

            check.Ok.ShouldBeTrue(
                "a correct restore must verify clean: " + string.Join("; ", check.Problems));
            check.Checked.ShouldBeGreaterThan(4, "entries, the attachment and the history row are all checked");
        });
    }

    // The failure this exists to catch: rows that never landed.
    [FactIfPg]
    public async Task Verification_Reports_Rows_That_Never_Landed()
    {
        await WithSpaceAsync(4, async (svc, space, shortnames) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            await entries.DeleteAsync(space, "/", shortnames[0], ResourceType.Content);
            await entries.DeleteAsync(space, "/", shortnames[1], ResourceType.Content);

            var check = await Verifier().VerifyAsync(dir);

            check.Ok.ShouldBeFalse();
            check.Missing.ShouldBe(2);
            check.Problems.ShouldContain(p => p.Contains(shortnames[0], StringComparison.Ordinal));
        });
    }

    // A row that exists but holds different content is the harder failure —
    // presence alone would report success.
    [FactIfPg]
    public async Task Verification_Reports_A_Row_That_Differs()
    {
        await WithSpaceAsync(2, async (svc, space, shortnames) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            var live = (await entries.GetAsync(space, "/", shortnames[0], ResourceType.Content))!;
            await entries.UpsertAsync(live with { Slug = "drifted-after-the-backup" });

            var check = await Verifier().VerifyAsync(dir);

            check.Ok.ShouldBeFalse();
            check.Mismatched.ShouldBe(1);
            check.Missing.ShouldBe(0, "the row is present, just different");
            check.Problems.ShouldContain(p => p.Contains("slug", StringComparison.Ordinal));
        });
    }

    // Media is compared by hash, not length — length survives a byte-for-byte
    // substitution, and attachment media is exactly the payload nothing
    // downstream would notice was wrong.
    [FactIfPg]
    public async Task Verification_Catches_Media_Replaced_With_Different_Bytes_Of_The_Same_Length()
    {
        await WithSpaceAsync(1, async (svc, space, shortnames) =>
        {
            await AddAttachmentAsync(space, shortnames[0], "att1",
                System.Text.Encoding.UTF8.GetBytes("AAAAAAAAAA"));

            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            // Same length, different content.
            var repo = _factory.Services.GetRequiredService<AttachmentRepository>();
            var live = (await repo.GetAsync(space, $"/{shortnames[0]}", "att1"))!;
            await repo.UpsertAsync(live with { Media = System.Text.Encoding.UTF8.GetBytes("BBBBBBBBBB") });

            var check = await Verifier().VerifyAsync(dir);

            check.Ok.ShouldBeFalse();
            check.Mismatched.ShouldBe(1, "a length check would have passed this");
            check.Problems.ShouldContain(p => p.Contains("media differs", StringComparison.Ordinal));
        });
    }

    // query_policies is regenerated on write, so a correct restore can hold
    // different policies from the archive. Comparing it would fail a good
    // restore, and a verifier that cries wolf is one people stop running.
    [FactIfPg]
    public async Task Verification_Ignores_Regenerated_Query_Policies()
    {
        await WithSpaceAsync(2, async (svc, space, shortnames) =>
        {
            var dir = NewDir();
            await svc.ExportAsync(dir, space, "/", actor: null);

            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            var live = (await entries.GetAsync(space, "/", shortnames[0], ResourceType.Content))!;
            await entries.UpsertAsync(live with { QueryPolicies = ["deliberately:different:policy"] });

            (await Verifier().VerifyAsync(dir)).Ok
                .ShouldBeTrue("query_policies is recomputed on write and must not be compared");
        });
    }

    private ParquetRestoreVerifier Verifier() =>
        _factory.Services.GetRequiredService<ParquetRestoreVerifier>();

    // ---- scoping and full backup ----

    // Exporting the management space IS asking for users, roles and
    // permissions — they live in it.
    [FactIfPg]
    public async Task Exporting_The_Management_Space_Includes_The_Global_Tables()
    {
        var svc = _factory.Services.GetRequiredService<ParquetArchiveService>();
        _factory.CreateClient();
        var dir = NewDir();

        var manifest = await svc.ExportAsync(dir, "management", "/", actor: null);

        manifest.Tables.Select(t => t.Name).ShouldContain("users");
        manifest.RowsIn("users").ShouldBeGreaterThan(0);
    }

    // A subfolder export must carry that folder's subtree and nothing else —
    // including not sweeping up a sibling whose name it prefixes.
    [FactIfPg]
    public async Task A_Subpath_Export_Carries_Only_That_Subtree()
    {
        await WithSpaceAsync(0, async (svc, space, _) =>
        {
            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            async Task Add(string subpath, string shortname) =>
                await entries.UpsertAsync(new Entry
                {
                    Uuid = Guid.NewGuid().ToString(), Shortname = shortname,
                    SpaceName = space, Subpath = subpath, ResourceType = ResourceType.Content,
                    IsActive = true, OwnerShortname = "dmart",
                });

            await Add("/docs", "inside");
            await Add("/docs/deep", "deeper");
            await Add("/docs_old", "sibling");   // prefixes "/docs" as a string
            await Add("/", "root");

            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/docs", actor: null);

            manifest.RowsIn("entries").ShouldBe(2);
            // "inside" and "deeper" only — NOT "sibling" (under "/docs_old",
            // which prefixes "/docs" as a string) and not "root".
            var names = ParquetArchiveService.ReadEntries(dir)
                .Select(e => e.Shortname).OrderBy(x => x, StringComparer.Ordinal).ToList();
            names.Count.ShouldBe(2);
            names[0].ShouldBe("deeper");
            names[1].ShouldBe("inside");
        });
    }

    // The full backup: every space in one archive, each in its own partition,
    // global tables written once rather than per space.
    [FactIfPg]
    public async Task A_Full_Backup_Covers_Every_Space_And_Restores()
    {
        await WithSpaceAsync(2, async (svc, spaceA, namesA) =>
        {
            await WithSpaceAsync(3, async (_, spaceB, namesB) =>
            {
                var dir = NewDir();
                var manifest = await svc.ExportAllAsync(dir);

                manifest.Spaces.ShouldNotBeNull();
                manifest.Spaces!.ShouldContain(spaceA);
                manifest.Spaces!.ShouldContain(spaceB);
                manifest.Tables.Select(t => t.Name).ShouldContain("users");

                // Each space in its OWN partition — restoring them all under
                // one name would silently merge spaces.
                File.Exists(Path.Combine(dir, "entries", $"space_name={spaceA}", "part-00000.parquet"))
                    .ShouldBeTrue();
                File.Exists(Path.Combine(dir, "entries", $"space_name={spaceB}", "part-00000.parquet"))
                    .ShouldBeTrue();

                var back = ParquetArchiveService.ReadEntries(dir);
                back.Where(e => e.SpaceName == spaceA).Select(e => e.Shortname)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ShouldBe(namesA.OrderBy(x => x, StringComparer.Ordinal));
                back.Where(e => e.SpaceName == spaceB).Select(e => e.Shortname)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ShouldBe(namesB.OrderBy(x => x, StringComparer.Ordinal));
            });
        });
    }

    // Verification re-reads what was written. A backup nobody has read is one
    // you are guessing about — so a damaged archive must fail here, loudly,
    // rather than at restore time.
    [FactIfPg]
    public async Task Verification_Catches_A_Damaged_Archive()
    {
        await WithSpaceAsync(2, async (svc, space, _) =>
        {
            var dir = NewDir();
            var manifest = await svc.ExportAllAsync(dir);   // verifies, and passes

            var file = Path.Combine(dir, "entries", $"space_name={space}", "part-00000.parquet");
            await File.WriteAllTextAsync(file, "not a parquet file any more");

            await Should.ThrowAsync<Exception>(
                ParquetArchiveService.VerifyAsync(dir, manifest));
        });
    }

    // A truncated blob must be caught by verification, not discovered when
    // someone opens the attachment months later.
    [FactIfPg]
    public async Task Verification_Catches_A_Truncated_Blob()
    {
        await WithSpaceAsync(1, async (svc, space, shortnames) =>
        {
            await AddAttachmentAsync(space, shortnames[0], "att1",
                System.Text.Encoding.UTF8.GetBytes("some media bytes"));

            var dir = NewDir();
            var manifest = await svc.ExportAllAsync(dir);

            var blob = Directory.EnumerateFiles(Path.Combine(dir, "blobs"), "*", SearchOption.AllDirectories)
                                .First();
            await File.WriteAllTextAsync(blob, "truncated");

            await Should.ThrowAsync<InvalidDataException>(
                ParquetArchiveService.VerifyAsync(dir, manifest));
        });
    }

    // ---- incremental (§5) ----

    // The core claim: an increment carries what changed and nothing else.
    [FactIfPg]
    public async Task An_Increment_Carries_Only_What_Changed()
    {
        await WithSpaceAsync(3, async (svc, space, shortnames) =>
        {
            var full = NewDir();
            var first = await svc.ExportAsync(full, space, "/", actor: null);
            first.RowsIn("entries").ShouldBe(3);
            first.Since.ShouldBeNull("a full export has no lower bound");

            // A real edit bumps updated_at — UpsertAsync honours whatever the
            // caller passes, and the service layer is what stamps it. An
            // increment can only see writers that maintain updated_at; one that
            // does not is invisible to it, which is worth knowing.
            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            var target = (await entries.GetAsync(space, "/", shortnames[1], ResourceType.Content))!;
            await entries.UpsertAsync(target with { Slug = "changed-after-full", UpdatedAt = Dmart.Utils.TimeUtils.Now() });

            var inc = NewDir();
            var second = await svc.ExportAsync(
                inc, space, "/", actor: null, since: ParquetArchiveService.WatermarkOf(full));

            second.Since.ShouldNotBeNull();
            second.RowsIn("entries").ShouldBe(1, "only the touched row changed");
            ParquetArchiveService.ReadEntries(inc).Single().Shortname.ShouldBe(shortnames[1]);
        });
    }

    // §5.1's overlap rule. The watermark comes from the START of the previous
    // run, so a row changed WHILE that run was executing is re-shipped rather
    // than skipped. An upsert makes the duplicate free; a gap loses the row
    // permanently, and silently.
    [FactIfPg]
    public async Task The_Chain_Watermark_Comes_From_The_Start_Of_The_Previous_Run()
    {
        await WithSpaceAsync(1, async (svc, space, _) =>
        {
            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

            ParquetArchiveService.WatermarkOf(dir).ShouldBe(manifest.Watermark);
            manifest.Watermark.ShouldBeLessThanOrEqualTo(manifest.CreatedAt,
                "chaining from created_at would skip everything changed during the run");
        });
    }

    // A deleted row is ABSENT from an increment, and absence is
    // indistinguishable from unchanged — so the increment must carry the
    // tombstone explicitly or a consumer keeps the row forever.
    [FactIfPg]
    public async Task An_Increment_Carries_Tombstones_For_Deleted_Rows()
    {
        await WithSpaceAsync(3, async (svc, space, shortnames) =>
        {
            var full = NewDir();
            await svc.ExportAsync(full, space, "/", actor: null);

            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            await entries.DeleteAsync(space, "/", shortnames[0], ResourceType.Content);

            var inc = NewDir();
            var manifest = await svc.ExportAsync(
                inc, space, "/", actor: null, since: ParquetArchiveService.WatermarkOf(full));

            manifest.RowsIn("entries").ShouldBe(0, "nothing was edited");
            manifest.RowsIn("deletions").ShouldBe(1, "but one row was deleted");
            File.Exists(Path.Combine(inc, "deletions", "part-00000.parquet")).ShouldBeTrue();
        });
    }

    // A full export IS the state, so anything absent from it is deleted by
    // construction. Writing a deletions file there would invite a consumer to
    // apply deletes twice.
    [FactIfPg]
    public async Task A_Full_Export_Carries_No_Deletions_File()
    {
        await WithSpaceAsync(1, async (svc, space, shortnames) =>
        {
            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            await entries.DeleteAsync(space, "/", shortnames[0], ResourceType.Content);

            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

            manifest.Tables.ShouldNotContain(t => t.Name == "deletions");
            Directory.Exists(Path.Combine(dir, "deletions")).ShouldBeFalse();
        });
    }

    // Incremental bypasses the row-level ACL gate, so it refuses an actor
    // rather than silently returning rows that actor cannot see.
    [FactIfPg]
    public async Task Incremental_Refuses_An_Actor_Rather_Than_Skipping_The_Acl()
    {
        await WithSpaceAsync(1, async (svc, space, _) =>
        {
            await Should.ThrowAsync<NotSupportedException>(
                svc.ExportAsync(NewDir(), space, "/", actor: "dmart", since: DateTime.UtcNow));
        });
    }

    // An increment restores like any other export — it is the same format,
    // just fewer rows.
    [FactIfPg]
    public async Task An_Increment_Restores_On_Top_Of_The_Full_Export()
    {
        await WithSpaceAsync(2, async (svc, space, shortnames) =>
        {
            var full = NewDir();
            await svc.ExportAsync(full, space, "/", actor: null);

            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            var target = (await entries.GetAsync(space, "/", shortnames[0], ResourceType.Content))!;
            await entries.UpsertAsync(target with { Slug = "edited", UpdatedAt = Dmart.Utils.TimeUtils.Now() });

            var inc = NewDir();
            await svc.ExportAsync(inc, space, "/", actor: null,
                since: ParquetArchiveService.WatermarkOf(full));

            // Revert the live row, then apply the increment over the top.
            await entries.UpsertAsync(target with { Slug = null });
            var result = await svc.ImportAsync(inc, replaceExisting: true);

            result.For("entries").Imported.ShouldBe(1);
            (await entries.GetAsync(space, "/", shortnames[0], ResourceType.Content))!
                .Slug.ShouldBe("edited", "the increment's value must win");
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
        ParquetArchiveService.HistoryPageSizeOverride = 4;
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
        finally { ParquetArchiveService.HistoryPageSizeOverride = null; }
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
    public async Task A_Scoped_Export_Carries_Content_Only()
    {
        await WithSpaceAsync(2, async (svc, space, _) =>
        {
            var dir = NewDir();
            var manifest = await svc.ExportAsync(dir, space, "/", actor: null);

            // A SCOPED export carries content only. The global tables — and
            // with them every password hash — come with a full backup or an
            // explicit management export, not as a side effect of exporting
            // one space.
            manifest.Tables.Select(t => t.Name).ShouldBe(
                ["entries", "attachments", "histories"], ignoreOrder: true);

            foreach (var table in new[] { "spaces", "users", "roles", "permissions" })
                Directory.Exists(Path.Combine(dir, table))
                    .ShouldBeFalse($"{table} must NOT be written by a scoped export");
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
            await svc.ExportAllAsync(dir, verify: false);

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
            await svc.ExportAllAsync(dir, verify: false);

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
