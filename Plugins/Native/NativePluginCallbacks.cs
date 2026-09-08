using System.Text;
using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Plugins.Native;

// Host-side implementations of the callbacks an external plugin can make back
// into dmart: read and write entries and users, run a query, send mail,
// broadcast on a channel, log, read attachment bytes.
//
// "Native" here means operator-supplied and external, as opposed to the
// managed plugins in Plugins/BuiltIn — not in-process. Plugins reach these
// over the subprocess line protocol; PluginCallbackDispatcher parses a
// callback frame and calls the matching Emit* method below.
//
// Every method is named Emit* and takes typed arguments so it can be driven
// directly from tests, and every one of them is written NOT to throw: a throw
// would escape into SubprocessPluginHost's read loop and strand an exchange
// with the plugin still waiting on its result. Failures are reported the way
// each callback's contract says — an {"error":…} document, or a non-zero int
// code — never by propagating.
//
// Thread model: these run synchronously on the thread inside
// SubprocessPluginHost.SendAndReceive, which is a thread-pool thread with no
// captured SynchronizationContext, so bridging dmart's async services with
// GetAwaiter().GetResult() cannot deadlock. The repositories use library-style
// `await` with no ConfigureAwait(true), so their continuations run on the pool.
public static class NativePluginCallbacks
{
    // One-way bridge from DI into the static methods below. Program.cs sets
    // this right after `app.Build()`. The methods resolve scoped services with
    // CreateScope() on each call.
    public static IServiceProvider? Services { get; set; }

    // ILoggerFactory is a singleton in the container, so resolving it once
    // is fine — and logging is on the hot path, so we want to avoid the
    // CreateScope/Resolve cost per call. Concurrent readers either both
    // see null (resolving to the same DI singleton, which we publish
    // atomically via Interlocked.CompareExchange) or both see the cached
    // reference. Reference reads are atomic on all .NET-supported CPUs,
    // so a Volatile.Read isn't strictly necessary.
    private static ILoggerFactory? _loggerFactoryCache;

    // DmartSettings is bound via `services.AddOptions<DmartSettings>().Bind(...)`
    // in Program.cs and has no IOptionsMonitor registration, so its value is
    // effectively immutable for the process lifetime. Cache the management
    // space name to skip a CreateScope + IOptions resolve on every
    // update_user call. Cleared by SetServicesForTesting so test swaps see
    // the new settings rather than the prior fixture's.
    private static string? _mgmtSpaceCache;

    // ========================================================================
    // Callback implementations
    // ========================================================================

    // internal for testing via dmart.Tests. Returns the JSON body the callback
    // surfaces: a serialized Entry, {"entry":null} on a miss, or {"error":…}.
    // Never throws — a throw would escape into SubprocessPluginHost's read loop
    // and strand the exchange with the plugin still waiting on its result.
    internal static string EmitLoadEntry(
        string sp, string sub, string sn, string? rtStr, ILogger? logger)
    {
        try
        {
            if (Services is null)
            {
                logger?.LogWarning("load_entry called before services initialized");
                return """{"error":"services_not_initialized"}""";
            }
            using var scope = Services.CreateScope();
            var entries = scope.ServiceProvider.GetRequiredService<EntryRepository>();

            Entry? entry;
            if (!string.IsNullOrEmpty(rtStr) && TryParseResourceType(rtStr, out var rt))
                entry = entries.GetAsync(sp, sub, sn, rt).GetAwaiter().GetResult();
            else
                entry = entries.GetAsync(sp, sub, sn).GetAwaiter().GetResult();

            if (entry is null)
            {
                logger?.LogDebug("load_entry miss {Space}/{Subpath}/{Shortname}", sp, sub, sn);
                return """{"entry":null}""";
            }
            logger?.LogDebug("load_entry hit {Space}/{Subpath}/{Shortname}", sp, sub, sn);
            return JsonSerializer.Serialize(entry, DmartJsonContext.Default.Entry);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "load_entry failed for {Space}/{Subpath}/{Shortname}", sp, sub, sn);
            return $$"""{"error":{{JsonEncode(ex.Message)}}}""";
        }
    }

    // internal for testing via dmart.Tests. Returns a serialized User,
    // {"user":null} on a miss, or {"error":…}. Never throws.
    internal static string EmitLoadUser(string sn, ILogger? logger)
    {
        try
        {
            if (Services is null)
            {
                logger?.LogWarning("load_user called before services initialized");
                return """{"error":"services_not_initialized"}""";
            }
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserRepository>();
            var user = users.GetByShortnameAsync(sn).GetAwaiter().GetResult();
            if (user is null)
            {
                logger?.LogDebug("load_user miss {User}", sn);
                return """{"user":null}""";
            }
            logger?.LogDebug("load_user hit {User}", sn);
            return JsonSerializer.Serialize(user, DmartJsonContext.Default.User);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "load_user failed for {User}", sn);
            return $$"""{"error":{{JsonEncode(ex.Message)}}}""";
        }
    }

    // internal for testing via dmart.Tests. Performs an atomic
    // prior-fetch + upsert via UpsertWithPriorAsync (SELECT FOR UPDATE +
    // INSERT ON CONFLICT in one transaction), computes a {field: {old, new}}
    // diff against the prior row, and appends a history record for non-empty
    // diffs on the update path. Returns 0 = ok, non-zero = error; the codes are
    // part of the callback's wire contract, so don't renumber them.
    //
    // Create path (`inserted == true`) writes no history row — Python parity
    // with EntryService.UpdateAsync, which only logs on update.
    // See the file-header thread-model block for the sync-over-async rationale.
    internal static int EmitSaveEntry(Entry entry, ILogger? logger)
    {
        if (Services is null)
        {
            logger?.LogWarning("save_entry rejected: services not ready");
            return 1;
        }
        try
        {
            using var scope = Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<EntryRepository>();
            var history = scope.ServiceProvider.GetRequiredService<HistoryRepository>();

            var (prior, inserted) = repo.UpsertWithPriorAsync(entry).GetAwaiter().GetResult();

            Dictionary<string, object>? diff = null;
            if (!inserted && prior is not null)
            {
                diff = HistoryDiffUtil.ComputeEntryDiff(prior, entry);
                if (diff.Count > 0)
                {
                    history.AppendAsync(entry.SpaceName, entry.Subpath, entry.Shortname,
                                        PluginInvocationContext.CurrentActor,
                                        BuildPluginMarkerHeaders(logger, "save_entry"),
                                        diff).GetAwaiter().GetResult();
                }
            }

            logger?.LogInformation(
                "save_entry ok {Space}/{Subpath}/{Shortname} byPlugin={Plugin} actor={Actor} inserted={Inserted} diffKeys={DiffKeys}",
                entry.SpaceName, entry.Subpath, entry.Shortname,
                PluginInvocationContext.CurrentShortname,
                PluginInvocationContext.CurrentActor,
                inserted, diff?.Count ?? 0);
            return 0;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "save_entry failed for {Space}/{Subpath}/{Shortname}",
                             entry.SpaceName, entry.Subpath, entry.Shortname);
            return 3;
        }
    }

    // internal for testing via dmart.Tests. Same shape as EmitSaveEntry —
    // atomic upsert (single-tx SELECT FOR UPDATE + INSERT ON CONFLICT)
    // followed by a conditional history-append addressing the user under the
    // configured management space. See EmitSaveEntry for the file-header
    // sync-over-async rationale.
    internal static int EmitUpdateUser(User user, ILogger? logger)
    {
        if (Services is null)
        {
            logger?.LogWarning("update_user rejected: services not ready");
            return 1;
        }
        try
        {
            using var scope = Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<UserRepository>();
            var history = scope.ServiceProvider.GetRequiredService<HistoryRepository>();

            var (prior, inserted) = repo.UpsertWithPriorAsync(user).GetAwaiter().GetResult();

            Dictionary<string, object>? diff = null;
            if (!inserted && prior is not null)
            {
                diff = HistoryDiffUtil.ComputeUserDiff(prior, user);
                if (diff.Count > 0)
                {
                    history.AppendAsync(ResolveManagementSpace(scope), "/users", user.Shortname,
                                        PluginInvocationContext.CurrentActor,
                                        BuildPluginMarkerHeaders(logger, "update_user"),
                                        diff).GetAwaiter().GetResult();
                }
            }

            logger?.LogInformation(
                "update_user ok {User} byPlugin={Plugin} actor={Actor} inserted={Inserted} diffKeys={DiffKeys}",
                user.Shortname,
                PluginInvocationContext.CurrentShortname,
                PluginInvocationContext.CurrentActor,
                inserted, diff?.Count ?? 0);
            return 0;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "update_user failed for {User}", user.Shortname);
            return 3;
        }
    }

    // Resolves the management-space name from DmartSettings, caching across
    // calls. The first call goes through DI; later calls return the cached
    // value (settings are immutable for the process lifetime — see
    // _mgmtSpaceCache for the rationale).
    private static string ResolveManagementSpace(IServiceScope scope)
    {
        var cached = Volatile.Read(ref _mgmtSpaceCache);
        if (cached is not null) return cached;
        var resolved = scope.ServiceProvider.GetRequiredService<IOptions<DmartSettings>>()
            .Value.ManagementSpace;
        Interlocked.CompareExchange(ref _mgmtSpaceCache, resolved, null);
        return Volatile.Read(ref _mgmtSpaceCache) ?? resolved;
    }

    // Builds the {x-source: "plugin", x-plugin: <shortname>} marker so audit
    // consumers can tell plugin-written rows apart from REST writes. When the
    // dispatcher hasn't seeded PluginInvocationContext.CurrentShortname the
    // history row carries no marker rather than the misleading literal
    // "unknown". Production showed this is NOT only a bug path: a plugin that
    // calls back from its own thread (or any continuation the AsyncLocal
    // doesn't flow into) legitimately lands here on every call — so the
    // warning is rate-limited to once per op per hour (Debug in between)
    // instead of flooding the log at request frequency.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _markerWarnLast = new();

    private static Dictionary<string, object>? BuildPluginMarkerHeaders(ILogger? logger, string opName)
    {
        if (PluginInvocationContext.CurrentShortname is { } shortname)
        {
            return new Dictionary<string, object>
            {
                ["x-source"] = "plugin",
                ["x-plugin"] = shortname,
            };
        }
        logger?.Log(
            ShouldWarnMarkerNow(opName) ? LogLevel.Warning : LogLevel.Debug,
            "{Op}: PluginInvocationContext.CurrentShortname is null — history row will lack plugin marker "
            + "(the plugin invoked this callback outside the hook invocation context, e.g. from its own thread)",
            opName);
        return null;
    }

    private static bool ShouldWarnMarkerNow(string opName)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var last = _markerWarnLast.GetOrAdd(opName, long.MinValue);
        if (last != long.MinValue && now - last < 3600) return false;
        return _markerWarnLast.TryUpdate(opName, now, last);
    }

    // internal for testing via dmart.Tests. 0 = ok, non-zero = error; the codes
    // are part of the callback's wire contract. Never throws.
    internal static int EmitSendEmail(string? t, string? s, string? b, ILogger? logger)
    {
        try
        {
            if (string.IsNullOrEmpty(t) || Services is null)
            {
                logger?.LogWarning("send_email rejected: empty 'to' or services not ready");
                return 1;
            }
            using var scope = Services.CreateScope();
            var smtp = scope.ServiceProvider.GetRequiredService<SmtpSender>();
            var ok = smtp.SendEmailAsync(t!, s ?? "", b ?? "").GetAwaiter().GetResult();
            if (ok)
                logger?.LogInformation("send_email ok to={To} subjectLen={SubjectLen}", t, s?.Length ?? 0);
            else
                logger?.LogWarning("send_email returned false to={To}", t);
            return ok ? 0 : 4;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "send_email failed to={To}", t);
            return 3;
        }
    }

    // internal for testing via dmart.Tests. 0 = ok, non-zero = error; the codes
    // are part of the callback's wire contract. Never throws.
    internal static int EmitWsBroadcast(string? ch, string? msg, ILogger? logger)
    {
        try
        {
            if (string.IsNullOrEmpty(ch) || Services is null)
            {
                logger?.LogWarning("ws_broadcast rejected: empty channel or services not ready");
                return 1;
            }
            // WsConnectionManager is a singleton so no scope needed, but use
            // CreateScope for consistency — the resolver short-circuits on
            // singletons.
            using var scope = Services.CreateScope();
            var ws = scope.ServiceProvider.GetRequiredService<WsConnectionManager>();
            var ok = ws.BroadcastToChannelAsync(ch!, msg ?? "").GetAwaiter().GetResult();
            if (ok)
                logger?.LogDebug("ws_broadcast ok channel={Channel} bytes={Bytes}", ch, msg?.Length ?? 0);
            else
                logger?.LogWarning("ws_broadcast returned false channel={Channel}", ch);
            return ok ? 0 : 4;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "ws_broadcast failed channel={Channel}", ch);
            return 3;
        }
    }

    // Plugin → host query: same QueryService the HTTP API uses. By default
    // the query runs as the user that triggered the hook (so user
    // permissions are honored). A plugin can override the actor by adding
    // an "as_actor" field to the query JSON:
    //   - field absent       → caller's actor (the exchange's ambient actor)
    //   - field = "username"  → impersonate that user
    //   - field = null       → no actor (system / no ACL)

    // internal for testing via dmart.Tests. `ambientActor` is who the query
    // runs as when the JSON carries no "as_actor" override — the user that
    // triggered the hook or API request. It is a PARAMETER rather than a read
    // of PluginInvocationContext so the subprocess bridge can pass the actor it
    // already knows from the exchange envelope instead of depending on thread
    // affinity. Changing that default is security-relevant: the three-tier
    // resolution in ResolveActor is what keeps a plugin query inside the
    // triggering user's permissions. Never throws.
    internal static string EmitQuery(string? json, string? ambientActor, ILogger? logger)
    {
        try
        {
            if (string.IsNullOrEmpty(json) || Services is null)
            {
                logger?.LogWarning("query rejected: empty payload or services not ready");
                return BuildQueryFailJson(InternalErrorCode.SOMETHING_WRONG,
                    "empty query or services not initialized", ErrorTypes.Internal);
            }

            // Single-parse: pull both the actor override (if any) and the
            // typed Query out of the same JsonDocument. Deserializing twice
            // doubled the parse cost on every plugin query.
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException jex)
            {
                logger?.LogWarning("query rejected: invalid json: {Message}", jex.Message);
                return BuildQueryFailJson(InternalErrorCode.INVALID_DATA,
                    $"invalid query json: {jex.Message}", ErrorTypes.Request);
            }

            using (doc)
            {
                var actor = ResolveActor(doc.RootElement, ambientActor);
                Query? query;
                try { query = doc.RootElement.Deserialize(DmartJsonContext.Default.Query); }
                catch (JsonException jex)
                {
                    logger?.LogWarning("query rejected: invalid json: {Message}", jex.Message);
                    return BuildQueryFailJson(InternalErrorCode.INVALID_DATA,
                        $"invalid query json: {jex.Message}", ErrorTypes.Request);
                }
                if (query is null)
                {
                    logger?.LogWarning("query rejected: deserialize returned null");
                    return BuildQueryFailJson(InternalErrorCode.INVALID_DATA,
                        "invalid query json", ErrorTypes.Request);
                }
                logger?.LogDebug("query type={Type} space={Space} actor={Actor}",
                    query.Type, query.SpaceName, actor);
                using var scope = Services.CreateScope();
                var qsvc = scope.ServiceProvider.GetRequiredService<QueryService>();
                var resp = qsvc.ExecuteAsync(query, actor).GetAwaiter().GetResult();
                return JsonSerializer.Serialize(resp, DmartJsonContext.Default.Response);
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "query failed");
            return BuildQueryFailJson(InternalErrorCode.SOMETHING_WRONG, ex.Message, ErrorTypes.Exception);
        }
    }

    // Three-tier actor resolution for plugin queries:
    //   "as_actor" present, string  → impersonate that user
    //   "as_actor" present, JSON null → run as system (no ACL filter)
    //   "as_actor" absent             → fall back to ambient (the user
    //                                   that triggered the hook / API
    //                                   request, set by the dispatcher).
    // internal for testing via dmart.Tests.
    internal static string? ResolveActor(JsonElement root, string? ambient)
    {
        if (root.ValueKind != JsonValueKind.Object) return ambient;
        if (!root.TryGetProperty("as_actor", out var asActor)) return ambient;
        return asActor.ValueKind == JsonValueKind.Null ? null : asActor.GetString();
    }

    // internal for testing via dmart.Tests.
    internal static string BuildQueryFailJson(int code, string message, string type)
        => JsonSerializer.Serialize(Response.Fail(code, message, type), DmartJsonContext.Default.Response);

    // Plugin → host: fetch the raw `media` BYTEA for an attachment by
    // (space, subpath, shortname). Returns null when the attachment is missing
    // or carries no media — the caller renders that as {"media":null}.
    //
    // internal for testing via dmart.Tests. Never throws.
    internal static byte[]? EmitGetMediaAttachment(
        string sp, string sub, string sn, ILogger? logger)
    {
        try
        {
            if (Services is null)
            {
                logger?.LogWarning("get_media called before services initialized");
                return null;
            }
            using var scope = Services.CreateScope();
            var atts = scope.ServiceProvider.GetRequiredService<AttachmentRepository>();
            var att = atts.GetAsync(sp, sub, sn).GetAwaiter().GetResult();
            if (att is null || !Guid.TryParse(att.Uuid, out var uuid))
            {
                logger?.LogDebug("get_media miss {Space}/{Subpath}/{Shortname}", sp, sub, sn);
                return null;
            }
            var (bytes, _) = atts.GetMediaAsync(uuid).GetAwaiter().GetResult();
            if (bytes is null || bytes.Length == 0)
            {
                logger?.LogDebug("get_media miss {Space}/{Subpath}/{Shortname}", sp, sub, sn);
                return null;
            }
            logger?.LogDebug("get_media ok {Space}/{Subpath}/{Shortname} bytes={Bytes}",
                sp, sub, sn, bytes.Length);
            return bytes;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "get_media failed {Space}/{Subpath}/{Shortname}", sp, sub, sn);
            return null;
        }
    }

    // Plugin → host structured log. The plugin's shortname (set by the
    // dispatcher in PluginInvocationContext.CurrentShortname) is prefixed
    // onto the category so a plugin can't impersonate "Microsoft.AspNetCore"
    // or "Dmart.Startup" to escape the operator's log-level filter config.
    //   category = null/empty → "plugin.<shortname>"
    //   category = "events"   → "plugin.<shortname>.events"
    // Messages are clamped to 16 KB to bound a runaway plugin's log volume;
    // when truncation occurs the recorded line is 16 KB + the literal
    // "…[truncated]" suffix (~12 bytes), so downstream consumers should
    // budget for "16 KB + suffix" rather than treating 16 KB as a hard cap.
    //
    // internal for testing via dmart.Tests.
    internal static void EmitPluginLog(int level, string? sub, string? msg)
    {
        var factory = GetLoggerFactory();
        if (factory is null) return;
        if (string.IsNullOrEmpty(msg)) return;
        if (msg.Length > 16384) msg = string.Concat(msg.AsSpan(0, 16384), "…[truncated]");

        var shortname = PluginInvocationContext.CurrentShortname ?? "unknown";
        var fullCategory = string.IsNullOrEmpty(sub)
            ? $"plugin.{shortname}"
            : $"plugin.{shortname}.{sub}";

        var lvl = ClampLevel(level);
        var logger = factory.CreateLogger(fullCategory);
        if (logger.IsEnabled(lvl)) logger.Log(lvl, "{Message}", msg);
    }

    // internal for testing via dmart.Tests. Returns a serialized JSON array
    // of firebase_token strings; never returns null.
    //
    // The empty-array "[]" on the error path is a deliberate deviation from
    // LoadEntry/LoadUser/SaveEntry's JSON error envelope. This callback's
    // sole consumer is push-notification dispatch, where "no devices to
    // push to" and "lookup failed" are functionally equivalent — the
    // plugin skips the push either way — so collapsing both cases keeps
    // the plugin code path uniform. Don't switch to an error envelope
    // without auditing every plugin that parses the result.
    internal static string EmitGetSessionFirebaseTokens(
        string shortname, int? inactivityTtlSeconds, ILogger? logger)
    {
        // Short-circuit empty shortname before opening a DI scope + DB
        // round-trip. The repository would return [] anyway, but a
        // null/empty pointer from the plugin is almost certainly a bug
        // and isn't worth the per-call overhead.
        if (string.IsNullOrEmpty(shortname)) return "[]";
        try
        {
            if (Services is null)
            {
                logger?.LogWarning("get_session_firebase_tokens called before services initialized");
                return "[]";
            }
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserRepository>();
            var tokens = users.GetSessionFirebaseTokensAsync(shortname, inactivityTtlSeconds)
                .GetAwaiter().GetResult();
            logger?.LogDebug("get_session_firebase_tokens {User} count={Count}", shortname, tokens.Count);
            return JsonSerializer.Serialize(tokens, DmartJsonContext.Default.ListString);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "get_session_firebase_tokens failed for {User}", shortname);
            return "[]";
        }
    }

    // internal for testing — lets tests inject a fake ILoggerFactory or
    // clear the cache between cases without going through DI.
    internal static void SetServicesForTesting(IServiceProvider? services)
    {
        Services = services;
        _loggerFactoryCache = null;
        _mgmtSpaceCache = null;
    }

    // Host-side logger for callback operations. Uses `plugin.<shortname>.callbacks`
    // so operator filter rules that already target `plugin.*` cover plugin-driven
    // host activity. `EmitPluginLog` writes to `plugin.<shortname>[.<sub>]`
    // directly — these two categories share a family but are distinct
    // subcategories so host-emitted vs plugin-emitted lines are filterable
    // independently.
    //
    // Must never throw: the dispatcher resolves this before doing any work, and
    // a throw here would escape into the read loop and strand the exchange.
    // `Services` can be mid-disposal during test teardown or shutdown, in which
    // case `GetService` / `CreateLogger` raise `ObjectDisposedException`.
    // Swallow and return null — the rest of the callback uses `logger?.Log…`
    // so the call becomes a no-op.
    //
    // internal so PluginCallbackDispatcher logs host-side callback activity
    // under the same category the callbacks themselves use.
    internal static ILogger? GetCallbackLogger()
    {
        try
        {
            var factory = GetLoggerFactory();
            if (factory is null) return null;
            var sn = PluginInvocationContext.CurrentShortname ?? "unknown";
            return factory.CreateLogger($"plugin.{sn}.callbacks");
        }
        catch
        {
            return null;
        }
    }

    private static ILoggerFactory? GetLoggerFactory()
    {
        // Volatile.Read is defensive — reference reads are already atomic on
        // .NET-supported CPUs, but Volatile guarantees we see a fully
        // initialized factory after the publishing thread's CompareExchange,
        // not a torn or stale view on weakly ordered architectures.
        var cached = Volatile.Read(ref _loggerFactoryCache);
        if (cached is not null) return cached;
        var resolved = Services?.GetService<ILoggerFactory>();
        if (resolved is null) return null;
        // CompareExchange publishes our reference atomically and yields to
        // any racing thread that already wrote one. Either way we return
        // whatever ended up cached, so all callers see the same instance.
        Interlocked.CompareExchange(ref _loggerFactoryCache, resolved, null);
        return Volatile.Read(ref _loggerFactoryCache);
    }

    // internal for testing via dmart.Tests.
    internal static LogLevel ClampLevel(int raw) => raw switch
    {
        0 => LogLevel.Trace,
        1 => LogLevel.Debug,
        2 => LogLevel.Information,
        3 => LogLevel.Warning,
        4 => LogLevel.Error,
        5 => LogLevel.Critical,
        6 => LogLevel.None,
        _ => LogLevel.Information,
    };

    // ========================================================================
    // Helpers
    // ========================================================================

    // Minimal JSON string encoder for the error-path only (avoids bringing
    // JsonSerializer into every error return). internal so the subprocess
    // callback bridge encodes its error messages the same way.
    internal static string JsonEncode(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static bool TryParseResourceType(string raw, out ResourceType value)
    {
        foreach (var a in Enum.GetValues<ResourceType>())
        {
            if (JsonbHelpers.EnumMember(a) == raw) { value = a; return true; }
        }
        value = default;
        return false;
    }
}
