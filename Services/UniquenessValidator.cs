using System.Buffers;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;

namespace Dmart.Services;

// Port of Python's `validate_uniqueness` (dmart/backend/data_adapters/sql/
// adapter.py:2624). The folder that contains the entry being created/updated
// can declare a `unique_fields` list in its payload body — a list of compound
// keys, where each compound is a list of attribute paths. For each compound,
// we extract the values from the incoming entry and search the folder for any
// other entry that matches on every path of the compound. Any hit means the
// uniqueness constraint is violated.
//
// Example folder payload body:
//   {
//     "unique_fields": [
//       ["payload.body.email"],
//       ["payload.body.firstname", "payload.body.lastname"]
//     ]
//   }
//
// Path forms:
//   - "payload.body.<dot.path>" → look up under entry.Payload.Body
//   - anything else             → look up under the entry's flat attributes
//
// List values: paths support `[]` segments to iterate through arrays.
//   - `payload.body.ids`            — primitive array; each element becomes a
//                                     uniqueness probe (auto-emits `ids[]:`).
//   - `payload.body.ids[]`          — same as above, explicit form.
//   - `payload.body.variants[].sku` — array of objects; pulls .sku from each
//                                     element. Writes search tokens with the
//                                     `[].sku:` literal so QueryHelper's
//                                     EXISTS-style JSONB predicate matches.
// QueryHelper handles the `field[].sub:value` SQL translation
// (DataAdapters/Sql/QueryHelper.cs::BuildPayloadArraySql) for ONE bracket
// segment per path. Paths with multiple bracket segments (e.g.
// `outer[].inner[].leaf`) extract values correctly on the validator side
// but the SQL probe degenerates — the second `[]` is treated as part of a
// literal key name and finds nothing. Pinned by the
// NestedArrays_Search_Is_Single_Bracket_Only integration test; lift that
// test once QueryHelper grows nested-EXISTS support.
//
// Implementation note: we hit EntryRepository.QueryAsync directly (not
// QueryService.ExecuteAsync). Uniqueness is a global constraint — if it
// went through QueryService with actor:null, the anonymous-permission gate
// would short-circuit to an empty result on any space that doesn't grant
// world-level view, hiding real conflicts. Going via the repo skips the
// ACL layer cleanly while reusing the same SQL search builder.
public sealed class UniquenessValidator(
    EntryRepository entries,
    UserRepository users,
    AccessRepository access,
    AttachmentRepository attachments,
    ILogger<UniquenessValidator> log)
{
    // Characters that QueryHelper's tokenizer treats specially; values that
    // contain any of these need to be wrapped in double quotes to round-trip.
    private static readonly SearchValues<char> SearchTokenizerSpecials =
        SearchValues.Create(" \t()|@");


    // Raw-attrs entry point used by RequestHandler for resource types that
    // bypass EntryService (User/Role/Permission/attachments). Mirrors
    // Python's adapter.py::validate_uniqueness which is invoked from
    // api/managed/utils.py for every create/update regardless of resource
    // type — the C# port previously only ran the validator for Entry writes,
    // so a folder with `unique_fields` configured for users/roles silently
    // allowed duplicates.
    //
    // We dispatch the conflict probe to the right backing table based on
    // resourceType (Python parity: adapter_helpers.set_table_for_query).
    //
    // Path-convention quirk (load-bearing): for User/Role/Permission, the
    // request payload (`rec.Attributes`) is FLAT at top level — `email`,
    // `msisdn`, `displayname.en` — because those resource types have
    // promoted columns and don't carry a generic `payload.body`. A folder
    // gating users on email must therefore declare `[["email"]]`, NOT
    // `[["payload.body.email"]]`. The latter form silently no-ops because
    // the attrs dict has no `payload` key. We surface this as a LogDebug
    // when a compound resolves to zero token sets — see the per-compound
    // log line below — so misconfiguration is at least visible in logs.
    // Attachment resource types DO carry payload.body and behave like
    // entries; either path form works for them.
    public async Task<Result<bool>> ValidateRawAsync(
        string spaceName,
        string subpath,
        string shortname,
        ResourceType resourceType,
        Dictionary<string, object>? rawAttrs,
        ActionType action,
        CancellationToken ct = default)
    {
        var (parentSubpath, folderShortname) = SplitSubpath(subpath);
        if (folderShortname.Length == 0) return Result<bool>.Ok(true);

        Entry? folder;
        try
        {
            folder = await entries.GetAsync(spaceName, parentSubpath, folderShortname, ResourceType.Folder, ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "uniqueness: parent folder load failed for {Space}/{Subpath}", spaceName, subpath);
            folder = null;
        }
        if (folder?.Payload?.Body is not JsonElement body
            || body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("unique_fields", out var uniqueFields)
            || uniqueFields.ValueKind != JsonValueKind.Array)
            return Result<bool>.Ok(true);

        // Materialize rawAttrs into a JsonElement so the existing WalkPath
        // helpers can drive both `payload.body.*` and flat (`email`/`msisdn`/
        // `displayname.en`/...) paths uniformly. Source-gen serializer is
        // happy because each value already round-trips as JsonElement.
        // A serialization failure means we can't run the gate at all on a
        // payload that may very well violate it — fail closed rather than
        // silently waving the request through.
        JsonElement? rootOpt = null;
        if (rawAttrs is not null)
        {
            try { rootOpt = JsonSerializer.SerializeToElement(rawAttrs, DmartJsonContext.Default.DictionaryStringObject); }
            catch (Exception ex)
            {
                log.LogWarning(ex, "uniqueness: failed to materialize rawAttrs for {Space}{Subpath}", spaceName, subpath);
                return Result<bool>.Fail(
                    InternalErrorCode.MISSING_DATA,
                    "uniqueness probe could not materialize attributes",
                    ErrorTypes.Request);
            }
        }
        if (rootOpt is null || rootOpt.Value.ValueKind != JsonValueKind.Object)
            return Result<bool>.Ok(true);
        var root = rootOpt.Value;

        foreach (var compound in uniqueFields.EnumerateArray())
        {
            if (compound.ValueKind != JsonValueKind.Array) continue;

            var perPathTokens = new List<List<string>>();
            var declaredPaths = 0;
            foreach (var pathEl in compound.EnumerateArray())
            {
                if (pathEl.ValueKind != JsonValueKind.String) continue;
                var path = pathEl.GetString();
                if (string.IsNullOrEmpty(path)) continue;
                declaredPaths++;

                var newValues = ReadPathFromAttrs(root, path);
                if (newValues.Count == 0) continue;

                perPathTokens.Add(BuildSearchTokens(path, newValues));
            }
            if (perPathTokens.Count == 0)
            {
                // A compound with declared paths that all resolve empty is
                // either intentional (the request didn't touch any of those
                // fields — fine on update) or a misconfiguration. The most
                // common misconfig is using `["payload.body.X"]` for User/
                // Role/Permission folders where the convention is flat
                // `["X"]`. Surface the path list at debug level so operators
                // can spot it in logs without breaking the request.
                if (declaredPaths > 0 && action == ActionType.Create)
                {
                    log.LogDebug(
                        "uniqueness: compound key on {Space}{Subpath} resolved to no token sets for {Resource} create — likely path-convention mismatch (flat vs payload.body). Compound: {Compound}",
                        spaceName, subpath, resourceType, compound);
                }
                continue;
            }

            foreach (var tokenSet in CartesianProduct(perPathTokens))
            {
                var search = string.Join(' ', tokenSet);
                var q = new Query
                {
                    Type = QueryType.Subpath,
                    SpaceName = spaceName,
                    Subpath = subpath,
                    ExactSubpath = true,
                    Search = search,
                    Limit = 2,
                    FilterSchemaNames = new(),
                };

                List<(string Shortname, string Subpath)> hits;
                try { hits = await ProbeAsync(resourceType, q, ct); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "uniqueness probe failed for {Space}{Subpath} search={Search}",
                        spaceName, subpath, search);
                    continue;
                }

                if (action == ActionType.Update)
                {
                    var normalizedSubpath = "/" + subpath.TrimStart('/');
                    hits = hits
                        .Where(h => !(string.Equals(h.Shortname, shortname, StringComparison.Ordinal)
                                      && string.Equals(h.Subpath, normalizedSubpath, StringComparison.Ordinal)))
                        .ToList();
                }
                if (hits.Count == 0) continue;

                return Result<bool>.Fail(
                    InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                    $"Entry properties should be unique: {search}",
                    ErrorTypes.Request);
            }
        }
        return Result<bool>.Ok(true);
    }

    private List<string> ReadPathFromAttrs(JsonElement root, string path)
    {
        var values = new List<string>();
        if (path.StartsWith("payload.body.", StringComparison.Ordinal))
        {
            if (!root.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("body", out var bodyEl)
                || bodyEl.ValueKind != JsonValueKind.Object)
                return values;
            WalkPath(bodyEl, path["payload.body.".Length..], values, originalPath: path);
            return values;
        }
        // Flat / nested non-payload path — walk straight from the attrs root.
        // Supports `email`, `msisdn`, `displayname.en`, `tags[]`, etc.
        WalkPath(root, path, values, originalPath: path);
        return values;
    }

    // Dispatches the conflict probe to the table that backs `resourceType`,
    // mirroring Python's set_table_for_query. Returns (shortname, subpath)
    // tuples so the self-filter on update can run uniformly across tables.
    //
    // Attachment resource types must route to AttachmentRepository — they
    // live in the `attachments` table, NOT `entries`. A previous version
    // routed everything not-User/Role/Permission to `entries.QueryAsync`,
    // which silently returned no hits for attachment types and let
    // duplicates through.
    private async Task<List<(string Shortname, string Subpath)>> ProbeAsync(
        ResourceType rt, Query q, CancellationToken ct)
    {
        switch (rt)
        {
            case ResourceType.User:
                var us = await users.QueryAsync(q, ct);
                return us.Select(u => (u.Shortname, u.Subpath)).ToList();
            case ResourceType.Role:
                var rs = await access.QueryRolesAsync(q, ct);
                return rs.Select(r => (r.Shortname, r.Subpath)).ToList();
            case ResourceType.Permission:
                var ps = await access.QueryPermissionsAsync(q, ct);
                return ps.Select(p => (p.Shortname, p.Subpath)).ToList();
            case ResourceType.Comment:
            case ResourceType.Reply:
            case ResourceType.Reaction:
            case ResourceType.Media:
            case ResourceType.Json:
            case ResourceType.Share:
            case ResourceType.Relationship:
            case ResourceType.Alteration:
            case ResourceType.Lock:
            case ResourceType.DataAsset:
                // Mirrors RequestHandler.CreateAttachmentAsync's switch list at
                // Api/Managed/RequestHandler.cs:329-339 — these are the types
                // that live in the `attachments` table. Csv/Jsonl/Sqlite/
                // Parquet fall through to the default branch because they're
                // routed through CreateEntryAsync (the `entries` table).
                var ats = await attachments.QueryAsync(q, ct);
                return ats.Select(a => (a.Shortname, a.Subpath)).ToList();
            case ResourceType.Space:
                // Spaces are root-level — no parent folder, so the validator's
                // SplitSubpath returns empty folder shortname and we never
                // reach this dispatch in practice. Fail loudly if a future
                // call site bypasses that guard, rather than silently
                // misrouting to the wrong table.
                throw new NotSupportedException(
                    "uniqueness probe is not supported for ResourceType.Space");
            default:
                var es = await entries.QueryAsync(q, ct);
                return es.Select(e => (e.Shortname, e.Subpath)).ToList();
        }
    }

    public async Task<Result<bool>> ValidateAsync(
        Entry entry, ActionType action, Entry? existing, CancellationToken ct = default)
    {
        // Locate the folder that owns this entry's subpath.
        var (parentSubpath, folderShortname) = SplitSubpath(entry.Subpath);
        if (folderShortname.Length == 0) return Result<bool>.Ok(true);

        Entry? folder;
        try
        {
            folder = await entries.GetAsync(entry.SpaceName, parentSubpath, folderShortname, ResourceType.Folder, ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "uniqueness: parent folder load failed for {Space}/{Subpath}", entry.SpaceName, entry.Subpath);
            folder = null;
        }
        if (folder?.Payload?.Body is not JsonElement body
            || body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("unique_fields", out var uniqueFields)
            || uniqueFields.ValueKind != JsonValueKind.Array)
            return Result<bool>.Ok(true);

        foreach (var compound in uniqueFields.EnumerateArray())
        {
            if (compound.ValueKind != JsonValueKind.Array) continue;

            // Build the per-element queries that satisfy the whole compound.
            // Each path in the compound contributes zero-or-more search tokens;
            // a list-valued path expands into N tokens (one per element) so we
            // detect a conflict if ANY element collides — matching the user's
            // intent for `payload.body.ids`.
            //
            // Per-path skip rules (Python parity, adapter.py:3144-3196): a
            // single bad/missing/unchanged path skips just that path, NOT the
            // whole compound. The remaining paths still contribute. A compound
            // where every path skipped is dropped via the
            // `perPathTokens.Count == 0` check below.
            var perPathTokens = new List<List<string>>();
            foreach (var pathEl in compound.EnumerateArray())
            {
                if (pathEl.ValueKind != JsonValueKind.String) continue;
                var path = pathEl.GetString();
                if (string.IsNullOrEmpty(path)) continue;

                if (!TryReadValue(entry, path, out var newValues)) continue;
                if (newValues.Count == 0) continue;

                // Path's value is unchanged on update: an entry whose new
                // compound matches us on this path would have already been
                // caught at create time, so dropping the token narrows the
                // probe without losing collisions on the OTHER paths.
                if (action == ActionType.Update && existing is not null
                    && TryReadValue(existing, path, out var oldValues)
                    && SequenceEquals(newValues, oldValues))
                {
                    continue;
                }

                perPathTokens.Add(BuildSearchTokens(path, newValues));
            }
            if (perPathTokens.Count == 0) continue;

            // For each combination of one token per path (Cartesian over the
            // expanded list values), run the search; ANY hit is a violation.
            foreach (var tokenSet in CartesianProduct(perPathTokens))
            {
                var search = string.Join(' ', tokenSet);
                var q = new Query
                {
                    Type = QueryType.Subpath,
                    SpaceName = entry.SpaceName,
                    Subpath = entry.Subpath,
                    ExactSubpath = true,
                    Search = search,
                    // Limit=2 is enough — any single hit (after self-filter)
                    // is a violation; we don't read totals so RetrieveTotal
                    // would just buy a wasted COUNT(*) round-trip.
                    Limit = 2,
                    // Default FilterSchemaNames is ["meta"] which would
                    // exclude every content entry that has no schema_shortname
                    // — exactly the rows we want to scan for collisions.
                    // Empty list = no schema filter.
                    FilterSchemaNames = new(),
                };

                // Hit the repo directly: ACL must NOT apply to a uniqueness
                // probe (a hidden conflict is still a conflict).
                List<Entry> hits;
                try { hits = await entries.QueryAsync(q, ct); }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "uniqueness probe failed for {Space}{Subpath} search={Search}",
                        entry.SpaceName, entry.Subpath, search);
                    continue;
                }

                if (action == ActionType.Update && existing is not null)
                {
                    hits = hits
                        .Where(e => !(string.Equals(e.Shortname, existing.Shortname, StringComparison.Ordinal)
                                      && string.Equals(e.Subpath, existing.Subpath, StringComparison.Ordinal)))
                        .ToList();
                }
                if (hits.Count == 0) continue;

                return Result<bool>.Fail(
                    InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                    $"Entry properties should be unique: {search}",
                    ErrorTypes.Request);
            }
        }

        return Result<bool>.Ok(true);
    }

    // Locator-normalized split: the DB stores subpath with a leading slash
    // ("/" or "/users/managed"), so the parent must too.
    //   "/users/managed" → ("/users", "managed")
    //   "/people"        → ("/",      "people")
    //   "/" or ""        → ("/",      "")  — no folder, caller skips
    private static (string parentSubpath, string folderShortname) SplitSubpath(string subpath)
    {
        var s = (subpath ?? "").Trim();
        while (s.StartsWith('/')) s = s[1..];
        if (s.Length == 0) return ("/", "");
        var ix = s.LastIndexOf('/');
        if (ix < 0) return ("/", s);
        return ("/" + s[..ix], s[(ix + 1)..]);
    }

    // Reads the value(s) at `path` from `entry`. Always returns a list:
    //   - missing or null              → empty list (caller treats as "skip path")
    //   - scalar (string/number/bool)  → single-element list with the canonical
    //                                    string form
    //   - JSON array of scalars        → one element per array item
    // Other shapes (objects, nested arrays) return empty so we don't
    // generate undefined search syntax.
    private bool TryReadValue(Entry entry, string path, out List<string> values)
    {
        values = new List<string>();

        if (path.StartsWith("payload.body.", StringComparison.Ordinal))
        {
            if (entry.Payload?.Body is not JsonElement bodyEl || bodyEl.ValueKind != JsonValueKind.Object)
                return true; // path applies but no body — empty list
            WalkPath(bodyEl, path["payload.body.".Length..], values, originalPath: path);
            return true;
        }

        // Fall back to a small set of flat entry attributes. Anything else
        // is unsupported (matches what dmart exposes in attributes_dict).
        return TryReadFlatAttribute(entry, path, values);
    }

    // Recursive path walker that supports object navigation (`.seg`) AND
    // array iteration (`seg[]`). When the path is fully consumed, the
    // current node is treated as a scalar or primitive array and emitted.
    //
    // Examples (path → behavior):
    //   "email"                   → node["email"], emit scalar
    //   "ids"                     → node["ids"], emit each element if array
    //   "ids[]"                   → node["ids"], iterate, emit each element
    //   "variants[].sku"          → node["variants"], iterate, emit .sku of each
    //   "outer[].inner[].leaf"    → nested iteration
    //
    // `originalPath` is threaded through purely for diagnostics — when
    // a path resolves to an object array but no `[].<sub>` was specified,
    // we LogDebug to surface the silent-no-op.
    private void WalkPath(JsonElement node, string path, List<string> values, string originalPath)
    {
        if (path.Length == 0)
        {
            EmitScalarOrPrimitiveArray(node, values, originalPath);
            return;
        }

        var bracketIdx = path.IndexOf("[]", StringComparison.Ordinal);
        var dotIdx = path.IndexOf('.');
        var bracketIsNext = bracketIdx >= 0 && (dotIdx < 0 || bracketIdx < dotIdx);

        if (bracketIsNext)
        {
            var segName = path[..bracketIdx];
            var rest = path[(bracketIdx + 2)..];
            if (rest.StartsWith('.')) rest = rest[1..];

            JsonElement arr;
            if (segName.Length == 0)
            {
                arr = node;
            }
            else
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segName, out arr))
                    return;
            }
            if (arr.ValueKind != JsonValueKind.Array) return;

            foreach (var item in arr.EnumerateArray())
                WalkPath(item, rest, values, originalPath);
            return;
        }

        string segNorm, remNorm;
        if (dotIdx < 0) { segNorm = path; remNorm = ""; }
        else { segNorm = path[..dotIdx]; remNorm = path[(dotIdx + 1)..]; }

        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segNorm, out var next)) return;
        WalkPath(next, remNorm, values, originalPath);
    }

    private void EmitScalarOrPrimitiveArray(JsonElement el, List<string> values, string originalPath)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return;
            case JsonValueKind.String:
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) values.Add(s);
                return;
            case JsonValueKind.Number:
                values.Add(el.GetRawText());
                return;
            case JsonValueKind.True:  values.Add("true"); return;
            case JsonValueKind.False: values.Add("false"); return;
            case JsonValueKind.Array:
                // Track per-array contributions so we can warn when the
                // ENTIRE array was object/array-shaped (i.e. user pointed at
                // an object array without specifying `[].<sub>`). A mixed
                // array that produces some scalars stays silent — those
                // scalars are useful uniqueness probes.
                var addedHere = 0;
                var skippedComposite = 0;
                foreach (var item in el.EnumerateArray())
                {
                    switch (item.ValueKind)
                    {
                        case JsonValueKind.String:
                            var ss = item.GetString();
                            if (!string.IsNullOrEmpty(ss)) { values.Add(ss); addedHere++; }
                            break;
                        case JsonValueKind.Number: values.Add(item.GetRawText()); addedHere++; break;
                        case JsonValueKind.True:   values.Add("true"); addedHere++; break;
                        case JsonValueKind.False:  values.Add("false"); addedHere++; break;
                        case JsonValueKind.Object:
                        case JsonValueKind.Array:
                            // Nested objects/arrays — skip; uniqueness on those
                            // shapes is undefined here. Use `[].sub` paths to
                            // pick out fields from object arrays.
                            skippedComposite++;
                            break;
                    }
                }
                if (addedHere == 0 && skippedComposite > 0)
                {
                    log.LogDebug(
                        "uniqueness: path {Path} resolved to an array of {Count} object/array element(s) but no `[].<sub>` syntax was specified — no tokens emitted, this compound key is effectively a no-op for object arrays",
                        originalPath, skippedComposite);
                }
                return;
        }
    }

    private static bool TryReadFlatAttribute(Entry entry, string path, List<string> values)
    {
        switch (path)
        {
            case "shortname":            if (!string.IsNullOrEmpty(entry.Shortname)) values.Add(entry.Shortname); return true;
            case "slug":                 if (!string.IsNullOrEmpty(entry.Slug)) values.Add(entry.Slug); return true;
            case "owner_shortname":      if (!string.IsNullOrEmpty(entry.OwnerShortname)) values.Add(entry.OwnerShortname); return true;
            case "displayname.en":       if (!string.IsNullOrEmpty(entry.Displayname?.En)) values.Add(entry.Displayname!.En!); return true;
            case "displayname.ar":       if (!string.IsNullOrEmpty(entry.Displayname?.Ar)) values.Add(entry.Displayname!.Ar!); return true;
            case "displayname.ku":       if (!string.IsNullOrEmpty(entry.Displayname?.Ku)) values.Add(entry.Displayname!.Ku!); return true;
            case "description.en":       if (!string.IsNullOrEmpty(entry.Description?.En)) values.Add(entry.Description!.En!); return true;
            case "description.ar":       if (!string.IsNullOrEmpty(entry.Description?.Ar)) values.Add(entry.Description!.Ar!); return true;
            case "description.ku":       if (!string.IsNullOrEmpty(entry.Description?.Ku)) values.Add(entry.Description!.Ku!); return true;
            case "tags":                 if (entry.Tags is { Count: > 0 }) values.AddRange(entry.Tags); return true;
            default:                     return false;
        }
    }

    private static List<string> BuildSearchTokens(string path, List<string> values)
    {
        // Field token in the search literal:
        //   - if the user wrote `[]` anywhere in the path, preserve it verbatim
        //     (`payload.body.variants[].sku` → token `@payload.body.variants[].sku:`).
        //   - else if it's a body path that yielded multiple values (i.e. the
        //     value at the path is a primitive array), append `[]` so
        //     QueryHelper translates to the EXISTS-style JSON predicate.
        //   - else use the path as-is.
        var isBody = path.StartsWith("payload.body.", StringComparison.Ordinal);
        var hasBracket = path.Contains("[]", StringComparison.Ordinal);
        string tokenField;
        if (hasBracket) tokenField = path;
        else if (isBody && values.Count > 1) tokenField = path + "[]";
        else tokenField = path;

        return values.Select(v => $"@{tokenField}:{EscapeSearchValue(v)}").ToList();
    }

    private static string EscapeSearchValue(string v)
    {
        // QueryHelper's tokenizer treats spaces as token separators. Wrap
        // anything that contains whitespace or parser metacharacters in
        // double quotes (the parser supports that form). Internal quotes
        // are stripped — uniqueness keys with embedded quotes are rare and
        // we'd rather under-match than emit malformed search strings.
        if (v.Length == 0) return "\"\"";
        var safe = v.Replace("\"", "");
        if (safe.AsSpan().IndexOfAny(SearchTokenizerSpecials) >= 0)
            return "\"" + safe + "\"";
        return safe;
    }

    private static bool SequenceEquals(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    // For each path's list of tokens, yield every combination of one token
    // per path. Used to expand list-valued paths into independent searches.
    private static IEnumerable<List<string>> CartesianProduct(List<List<string>> sets)
    {
        if (sets.Count == 0) yield break;
        var idx = new int[sets.Count];
        while (true)
        {
            var combo = new List<string>(sets.Count);
            for (var i = 0; i < sets.Count; i++) combo.Add(sets[i][idx[i]]);
            yield return combo;

            var k = sets.Count - 1;
            while (k >= 0)
            {
                if (++idx[k] < sets[k].Count) break;
                idx[k--] = 0;
            }
            if (k < 0) yield break;
        }
    }
}
