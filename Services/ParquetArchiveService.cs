using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dmart.DataAdapters.Parquet;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Dmart.Config;
using Dmart.Utils;

namespace Dmart.Services;

// The Parquet archive path, both directions — docs/parquet-export-design.md §4.
//
// Named "Archive" rather than "Export" because an export that cannot be
// imported is not a backup (§2.3), so the two halves belong together and are
// tested against each other.
//
// Separate from ImportExportService rather than a mode inside it, because it
// shares almost nothing with the zip format: different layout, different unit
// of work, different memory profile. What it does share is the PAGER, which is
// deliberately reused rather than reimplemented — the silent-truncation and
// unstable-sort traps ImportExportService.ForEachMatchAsync documents apply
// identically here, and a second copy would drift.
//
// Covers a FULL export of every table: entries, attachments (with their media
// blobs, §4.3), histories, spaces, users, roles and permissions. The
// incremental watermark (§5) is stamped but not yet USED to select rows.
//
// The users table carries the Argon2 PASSWORD HASH, so an export directory is
// credential material and needs the handling a database dump gets. That is a
// deliberate divergence from the zip export, which omits it and therefore
// cannot restore a login.
public sealed class ParquetArchiveService(
    IDbConnectionFactory db,
    ImportExportService importExport,
    EntryRepository entries,
    AttachmentRepository attachments,
    HistoryRepository histories,
    SpaceRepository spaces,
    UserRepository users,
    AccessRepository access,
    PermissionService perms,
    IOptions<DmartSettings> settingsOpt,
    ILogger<ParquetArchiveService> log)
{
    private string MgmtSpace => settingsOpt.Value.ManagementSpace;

    /// <summary>
    /// Rows per row group — §4.2's target, and the unit of writer memory. Only
    /// this many entries are resident at once, which is what keeps a multi-GB
    /// export bounded.
    /// </summary>
    /// <remarks>
    /// Settable for tests so the multi-row-group path runs on a handful of
    /// rows; exercising it at the real size would need 50,000 entries.
    /// Test-only, not thread-safe, and nothing in the request path writes it.
    /// </remarks>
    internal static int? RowGroupRowsOverride { get; set; }
    private int RowGroupRows => RowGroupRowsOverride ?? settingsOpt.Value.ParquetRowGroupRows;

    public const int FormatVersion = 1;

    /// <summary>
    /// Exports an entire space/subpath — the form a backup should use.
    /// </summary>
    /// <remarks>
    /// This overload exists because <see cref="Query.Limit"/> DEFAULTS TO 10.
    /// A caller who builds a Query by hand and forgets <c>Limit = 0</c> gets a
    /// ten-row export that reports success, which is the same silent-truncation
    /// shape as the 100k cap fixed in #156 — just with a smaller number. The
    /// unbounded intent belongs in a named method, not in a flag every caller
    /// has to remember.
    /// </remarks>
    /// <param name="since">
    /// When set, exports only rows changed at or after this instant, plus the
    /// tombstones recorded since then — an INCREMENTAL export (§5).
    /// </param>
    public Task<ParquetExportManifest> ExportAsync(
        string outputDirectory, string spaceName, string? subpath, string? actor,
        DateTime? since = null, bool forceGlobal = false, CancellationToken ct = default)
        => ExportAsync(outputDirectory, new Query
        {
            Type = QueryType.Search,
            SpaceName = spaceName,
            Subpath = subpath ?? "/",
            FilterSchemaNames = new(),
            Limit = 0,   // 0 = unbounded; ForEachMatchAsync pages to the end
            RetrieveJsonPayload = true,
        }, actor, since, forceGlobal, ct);

    /// <summary>
    /// Full backup: every space, plus the global tables, into one directory.
    /// </summary>
    /// <remarks>
    /// Each space lands in its own Hive partition, so the result is one
    /// directory a consumer reads with `hive_partitioning=true` and a restore
    /// replays whole. The global tables are written ONCE, not per space.
    ///
    /// Verification is on by default and re-reads every file and every blob
    /// before reporting success. It roughly doubles read I/O, which is the
    /// right trade for a backup: one that has never been read is one you are
    /// guessing about.
    /// </remarks>
    public async Task<ParquetExportManifest> ExportAllAsync(
        string outputDirectory, string? actor = null, DateTime? since = null,
        bool verify = true, CancellationToken ct = default)
    {
        var watermark = TimeUtils.Now();
        var all = await spaces.ListAsync(ct);
        if (all.Count == 0)
            log.LogWarning("parquet backup: no spaces found — the archive will hold only global tables");

        var tables = new List<ParquetTableManifest>();
        var exported = new List<string>();
        var blobCount = 0;
        long blobBytes = 0;

        foreach (var space in all)
        {
            ct.ThrowIfCancellationRequested();
            // Per space, content only. The global tables come once, below —
            // writing them per space would repeat every password hash N times.
            var m = await ExportAsync(outputDirectory, space.Shortname, "/", actor, since,
                                      forceGlobal: false, ct);
            foreach (var t in m.Tables) MergeTable(tables, t);
            blobCount += m.BlobCount;
            blobBytes += m.BlobBytes;
            exported.Add(space.Shortname);
        }

        // Global tables once, for the whole backup.
        var globals = await ExportGlobalTablesAsync(outputDirectory, ct);
        foreach (var t in globals) MergeTable(tables, t);

        var manifest = await WriteManifestAsync(
            outputDirectory, MgmtSpace, watermark, tables,
            tables.Sum(t => t.RowCount), blobCount, blobBytes, since, exported, ct);

        if (verify) await VerifyAsync(outputDirectory, manifest, ct);
        return manifest;
    }

    // Row counts and file lists accumulate across spaces; one entry per table.
    private static void MergeTable(List<ParquetTableManifest> into, ParquetTableManifest add)
    {
        var existing = into.FindIndex(t => t.Name == add.Name);
        if (existing < 0) { into.Add(add); return; }
        var merged = into[existing];
        into[existing] = merged with
        {
            Files = [.. merged.Files, .. add.Files],
            RowCount = merged.RowCount + add.RowCount,
        };
    }

    private async Task<List<ParquetTableManifest>> ExportGlobalTablesAsync(
        string outputDirectory, CancellationToken ct) =>
    [
        await WriteGlobalAsync(outputDirectory, "spaces", SpaceParquetTable.Schema,
            await spaces.ListAsync(ct), SpaceParquetTable.BuildPages, RowGroupRows, ct),
        await WriteGlobalAsync(outputDirectory, "users", UserParquetTable.Schema,
            await CollectAsync<User>("/users", q => users.QueryAsync(q, ct)),
            UserParquetTable.BuildPages, RowGroupRows, ct),
        await WriteGlobalAsync(outputDirectory, "roles", RoleParquetTable.Schema,
            await CollectAsync<Role>("/roles", q => access.QueryRolesAsync(q, ct)),
            RoleParquetTable.BuildPages, RowGroupRows, ct),
        await WriteGlobalAsync(outputDirectory, "permissions", PermissionParquetTable.Schema,
            await CollectAsync<Permission>("/permissions", q => access.QueryPermissionsAsync(q, ct)),
            PermissionParquetTable.BuildPages, RowGroupRows, ct),
    ];

    /// <summary>
    /// Deletes blobs no attachment row references. Returns what it removed.
    /// </summary>
    /// <remarks>
    /// Blobs are content-addressed and written only if absent, so nothing ever
    /// overwrites or removes one. Export repeatedly into the SAME directory —
    /// which a nightly job pointed at a fixed path does — and the blob store
    /// keeps every version of every attachment ever exported, while the parquet
    /// files are replaced each run. That grows without bound and the growth is
    /// invisible, because the manifest only counts what the LAST run wrote.
    ///
    /// Safety: the reference set is built from the archive's own attachment
    /// rows, so a blob is deleted only when nothing in THIS archive names it.
    /// Refuses to run when the attachments table is absent from the manifest,
    /// because "no attachments table" and "no attachments" would otherwise look
    /// the same and delete the entire blob store.
    /// </remarks>
    public static BlobGcResult CollectGarbageBlobs(
        string exportDirectory, bool dryRun = false, CancellationToken ct = default)
    {
        var manifest = ReadManifest(exportDirectory);
        if (!manifest.Tables.Any(t => t.Name == "attachments"))
            throw new InvalidDataException(
                "this archive has no attachments table, so the set of referenced blobs is unknown — "
                + "refusing to collect, because that is indistinguishable from 'no blobs are used'");

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ReadAttachmentRows(exportDirectory))
            if (row.MediaSha256 is { } sha) referenced.Add(sha);

        var root = Path.Combine(exportDirectory, BlobStore.DirectoryName);
        if (!Directory.Exists(root)) return new BlobGcResult(0, 0, 0);

        int kept = 0, removed = 0;
        long freed = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);

            // A half-written temp file from an interrupted export is garbage by
            // definition — it is not named for its own hash, so nothing can
            // reference it.
            var orphaned = name.Contains(".tmp-", StringComparison.Ordinal) || !referenced.Contains(name);
            if (!orphaned) { kept++; continue; }

            var size = new FileInfo(file).Length;
            if (!dryRun) File.Delete(file);
            removed++;
            freed += size;
        }

        return new BlobGcResult(kept, removed, freed);
    }

    /// <summary>
    /// Re-reads every file and every blob an export wrote, verifying blob
    /// contents against their own names.
    /// </summary>
    /// <remarks>
    /// Deliberately reads the files back through the READER rather than
    /// trusting the writer's own row counts: a writer that miscounted would
    /// agree with itself. Throws on the first failure, because a backup that is
    /// partially readable is not one an operator should be told is fine.
    /// </remarks>
    public static async Task VerifyAsync(
        string exportDirectory, ParquetExportManifest manifest, CancellationToken ct = default)
    {
        foreach (var table in manifest.Tables)
        {
            long rows = 0;
            foreach (var relative in table.Files)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(exportDirectory, relative);
                if (!File.Exists(path))
                    throw new InvalidDataException($"{table.Name}: '{relative}' is missing from the archive");
                rows += ParquetFileReader.ReadFile(path).RowCount;
            }
            if (rows != table.RowCount)
                throw new InvalidDataException(
                    $"{table.Name}: manifest claims {table.RowCount} rows, the files hold {rows}");
        }

        // Every blob, rehashed. BlobStore.Read verifies the content against the
        // filename, which is the only check that catches a truncated blob.
        var blobRoot = Path.Combine(exportDirectory, BlobStore.DirectoryName);
        var blobs = 0;
        if (Directory.Exists(blobRoot))
            foreach (var file in Directory.EnumerateFiles(blobRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                BlobStore.Read(exportDirectory, Path.GetFileName(file));
                blobs++;
            }

        if (blobs != manifest.BlobCount)
            throw new InvalidDataException(
                $"manifest claims {manifest.BlobCount} blobs, the store holds {blobs}");

        await Task.CompletedTask;
    }

    public async Task<ParquetExportManifest> ExportAsync(
        string outputDirectory, Query clientQuery, string? actor,
        DateTime? since = null, bool forceGlobal = false, CancellationToken ct = default)
    {
        // An incremental export selects straight from the repositories by
        // updated_at, which bypasses the row-level ACL gate the full export
        // applies. Rather than silently returning rows the actor cannot see,
        // refuse: incremental is an operator/pipeline operation, and the CLI
        // already runs it with actor: null.
        if (since is not null && actor is not null)
            throw new NotSupportedException(
                "incremental export does not apply the row-level ACL filter; "
                + "run it without an actor, or take a full export");

        var spaceName = clientQuery.SpaceName;
        var subpath = string.IsNullOrEmpty(clientQuery.Subpath) ? "/" : clientQuery.Subpath;

        // Exporting the management space IS asking for users, roles and
        // permissions — they live in it. `--all` forces it on for every space.
        var includeGlobal = forceGlobal
            || string.Equals(spaceName, MgmtSpace, StringComparison.Ordinal);

        // The watermark is stamped BEFORE reading anything. §5.1: a later
        // incremental run selects `updated_at >= watermark`, and taking it from
        // the START of this export makes the two overlap. Overlap costs a
        // re-shipped row that the import upserts away; a gap loses one silently.
        //
        // TimeUtils.Now(), NOT DateTime.UtcNow: dmart stores timestamps
        // LOCAL-NAIVE in `timestamp without time zone` columns, so a watermark
        // in UTC would be compared against a different clock. On a host ahead
        // of UTC that makes every increment a full export (wasteful but safe);
        // on a host BEHIND UTC it silently skips every row changed inside the
        // offset — which is the corruption this whole mechanism exists to
        // prevent. Caught by the increment tests returning 3 rows instead of 1.
        var watermark = TimeUtils.Now();

        // Row-level ACL, same gate the zip export applies. An unauthenticated
        // caller skips it and gets unfiltered rows.
        List<string>? policies = null;
        if (actor is not null)
        {
            policies = await perms.BuildUserQueryPoliciesAsync(actor, spaceName, subpath, ct);
            if (policies.Count == 0)
                return await WriteManifestAsync(outputDirectory, spaceName, watermark, [], 0, 0, 0, since, null, ct);
        }

        Directory.CreateDirectory(outputDirectory);

        var query = clientQuery with
        {
            FilterSchemaNames = new(),
            RetrieveJsonPayload = true,
            Limit = clientQuery.Limit,   // uncapped; <= 0 means everything
        };

        var relativePath = Path.Combine("entries", $"space_name={spaceName}", "part-00000.parquet");
        var absolutePath = Path.Combine(outputDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        long rowCount = 0;
        var buffer = new List<Entry>(Math.Min(RowGroupRows, 1024));

        // FileOptions.SequentialScan: this is written once, front to back.
        await using (var file = new FileStream(
            absolutePath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.SequentialScan))
        {
            var writer = new ParquetFileWriter(EntryParquetTable.Schema);
            writer.Start(file);

            void Flush()
            {
                writer.WriteRowGroup(EntryParquetTable.BuildPages(buffer), buffer.Count);
                rowCount += buffer.Count;
                buffer.Clear();
            }

            if (since is { } watermarkFloor)
            {
                // Query.FromDate filters on created_at, so it CANNOT serve this:
                // an entry edited since the last run still has its original
                // created_at and would be silently missed — exactly the rows an
                // increment exists to carry. Hence a dedicated updated_at scan.
                var offset = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var page = await entries.ListForSpaceUpdatedSincePagedAsync(
                        spaceName, watermarkFloor, ImportExportService.ExportPageSize, offset,
                        subpath, ct);
                    if (page.Count == 0) break;

                    foreach (var entry in page)
                    {
                        buffer.Add(entry);
                        if (buffer.Count >= RowGroupRows) Flush();
                    }

                    offset += page.Count;
                    if (page.Count < ImportExportService.ExportPageSize) break;
                }
            }
            else
            {
                await ImportExportService.ForEachMatchAsync(
                    query,
                    q => actor is not null
                        ? entries.QueryAsync(q, actor, policies!, ct)
                        : entries.QueryAsync(q, ct),
                    entry =>
                    {
                        buffer.Add(entry);
                        if (buffer.Count >= RowGroupRows) Flush();
                        return Task.CompletedTask;
                    },
                    ct);
            }

            // The tail. Writing an empty row group is rejected by the encoder,
            // so a total that lands exactly on the boundary must not flush again.
            if (buffer.Count > 0) Flush();

            writer.Finish();
        }

        // An export with no matching rows still produces a valid, empty file —
        // a restore must be able to tell "nothing matched" from "the export
        // failed", and a missing file cannot express the difference.
        log.LogInformation(
            "parquet export: {Rows} entries from {Space}{Subpath} to {Path}",
            rowCount, spaceName, subpath, outputDirectory);

        var tables = new List<ParquetTableManifest>
        {
            new("entries", [relativePath], rowCount),
        };

        var (attachmentTable, blobCount, blobBytes) =
            await WriteAttachmentsAsync(outputDirectory, spaceName, since, subpath, ct);
        tables.Add(attachmentTable);

        tables.Add(await WriteHistoriesAsync(outputDirectory, spaceName, since, subpath, ct));

        // Tombstones, INCREMENTS ONLY (§4.1). A full export is the state, so
        // anything absent from it is deleted by construction; writing a
        // deletions file there would invite a consumer to apply deletes twice.
        if (since is { } deletionsFloor)
        {
            tables.Add(await WriteDeletionsAsync(outputDirectory, spaceName, deletionsFloor, ct));
            await WarnIfWatermarkPredatesTombstonesAsync(deletionsFloor, ct);
        }

        // The global tables — spaces, users, roles, permissions.
        //
        // Written only for a FULL BACKUP or an explicit management-space export.
        // A scoped export of one space or one folder carries that content and
        // nothing else, for two reasons: repeating the entire user table in
        // every scoped export is waste, and — the one that matters — the users
        // table holds Argon2 PASSWORD HASHES. Writing those to disk is a
        // consequence an operator should get when they ask for a backup or for
        // management, not as a side effect of exporting a folder.
        //
        // Not Hive-partitioned: §4.1 puts them at `<table>/part-00000.parquet`
        // with no `space_name=` directory, so unlike entries they DO carry
        // space_name as a column. Users, roles and permissions all live in the
        // management space; spaces span every space by definition.
        if (includeGlobal)
        {
            tables.Add(await WriteGlobalAsync(outputDirectory, "spaces", SpaceParquetTable.Schema,
                await spaces.ListAsync(ct), SpaceParquetTable.BuildPages, RowGroupRows, ct));

            tables.Add(await WriteGlobalAsync(outputDirectory, "users", UserParquetTable.Schema,
                await CollectAsync<User>("/users", q => users.QueryAsync(q, ct)),
                UserParquetTable.BuildPages, RowGroupRows, ct));

            tables.Add(await WriteGlobalAsync(outputDirectory, "roles", RoleParquetTable.Schema,
                await CollectAsync<Role>("/roles", q => access.QueryRolesAsync(q, ct)),
                RoleParquetTable.BuildPages, RowGroupRows, ct));

            tables.Add(await WriteGlobalAsync(outputDirectory, "permissions", PermissionParquetTable.Schema,
                await CollectAsync<Permission>("/permissions", q => access.QueryPermissionsAsync(q, ct)),
                PermissionParquetTable.BuildPages, RowGroupRows, ct));
        }

        return await WriteManifestAsync(
            outputDirectory, spaceName, watermark, tables,
            tables.Sum(t => t.RowCount), blobCount, blobBytes, since, null, ct);
    }

    // Writes histories/space_name=<s>/part-00000.parquet.
    //
    // Streamed a page at a time like entries. History is the largest table in
    // most installations — one row per change, forever — so buffering it whole
    // would undo the memory bound the rest of the export maintains.
    private async Task<ParquetTableManifest> WriteHistoriesAsync(
        string outputDirectory, string spaceName, DateTime? since, string? subpath, CancellationToken ct)
    {
        var relative = Path.Combine("histories", $"space_name={spaceName}", "part-00000.parquet");
        var absolute = Path.Combine(outputDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        long rows = 0;
        var buffer = new List<Models.Core.HistoryRow>(Math.Min(RowGroupRows, 1024));

        await using (var file = new FileStream(
            absolute, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.SequentialScan))
        {
            var writer = new ParquetFileWriter(HistoryParquetTable.Schema);
            writer.Start(file);

            var offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await histories.ListForSpacePagedAsync(
                    spaceName, HistoryPageSize, offset, since, subpath, ct);
                if (page.Count == 0) break;

                foreach (var row in page)
                {
                    buffer.Add(row);
                    if (buffer.Count >= RowGroupRows)
                    {
                        writer.WriteRowGroup(HistoryParquetTable.BuildPages(buffer), buffer.Count);
                        rows += buffer.Count;
                        buffer.Clear();
                    }
                }

                offset += page.Count;
                if (page.Count < HistoryPageSize) break;
            }

            if (buffer.Count > 0)
            {
                writer.WriteRowGroup(HistoryParquetTable.BuildPages(buffer), buffer.Count);
                rows += buffer.Count;
            }

            writer.Finish();
        }

        if (rows > 0)
            log.LogInformation("parquet export: {Rows} history rows", rows);

        return new ParquetTableManifest("histories", [relative], rows);
    }

    // §5.2's last rule, made checkable: an increment can only carry deletions
    // that were RECORDED, and nothing was recorded before the retention floor.
    //
    // This warns only when it is CERTAIN — the watermark strictly predates the
    // floor — rather than on the ambiguous "no old tombstones exist", which is
    // equally consistent with nothing having been deleted. A warning that fires
    // on healthy exports is one operators learn to ignore, and this is the one
    // they cannot afford to.
    private async Task WarnIfWatermarkPredatesTombstonesAsync(DateTime since, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var floor = await Tombstones.ReadRetentionFloorAsync(conn, ct);

        if (floor is null)
        {
            log.LogWarning(
                "parquet export: no tombstone retention floor recorded — this archive cannot "
                + "attest that deletions since {Since:o} were captured", since);
            return;
        }

        if (since < floor)
            log.LogWarning(
                "parquet export: DELETIONS MAY HAVE BEEN LOST. The watermark {Since:o} predates "
                + "the tombstone retention floor {Floor:o}, so deletions in that window were never "
                + "recorded. Take a FULL export to resynchronise; an increment cannot close this gap.",
                since, floor.Value);
    }

    // Writes deletions/part-00000.parquet — the record of what an increment
    // must REMOVE, as opposed to what it must upsert.
    private async Task<ParquetTableManifest> WriteDeletionsAsync(
        string outputDirectory, string spaceName, DateTime since, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = await Tombstones.ReadSinceAsync(conn, spaceName, since, ct);

        var relative = Path.Combine("deletions", "part-00000.parquet");
        var absolute = Path.Combine(outputDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        await using (var file = new FileStream(
            absolute, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.SequentialScan))
        {
            var writer = new ParquetFileWriter(DeletionParquetTable.Schema);
            writer.Start(file);
            for (var offset = 0; offset < rows.Count; offset += RowGroupRows)
            {
                var slice = rows.GetRange(offset, Math.Min(RowGroupRows, rows.Count - offset));
                writer.WriteRowGroup(DeletionParquetTable.BuildPages(slice), slice.Count);
            }
            writer.Finish();
        }

        if (rows.Count > 0)
            log.LogInformation("parquet export: {Rows} tombstones since {Since:o}", rows.Count, since);

        return new ParquetTableManifest("deletions", [relative], rows.Count);
    }

    /// <summary>History rows fetched per page.</summary>
    internal static int? HistoryPageSizeOverride { get; set; }
    private int HistoryPageSize => HistoryPageSizeOverride ?? settingsOpt.Value.ParquetHistoryPageSize;

    /// <summary>
    /// Attachments per page. Metadata only — the bytes are fetched one blob at
    /// a time — so this bounds row-buffer memory, not blob memory.
    /// </summary>
    internal static int? AttachmentPageSizeOverride { get; set; }
    private int AttachmentPageSize => AttachmentPageSizeOverride ?? settingsOpt.Value.ParquetAttachmentPageSize;

    // Writes attachments/space_name=<s>/part-00000.parquet plus the blob store.
    //
    // The shape here is dictated by memory. Media is where the gigabytes are,
    // so the listing deliberately does NOT select bytes; each blob is fetched
    // by uuid, hashed, written, and released before the next one. Peak media
    // residency is ONE blob regardless of how large the space is, which is the
    // property that makes a multi-GB export possible at all.
    //
    // The cost is a query per attachment that HAS media. Attachments without
    // media skip it entirely, which is why the listing returns the size.
    private async Task<(ParquetTableManifest Table, int Blobs, long BlobBytes)> WriteAttachmentsAsync(
        string outputDirectory, string spaceName, DateTime? since, string? subpath, CancellationToken ct)
    {
        var relative = Path.Combine("attachments", $"space_name={spaceName}", "part-00000.parquet");
        var absolute = Path.Combine(outputDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        long rows = 0;
        var blobs = 0;
        long blobBytes = 0;
        // Distinct hashes seen, so the dedup saving is REPORTED rather than
        // merely happening — an operator comparing export size to database size
        // otherwise has no way to explain the difference.
        var distinctBlobs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new List<AttachmentParquetTable.Row>(Math.Min(RowGroupRows, 1024));

        await using (var file = new FileStream(
            absolute, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.SequentialScan))
        {
            var writer = new ParquetFileWriter(AttachmentParquetTable.Schema);
            writer.Start(file);

            var offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = await attachments.ListForSpacePagedAsync(
                    spaceName, AttachmentPageSize, offset, since, subpath, ct);
                if (page.Count == 0) break;

                foreach (var (attachment, mediaSize) in page)
                {
                    string? sha = null;
                    if (mediaSize > 0)
                    {
                        var (bytes, _) = await attachments.GetMediaAsync(Guid.Parse(attachment.Uuid), ct);
                        if (bytes is not null)
                        {
                            sha = BlobStore.Write(outputDirectory, bytes);
                            if (distinctBlobs.Add(sha)) { blobs++; blobBytes += bytes.Length; }
                        }
                    }

                    buffer.Add(new AttachmentParquetTable.Row(attachment, sha, mediaSize));
                    if (buffer.Count >= RowGroupRows)
                    {
                        writer.WriteRowGroup(AttachmentParquetTable.BuildPages(buffer), buffer.Count);
                        rows += buffer.Count;
                        buffer.Clear();
                    }
                }

                offset += page.Count;
                if (page.Count < AttachmentPageSize) break;
            }

            if (buffer.Count > 0)
            {
                writer.WriteRowGroup(AttachmentParquetTable.BuildPages(buffer), buffer.Count);
                rows += buffer.Count;
            }

            writer.Finish();
        }

        if (rows > 0)
            log.LogInformation(
                "parquet export: {Rows} attachments, {Blobs} distinct blobs ({Bytes} bytes)",
                rows, blobs, blobBytes);

        return (new ParquetTableManifest("attachments", [relative], rows), blobs, blobBytes);
    }

    // Pages a management-space listing into memory. These tables are small by
    // nature — users, roles and permissions are administrative, not content —
    // so unlike entries they are not streamed. If an installation ever has
    // enough users for that to matter, this is the line to change.
    private Task<List<T>> CollectAsync<T>(string subpath, Func<Query, Task<List<T>>> fetch)
    {
        var q = new Query
        {
            Type = QueryType.Search, SpaceName = MgmtSpace, Subpath = subpath,
            FilterSchemaNames = new(), Limit = 0, RetrieveJsonPayload = true,
        };
        var collected = new List<T>();
        return ImportExportService
            .ForEachMatchAsync(q, fetch, row => { collected.Add(row); return Task.CompletedTask; },
                               CancellationToken.None)
            .ContinueWith(_ => collected, TaskScheduler.Default);
    }

    private static async Task<ParquetTableManifest> WriteGlobalAsync<T>(
        string outputDirectory, string table,
        IReadOnlyList<ParquetFileWriter.ColumnSpec> schema,
        List<T> rows,
        Func<IReadOnlyList<T>, IReadOnlyList<ParquetFileWriter.ColumnPage>> build,
        int rowGroupRows,
        CancellationToken ct)
    {
        var relative = Path.Combine(table, "part-00000.parquet");
        var absolute = Path.Combine(outputDirectory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        await using (var file = new FileStream(
            absolute, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.SequentialScan))
        {
            var writer = new ParquetFileWriter(schema);
            writer.Start(file);
            // Row groups still apply: a large table is chunked rather than
            // written as one enormous group.
            for (var offset = 0; offset < rows.Count; offset += rowGroupRows)
            {
                var slice = rows.GetRange(offset, Math.Min(rowGroupRows, rows.Count - offset));
                writer.WriteRowGroup(build(slice), slice.Count);
            }
            writer.Finish();
        }

        ct.ThrowIfCancellationRequested();
        return new ParquetTableManifest(table, [relative], rows.Count);
    }

    private static async Task<ParquetExportManifest> WriteManifestAsync(
        string directory, string spaceName, DateTime watermark,
        List<ParquetTableManifest> tables, long rowCount,
        int blobCount, long blobBytes, DateTime? since,
        List<string>? spacesExported, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        // TimeUtils.Now() so the manifest is in ONE clock: mixing a local-naive
        // watermark with a UTC created_at makes the pair incomparable, and the
        // watermark is the field a chain depends on.
        var manifest = new ParquetExportManifest(
            FormatVersion, TimeUtils.Now(), watermark, spaceName, tables, rowCount,
            blobCount, blobBytes, since, spacesExported ?? [spaceName]);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, ParquetManifestJsonContext.Default.ParquetExportManifest),
            ct);

        return manifest;
    }

    /// <summary>
    /// Restores an export into the database.
    /// </summary>
    /// <param name="replaceExisting">
    /// False (the default) skips rows that already exist, which makes a rerun
    /// idempotent — the same behaviour as `dmart import` without -r. True
    /// rewrites them from the archive.
    /// </param>
    /// <remarks>
    /// One row at a time. The zip importer has bulk-COPY machinery worth
    /// several times this on PostgreSQL, and reusing it here is the obvious
    /// next step — but it is a large piece of the zip path and wiring it in
    /// unverified would trade a correct slow restore for a fast unproven one.
    /// Stated rather than hidden, because "restore is slow" is a real property
    /// of this build.
    /// </remarks>
    public async Task<ParquetImportResult> ImportAsync(
        string exportDirectory, bool replaceExisting = false, CancellationToken ct = default)
    {
        // Order matters. Spaces, roles and permissions come before users and
        // entries because those reference them: restoring a user whose roles do
        // not exist yet, or an entry in an absent space, trips foreign keys or
        // leaves dangling references depending on the driver.
        var (si, sk, sf) = await RestoreGlobalAsync(
            exportDirectory, "spaces", SpaceParquetTable.FromTable,
            (s, c) => spaces.GetAsync(s.Shortname, c, ct), (s, c) => spaces.UpsertAsync(s, c, ct),
            replaceExisting, ct);

        var (ri, rk, rf) = await RestoreGlobalAsync(
            exportDirectory, "roles", RoleParquetTable.FromTable,
            (r, c) => access.GetRoleAsync(r.Shortname, c, ct), (r, c) => access.UpsertRoleAsync(r, c, ct),
            replaceExisting, ct);

        var (pi, pk, pf) = await RestoreGlobalAsync(
            exportDirectory, "permissions", PermissionParquetTable.FromTable,
            (p, c) => access.GetPermissionAsync(p.Shortname, c, ct),
            (p, c) => access.UpsertPermissionAsync(p, c, ct),
            replaceExisting, ct);

        var (ui, uk, uf) = await RestoreGlobalAsync(
            exportDirectory, "users", UserParquetTable.FromTable,
            (u, c) => users.GetByShortnameAsync(u.Shortname, c, ct), (u, c) => users.UpsertAsync(u, c, ct),
            replaceExisting, ct);

        var perTable = new List<ParquetTableResult>
        {
            new("spaces", si, sk, sf),
            new("roles", ri, rk, rf),
            new("permissions", pi, pk, pf),
            new("users", ui, uk, uf),
        };

        var rows = ReadEntries(exportDirectory);
        int imported = si + ri + pi + ui,
            skipped = sk + rk + pk + uk,
            failed = sf + rf + pf + uf;

        var (entriesImported, entriesSkipped, entriesFailed) =
            await RestoreEntriesAsync(rows, replaceExisting, ct);

        imported += entriesImported;
        skipped += entriesSkipped;
        failed += entriesFailed;

        // Attachments come AFTER entries: an attachment's subpath is
        // "<parent subpath>/<parent shortname>", so restoring one before its
        // parent leaves it pointing at nothing.
        var (ai, ak, af) = await RestoreAttachmentsAsync(exportDirectory, replaceExisting, ct);
        imported += ai; skipped += ak; failed += af;
        perTable.Add(new ParquetTableResult("attachments", ai, ak, af));

        var (hi, hk, hf) = await RestoreHistoriesAsync(exportDirectory, ct);
        imported += hi; skipped += hk; failed += hf;
        perTable.Add(new ParquetTableResult("histories", hi, hk, hf));

        log.LogInformation(
            "parquet import: {Imported} imported, {Skipped} skipped, {Failed} failed from {Path}",
            imported, skipped, failed, exportDirectory);

        perTable.Add(new ParquetTableResult("entries", entriesImported, entriesSkipped, entriesFailed));
        return new ParquetImportResult(
            imported, skipped, failed, imported + skipped + failed, perTable);
    }

    // Restores the audit trail, preserving each row's original uuid and
    // timestamp.
    //
    // There is no replaceExisting here, deliberately. History is append-only
    // and immutable: an existing uuid is the same past event, and nothing a
    // later export could say would legitimately correct it. Rewriting one would
    // only ever be a way to falsify an audit record.
    private async Task<(int Imported, int Skipped, int Failed)> RestoreHistoriesAsync(
        string exportDirectory, CancellationToken ct)
    {
        var rows = ReadHistoryRows(exportDirectory);

        int imported = 0, skipped = 0, failed = 0;

        // Batched multi-row INSERT, on BOTH drivers. History is append-only
        // with ON CONFLICT DO NOTHING and no dependent merge, so it needs none
        // of the COPY scratch-table machinery — and this way SQLite gets a bulk
        // path too, which it does not for entries or attachments.
        if (!ForcePerRowRestore)
        {
            try
            {
                imported = await histories.RestoreManyAsync(rows, ct);
                skipped = rows.Count - imported;
                return (imported, skipped, 0);
            }
            catch (Exception ex)
            {
                // One malformed row fails its whole batch, so fall back to
                // per-row to isolate it rather than losing the batch. Same
                // reasoning as the entry/attachment replay.
                log.LogWarning(ex,
                    "parquet import: batched history insert failed; replaying row-by-row to isolate");
            }
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await histories.RestoreAsync(row, ct)) imported++;
                else skipped++;
            }
            catch (Exception ex)
            {
                failed++;
                log.LogWarning(ex, "parquet import: failed to restore history row {Uuid}", row.Uuid);
            }
        }

        if (rows.Count > 0)
            log.LogInformation(
                "parquet import: histories — {Imported} imported, {Skipped} skipped, {Failed} failed",
                imported, skipped, failed);

        return (imported, skipped, failed);
    }

    /// <summary>
    /// Restores entries, using the bulk COPY path on PostgreSQL and falling
    /// back to per-row elsewhere.
    /// </summary>
    /// <remarks>
    /// The bulk path is ImportExportService.BulkUpsertEntriesAsync — the SAME
    /// machinery the zip importer uses, deliberately reused rather than
    /// reimplemented. Its COPY semantics are not obvious: query_policies must
    /// be regenerated because COPY writes the column verbatim, the scratch
    /// tables must exist on the session, and an integrity violation aborts a
    /// whole batch and is replayed row-by-row to isolate the offender. A second
    /// implementation would get one of those wrong, quietly.
    ///
    /// Bulk requires PostgreSQL — the COPY session, the scratch tables and the
    /// SQLSTATE-driven replay are all Npgsql. SQLite takes the per-row path,
    /// which is correct, just slower.
    ///
    /// `replaceExisting` maps to the importer's INVERSE flag: it calls the
    /// parameter `preserveExisting`, so restoring over the top means passing
    /// preserveExisting: false. Getting that backwards silently turns a
    /// restore into a no-op on every row that already exists.
    /// </remarks>
    private async Task<(int Imported, int Skipped, int Failed)> RestoreEntriesAsync(
        List<Entry> rows, bool replaceExisting, CancellationToken ct)
    {
        if (rows.Count == 0) return (0, 0, 0);

        // Two restore paths now exist, so they must agree on semantics. The
        // toggle exists to test exactly that, and to measure the difference.
        if (db is Db bulk && importExport is not null && !ForcePerRowRestore)
        {
            var session = await bulk.BeginBatchImportSessionAsync(ct);
            try
            {
                await session.InitConnectionAsync(
                    ImportExportService.CreateImportScratchTablesAsync, ct);

                int imported = 0, skipped = 0, failed = 0;
                for (var offset = 0; offset < rows.Count; offset += BulkBatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = rows.GetRange(offset, Math.Min(BulkBatchSize, rows.Count - offset));
                    var (affected, wasSkipped, failures) = await importExport.BulkUpsertEntriesAsync(
                        session, "parquet-restore", batch,
                        preserveExisting: !replaceExisting, ct);

                    imported += affected;
                    skipped += wasSkipped;
                    failed += failures.Count;
                    foreach (var f in failures)
                        log.LogWarning("parquet import: entry failed — {Path}: {Error}",
                            f.GetValueOrDefault("path"), f.GetValueOrDefault("error"));
                }

                session.MarkSuccess();
                log.LogInformation(
                    "parquet import: entries via bulk COPY — {Imported} imported, {Skipped} skipped, {Failed} failed",
                    imported, skipped, failed);
                return (imported, skipped, failed);
            }
            finally { await session.DisposeAsync(); }
        }

        return await RestoreEntriesPerRowAsync(rows, replaceExisting, ct);
    }

    /// <summary>
    /// Forces the per-row path even on PostgreSQL. Test-only: it exists so the
    /// two paths can be asserted equivalent, and so the difference between them
    /// can be measured. Not thread-safe; nothing in the request path writes it.
    /// </summary>
    internal static bool ForcePerRowRestore { get; set; }

    /// <summary>Rows per bulk COPY batch. Bounds the scratch table and the replay unit.</summary>
    internal static int? BulkBatchSizeOverride { get; set; }
    private int BulkBatchSize => BulkBatchSizeOverride ?? settingsOpt.Value.ParquetBulkBatchSize;

    private async Task<(int Imported, int Skipped, int Failed)> RestoreEntriesPerRowAsync(
        List<Entry> rows, bool replaceExisting, CancellationToken ct)
    {
        int imported = 0, skipped = 0, failed = 0;
        foreach (var entry in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!replaceExisting)
                {
                    var existing = await entries.GetAsync(
                        entry.SpaceName, entry.Subpath, entry.Shortname, entry.ResourceType, ct);
                    if (existing is not null) { skipped++; continue; }
                }

                await entries.UpsertAsync(entry, ct);
                imported++;
            }
            catch (Exception ex)
            {
                // One bad row must not abandon the rest of a restore, but it
                // must be counted — a summary that says "imported 900" out of
                // 1000 with no failure count reads as success.
                failed++;
                log.LogWarning(ex, "parquet import: failed to restore {Space}{Subpath}/{Shortname}",
                    entry.SpaceName, entry.Subpath, entry.Shortname);
            }
        }
        return (imported, skipped, failed);
    }

    // Restores attachments and rehydrates their media from the blob store.
    //
    // A row whose media_sha256 names a blob that is missing or corrupt FAILS
    // rather than restoring without its bytes. An attachment silently restored
    // empty is undetectable afterwards — the bytes are opaque and nothing
    // downstream checks them — so the loud failure is the safer outcome.
    private async Task<(int Imported, int Skipped, int Failed)> RestoreAttachmentsAsync(
        string exportDirectory, bool replaceExisting, CancellationToken ct)
    {
        var rows = ReadAttachmentRows(exportDirectory);

        // Rehydrate media BEFORE the write, whichever path takes it. BlobStore
        // verifies each blob against its own name, so a truncated file throws
        // here rather than becoming attachment media — the check is the same on
        // both paths, and it is the reason a bad blob fails its row.
        var hydrated = new List<Attachment>(rows.Count);
        int imported = 0, skipped = 0, failed = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var media = row.MediaSha256 is { } sha ? BlobStore.Read(exportDirectory, sha) : null;
                hydrated.Add(row.Attachment with { Media = media });
            }
            catch (Exception ex)
            {
                failed++;
                log.LogWarning(ex, "parquet import: blob missing or corrupt for {Space}{Subpath}/{Shortname}",
                    row.Attachment.SpaceName, row.Attachment.Subpath, row.Attachment.Shortname);
            }
        }

        if (db is Db bulk && importExport is not null && !ForcePerRowRestore)
        {
            var session = await bulk.BeginBatchImportSessionAsync(ct);
            try
            {
                await session.InitConnectionAsync(
                    ImportExportService.CreateImportScratchTablesAsync, ct);

                for (var offset = 0; offset < hydrated.Count; offset += BulkBatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var batch = hydrated.GetRange(offset, Math.Min(BulkBatchSize, hydrated.Count - offset));
                    var (affected, wasSkipped, failures) = await importExport.BulkUpsertAttachmentsAsync(
                        session, "parquet-restore", batch, preserveExisting: !replaceExisting, ct);

                    imported += affected;
                    skipped += wasSkipped;
                    failed += failures.Count;
                    foreach (var f in failures)
                        log.LogWarning("parquet import: attachment failed — {Path}: {Error}",
                            f.GetValueOrDefault("path"), f.GetValueOrDefault("error"));
                }
                session.MarkSuccess();
            }
            finally { await session.DisposeAsync(); }
        }
        else
        {
            foreach (var attachment in hydrated)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!replaceExisting)
                    {
                        var existing = await attachments.GetAsync(
                            attachment.SpaceName, attachment.Subpath, attachment.Shortname, ct);
                        if (existing is not null) { skipped++; continue; }
                    }

                    await attachments.UpsertAsync(attachment, ct);
                    imported++;
                }
                catch (Exception ex)
                {
                    failed++;
                    log.LogWarning(ex, "parquet import: failed to restore attachment {Space}{Subpath}/{Shortname}",
                        attachment.SpaceName, attachment.Subpath, attachment.Shortname);
                }
            }
        }

        if (rows.Count > 0)
            log.LogInformation(
                "parquet import: attachments — {Imported} imported, {Skipped} skipped, {Failed} failed",
                imported, skipped, failed);

        return (imported, skipped, failed);
    }

    // Restores one global table. Same skip/replace rule as entries, and the
    // same "a bad row is counted, not fatal" rule — a restore that abandons the
    // remaining users because one of them failed is worse than a partial one
    // that says so.
    private async Task<(int Imported, int Skipped, int Failed)> RestoreGlobalAsync<T>(
        string exportDirectory, string table,
        Func<ParquetFileReader.ParquetTable, List<T>> fromTable,
        Func<T, DbConnection, Task<T?>> get, Func<T, DbConnection, Task> upsert,
        bool replaceExisting, CancellationToken ct)
        where T : class
    {
        var rows = ReadTable(exportDirectory, table, fromTable);
        int imported = 0, skipped = 0, failed = 0;
        if (rows.Count == 0) return (0, 0, 0);

        // ONE connection for the whole table rather than one per row, which the
        // parameterless repository overloads would each open.
        //
        // Honest note on why this is NOT the speed fix: it was expected to be,
        // and measurement said otherwise — 5.86s to 5.75s on a 5k-row restore.
        // Npgsql pools connections, so opening one per row was already cheap.
        // The real cost is one round trip per statement (~1.7ms), and only
        // batching would remove it.
        //
        // Kept anyway because reusing a connection across a loop of writes is
        // the right shape, not because it is faster.
        //
        // Batching users specifically is deliberately NOT done here: their
        // upsert carries `password = COALESCE(EXCLUDED.password,
        // users.password)`, and a hand-written multi-row path would have to
        // reproduce that exactly or silently wipe every credential it restored.
        // At ~1.7ms per row this is seconds on a realistic install; that trade
        // is not worth a fifth bulk path today.
        await using var conn = await db.OpenAsync(ct);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!replaceExisting && await get(row, conn) is not null) { skipped++; continue; }
                await upsert(row, conn);
                imported++;
            }
            catch (Exception ex)
            {
                failed++;
                log.LogWarning(ex, "parquet import: failed to restore a row of {Table}", table);
            }
        }

        if (rows.Count > 0)
            log.LogInformation("parquet import: {Table} — {Imported} imported, {Skipped} skipped, {Failed} failed",
                table, imported, skipped, failed);

        return (imported, skipped, failed);
    }

    // A table absent from the manifest yields no rows rather than throwing:
    // archives written by an earlier build genuinely do not have these files,
    // and refusing to restore entries because there is no users table would be
    // worse than restoring what is there.
    private static List<T> ReadTable<T>(
        string exportDirectory, string table,
        Func<ParquetFileReader.ParquetTable, List<T>> fromTable)
    {
        var manifest = ReadManifest(exportDirectory);
        var entry = manifest.Tables.FirstOrDefault(t => t.Name == table);
        if (entry is null) return [];

        var result = new List<T>();
        foreach (var relative in entry.Files)
            result.AddRange(fromTable(ParquetFileReader.ReadFile(Path.Combine(exportDirectory, relative))));

        if (result.Count != entry.RowCount)
            throw new InvalidDataException(
                $"manifest claims {entry.RowCount} rows in '{table}' but the files hold {result.Count}");

        return result;
    }

    // Like ReadTable, but hands each file's Hive partition value to the mapper
    // so a multi-space backup restores each space under its own name.
    private static List<T> ReadPartitionedTable<T>(
        string exportDirectory, string table,
        Func<ParquetFileReader.ParquetTable, string, List<T>> fromTable, string fallbackSpace)
    {
        var manifest = ReadManifest(exportDirectory);
        var entry = manifest.Tables.FirstOrDefault(t => t.Name == table);
        if (entry is null) return [];

        var result = new List<T>();
        foreach (var relative in entry.Files)
            result.AddRange(fromTable(
                ParquetFileReader.ReadFile(Path.Combine(exportDirectory, relative)),
                SpaceFromPartitionPath(relative, fallbackSpace)));

        if (result.Count != entry.RowCount)
            throw new InvalidDataException(
                $"manifest claims {entry.RowCount} rows in '{table}' but the files hold {result.Count}");

        return result;
    }

    /// <summary>Reads an export's manifest, rejecting a format version this build cannot read.</summary>
    public static ParquetExportManifest ReadManifest(string exportDirectory)
    {
        var manifestPath = Path.Combine(exportDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidDataException($"no manifest.json in '{exportDirectory}' — not a dmart Parquet export");

        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(manifestPath), ParquetManifestJsonContext.Default.ParquetExportManifest)
            ?? throw new InvalidDataException("manifest.json is empty or unreadable");

        // A newer writer may have changed the layout in ways this cannot see.
        // Reading on and producing partial results is the failure mode a
        // restore can least afford.
        if (manifest.FormatVersion > FormatVersion)
            throw new NotSupportedException(
                $"export declares format version {manifest.FormatVersion}; this build understands {FormatVersion}");

        return manifest;
    }

    /// <summary>
    /// The watermark to pass as <c>since</c> for the NEXT run, read from a
    /// previous export directory.
    /// </summary>
    /// <remarks>
    /// Returns the previous run's <c>watermark</c> — stamped BEFORE it read
    /// anything — not its <c>created_at</c>. §5.1: the two runs must overlap.
    /// Chaining from the end of the previous export would skip every row
    /// changed WHILE it ran, and those rows would never be picked up again.
    /// </remarks>
    public static DateTime WatermarkOf(string exportDirectory) =>
        ReadManifest(exportDirectory).Watermark;

    /// <summary>
    /// The Hive partition value encoded in a path like
    /// `entries/space_name=alpha/part-00000.parquet`.
    /// </summary>
    /// <remarks>
    /// Taken from the PATH, not the manifest. A full backup holds many spaces
    /// in one archive, so a single manifest-level space name would restore
    /// every one of them under the same name — silently merging spaces, which
    /// is unrecoverable without the original.
    /// </remarks>
    internal static string SpaceFromPartitionPath(string relativePath, string fallback)
    {
        foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
            if (segment.StartsWith("space_name=", StringComparison.Ordinal))
                return segment["space_name=".Length..];
        return fallback;
    }

    /// <summary>Reads the attachment rows an export wrote, with their blob addresses.</summary>
    internal static List<AttachmentParquetTable.Row> ReadAttachmentRows(string exportDirectory)
    {
        var manifest = ReadManifest(exportDirectory);
        return ReadPartitionedTable(exportDirectory, "attachments",
            (t, space) => AttachmentParquetTable.FromTable(t, space), manifest.SpaceName);
    }

    /// <summary>Reads the history rows an export wrote.</summary>
    public static List<HistoryRow> ReadHistoryRows(string exportDirectory)
    {
        var manifest = ReadManifest(exportDirectory);
        return ReadPartitionedTable(exportDirectory, "histories",
            (t, space) => HistoryParquetTable.FromTable(t, space), manifest.SpaceName);
    }

    /// <summary>Reads back the entries an export wrote, in file order.</summary>
    /// <remarks>
    /// The restore half. Kept here rather than in the reader so the reader stays
    /// a format concern and this stays a dmart one.
    /// </remarks>
    public static List<Entry> ReadEntries(string exportDirectory)
    {
        var manifest = ReadManifest(exportDirectory);
        var table = manifest.Tables.FirstOrDefault(t => t.Name == "entries");
        if (table is null) return [];

        var result = new List<Entry>();
        foreach (var relative in table.Files)
            result.AddRange(EntryParquetTable.FromTable(
                ParquetFileReader.ReadFile(Path.Combine(exportDirectory, relative)),
                SpaceFromPartitionPath(relative, manifest.SpaceName)));

        // The manifest's count is written independently of the files, so a
        // disagreement means one of them is wrong — most likely a truncated
        // copy, which is exactly what a restore must refuse.
        if (result.Count != table.RowCount)
            throw new InvalidDataException(
                $"manifest claims {table.RowCount} entries but the files hold {result.Count}");

        return result;
    }
}

/// <param name="Watermark">
/// Taken at the START of the export. A later incremental run selects
/// `updated_at >= watermark` from here — see design §5.1.
/// </param>
public sealed record ParquetExportManifest(
    [property: JsonPropertyName("format_version")] int FormatVersion,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt,
    [property: JsonPropertyName("watermark")] DateTime Watermark,
    [property: JsonPropertyName("space_name")] string SpaceName,
    [property: JsonPropertyName("tables")] List<ParquetTableManifest> Tables,
    [property: JsonPropertyName("row_count")] long RowCount,
    [property: JsonPropertyName("blob_count")] int BlobCount = 0,
    [property: JsonPropertyName("blob_bytes")] long BlobBytes = 0,
    /// <summary>
    /// The lower bound this export selected from, or null for a full export.
    /// Present so a chain of increments is auditable: `since` here should equal
    /// the `watermark` of the run it follows, and a gap between them is exactly
    /// the window in which changes could have been lost.
    /// </summary>
    [property: JsonPropertyName("since")] DateTime? Since = null,
    /// <summary>
    /// Every space this archive covers. A scoped export lists one; a full
    /// backup lists them all. Present so a restore can report what it is about
    /// to touch before it touches it.
    /// </summary>
    [property: JsonPropertyName("spaces")] List<string>? Spaces = null)
{
    /// <summary>
    /// Rows written for one table. <see cref="RowCount"/> is the total across
    /// ALL tables, so a caller asking "how many entries?" must ask by name —
    /// reading the aggregate instead silently counts users and roles too.
    /// </summary>
    public long RowsIn(string table) =>
        Tables.FirstOrDefault(t => t.Name == table)?.RowCount ?? 0;
}

/// <param name="Total">
/// Rows read from the archive across every table. Imported + Skipped + Failed
/// must equal it — a restore summary that does not add up is hiding a row.
/// </param>
/// <param name="Tables">
/// Per-table breakdown. The aggregate alone is close to useless on a restore:
/// "imported 900" across five tables does not say whether the users landed.
/// </param>
/// <param name="Freed">Bytes reclaimed, or that would be by a real run.</param>
public sealed record BlobGcResult(int Kept, int Removed, long Freed);

public sealed record ParquetImportResult(
    int Imported, int Skipped, int Failed, int Total,
    IReadOnlyList<ParquetTableResult> Tables)
{
    /// <summary>Result for one table, or an all-zero row if it was absent.</summary>
    public ParquetTableResult For(string table) =>
        Tables.FirstOrDefault(t => t.Table == table) ?? new ParquetTableResult(table, 0, 0, 0);
}

public sealed record ParquetTableResult(string Table, int Imported, int Skipped, int Failed);

public sealed record ParquetTableManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("files")] List<string> Files,
    [property: JsonPropertyName("row_count")] long RowCount);

// Source-generated, like everything else that serializes here: reflection-based
// serialization is off for the whole project (§ AOT).
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ParquetExportManifest))]
internal partial class ParquetManifestJsonContext : JsonSerializerContext;
