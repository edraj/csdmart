using System.Text.Json;
using Parquet.Serialization;

namespace Dmart.ParquetAdapter;

// Round-trip companion to ParquetArchiver. Reads a .parquet file
// produced by ArchiveFolderAsync and reconstitutes the dmart export
// layout under a target directory:
//
//   <target>/.dm/<shortname>/meta.<rt>.json       — from MetaJson
//   <target>/<shortname>.<ext>                     — from BodyBytes
//                                                    (extension picked
//                                                     from payload's
//                                                     content_type)
//
// Binary attachments stay where ArchiveFolderAsync left them on disk;
// the extractor only reconstructs the in-parquet payload. The caller
// (dmart unarchive CLI) is responsible for stitching the
// .attachments/ sibling tree back together if it was preserved.
public sealed class ParquetExtractor
{
    public sealed record ExtractResult(
        string TargetFolder,
        int EntriesExtracted,
        IReadOnlyList<string> Warnings);

    public async Task<ExtractResult> ExtractToFolderAsync(
        string parquetPath,
        string targetFolderPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException($"unarchive: parquet file not found", parquetPath);

        Directory.CreateDirectory(targetFolderPath);
        var dmRoot = Path.Combine(targetFolderPath, ".dm");
        Directory.CreateDirectory(dmRoot);

        var warnings = new List<string>();
        int count = 0;

        await using var stream = File.OpenRead(parquetPath);
        // Parquet.Net 6.x's DeserializeAsync returns DeserializationResult<T>
        // which wraps the row list under .Data. The wrapper also carries
        // schema metadata we don't need here.
        var result = await ParquetSerializer.DeserializeAsync<EntryRow>(stream, cancellationToken: ct);
        var rows = result.Data;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await WriteEntryAsync(row, dmRoot, targetFolderPath, warnings, ct);
                count++;
            }
            catch (Exception ex)
            {
                warnings.Add($"{row.Shortname}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new ExtractResult(targetFolderPath, count, warnings);
    }

    private static async Task WriteEntryAsync(
        EntryRow row, string dmRoot, string terminalFolder, List<string> warnings, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(row.Shortname))
        {
            warnings.Add($"row uuid={row.Uuid}: missing shortname, skipping");
            return;
        }

        var entryDir = Path.Combine(dmRoot, row.Shortname);
        Directory.CreateDirectory(entryDir);
        var metaPath = Path.Combine(entryDir, $"meta.{row.ResourceType}.json");
        await File.WriteAllTextAsync(metaPath, row.MetaJson, ct);

        // Reconstitute the body file when we inlined bytes. The
        // filename extension comes from the meta's payload.content_type
        // field (json / html / markdown / text / csv / jsonl); if
        // missing, default to .json since that's the most common
        // dmart payload shape.
        if (!string.IsNullOrEmpty(row.BodyBase64))
        {
            var bytes = Convert.FromBase64String(row.BodyBase64);
            var ext = ExtractBodyExtension(row.MetaJson);
            var bodyPath = Path.Combine(terminalFolder, $"{row.Shortname}{ext}");
            await File.WriteAllBytesAsync(bodyPath, bytes, ct);
        }
        // When BodyFilename is set but BodyBase64 is null, the body was
        // too large to inline; we leave the on-disk file untouched. If
        // the operator removed it between archive + unarchive, the
        // post-unarchive `dmart import` will surface a missing-body
        // failure, which is the right signal (we don't try to fabricate
        // empty bodies).
    }

    private static string ExtractBodyExtension(string metaJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(metaJson);
            if (!doc.RootElement.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object) return ".json";
            if (!payload.TryGetProperty("content_type", out var ct)
                || ct.ValueKind != JsonValueKind.String) return ".json";
            return ct.GetString() switch
            {
                "json" => ".json",
                "html" => ".html",
                "markdown" => ".md",
                "text" => ".txt",
                "csv" => ".csv",
                "jsonl" => ".jsonl",
                _ => ".json",
            };
        }
        catch
        {
            return ".json";
        }
    }
}
