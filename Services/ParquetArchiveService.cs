using System.Text.Json;
using System.Text.Json.Serialization;
using Dmart.DataAdapters.Parquet;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.Logging;

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
// This increment covers the `entries` table of a FULL export. Attachments,
// histories, spaces/users/roles/permissions, blob sharding (§4.3) and the
// incremental watermark (§5) are not wired yet; the manifest records enough for
// them to be added without changing what is already written.
public sealed class ParquetArchiveService(
    EntryRepository entries,
    PermissionService perms,
    ILogger<ParquetArchiveService> log)
{
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
                return await WriteManifestAsync(outputDirectory, spaceName, watermark, [], 0, ct);
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

        return await WriteManifestAsync(
            outputDirectory, spaceName, watermark,
            [new ParquetTableManifest("entries", [relativePath], rowCount)],
            rowCount, ct);
    }

    private static async Task<ParquetExportManifest> WriteManifestAsync(
        string directory, string spaceName, DateTime watermark,
        List<ParquetTableManifest> tables, long rowCount, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var manifest = new ParquetExportManifest(
            FormatVersion, DateTime.UtcNow, watermark, spaceName, tables, rowCount);

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
        var rows = ReadEntries(exportDirectory);
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

        log.LogInformation(
            "parquet import: {Imported} imported, {Skipped} skipped, {Failed} failed from {Path}",
            imported, skipped, failed, exportDirectory);

        return new ParquetImportResult(imported, skipped, failed, rows.Count);
    }

    /// <summary>Reads back the entries an export wrote, in file order.</summary>
    /// <remarks>
    /// The restore half. Kept here rather than in the reader so the reader stays
    /// a format concern and this stays a dmart one.
    /// </remarks>
    public static List<Entry> ReadEntries(string exportDirectory)
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
    [property: JsonPropertyName("row_count")] long RowCount);

/// <param name="Total">
/// Rows read from the archive. Imported + Skipped + Failed must equal it — a
/// restore summary that does not add up is hiding a row.
/// </param>
public sealed record ParquetImportResult(int Imported, int Skipped, int Failed, int Total);

public sealed record ParquetTableManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("files")] List<string> Files,
    [property: JsonPropertyName("row_count")] long RowCount);

// Source-generated, like everything else that serializes here: reflection-based
// serialization is off for the whole project (§ AOT).
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ParquetExportManifest))]
internal partial class ParquetManifestJsonContext : JsonSerializerContext;
