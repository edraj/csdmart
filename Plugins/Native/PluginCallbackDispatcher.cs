using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dmart.Models.Json;

namespace Dmart.Plugins.Native;

// Routes plugin → host callbacks arriving over the subprocess line protocol to
// the same managed implementations the in-process C ABI uses.
//
// Every op below lands in a `NativePluginCallbacks.Emit*` method holding the
// actual implementation, so this file stays a routing table: parse the frame,
// pick the op, render the result.
//
// Wire format (see custom_plugins_sdk/README.md for the author-facing version):
//
//   plugin → dmart:  {"type":"callback","id":1,"op":"load_entry","args":{...}}
//   dmart → plugin:  {"type":"callback_result","id":1,"ok":true,"result":{...}}
//   dmart → plugin:  {"type":"callback_result","id":1,"ok":false,"error":"..."}
//
// `id` is echoed back verbatim (whatever JSON value the plugin used) so a
// plugin can correlate without the host imposing a numbering scheme.
//
// `result` shape depends on the op: document-returning ops yield that JSON
// document as-is, and the rest yield {"code":N} with 0 = ok, non-zero = error.
//
// `ok:false` means the CALLBACK ITSELF was malformed (unknown op, bad args) —
// not that the operation failed. A failed operation is `ok:true` with an error
// document or a non-zero code inside `result`. The split matters: a plugin can
// tell "dmart does not understand me" (rebuild needed) from "the save did not
// work" (retry or report), which one flag conflating both would hide.
internal static class PluginCallbackDispatcher
{
    // True when the line is a callback frame rather than the exchange's final
    // response. Anything that isn't a JSON object with "type":"callback" is
    // treated as the response, so a plugin written against an older dmart —
    // which never sends callbacks — behaves exactly as before.
    public static bool IsCallback(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty("type", out var t)
           && t.ValueKind == JsonValueKind.String
           && t.GetString() == "callback";

    // Handles one callback frame and returns the JSON line to write back.
    // Never throws: a throw here would escape into SendAndReceive and strand
    // the exchange with the plugin still waiting on its result.
    //
    // `ambientActor` is the actor the exchange runs as (the hook's
    // user_shortname / the API request's resolved user). It is passed down
    // rather than read from PluginInvocationContext so `query` honors the
    // triggering user's permissions without depending on thread affinity —
    // see NativePluginCallbacks.EmitQuery.
    public static string Handle(JsonElement root, string? ambientActor)
    {
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetRawText() : "null";
        var logger = NativePluginCallbacks.GetCallbackLogger();

        try
        {
            var op = root.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.String
                ? opEl.GetString()
                : null;
            if (string.IsNullOrEmpty(op)) return Fail(id, "callback missing \"op\"");

            var args = root.TryGetProperty("args", out var a) ? a : default;

            switch (op)
            {
                case "load_entry":
                    return Ok(id, NativePluginCallbacks.EmitLoadEntry(
                        Str(args, "space") ?? "",
                        Str(args, "subpath") ?? "",
                        Str(args, "shortname") ?? "",
                        Str(args, "resource_type"),
                        logger));

                case "load_user":
                    return Ok(id, NativePluginCallbacks.EmitLoadUser(Str(args, "shortname") ?? "", logger));

                case "save_entry":
                {
                    // Nested under "entry" rather than being the whole args
                    // payload, so the frame has room for future per-call
                    // options without a breaking change.
                    if (!args.TryGetProperty("entry", out var entryEl))
                        return Fail(id, "save_entry requires args.entry");
                    var entry = Deserialize(entryEl, DmartJsonContext.Default.Entry, out var err);
                    if (entry is null) return Ok(id, Code(err is null ? 2 : 3));
                    return Ok(id, Code(NativePluginCallbacks.EmitSaveEntry(entry, logger)));
                }

                case "update_user":
                {
                    if (!args.TryGetProperty("user", out var userEl))
                        return Fail(id, "update_user requires args.user");
                    var user = Deserialize(userEl, DmartJsonContext.Default.User, out var err);
                    if (user is null) return Ok(id, Code(err is null ? 2 : 3));
                    return Ok(id, Code(NativePluginCallbacks.EmitUpdateUser(user, logger)));
                }

                case "send_email":
                    return Ok(id, Code(NativePluginCallbacks.EmitSendEmail(
                        Str(args, "to"), Str(args, "subject"), Str(args, "html"), logger)));

                case "ws_broadcast":
                    return Ok(id, Code(NativePluginCallbacks.EmitWsBroadcast(
                        Str(args, "channel"), Str(args, "message"), logger)));

                case "query":
                    // args IS the query document, including any "as_actor"
                    // override, so ResolveActor sees exactly what the plugin
                    // wrote.
                    return Ok(id, NativePluginCallbacks.EmitQuery(
                        args.ValueKind == JsonValueKind.Object ? args.GetRawText() : null,
                        ambientActor, logger));

                case "log":
                    NativePluginCallbacks.EmitPluginLog(
                        args.TryGetProperty("level", out var lv) && lv.TryGetInt32(out var lvi) ? lvi : 2,
                        Str(args, "category"),
                        Str(args, "message"));
                    return Ok(id, Code(0));

                case "get_session_firebase_tokens":
                {
                    int? ttl = args.TryGetProperty("inactivity_ttl_seconds", out var t)
                               && t.TryGetInt32(out var ttlv) && ttlv > 0
                        ? ttlv : null;
                    return Ok(id, NativePluginCallbacks.EmitGetSessionFirebaseTokens(
                        Str(args, "shortname") ?? "", ttl, logger));
                }

                case "get_media_attachment":
                {
                    // Base64 rather than a side channel: it keeps one framing
                    // for the whole protocol, at 33% inflation on the one
                    // callback that exists to move large blobs. Worth measuring
                    // against a length-prefixed binary frame if a plugin ever
                    // pulls attachments in bulk.
                    var bytes = NativePluginCallbacks.EmitGetMediaAttachment(
                        Str(args, "space") ?? "",
                        Str(args, "subpath") ?? "",
                        Str(args, "shortname") ?? "",
                        logger);
                    return Ok(id, bytes is null
                        ? """{"media":null}"""
                        : $$"""{"media_b64":"{{Convert.ToBase64String(bytes)}}","length":{{bytes.Length}}}""");
                }

                default:
                    return Fail(id, $"unknown callback op: {op}");
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "subprocess callback dispatch failed");
            return Fail(id, ex.Message);
        }
    }

    // Deserialize with the source-generated context (AOT-safe). `err` is set
    // when the payload was syntactically bad, so the caller can tell "parsed to
    // null" (code 2) apart from "threw while parsing" (code 3).
    private static T? Deserialize<T>(JsonElement el, JsonTypeInfo<T> info,
                                     out string? err) where T : class
    {
        try
        {
            err = null;
            return el.Deserialize(info);
        }
        catch (JsonException jex)
        {
            err = jex.Message;
            return null;
        }
    }

    private static string? Str(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // 0 = ok, non-zero = error. The numbers are part of the wire contract —
    // SDK helpers switch on them, so don't renumber.
    private static string Code(int code) => $$"""{"code":{{code}}}""";

    // `result` is spliced in as raw JSON — every Emit* above returns a complete
    // JSON document, so re-serializing it through a string would double-encode.
    private static string Ok(string id, string resultJson)
        => $$"""{"type":"callback_result","id":{{id}},"ok":true,"result":{{resultJson}}}""";

    private static string Fail(string id, string message)
        => $$"""{"type":"callback_result","id":{{id}},"ok":false,"error":{{NativePluginCallbacks.JsonEncode(message)}}}""";
}
