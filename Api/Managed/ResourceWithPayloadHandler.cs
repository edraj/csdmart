using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Middleware;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

// Marker used as the TCategory for ILogger<T> on this handler. The handler itself
// is a static class, so it can't be used directly as a generic type argument.
public sealed class ResourceWithPayloadMarker { }

// Mirrors dmart's POST /managed/resource_with_payload (api/managed/router.py +
// api/managed/utils.py::create_or_update_resource_with_payload_handler).
//
// Multipart fields (matching dmart):
//   * payload_file   (file)        — the binary or JSON content
//   * request_record (file)        — JSON-encoded core.Record
//   * space_name     (form text)
//   * sha            (form text)   — optional client-side checksum
//
// Routing rule (matching dmart):
//   * Attachment-flavor resource types (comment, media, json, reaction, reply, share,
//     relationship, alteration, lock, data_asset)         → attachments table; bytes
//                                                          stored in attachments.media
//                                                          column; payload.body holds
//                                                          the filename string
//   * Content / Ticket / Schema                          → entries table; payload file
//                                                          must be JSON, parsed and
//                                                          stored in entries.payload.body
public static class ResourceWithPayloadHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/resource_with_payload",
            async Task<Response> (HttpRequest req, EntryService entries,
                                  AttachmentRepository attachments,
                                  PermissionService perms,
                                  FolderContentValidator folderContent,
                                  ILogger<ResourceWithPayloadMarker> log, HttpContext http,
                                  CancellationToken ct) =>
                await HandleAsync(req, entries, attachments, perms, folderContent, http.ActorOrAnonymous(), log, ct))
          .Produces<Response>()
          .DisableAntiforgery();

        g.MapPost("/resources_from_csv/{resource_type}/{space}/{**rest}",
            async Task<Response> (string resource_type, string space, string rest,
                                  HttpRequest req, CsvService csv, HttpContext http, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE, "unknown resource type", ErrorTypes.Request);
                var split = RouteParts.SplitSubpathAndShortname(rest);
                var subpath = split.Subpath;
                var schema = split.Shortname;
                if (string.IsNullOrEmpty(schema))
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "schema_shortname required", ErrorTypes.Request);
                if (!req.HasFormContentType)
                    return Response.Fail(InternalErrorCode.INVALID_DATA, "expected multipart/form-data", ErrorTypes.Request);

                var form = await req.ReadFormAsync(ct);
                var csvFile = form.Files["resources_file"] ?? form.Files.FirstOrDefault();
                if (csvFile is null)
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "csv file required", ErrorTypes.Request);

                // Python parity: `?is_update=true` switches each row from CREATE to
                // an UpdateAsync that deep-merges only the row's columns into the
                // existing entry's payload.body. Tolerant flag parsing — same shape
                // as other Python wire-flag toggles in this codebase ("true"/"1"/"yes").
                var isUpdate = ParseBoolFlag(req.Query["is_update"].FirstOrDefault());

                // `?start_row=N` resumes an import the request deadline cut off:
                // rows before N are skipped untouched (N = the error's resume_row).
                var startRow = 1;
                if (req.Query["start_row"].FirstOrDefault() is { Length: > 0 } rawStart
                    && (!int.TryParse(rawStart, NumberStyles.None, CultureInfo.InvariantCulture, out startRow) || startRow < 1))
                    return Response.Fail(InternalErrorCode.INVALID_DATA,
                        "start_row must be a positive whole number", ErrorTypes.Request);

                // `?first_row=N` is the same resume, for a caller that uploads
                // only the header plus the rows it still needs instead of the
                // whole file again: it says which file row this body's first
                // data row is, so `failed[].row` and `resume_row` keep counting
                // in the operator's own file. Re-sending the whole file once per
                // part made the transfer quadratic in the number of parts.
                var firstRow = 1;
                if (req.Query["first_row"].FirstOrDefault() is { Length: > 0 } rawFirst
                    && (!int.TryParse(rawFirst, NumberStyles.None, CultureInfo.InvariantCulture, out firstRow) || firstRow < 1))
                    return Response.Fail(InternalErrorCode.INVALID_DATA,
                        "first_row must be a positive whole number", ErrorTypes.Request);

                await using var stream = csvFile.OpenReadStream();
                try
                {
                    return await csv.ImportAsync(space, "/" + subpath.TrimStart('/'), rt,
                        string.IsNullOrEmpty(schema) ? null : schema,
                        stream, http.Actor(), ct, isUpdate, startRow, firstRow);
                }
                catch (CsvImportInterruptedException cut) when (RequestDeadline.Of(http) is { Expired: true } deadline)
                {
                    // Re-thrown for RequestDeadlineMiddleware to answer with a 504;
                    // this only supplies the words.
                    deadline.Explain(CsvTimeoutMessage(cut, deadline, isUpdate), CsvTimeoutInfo(cut));
                    throw;
                }
            })
          .Produces<Response>()
          .DisableAntiforgery();
    }

    public static async Task<Response> HandleAsync(
        HttpRequest req, EntryService entries, AttachmentRepository attachments,
        PermissionService perms, FolderContentValidator folderContent, string actor, ILogger log, CancellationToken ct)
    {
        if (!req.HasFormContentType)
            return Response.Fail(InternalErrorCode.INVALID_DATA, "expected multipart/form-data", ErrorTypes.Request);

        var form = await req.ReadFormAsync(ct);
        var spaceName = form["space_name"].ToString();
        var sha = form["sha"].FirstOrDefault();
        var payloadFile = form.Files["payload_file"];
        var requestRecordFile = form.Files["request_record"];

        if (string.IsNullOrEmpty(spaceName))
            return Response.Fail(InternalErrorCode.INVALID_SPACE_NAME, "space_name is required", ErrorTypes.Request);
        if (payloadFile is null)
            return Response.Fail(InternalErrorCode.MISSING_DATA, "payload_file is required", ErrorTypes.Request);
        if (requestRecordFile is null)
            return Response.Fail(InternalErrorCode.MISSING_DATA, "request_record is required", ErrorTypes.Request);

        Record? record;
        try
        {
            await using var recStream = requestRecordFile.OpenReadStream();
            record = await JsonSerializer.DeserializeAsync(recStream, DmartJsonContext.Default.Record, ct);
        }
        catch (JsonException)
        {
            return Response.Fail(InternalErrorCode.INVALID_DATA, "invalid request body", ErrorTypes.Request);
        }
        if (record is null)
            return Response.Fail(InternalErrorCode.INVALID_DATA, "request_record is empty", ErrorTypes.Request);

        // Python: shortname "auto" → generate from UUID first 8 chars. Resolution
        // is deferred to each persist attempt below so an auto shortname can be
        // re-minted on a collision (entry path) — see RetryOnShortnameCollisionAsync.
        var wasAuto = RequestHandler.IsAutoShortname(record.Shortname);

        // Identifier validation — the same RequestRegex gate /managed/request
        // applies (RequestHandler.cs:74-103). The multipart path reached
        // EntryService and the attachment store with no validation at all, so a
        // shortname containing '/' could make an attachment's authorizing parent
        // resolve to a different row than the create gate checked, and a
        // space_name carrying path separators reached SpaceEventLogger's
        // Path.Combine.
        if (!Utils.RequestRegex.IsValidSpaceName(spaceName))
            return Response.Fail(InternalErrorCode.INVALID_SPACE_NAME,
                $"invalid space_name: '{spaceName}' fails {Utils.RequestRegex.SpaceNamePattern}", ErrorTypes.Request);
        if (!Utils.RequestRegex.IsValidSubpath(record.Subpath))
            return Response.Fail(InternalErrorCode.INVALID_DATA,
                $"invalid subpath: '{record.Subpath}' fails {Utils.RequestRegex.SubpathPattern}", ErrorTypes.Request);
        if (!wasAuto && !Utils.RequestRegex.IsValidShortname(record.Shortname))
            return Response.Fail(InternalErrorCode.INVALID_DATA,
                $"invalid shortname: '{record.Shortname}' fails {Utils.RequestRegex.ShortnamePattern}", ErrorTypes.Request);

        // Anonymous callers never pick their own shortname and never carry an
        // acl / owner group / relationships of their own — the policy
        // /public/submit and the Python-contract /public/attach already enforce.
        // Without it, the back-compat multipart shape let an anonymous caller
        // squat a name and self-grant an ACL on what it created.
        if (actor == "anonymous")
        {
            record = record with { Shortname = "auto", Attributes = StripAnonymousAttributes(record.Attributes) };
            wasAuto = true;
        }

        // Read full bytes (acceptable here — dmart also reads the whole file at once
        // because it computes a sha256 over the entire payload before storing).
        byte[] fileBytes;
        await using (var src = payloadFile.OpenReadStream())
        await using (var ms = new MemoryStream())
        {
            await src.CopyToAsync(ms, ct);
            fileBytes = ms.ToArray();
        }

        const int MaxPayloadSize = 50 * 1024 * 1024; // 50 MB
        if (fileBytes.Length > MaxPayloadSize)
            return Response.Fail(InternalErrorCode.INVALID_DATA, $"payload file exceeds {MaxPayloadSize / (1024 * 1024)}MB limit", ErrorTypes.Request);

        var checksum = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
        if (!string.IsNullOrEmpty(sha) && !string.Equals(sha, checksum, StringComparison.OrdinalIgnoreCase))
            return Response.Fail(InternalErrorCode.INVALID_DATA, "the provided file doesn't match the sha", ErrorTypes.Request);

        var ext = (Path.GetExtension(payloadFile.FileName) ?? "").TrimStart('.').ToLowerInvariant();
        var resourceContentType = InferContentType(payloadFile.ContentType, ext);
        var schemaShortname = ExtractSchemaShortname(record.Attributes);

        if (IsAttachmentResourceType(record.ResourceType))
            // For a server-minted "auto" shortname, StoreAttachmentAsync inserts
            // (failOnConflict) so a collision surfaces and we re-mint; an explicit
            // shortname keeps the overwrite-in-place upsert (single attempt).
            return await RequestHandler.RetryOnShortnameCollisionAsync(
                wasAuto,
                () => StoreAttachmentAsync(
                    RequestHandler.ResolveAutoShortname(record), spaceName, actor, fileBytes, ext,
                    resourceContentType, checksum, sha, schemaShortname, attachments, perms, folderContent, log,
                    failOnConflict: wasAuto, ct),
                r => r.Error?.Code == InternalErrorCode.SHORTNAME_ALREADY_EXIST);

        // Entry-with-payload goes through EntryService.CreateAsync, which rejects a
        // duplicate shortname — re-mint and retry past the rare auto collision.
        return await RequestHandler.RetryOnShortnameCollisionAsync(
            wasAuto,
            () => StoreEntryAsync(
                RequestHandler.ResolveAutoShortname(record), spaceName, actor, fileBytes, ext,
                resourceContentType, checksum, sha, schemaShortname, entries, ct),
            r => r.Error?.Code == InternalErrorCode.SHORTNAME_ALREADY_EXIST);
    }

    // Fields an anonymous multipart caller must never set on what it creates.
    private static readonly string[] AnonymousStrippedAttributes =
        ["acl", "owner_group_shortname", "relationships"];

    private static Dictionary<string, object>? StripAnonymousAttributes(Dictionary<string, object>? attrs)
    {
        if (attrs is null) return null;
        var copy = new Dictionary<string, object>(attrs, StringComparer.Ordinal);
        foreach (var key in AnonymousStrippedAttributes) copy.Remove(key);
        return copy;
    }

    public static async Task<Response> StoreAttachmentAsync(
        Record record, string spaceName, string actor, byte[] fileBytes, string ext,
        ContentType contentType, string checksum, string? clientChecksum, string? schemaShortname,
        AttachmentRepository attachments, PermissionService perms, FolderContentValidator folderContent, ILogger log,
        bool failOnConflict, CancellationToken ct)
    {
        var gateLocator = new Locator(record.ResourceType, spaceName, "/" + record.Subpath.TrimStart('/'), record.Shortname);
        if (!await perms.CanCreateAsync(actor, gateLocator, record.Attributes, ct))
            return Response.Fail(InternalErrorCode.NOT_ALLOWED,
                $"not allowed to create {record.ResourceType}", ErrorTypes.Request);

        // Folder content policy — the same gate the JSON /managed/request attachment
        // path applies (RequestHandler.DispatchCreateAsync). Without it, a folder's
        // content_resource_types / content_schema_shortnames could be bypassed by
        // uploading the attachment via multipart instead of JSON.
        var contentCheck = await folderContent.ValidateRawAsync(
            spaceName, gateLocator.Subpath, record.Shortname, record.ResourceType, record.Attributes, ct);
        if (!contentCheck.IsOk)
            return Response.Fail(contentCheck.ErrorCode, contentCheck.ErrorMessage!, contentCheck.ErrorType ?? ErrorTypes.Request);

        var bodyRef = $"{record.Shortname}.{ext}";
        // Pull meta-level fields out of attributes — same shape entries get via
        // RequestHandler.MaterializeEntry. Without this displayname / description
        // / tags / slug land in the response (echoed back from the request) but
        // never reach the attachments table.
        var attrs = record.Attributes ?? new Dictionary<string, object>();
        var attachment = new Attachment
        {
            Uuid = string.IsNullOrEmpty(record.Uuid) ? Guid.NewGuid().ToString() : record.Uuid,
            Shortname = record.Shortname,
            SpaceName = spaceName,
            // Normalized, matching RequestHandler.CreateAttachmentAsync. Every
            // READ of this table normalizes — AttachmentRepository.GetAsync runs
            // Locator.NormalizeSubpath — but BindAttachment writes a.Subpath
            // verbatim, so a record arriving as "docs/x" was stored as "docs/x"
            // and then never found again by a lookup for "/docs/x". The gate
            // locator two dozen lines up already normalizes for exactly this
            // reason.
            //
            // It matters more now than it did: the cross-type collision guard
            // below queries through GetAsync, so an un-normalized occupant was
            // invisible to it and the overwrite it exists to refuse went ahead.
            Subpath = "/" + record.Subpath.TrimStart('/'),
            ResourceType = record.ResourceType,
            OwnerShortname = actor,
            IsActive = true,
            Slug = attrs.TryGetValue("slug", out var slug) ? slug?.ToString() : null,
            Displayname = attrs.TryGetValue("displayname", out var dn) ? RequestHandler.ParseTranslation(dn) : null,
            Description = attrs.TryGetValue("description", out var de) ? RequestHandler.ParseTranslation(de) : null,
            Tags = ExtractTags(attrs),
            Media = fileBytes,
            // dmart sets payload.body to the filename string for attachment-typed
            // resources; the actual bytes go into the media column.
            Payload = new Payload
            {
                ContentType = contentType,
                Checksum = checksum,
                ClientChecksum = clientChecksum,
                SchemaShortname = schemaShortname,
                Body = StringJsonElement(bodyRef),
            },
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        };

        try
        {
            // failOnConflict (the server-minted "auto" shortname case): insert-only so
            // a collision surfaces SHORTNAME_ALREADY_EXIST and the caller can re-mint,
            // instead of silently overwriting an existing attachment. Explicit
            // shortnames keep the upsert's overwrite-in-place idempotency.
            if (failOnConflict)
            {
                if (!await attachments.TryInsertAsync(attachment, ct))
                    return Response.Fail(InternalErrorCode.SHORTNAME_ALREADY_EXIST,
                        "attachment exists", ErrorTypes.Db);
            }
            else
            {
                // Same cross-type collision guard as RequestHandler's attachment
                // create: the upsert rewrites resource_type along with everything
                // else, so overwriting in place is idempotency only while the type
                // matches. See RequestHandler.AttachmentTypeCollisionAsync.
                if (await RequestHandler.AttachmentTypeCollisionAsync(record, spaceName, attachments, ct)
                    is { } collision)
                    return collision;
                await attachments.UpsertAsync(attachment, ct);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "failed to save attachment {Space}/{Subpath}/{Shortname}",
                spaceName, record.Subpath, record.Shortname);
            return Response.Fail(InternalErrorCode.OBJECT_NOT_SAVED,
                "failed to save attachment", ErrorTypes.Attachment);
        }

        return Response.Ok(records: new[] { record with { Uuid = attachment.Uuid } });
    }

    private static async Task<Response> StoreEntryAsync(
        Record record, string spaceName, string actor, byte[] fileBytes, string ext,
        ContentType contentType, string checksum, string? clientChecksum, string? schemaShortname,
        EntryService entries, CancellationToken ct)
    {
        // dmart only allows Content/Ticket/Schema here, all of which expect JSON payloads.
        // Parse the file as JSON and inline it into payload.body.
        JsonElement? body;
        try
        {
            using var doc = JsonDocument.Parse(fileBytes);
            body = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Response.Fail(InternalErrorCode.INVALID_DATA,
                "invalid request body", ErrorTypes.Request);
        }

        // Mirrors Python's api/managed/utils.py::create_or_update_resource_with_payload_handler
        // which calls Meta.from_record(record) — that unpacks every attribute on the
        // record (workflow_shortname, state, tags, description, reporter, ...) into
        // the meta object. Reusing MaterializeEntry here preserves those fields; we
        // then override Uuid, Payload and IsActive with the multipart-derived values.
        var entry = RequestHandler.MaterializeEntry(record, spaceName, actor) with
        {
            Uuid = string.IsNullOrEmpty(record.Uuid) ? Guid.NewGuid().ToString() : record.Uuid,
            IsActive = true,
            Payload = new Payload
            {
                ContentType = contentType,
                Checksum = checksum,
                ClientChecksum = clientChecksum,
                // dmart's schema rules: meta_schema for schema resources, otherwise
                // pick from the record's attributes.payload.schema_shortname.
                SchemaShortname = record.ResourceType == ResourceType.Schema
                    ? "meta_schema" : schemaShortname,
                Body = body,
            },
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        };

        // Python parity: pass record.attributes (as submitted) to the gate so
        // restricted_fields tests the same surface Python's
        // serve_request_create_check_access does.
        var result = await entries.CreateAsync(entry, actor, record.Attributes, ct: ct);
        if (!result.IsOk)
            return Response.Fail(result.ErrorCode, result.ErrorMessage!, result.ErrorType ?? "request");
        return Response.Ok(records: new[] { record with { Uuid = result.Value!.Uuid } });
    }

    // AOT-safe JsonElement of kind String. Source-gen typeinfo routes through
    // the context so we don't trip IL2026/IL3050.
    private static JsonElement StringJsonElement(string value)
        => JsonSerializer.SerializeToElement(value, DmartJsonContext.Default.String);

    // The REQUEST_TIMEOUT answer for a CSV import that ran out of time. Rows
    // commit one at a time, so the rows before the cut are saved: the message
    // names where the import stopped, what the earlier rows came to, and how to
    // finish — a partial import must read as one, not as a bare timeout.
    // Row numbers count data rows after the header, like `failed[].row`.
    internal static string CsvTimeoutMessage(CsvImportInterruptedException cut, RequestDeadline deadline, bool isUpdate)
    {
        var inv = CultureInfo.InvariantCulture;
        // An interruption raised through one of the standard constructors
        // measured nothing: quoting its defaults would tell the operator "start
        // again from row 1" after an import that may have written most of the
        // file, duplicating every auto-shortname row.
        if (!cut.KnowsProgress)
            return string.Format(inv,
                "The import reached the server's {0} time limit and was stopped. How far it got is not "
                + "known — check the folder before uploading the file again.", deadline.LimitText);

        var processed = cut.LastRow >= cut.FirstRow
            ? string.Format(inv, "Rows {0:N0}–{1:N0} were processed ({2:N0} {3}, {4:N0} failed).",
                cut.FirstRow, cut.LastRow, cut.Inserted, isUpdate ? "updated" : "saved", cut.Failed.Count)
            : "No rows were processed.";
        if (cut.Finished)
            return string.Format(inv, "{0} The import finished, but ran past the server's {1} time limit.",
                processed, deadline.LimitText);

        return string.Format(inv,
            "Import stopped at row {0:N0}: it reached the server's {1} time limit. {2} "
            + "Upload the same file again with start_row={0} to {3} the rest.",
            cut.ResumeRow, deadline.LimitText, processed, isUpdate ? "update" : "import");
    }

    // Same progress, machine-readable, in the error's `info` — built by
    // CsvService.Progress, which caps the per-row `failed` list so an import
    // where every row fails cannot turn an over-budget request into a
    // multi-megabyte error body. `resume_row` is absent when every row was
    // processed and only the answer was cut off, and when the interruption did
    // not measure its progress at all (see CsvImportInterruptedException.
    // KnowsProgress) — an absent resume_row reads as "do not resume blindly".
    internal static List<Dictionary<string, object>>? CsvTimeoutInfo(CsvImportInterruptedException cut)
    {
        if (!cut.KnowsProgress) return null;
        var info = CsvService.Progress(cut.Inserted, cut.Failed);
        if (!cut.Finished) info[0]["resume_row"] = cut.ResumeRow;
        return info;
    }

    // Tolerant wire-flag parser. Accepts the truthy values dmart Python uses
    // ("true"/"1"/"yes", case-insensitive); everything else (including the
    // empty/missing case) is false. Kept local — only the CSV endpoint reads
    // a bool flag from the query string.
    private static bool ParseBoolFlag(string? raw)
        => raw is not null
           && (raw.Equals("true", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("1", StringComparison.Ordinal)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase));

    // Pull `tags` out of a record's attributes dict. Accepts a JSON array, a
    // List<string>, or an IEnumerable<object>; returns an empty list otherwise
    // so the Attachment record's `Tags` (non-nullable) is always populated.
    private static List<string> ExtractTags(Dictionary<string, object> attrs) => AttrHelper.ExtractTags(attrs);

    // dmart's set of attachment-flavor resource types (anything stored in the
    // attachments table). All other types live in the entries table.
    public static bool IsAttachmentResourceType(ResourceType type) => type switch
    {
        ResourceType.Comment      => true,
        ResourceType.Reply        => true,
        ResourceType.Reaction     => true,
        ResourceType.Media        => true,
        ResourceType.Json         => true,
        ResourceType.Share        => true,
        ResourceType.Relationship => true,
        ResourceType.Alteration   => true,
        ResourceType.Lock         => true,
        ResourceType.DataAsset    => true,
        _                         => false,
    };

    // Pulls schema_shortname out of attributes.payload.schema_shortname (the dmart
    // wire convention). Tolerant of either a JsonElement or a Dictionary value.
    public static string? ExtractSchemaShortname(Dictionary<string, object>? attrs)
    {
        if (attrs is null || !attrs.TryGetValue("payload", out var payloadObj)) return null;
        if (payloadObj is JsonElement el && el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("schema_shortname", out var ss) && ss.ValueKind == JsonValueKind.String)
            return ss.GetString();
        if (payloadObj is Dictionary<string, object> d
            && d.TryGetValue("schema_shortname", out var ssRaw))
            return ssRaw?.ToString();
        return null;
    }

    // Maps an HTTP MIME type or filename extension to dmart's ContentType enum.
    public static ContentType InferContentType(string? mime, string ext)
    {
        var mt = (mime ?? "").ToLowerInvariant();
        return mt switch
        {
            "image" => ContentType.Image,
            "image/jpeg" => ContentType.ImageJpeg,
            "image/png"  => ContentType.ImagePng,
            "image/svg+xml" => ContentType.ImageSvg,
            "image/gif"  => ContentType.ImageGif,
            "image/webp" => ContentType.ImageWebp,
            "application/pdf" => ContentType.Pdf,
            "audio/mpeg" => ContentType.Audio,
            "video/mp4"  => ContentType.Video,
            "text/plain" => ContentType.Text,
            "text/markdown" => ContentType.Markdown,
            "text/html"  => ContentType.Html,
            "text/csv"   => ContentType.Csv,
            "application/json" => ContentType.Json,
            "application/jsonlines" or "application/x-ndjson" => ContentType.Jsonl,
            "application/x-python" or "text/x-python" => ContentType.Python,
            "application/vnd.android.package-archive" => ContentType.Apk,
            "application/vnd.sqlite3" => ContentType.Sqlite,
            _ => ext switch
            {
                "jpg" or "jpeg" => ContentType.ImageJpeg,
                "png"  => ContentType.ImagePng,
                "svg"  => ContentType.ImageSvg,
                "gif"  => ContentType.ImageGif,
                "webp" => ContentType.ImageWebp,
                "pdf"  => ContentType.Pdf,
                "mp3" or "wav" or "ogg" => ContentType.Audio,
                "mp4" or "mov" or "webm" => ContentType.Video,
                "txt"  => ContentType.Text,
                "md"   => ContentType.Markdown,
                "html" or "htm" => ContentType.Html,
                "csv"  => ContentType.Csv,
                "json" => ContentType.Json,
                "jsonl" or "ndjson" => ContentType.Jsonl,
                "py"   => ContentType.Python,
                "apk"  => ContentType.Apk,
                "sqlite" or "db" => ContentType.Sqlite,
                "parquet" => ContentType.Parquet,
                // Not json. An upload we cannot place is opaque, and calling it
                // JSON is a claim about the bytes rather than an absence of one:
                // a .docx stored as json was served as application/json with no
                // disposition at all, dumping binary into the browser tab, and
                // the MCP download tool labelled it the same way. `binary` says
                // what we actually know, and PayloadHandler turns it into an
                // octet-stream attachment under its own filename.
                _ => ContentType.Binary,
            },
        };
    }
}
