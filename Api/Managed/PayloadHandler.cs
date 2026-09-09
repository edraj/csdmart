using System.Text;
using System.Text.Json;
using Dmart.Api;
using MediaTypeNames = System.Net.Mime.MediaTypeNames;
using ContentDispositionHeaderValue = Microsoft.Net.Http.Headers.ContentDispositionHeaderValue;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

// Serves the binary or JSON payload of a resource. dmart's storage convention:
//   * Attachment-flavor types (Comment, Reply, Reaction, Media, Json, Share, Lock,
//     DataAsset, Relationship, Alteration) → bytes live in attachments.media
//   * Entry-flavor types (Content, Folder, Schema, Ticket) → JSON payload lives
//     in entries.payload.body (jsonb)
public static class PayloadHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Catchall captures multi-segment subpath + filename. The filename is then
        // parsed by RouteParts.SplitPayloadParts to identify shortname / schema / ext.
        // Mirrors dmart Python's `/payload/{resource_type}/{space}/{subpath:path}/{shortname}.{ext}`
        // and `/payload/{resource_type}/{space}/{subpath:path}/{shortname}.{schema}.{ext}`.
        g.MapGet("/payload/{resource_type}/{space}/{**rest}",
            async (string resource_type, string space, string rest,
                   AttachmentRepository attachments, EntryService entries,
                   PermissionService perms, HttpContext http, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ResourceType>(resource_type, true, out var rt))
                    return Results.BadRequest($"unknown resource_type '{resource_type}'");
                var parts = RouteParts.SplitPayloadParts(rest);
                if (parts is null)
                    return Results.BadRequest($"invalid payload path '{rest}' — expected {{subpath}}/{{shortname}}.{{ext}}");
                var (subpath, shortname, _schema, ext) = parts.Value;
                return await ServePayloadAsync(rt, space, subpath, shortname, ext,
                    attachments, entries, perms, http.Actor(), http, ct);
            });
    }

    public static async Task<IResult> ServePayloadAsync(
        ResourceType rt, string space, string subpath, string shortname, string ext,
        AttachmentRepository attachments, EntryService entries, PermissionService perms,
        string? actor, HttpContext http, CancellationToken ct)
    {
        var normalizedSubpath = Locator.NormalizeSubpath(subpath);

        if (ResourceWithPayloadHandler.IsAttachmentResourceType(rt))
        {
            // Metadata + byte length, not the bytes. Everything below decides on
            // metadata alone, and the body is streamed after — so a 401 or a
            // Range no longer drags the whole blob out of the database. See
            // AttachmentRepository.GetWithMediaLengthAsync.
            var found = await attachments.GetWithMediaLengthAsync(space, normalizedSubpath, shortname, ct);
            if (found is null || found.Value.MediaLength <= 0) return Results.NotFound();
            var (att, mediaLength) = found.Value;
            // Gate on the ROW's resource_type, never the URL's. AttachmentRepository
            // has no typed lookup at all — (space, subpath, shortname) is the whole
            // identity — so `rt` here is an unverified caller claim. Trusting it let
            // a view grant on resource_types:["comment"] pull down the bytes of a
            // media attachment simply by asking for /payload/comment/...
            var attachmentLocator = new Locator(att.ResourceType, space, normalizedSubpath, shortname);
            if (!await perms.CanReadAsync(actor, attachmentLocator, PermissionService.FromAttachment(att), ct))
            {
                return Results.Json(
                    Response.Fail(InternalErrorCode.NOT_ALLOWED,
                        "You don't have permission to this action", ErrorTypes.Request),
                    DmartJsonContext.Default.Response,
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            var mime = MimeFor(att.Payload?.ContentType, ext);
            var fileName = $"{shortname}.{ext}";
            var forceDownload = WantsDownload(http.Request);
            var body = new AttachmentMediaStream(attachments, space, normalizedSubpath, shortname, mediaLength);

            // Ranges on every branch, not just the inline one. Resumability is
            // orthogonal to disposition: a ?download=1 on a 50MB video that drops
            // at 90% should resume rather than restart, and a download manager
            // should be able to parallelise it.
            const bool ranges = true;

            // JSON payloads (either by stored content-type or by ".json" URL
            // suffix, case-insensitive) come back with no disposition header at
            // all, so callers can read the body straight off the response —
            // unless ?download was asked for explicitly.
            if (IsJsonResponse(mime, ext))
                return Results.File(body, ContentTypeHeaderFor(mime),
                    forceDownload ? fileName : null, enableRangeProcessing: ranges);

            if (RendersInline(mime) && !forceDownload)
            {
                // Results.File writes Content-Disposition only when handed a
                // fileDownloadName, and it always spells it `attachment`. So
                // write the inline form here and leave that argument null, or
                // it gets clobbered. SetHttpFileName does the quoting and the
                // RFC 5987 filename*, which also keeps a crafted shortname from
                // breaking out of the header.
                var cd = new ContentDispositionHeaderValue("inline");
                cd.SetHttpFileName(fileName);
                var disposition = cd.ToString();
                // Deferred to OnStarting so an unsatisfiable Range does not come
                // back as a 416 still claiming to carry an inline body. By the
                // time this runs, Results.File has settled the status code.
                http.Response.OnStarting(() =>
                {
                    if (http.Response.StatusCode is StatusCodes.Status200OK
                        or StatusCodes.Status206PartialContent)
                        http.Response.Headers.ContentDisposition = disposition;
                    return Task.CompletedTask;
                });
                // The global X-Frame-Options: DENY is aimed at clickjacking the
                // SPA's own documents; applied to a payload it also blocks the
                // in-app viewers, which embed a PDF in an <iframe> on the SAME
                // origin. Downgrading to SAMEORIGIN for this response keeps
                // cross-origin framing refused while letting cxb and catalog
                // render what this endpoint went inline for. Set directly rather
                // than via OnStarting: ResponseHeadersMiddleware's callback runs
                // last (OnStarting is LIFO) and defers to a value already
                // present. Inline only — an attachment is never framed.
                http.Response.Headers.XFrameOptions = "SAMEORIGIN";
                // Ranges let <audio>/<video> seek and let PDF viewers fetch
                // page-at-a-time instead of pulling the whole file.
                return Results.File(body, ContentTypeHeaderFor(mime), enableRangeProcessing: ranges);
            }
            return Results.File(body, ContentTypeHeaderFor(mime), fileName, enableRangeProcessing: ranges);
        }

        // Entry-flavor: serialize the inline JSON payload from
        // entries.payload.body. The body is always JSON, so inline by default —
        // no filename, no attachment header. UTF-8 explicit so non-ASCII
        // content (Arabic, Kurdish payloads) round-trips intact.
        var locator = new Locator(rt, space, normalizedSubpath, shortname);
        var entry = await entries.GetAsync(locator, actor, ct);
        if (entry?.Payload?.Body is null) return Results.NotFound();
        var bodyJson = JsonSerializer.Serialize(entry.Payload.Body!.Value, DmartJsonContext.Default.JsonElement);
        // ?download works here too. WantsDownload's contract is "for any type",
        // and Results.Content never emits a disposition — so this branch used to
        // render in the tab and silently ignore the flag a caller had trusted.
        if (WantsDownload(http.Request))
        {
            var entryCd = new ContentDispositionHeaderValue("attachment");
            entryCd.SetHttpFileName($"{shortname}.json");
            http.Response.Headers.ContentDisposition = entryCd.ToString();
        }
        return Results.Content(bodyJson, MediaTypeNames.Application.Json, Encoding.UTF8);
    }

    // MIME types a browser renders passively, so a pasted payload URL shows the
    // thing instead of downloading it. Python's dmart sends no Content-Disposition
    // at all; this is that behaviour minus the types that are not passive.
    //
    // text/html and image/svg+xml are deliberately absent. Both are documents
    // the browser will execute script from, attachments are user-uploaded, and
    // this API authenticates by the auth_token cookie — inline would be stored
    // XSS on our own origin. The global CSP only attaches to text/html
    // responses, so svg in particular would carry no policy whatsoever.
    internal static bool RendersInline(string mime) => mime switch
    {
        "image/jpeg" or "image/png" or "image/gif" or "image/webp" => true,
        "application/pdf" => true,
        "audio/mpeg" or "video/mp4" => true,
        "text/plain" or "text/markdown" or "text/csv" => true,
        _ => false,
    };

    // ?download=1 (or =true) forces the attachment header for any type, so a
    // client that wants a save-to-disk always has one. Checking the VALUE and
    // not merely the key means ?download=0 still renders inline.
    //
    // Read as a list rather than via StringValues.ToString(), which JOINS
    // repeats with a comma: ?download=1&download=1 — trivially produced by a
    // client appending the param to a URL that already carries it — flattened
    // to "1,1", matched nothing, and silently turned the user's save-to-disk
    // back into a render. The last non-empty value wins, trimmed, which is the
    // same precedence model binders use.
    internal static bool WantsDownload(HttpRequest req)
    {
        if (!req.Query.TryGetValue("download", out var values)) return false;
        for (var i = values.Count - 1; i >= 0; i--)
        {
            var raw = values[i]?.Trim();
            if (string.IsNullOrEmpty(raw)) continue;
            return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    internal static bool IsJsonResponse(string mime, string ext) =>
        string.Equals(mime, MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ext, "json", StringComparison.OrdinalIgnoreCase);

    // The Content-Type header value: the media type plus a charset for the
    // formats where its absence corrupts the render.
    //
    // MimeFor stays parameter-free because RendersInline and IsJsonResponse
    // compare against bare media types, and because it is public API (McpTools
    // passes the result to an MCP download envelope). The charset belongs on
    // the wire, not in the switch.
    //
    // Without this, an inline text/plain of UTF-8 Arabic arrived with a bare
    // `text/plain`; the global X-Content-Type-Options: nosniff stops the browser
    // recovering the encoding, so it falls back to the locale default and the
    // text renders as mojibake. It never mattered while these types downloaded.
    internal static string ContentTypeHeaderFor(string mime) =>
        mime.StartsWith("text/", StringComparison.Ordinal)
        || string.Equals(mime, MediaTypeNames.Application.Json, StringComparison.Ordinal)
            ? $"{mime}; charset=utf-8"
            : mime;

    // Stored ContentType is coarse — one `audio` value covers mp3/wav/ogg, one
    // `video` covers mp4/webm/mov — and InferContentType's fallback for an
    // unrecognised upload is `json`, not octet-stream. Both matter far more now
    // that the type decides whether a browser is asked to RENDER the bytes:
    //
    //   * a .webm labelled video/mp4 reaches the MP4 demuxer and fails to play,
    //     where before it downloaded and the OS picked a decoder;
    //   * a .docx stored as `json` was served as application/json with no
    //     disposition at all, dumping binary into the tab.
    //
    // So the URL extension disambiguates when the stored type is coarse or is
    // the catch-all. An empty ext (McpTools) keeps the historical defaults.
    public static string MimeFor(ContentType? contentType, string ext)
    {
        var e = (ext ?? "").ToLowerInvariant();
        return contentType switch
        {
            ContentType.Text       => "text/plain",
            ContentType.Markdown   => "text/markdown",
            ContentType.Html       => "text/html",
            // The catch-all: only really JSON when the URL agrees. Anything else
            // is an upload InferContentType could not place, so let the extension
            // speak and fall back to octet-stream — which RendersInline refuses,
            // so it downloads under its own filename.
            ContentType.Json       => e is "" or "json" ? "application/json" : MimeForExtension(e),
            // Comment and Reaction attachments carry JSON bodies; without a case
            // here they fell to octet-stream.
            ContentType.Comment or ContentType.Reaction => "application/json",
            ContentType.ImageJpeg  => "image/jpeg",
            ContentType.ImagePng   => "image/png",
            ContentType.ImageSvg   => "image/svg+xml",
            ContentType.ImageGif   => "image/gif",
            ContentType.ImageWebp  => "image/webp",
            // The generic `image` value, which InferContentType produces for a
            // bare "image" MIME and ParseContentType accepts off the wire. It had
            // no case at all, so the very attachments this endpoint went inline
            // for still downloaded. Python's dmart maps image → image_jpeg.
            ContentType.Image      => MimeForExtension(e) is { } m && m.StartsWith("image/", StringComparison.Ordinal)
                                        ? m : "image/jpeg",
            ContentType.Pdf        => "application/pdf",
            ContentType.Audio      => e switch
            {
                "wav"  => "audio/wav",
                "ogg" or "oga" => "audio/ogg",
                "m4a"  => "audio/mp4",
                "flac" => "audio/flac",
                _      => "audio/mpeg",
            },
            ContentType.Video      => e switch
            {
                "webm" => "video/webm",
                "mov"  => "video/quicktime",
                "ogv"  => "video/ogg",
                _      => "video/mp4",
            },
            ContentType.Csv        => "text/csv",
            ContentType.Jsonl      => "application/jsonlines",
            ContentType.Python     => "text/x-python",
            ContentType.Apk        => "application/vnd.android.package-archive",
            ContentType.Sqlite     => "application/vnd.sqlite3",
            ContentType.Parquet    => "application/octet-stream",
            _                      => "application/octet-stream",
        };
    }

    // Extension → media type for the types InferContentType has no enum value
    // for, so they at least download correctly labelled instead of as JSON.
    // Deliberately short: it exists to rescue the catch-all, not to become a
    // second MIME registry.
    private static string MimeForExtension(string ext) => ext switch
    {
        "zip"  => "application/zip",
        "gz" or "tgz" => "application/gzip",
        "tar"  => "application/x-tar",
        "7z"   => "application/x-7z-compressed",
        "doc"  => "application/msword",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xls"  => "application/vnd.ms-excel",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "ppt"  => "application/vnd.ms-powerpoint",
        "pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "jpg" or "jpeg" => "image/jpeg",
        "png"  => "image/png",
        "gif"  => "image/gif",
        "webp" => "image/webp",
        _      => "application/octet-stream",
    };
}
