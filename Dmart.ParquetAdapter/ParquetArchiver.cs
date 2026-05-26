using System.Text.Json;
using Parquet.Serialization;

namespace Dmart.ParquetAdapter;

// Packs a single terminal folder of a dmart filesystem export into a
// single Apache Parquet file. "Terminal folder" = a folder under a
// space's tree that contains `.dm/<shortname>/meta.*.json` entry rows.
// One row per entry. Snappy compression by default.
//
// Body handling:
//   * Small JSON / HTML / TXT / MD / CSV / JSONL payload bodies that
//     sit next to the .dm/ folder are inlined into the BodyBytes
//     column (capped at 1MB).
//   * Larger bodies + any binary attachments stay on disk; their
//     filename is recorded so `dmart unarchive` can re-link them.
//
// Threading: each call is single-threaded over its own folder. The
// caller (dmart archive CLI) parallelises ACROSS terminal folders.
public sealed class ParquetArchiver
{
    private const long InlineCapBytes = 1024 * 1024;  // 1 MB

    private static readonly HashSet<string> InlinableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".html", ".txt", ".md", ".csv", ".jsonl" };

    public sealed record ArchiveResult(
        string OutputPath,
        int EntryCount,
        long ParquetBytes,
        IReadOnlyList<string> SkippedFiles);

    public async Task<ArchiveResult> ArchiveFolderAsync(
        string terminalFolderPath,
        string outputParquetPath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(terminalFolderPath))
            throw new DirectoryNotFoundException($"archive: source '{terminalFolderPath}' does not exist");

        var dmRoot = Path.Combine(terminalFolderPath, ".dm");
        if (!Directory.Exists(dmRoot))
            throw new InvalidOperationException(
                $"archive: '{terminalFolderPath}' has no .dm/ directory — not a dmart terminal folder");

        // Each subdirectory of .dm/ is one entry, identified by its
        // directory name (= the shortname). Each entry has exactly
        // one meta.<rt>.json inside.
        var entryDirs = Directory.EnumerateDirectories(dmRoot).ToList();
        var rows = new List<EntryRow>(entryDirs.Count);
        var skipped = new List<string>();

        foreach (var dir in entryDirs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var row = await LoadEntryAsync(dir, terminalFolderPath, ct);
                if (row is not null) rows.Add(row);
                else skipped.Add(dir);
            }
            catch
            {
                skipped.Add(dir);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputParquetPath)!);
        await using (var stream = File.Create(outputParquetPath))
        {
            await ParquetSerializer.SerializeAsync(rows, stream, cancellationToken: ct);
        }

        var info = new FileInfo(outputParquetPath);
        return new ArchiveResult(outputParquetPath, rows.Count, info.Length, skipped);
    }

    private static async Task<EntryRow?> LoadEntryAsync(string entryDmDir, string terminalFolder, CancellationToken ct)
    {
        var metaPath = Directory.EnumerateFiles(entryDmDir, "meta.*.json").FirstOrDefault();
        if (metaPath is null) return null;

        var metaJson = await File.ReadAllTextAsync(metaPath, ct);
        using var doc = JsonDocument.Parse(metaJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

        var shortname = Read(doc, "shortname") ?? "";
        var uuid = Read(doc, "uuid") ?? "";
        var resourceType = Read(doc, "resource_type") ?? ExtractRtFromFilename(metaPath);
        if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(shortname))
            return null;

        byte[]? body = null;
        string? bodyFilename = null;
        foreach (var ext in InlinableExtensions)
        {
            var candidate = Path.Combine(terminalFolder, $"{shortname}{ext}");
            if (!File.Exists(candidate)) continue;
            var fi = new FileInfo(candidate);
            if (fi.Length > InlineCapBytes)
            {
                bodyFilename = Path.GetFileName(candidate);
            }
            else
            {
                body = await File.ReadAllBytesAsync(candidate, ct);
            }
            break;
        }

        return new EntryRow
        {
            Uuid = uuid,
            Shortname = shortname,
            ResourceType = resourceType,
            Subpath = Read(doc, "subpath") ?? "/",
            CreatedAt = ReadDt(doc, "created_at"),
            UpdatedAt = ReadDt(doc, "updated_at"),
            MetaJson = metaJson,
            // Base64 the inline body so Parquet.Net's serializer (which
            // can't ingest byte[] columns) writes it as a string. The
            // extractor decodes on the way out.
            BodyBase64 = body is null ? null : Convert.ToBase64String(body),
            BodyFilename = bodyFilename,
        };
    }

    // ---- small helpers --------------------------------------------

    private static string? Read(JsonDocument doc, string field)
        => doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static DateTime? ReadDt(JsonDocument doc, string field)
    {
        if (!doc.RootElement.TryGetProperty(field, out var v)) return null;
        if (v.ValueKind != JsonValueKind.String) return null;
        return DateTime.TryParse(v.GetString(), out var dt) ? dt : null;
    }

    private static string ExtractRtFromFilename(string metaPath)
    {
        // "meta.content.json" → "content"; "meta.ticket.json" → "ticket"
        var name = Path.GetFileNameWithoutExtension(metaPath);
        var dot = name.IndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : "content";
    }
}
