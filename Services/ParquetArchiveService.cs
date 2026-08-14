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
// Covers a FULL export of entries, attachments (with their media blobs, §4.3),
// spaces, users, roles and permissions. Histories and the incremental watermark
// (§5) are not wired yet; the manifest records enough for them to be added
// without changing what is already written.
//
// The users table carries the Argon2 PASSWORD HASH, so an export directory is
// credential material and needs the handling a database dump gets. That is a
// deliberate divergence from the zip export, which omits it and therefore
// cannot restore a login.
public sealed class ParquetArchiveService(
    EntryRepository entries,
    AttachmentRepository attachments,
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
    internal static int RowGroupRows { get; set; } = 50_000;

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
    public Task<ParquetExportManifest> ExportAsync(
        string outputDirectory, string spaceName, string? subpath, string? actor,
        CancellationToken ct = default)
        => ExportAsync(outputDirectory, new Query
        {
            Type = QueryType.Search,
            SpaceName = spaceName,
            Subpath = subpath ?? "/",
            FilterSchemaNames = new(),
            Limit = 0,   // 0 = unbounded; ForEachMatchAsync pages to the end
            RetrieveJsonPayload = true,
        }, actor, ct);

    public async Task<ParquetExportManifest> ExportAsync(
        string outputDirectory, Query clientQuery, string? actor, CancellationToken ct = default)
    {
        var spaceName = clientQuery.SpaceName;
        var subpath = string.IsNullOrEmpty(clientQuery.Subpath) ? "/" : clientQuery.Subpath;

        // The watermark is stamped BEFORE reading anything. §5.1: a later
        // incremental run selects `updated_at >= watermark`, and taking it from
        // the START of this export makes the two overlap. Overlap costs a
        // re-shipped row that the import upserts away; a gap loses one silently.
        var watermark = DateTime.UtcNow;

        // Row-level ACL, same gate the zip export applies. An unauthenticated
        // caller skips it and gets unfiltered rows.
        List<string>? policies = null;
        if (actor is not null)
        {
            policies = await perms.BuildUserQueryPoliciesAsync(actor, spaceName, subpath, ct);
            if (policies.Count == 0)
                return await WriteManifestAsync(outputDirectory, spaceName, watermark, [], 0, 0, 0, ct);
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

            await ImportExportService.ForEachMatchAsync(
                query,
                q => actor is not null
                    ? entries.QueryAsync(q, actor, policies!, ct)
                    : entries.QueryAsync(q, ct),
                entry =>
                {
                    buffer.Add(entry);
                    if (buffer.Count >= RowGroupRows)
                    {
                        writer.WriteRowGroup(EntryParquetTable.BuildPages(buffer), buffer.Count);
                        rowCount += buffer.Count;
                        buffer.Clear();
                    }
                    return Task.CompletedTask;
                },
                ct);

            // The tail. Writing an empty row group is rejected by the encoder,
            // so a total that lands exactly on the boundary must not flush again.
            if (buffer.Count > 0)
            {
                writer.WriteRowGroup(EntryParquetTable.BuildPages(buffer), buffer.Count);
                rowCount += buffer.Count;
            }

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
            await WriteAttachmentsAsync(outputDirectory, spaceName, ct);
        tables.Add(attachmentTable);

        // The global tables. Without them a restore gives you content in a
        // system nobody can log into, with no ACL and no space definitions —
        // which is the difference between a backup and a table dump.
        //
        // Not Hive-partitioned: §4.1 puts them at `<table>/part-00000.parquet`
        // with no `space_name=` directory, so unlike entries they DO carry
        // space_name as a column. Users, roles and permissions all live in the
        // management space; spaces span every space by definition.
        tables.Add(await WriteGlobalAsync(outputDirectory, "spaces", SpaceParquetTable.Schema,
            await spaces.ListAsync(ct), SpaceParquetTable.BuildPages, ct));

        tables.Add(await WriteGlobalAsync(outputDirectory, "users", UserParquetTable.Schema,
            await CollectAsync<User>("/users", q => users.QueryAsync(q, ct)),
            UserParquetTable.BuildPages, ct));

        tables.Add(await WriteGlobalAsync(outputDirectory, "roles", RoleParquetTable.Schema,
            await CollectAsync<Role>("/roles", q => access.QueryRolesAsync(q, ct)),
            RoleParquetTable.BuildPages, ct));

        tables.Add(await WriteGlobalAsync(outputDirectory, "permissions", PermissionParquetTable.Schema,
            await CollectAsync<Permission>("/permissions", q => access.QueryPermissionsAsync(q, ct)),
            PermissionParquetTable.BuildPages, ct));

        return await WriteManifestAsync(
            outputDirectory, spaceName, watermark, tables,
            tables.Sum(t => t.RowCount), blobCount, blobBytes, ct);
    }

    /// <summary>
    /// Attachments per page. Metadata only — the bytes are fetched one blob at
    /// a time — so this bounds row-buffer memory, not blob memory.
    /// </summary>
    internal static int AttachmentPageSize { get; set; } = 500;

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
        string outputDirectory, string spaceName, CancellationToken ct)
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
                    spaceName, AttachmentPageSize, offset, ct);
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
            for (var offset = 0; offset < rows.Count; offset += RowGroupRows)
            {
                var slice = rows.GetRange(offset, Math.Min(RowGroupRows, rows.Count - offset));
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
        int blobCount, long blobBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var manifest = new ParquetExportManifest(
            FormatVersion, DateTime.UtcNow, watermark, spaceName, tables, rowCount,
            blobCount, blobBytes);

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
            s => spaces.GetAsync(s.Shortname, ct), s => spaces.UpsertAsync(s, ct),
            replaceExisting, ct);

        var (ri, rk, rf) = await RestoreGlobalAsync(
            exportDirectory, "roles", RoleParquetTable.FromTable,
            r => access.GetRoleAsync(r.Shortname, ct), r => access.UpsertRoleAsync(r, ct),
            replaceExisting, ct);

        var (pi, pk, pf) = await RestoreGlobalAsync(
            exportDirectory, "permissions", PermissionParquetTable.FromTable,
            p => access.GetPermissionAsync(p.Shortname, ct), p => access.UpsertPermissionAsync(p, ct),
            replaceExisting, ct);

        var (ui, uk, uf) = await RestoreGlobalAsync(
            exportDirectory, "users", UserParquetTable.FromTable,
            u => users.GetByShortnameAsync(u.Shortname, ct), u => users.UpsertAsync(u, ct),
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

        int entriesImported = 0, entriesSkipped = 0, entriesFailed = 0;
        foreach (var entry in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!replaceExisting)
                {
                    var existing = await entries.GetAsync(
                        entry.SpaceName, entry.Subpath, entry.Shortname, entry.ResourceType, ct);
                    if (existing is not null) { entriesSkipped++; continue; }
                }

                await entries.UpsertAsync(entry, ct);
                entriesImported++;
            }
            catch (Exception ex)
            {
                // One bad row must not abandon the rest of a restore, but it
                // must be counted — a summary that says "imported 900" out of
                // 1000 with no failure count reads as success.
                entriesFailed++;
                log.LogWarning(ex, "parquet import: failed to restore {Space}{Subpath}/{Shortname}",
                    entry.SpaceName, entry.Subpath, entry.Shortname);
            }
        }

        imported += entriesImported;
        skipped += entriesSkipped;
        failed += entriesFailed;

        // Attachments come AFTER entries: an attachment's subpath is
        // "<parent subpath>/<parent shortname>", so restoring one before its
        // parent leaves it pointing at nothing.
        var (ai, ak, af) = await RestoreAttachmentsAsync(exportDirectory, replaceExisting, ct);
        imported += ai; skipped += ak; failed += af;
        perTable.Add(new ParquetTableResult("attachments", ai, ak, af));

        log.LogInformation(
            "parquet import: {Imported} imported, {Skipped} skipped, {Failed} failed from {Path}",
            imported, skipped, failed, exportDirectory);

        perTable.Add(new ParquetTableResult("entries", entriesImported, entriesSkipped, entriesFailed));
        return new ParquetImportResult(
            imported, skipped, failed, imported + skipped + failed, perTable);
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
        var manifest = ReadManifest(exportDirectory);
        var rows = ReadTable(exportDirectory, "attachments",
            t => AttachmentParquetTable.FromTable(t, manifest.SpaceName));

        int imported = 0, skipped = 0, failed = 0;
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!replaceExisting)
                {
                    var existing = await attachments.GetAsync(
                        row.Attachment.SpaceName, row.Attachment.Subpath, row.Attachment.Shortname, ct);
                    if (existing is not null) { skipped++; continue; }
                }

                // Read verifies the blob against its own name, so a truncated
                // file throws here rather than becoming attachment media.
                var media = row.MediaSha256 is { } sha
                    ? BlobStore.Read(exportDirectory, sha)
                    : null;

                await attachments.UpsertAsync(row.Attachment with { Media = media }, ct);
                imported++;
            }
            catch (Exception ex)
            {
                failed++;
                log.LogWarning(ex, "parquet import: failed to restore attachment {Space}{Subpath}/{Shortname}",
                    row.Attachment.SpaceName, row.Attachment.Subpath, row.Attachment.Shortname);
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
        Func<T, Task<T?>> get, Func<T, Task> upsert,
        bool replaceExisting, CancellationToken ct)
        where T : class
    {
        var rows = ReadTable(exportDirectory, table, fromTable);
        int imported = 0, skipped = 0, failed = 0;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!replaceExisting && await get(row) is not null) { skipped++; continue; }
                await upsert(row);
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

    private static ParquetExportManifest ReadManifest(string exportDirectory)
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
                manifest.SpaceName));

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
    [property: JsonPropertyName("blob_bytes")] long BlobBytes = 0)
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
