using System.Collections.Concurrent;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Json.Schema;

namespace Dmart.Services;

// One failing schema constraint, kept structured so consumers can name the
// offending field without re-parsing the display message. Location is the raw
// RFC 6901 pointer in the validated document ("" = root). Key is the same
// path decoded and dot-joined — matching CSV export's FlattenInto column
// convention — and empty for root-level errors (e.g. `required`), which have
// no single field to name. Value is the element actually at that location in
// the document the validator checked (null for root errors or when the
// pointer names a missing property).
public sealed record SchemaError(string Location, string Key, string Keyword, string Message, JsonElement? Value)
{
    // Display form; ValidateAsync/FormatMessage rely on this to keep the wire
    // message byte-identical to the historical "<pointer>: <keyword>: <msg>".
    public override string ToString() => $"{Location}: {Keyword}: {Message}";
}

// Mirrors dmart Python's payload-vs-schema validation. dmart stores JSON Schema
// documents as `schema`-typed entries in each space (typically under /schema or
// /schemas subpath). Other entries reference them via payload.schema_shortname,
// and the payload.body is validated against that schema before being stored.
//
// Compiled schemas are cached in-process keyed by (space, shortname). Cache is
// invalidated lazily — call ClearCache after upserting a schema entry.
public sealed class SchemaValidator(EntryRepository entries, ILogger<SchemaValidator> log)
{
    private static readonly EvaluationOptions Options = new()
    {
        OutputFormat = OutputFormat.List,
    };

    private readonly ConcurrentDictionary<(string Space, string Shortname), JsonSchema?> _cache = new();

    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// Validates a JSON payload body against a schema named in the same space.
    /// Returns null on success; the list of structured errors on failure.
    /// Returns null when the schema can't be found (treats missing schemas as
    /// non-fatal — matches dmart's lenient behavior on first-write of a schema-
    /// less payload).
    /// </summary>
    public async Task<List<SchemaError>?> ValidateDetailedAsync(
        string spaceName, string schemaShortname, JsonElement body, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(schemaShortname)) return null;
        // folder_rendering is CENTRALIZED: the canonical (strict,
        // additionalProperties:false) definition lives in the management space
        // only, so every space's folder bodies validate against the same
        // document — a per-space copy can't weaken or fork it.
        if (schemaShortname == "folder_rendering") spaceName = "management";
        var schema = await GetCompiledAsync(spaceName, schemaShortname, ct);
        if (schema is null) return null;   // schema not found — pass through

        var result = schema.Evaluate(body, Options);
        if (result.IsValid) return null;

        var errors = new List<SchemaError>();
        Collect(result, body, errors);
        return errors.Count > 0 ? errors : null;
    }

    /// <summary>String-message form of ValidateDetailedAsync (same contract).</summary>
    public async Task<List<string>?> ValidateAsync(string spaceName, string schemaShortname, JsonElement body, CancellationToken ct = default)
    {
        var errors = await ValidateDetailedAsync(spaceName, schemaShortname, body, ct);
        return errors?.Select(e => e.ToString()).ToList();
    }

    /// <summary>
    /// Structured payload-level gate: returns null when there is nothing to
    /// validate (no payload / no schema_shortname / no body / the entry IS a
    /// schema) or the body is valid; the structured error list on failure.
    /// </summary>
    public async Task<List<SchemaError>?> ValidatePayloadDetailedAsync(
        string spaceName, ResourceType resourceType, Payload? payload, CancellationToken ct = default)
    {
        if (payload is null) return null;
        if (string.IsNullOrEmpty(payload.SchemaShortname)) return null;
        if (payload.Body is null) return null;
        if (resourceType == ResourceType.Schema) return null;  // schemas are themselves JSON Schemas

        return await ValidateDetailedAsync(spaceName, payload.SchemaShortname, payload.Body.Value, ct);
    }

    /// <summary>
    /// Payload-level gate for the non-entry "principal" paths
    /// (User/Role/Group/Permission/Space) that don't run through EntryService.
    /// Mirrors EntryService.ValidatePayloadAsync: returns null when there is
    /// nothing to validate or the body is valid; an error message on failure.
    /// </summary>
    public async Task<string?> ValidatePayloadAsync(
        string spaceName, ResourceType resourceType, Payload? payload, CancellationToken ct = default)
    {
        var errors = await ValidatePayloadDetailedAsync(spaceName, resourceType, payload, ct);
        return errors is null ? null : FormatMessage(errors);
    }

    /// <summary>The historical wire message for a failed validation.</summary>
    public static string FormatMessage(List<SchemaError> errors) =>
        "payload failed schema validation: " + string.Join("; ", errors);

    // Wire-shaped detail entries (one dict per failing constraint) for
    // Result<T>.Info / api Error.Info — mirrors Python's api.Error.info being
    // list[dict]. `key`/`value` are omitted (not empty) when the error has no
    // nameable field, so consumers can select field-level errors by presence.
    public static List<Dictionary<string, object>> ToInfo(List<SchemaError> errors)
    {
        var info = new List<Dictionary<string, object>>(errors.Count);
        foreach (var e in errors)
        {
            var d = new Dictionary<string, object>
            {
                ["pointer"] = e.Location,
                ["keyword"] = e.Keyword,
                ["message"] = e.Message,
            };
            if (e.Key.Length > 0) d["key"] = e.Key;
            if (e.Value is { } v) d["value"] = v;
            info.Add(d);
        }
        return info;
    }

    private static void Collect(EvaluationResults r, JsonElement body, List<SchemaError> errors)
    {
        if (r.Errors is not null)
        {
            var ptr = r.InstanceLocation;
            var key = "";
            JsonElement? value = null;
            if (ptr.SegmentCount > 0)
            {
                var segments = new string[ptr.SegmentCount];
                for (var i = 0; i < ptr.SegmentCount; i++) segments[i] = ptr[i].ToString();
                key = string.Join(".", segments);
                // Resolve against the SAME document the schema was evaluated on,
                // so the reported value is the one that failed — for entry
                // updates that's the merged body, not the caller's patch.
                // Clone: the errors outlive any JsonDocument the body came from.
                value = ptr.Evaluate(body) is { } el ? el.Clone() : null;
            }
            foreach (var (keyword, msg) in r.Errors)
                errors.Add(new SchemaError(ptr.ToString(), key, keyword, msg, value));
        }
        if (r.Details is { Count: > 0 })
            foreach (var d in r.Details) Collect(d, body, errors);
    }

    private async Task<JsonSchema?> GetCompiledAsync(string spaceName, string shortname, CancellationToken ct)
    {
        var key = (spaceName, shortname);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        // dmart stores schemas as entries with resource_type='schema'. The actual
        // JSON Schema document lives in payload.body (jsonb).
        // We try a few canonical subpaths since dmart projects vary; the first hit wins.
        Entry? schemaEntry = null;
        foreach (var sub in new[] { "/schema", "/schemas", "/" })
        {
            schemaEntry = await entries.GetAsync(spaceName, sub, shortname, ResourceType.Schema, ct);
            if (schemaEntry is not null) break;
        }

        if (schemaEntry?.Payload?.Body is null)
        {
            // Don't cache misses — callers supply schema_shortname intentionally,
            // and caching null would pin the "not found" result until process
            // restart even after the schema is created externally (e.g. via
            // dmart Python or direct SQL). The cost is a repeat DB lookup per
            // payload that references a missing schema, which should be rare.
            log.LogDebug("schema {Space}/{Shortname} not found", spaceName, shortname);
            return null;
        }

        try
        {
            var json = JsonSerializer.Serialize(schemaEntry.Payload.Body!.Value, DmartJsonContext.Default.JsonElement);
            var schema = JsonSchema.FromText(json);
            _cache[key] = schema;
            return schema;
        }
        catch (Exception ex)
        {
            // Compile failure is (probably) a programming error in the schema itself;
            // still don't cache it — the author may fix and re-upsert.
            log.LogWarning(ex, "failed to compile schema {Space}/{Shortname}", spaceName, shortname);
            return null;
        }
    }
}
