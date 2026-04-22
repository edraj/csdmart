using System.IO.Compression;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.Services;

// Bulk import/export of dmart spaces as zip archives.
//
// Zip layout (what this service both produces on export and reads on import):
//
//   archive.zip
//     └── {subpath}/                                       (subpath folder; "" for root)
//         └── .dm/
//             └── {entry_shortname}/
//                 ├── meta.{resource_type}.json            Entry meta incl. Payload.Body inline
//                 ├── {entry_shortname}.json               Python-compat: payload body only,
//                 │                                         present when ContentType == json
//                 └── attachments/
//                     ├── {att_shortname}.meta.{rt}.json   Attachment meta (Media=null)
//                     └── {att_shortname}.bin              Raw bytes for attachments that
//                                                          had Attachment.Media on export
//
// Export honours the actor's row-level ACL — the entries/attachments surfaced are
// exactly what that caller could read through /managed/query. An unauthenticated
// caller gets nothing (also matches /managed/query).
public sealed class ImportExportService(
    EntryRepository entries,
    AttachmentRepository attachments,
    EntryService entryService,
    PermissionService perms,
    ILogger<ImportExportService> log)
{
    private const int ExportEntryLimit = 10_000;

    // ---- EXPORT ----

    public async Task<Stream> ExportAsync(string spaceName, string? subpath, string? actor, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Row-level ACL: same mechanism as /managed/query. Empty policies
            // → the actor has no query reach here → write an empty archive.
            List<string>? policies = null;
            if (actor is not null)
            {
                policies = await perms.BuildUserQueryPoliciesAsync(actor, spaceName, subpath ?? "/", ct);
                if (policies.Count == 0)
                {
                    ms.Position = 0;
                    return ms;
                }
            }

            var query = new Query
            {
                Type = QueryType.Search,
                SpaceName = spaceName,
                Subpath = subpath ?? "/",
                FilterSchemaNames = new(),   // export everything regardless of schema
                Limit = ExportEntryLimit,
                RetrieveJsonPayload = true,
            };
            var rows = actor is not null
                ? await entries.QueryAsync(query, actor, policies, ct)
                : await entries.QueryAsync(query, ct);

            foreach (var entry in rows)
            {
                var rt = JsonbHelpers.EnumMember(entry.ResourceType);
                var subpathClean = entry.Subpath.TrimStart('/').TrimEnd('/');
                var dirPath = string.IsNullOrEmpty(subpathClean) ? "" : subpathClean + "/";
                var metaDir = $"{dirPath}.dm/{entry.Shortname}";

                // Entry meta (includes Payload.Body inline).
                var metaPath = $"{metaDir}/meta.{rt}.json";
                var metaJson = JsonSerializer.Serialize(entry, DmartJsonContext.Default.Entry);
                await WriteTextAsync(zip, metaPath, metaJson, ct);

                // Python-compat copy of the JSON payload body.
                if (entry.Payload?.Body is not null && entry.Payload.ContentType == ContentType.Json)
                {
                    var bodyJson = JsonSerializer.Serialize(entry.Payload.Body.Value, DmartJsonContext.Default.JsonElement);
                    await WriteTextAsync(zip, $"{dirPath}{entry.Shortname}.json", bodyJson, ct);
                }

                // Attachments belonging to this entry.
                var attList = await attachments.ListForParentAsync(spaceName, entry.Subpath, entry.Shortname, ct);
                foreach (var att in attList)
                {
                    var attRt = JsonbHelpers.EnumMember(att.ResourceType);
                    var attMetaPath = $"{metaDir}/attachments/{att.Shortname}.meta.{attRt}.json";

                    // Strip bytes from the meta record so the JSON stays small.
                    var attForJson = att with { Media = null };
                    var attJson = JsonSerializer.Serialize(attForJson, DmartJsonContext.Default.Attachment);
                    await WriteTextAsync(zip, attMetaPath, attJson, ct);

                    if (att.Media is { Length: > 0 } bytes)
                    {
                        var binPath = $"{metaDir}/attachments/{att.Shortname}.bin";
                        await WriteBytesAsync(zip, binPath, bytes, ct);
                    }
                }
            }
        }

        ms.Position = 0;
        return ms;
    }

    // ---- IMPORT ----

    public async Task<Response> ImportZipAsync(Stream zip, string? actor, CancellationToken ct = default)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read);

        // First pass — index everything so we can process entries before
        // their attachments (the attachment FK invariant is subpath-based,
        // but creating attachments for a parent that isn't in the DB yet
        // leaves orphans).
        var entryMetaEntries = new List<ZipArchiveEntry>();
        var attachmentMetaEntries = new List<ZipArchiveEntry>();
        var binByShortname = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);

        foreach (var ze in archive.Entries)
        {
            var name = ze.FullName;
            if (!name.Contains(".dm/", StringComparison.Ordinal)) continue;

            if (name.Contains("/attachments/", StringComparison.Ordinal))
            {
                if (ze.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                    binByShortname[name] = ze;
                else if (ze.Name.Contains(".meta.", StringComparison.Ordinal)
                         && ze.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    attachmentMetaEntries.Add(ze);
            }
            else if (ze.Name.StartsWith("meta.", StringComparison.OrdinalIgnoreCase)
                     && ze.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                entryMetaEntries.Add(ze);
            }
        }

        var entriesInserted = 0;
        var attachmentsInserted = 0;
        var failed = new List<Dictionary<string, object>>();

        // Pass 1: entries. Attempt CreateAsync (plugin + schema + perm path);
        // if the entry already exists, fall through to a direct repo upsert
        // so import/export stays idempotent — round-tripping an export into
        // the same DB should land zero "entry exists" errors. Parity with
        // attachment upsert below.
        foreach (var ze in entryMetaEntries)
        {
            try
            {
                await using var stream = ze.Open();
                var loaded = await JsonSerializer.DeserializeAsync(stream, DmartJsonContext.Default.Entry, ct);
                if (loaded is null)
                {
                    failed.Add(new() { ["path"] = ze.FullName, ["error"] = "empty" });
                    continue;
                }

                var result = await entryService.CreateAsync(loaded, actor, ct);
                if (result.IsOk)
                {
                    entriesInserted++;
                    continue;
                }
                if (result.ErrorCode == InternalErrorCode.SHORTNAME_ALREADY_EXIST)
                {
                    await entries.UpsertAsync(loaded, ct);
                    entriesInserted++;
                    continue;
                }
                failed.Add(new()
                {
                    ["path"] = ze.FullName,
                    ["shortname"] = loaded.Shortname,
                    ["kind"] = "entry",
                    ["error"] = result.ErrorMessage ?? "unknown",
                    ["code"] = result.ErrorCode,
                });
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "import entry failed for {Path}", ze.FullName);
                failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "entry", ["error"] = ex.Message });
            }
        }

        // Pass 2: attachments. Parent FK is implicit via subpath — Attachment
        // meta already carries (space_name, subpath = parent_subpath/parent_shortname,
        // shortname). We need the raw .bin companion (if any) before upsert.
        foreach (var ze in attachmentMetaEntries)
        {
            try
            {
                await using var stream = ze.Open();
                var loaded = await JsonSerializer.DeserializeAsync(stream, DmartJsonContext.Default.Attachment, ct);
                if (loaded is null)
                {
                    failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "attachment", ["error"] = "empty" });
                    continue;
                }

                // Re-attach the raw media bytes if the sibling .bin is present.
                var binPath = ze.FullName[..^".meta.xxxxx.json".Length] + ".bin";
                // The suffix strip above is fragile when the resource_type piece
                // has a different length — recompute by convention instead.
                binPath = DeriveBinPathFromMeta(ze.FullName);
                if (binByShortname.TryGetValue(binPath, out var binEntry))
                {
                    await using var bs = binEntry.Open();
                    using var mem = new MemoryStream();
                    await bs.CopyToAsync(mem, ct);
                    loaded = loaded with { Media = mem.ToArray() };
                }

                await attachments.UpsertAsync(loaded, ct);
                attachmentsInserted++;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "import attachment failed for {Path}", ze.FullName);
                failed.Add(new() { ["path"] = ze.FullName, ["kind"] = "attachment", ["error"] = ex.Message });
            }
        }

        return Response.Ok(attributes: new()
        {
            ["entries_inserted"] = entriesInserted,
            ["attachments_inserted"] = attachmentsInserted,
            ["failed_count"] = failed.Count,
            ["failed"] = failed,
        });
    }

    // ---- helpers ----

    // Given e.g. ".dm/abc/attachments/med1.meta.media.json" return
    //        ".dm/abc/attachments/med1.bin"
    private static string DeriveBinPathFromMeta(string metaPath)
    {
        var slash = metaPath.LastIndexOf('/');
        if (slash < 0) return metaPath + ".bin";
        var dir = metaPath[..slash];
        var file = metaPath[(slash + 1)..];
        // file = "{att_shortname}.meta.{rt}.json" — split on first ".meta."
        var cut = file.IndexOf(".meta.", StringComparison.Ordinal);
        var attShortname = cut > 0 ? file[..cut] : Path.GetFileNameWithoutExtension(file);
        return $"{dir}/{attShortname}.bin";
    }

    private static async Task WriteTextAsync(ZipArchive zip, string path, string content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var s = entry.Open();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        await s.WriteAsync(bytes, ct);
    }

    private static async Task WriteBytesAsync(ZipArchive zip, string path, byte[] bytes, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var s = entry.Open();
        await s.WriteAsync(bytes, ct);
    }
}
