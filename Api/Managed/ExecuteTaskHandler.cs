using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Managed;

public static class ExecuteTaskHandler
{
    private static readonly Regex UnresolvedSearchParam = new(
        @"@\w*\:({|\()?\$\w*(}|\))?", RegexOptions.Compiled);

    public static void Map(RouteGroupBuilder g)
    {
        // POST /managed/execute/{task_type}/{space_name}
        MapExecute(g, "/execute/{task_type}/{space_name}");
        // Python's route is misspelled and clients rely on that spelling.
        MapExecute(g, "/excute/{task_type}/{space_name}");
    }

    private static void MapExecute(RouteGroupBuilder g, string pattern)
    {
        // dmart's only defined task_type is "query": resolve a saved query entry
        // by shortname (from the body or URL) into a Query, then execute + render
        // it the same way POST /managed/query does — top-level jq_filter included.
        g.MapPost(pattern,
            async (string task_type, string space_name, HttpRequest req,
                   EntryService entries, QueryService queries, HttpContext http,
                   IOptions<DmartSettings> settings, CancellationToken ct) =>
            {
                var resolved = await ResolveFromBodyAsync(task_type, space_name, req, entries, http.Actor(), ct);
                if (!resolved.IsOk)
                    return (object?)Response.Fail(resolved.ErrorCode, resolved.ErrorMessage!,
                        resolved.ErrorType ?? ErrorTypes.Request, resolved.Info);
                return await ExecuteAndWriteQueryAsync(
                    http.Response, queries, resolved.Value!, http.Actor(), settings.Value.JqTimeout, ct);
            })
            // Body may be either the back-compat `{shortname, subpath?,
            // query_overrides?}` shape (documented here) or a full Record
            // envelope with `resource_type`. Both work at runtime.
            .Accepts<Dmart.Models.Api.ExecuteTaskBody>("application/json")
            .Produces<Response>();

        // Apply-alteration is wired in AlterationHandler.cs.
    }

    // Resolve the saved query addressed by the request body into an executable
    // Query. Body is either a Python-style core.Record (has `resource_type`) or
    // the back-compat `{shortname, subpath?, query_overrides?}` shape. The
    // resolved Query owns its top-level jq_filter; execution + rendering is the
    // caller's concern (see ExecuteAndWriteQueryAsync).
    public static async Task<Result<Query>> ResolveFromBodyAsync(
        string taskType, string spaceName, HttpRequest req,
        EntryService entries, string? actor, CancellationToken ct)
    {
        if (!string.Equals(taskType, "query", StringComparison.OrdinalIgnoreCase))
            return Result<Query>.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                $"unknown task type '{taskType}'", ErrorTypes.Request);

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            return Result<Query>.Fail(InternalErrorCode.INVALID_DATA,
                $"invalid request body: {ex.Message}", ErrorTypes.Request);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Result<Query>.Fail(InternalErrorCode.INVALID_DATA,
                    "request body must be an object", ErrorTypes.Request);

            // Python-compatible body: core.Record.
            if (doc.RootElement.TryGetProperty("resource_type", out _))
            {
                var bodyRecord = doc.RootElement.Deserialize(DmartJsonContext.Default.Record);
                if (bodyRecord is null)
                    return Result<Query>.Fail(InternalErrorCode.INVALID_DATA,
                        "record body is empty", ErrorTypes.Request);
                return await ResolveSavedQueryAsync(spaceName, bodyRecord, actor, entries, ct);
            }

            // Back-compat body used by the original C# handler:
            // {"shortname":"...", "subpath":"/tasks", "query_overrides":{...}}
            var shortname = doc.RootElement.TryGetProperty("shortname", out var sn) && sn.ValueKind == JsonValueKind.String
                ? sn.GetString()
                : null;
            if (string.IsNullOrEmpty(shortname))
                return Result<Query>.Fail(InternalErrorCode.MISSING_DATA,
                    "task shortname required in body", ErrorTypes.Request);

            var subpath = doc.RootElement.TryGetProperty("subpath", out var sp) && sp.ValueKind == JsonValueKind.String
                ? sp.GetString() ?? "/tasks"
                : "/tasks";
            var attrs = new Dictionary<string, object>();
            if (doc.RootElement.TryGetProperty("query_overrides", out var overrides) &&
                overrides.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in overrides.EnumerateObject())
                    attrs[p.Name] = p.Value.Clone();
            }

            var legacyRecord = new Record
            {
                ResourceType = ResourceType.Content,
                Shortname = shortname,
                Subpath = subpath,
                Attributes = attrs,
            };
            return await ResolveSavedQueryAsync(spaceName, legacyRecord, actor, entries, ct);
        }
    }

    // Load the saved query/report/api entry and transform its payload into an
    // executable Query (schema-aware unwrap + parameter substitution). Returns
    // the Query on success, or a Fail carrying the wire error triple.
    public static async Task<Result<Query>> ResolveSavedQueryAsync(
        string spaceName, Record record, string? actor,
        EntryService entries, CancellationToken ct)
    {
        var locator = new Locator(ResourceType.Content, spaceName, "/" + record.Subpath.TrimStart('/'), record.Shortname);
        var taskEntry = await entries.GetAsync(locator, actor, ct);
        if (taskEntry?.Payload?.Body is null)
            return Result<Query>.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST,
                $"task '{record.Shortname}' not found at {spaceName}{locator.Subpath}", ErrorTypes.Request);

        var queryJson = BuildQueryJson(taskEntry, record);
        if (queryJson is null)
            return Result<Query>.Fail(InternalErrorCode.INVALID_DATA,
                "task body is not a valid Query", ErrorTypes.Request);

        Query? query;
        try
        {
            query = JsonSerializer.Deserialize(queryJson, DmartJsonContext.Default.Query);
        }
        catch (JsonException ex)
        {
            return Result<Query>.Fail(InternalErrorCode.INVALID_DATA,
                $"task body is not a valid Query: {ex.Message}", ErrorTypes.Request);
        }
        if (query is null)
            return Result<Query>.Fail(InternalErrorCode.INVALID_DATA, "task body is empty", ErrorTypes.Request);

        return Result<Query>.Ok(query);
    }

    // Execute a resolved Query and stream its Response, reshaping records[]
    // through the query's own top-level jq_filter — the same render path as
    // POST /managed/query (Api/Managed/QueryHandler.cs). Execution failures keep
    // their proper HTTP status via the group's FailedResponseFilter (returned as
    // a raw Response); only successful result sets flow through jq.
    public static async Task<object?> ExecuteAndWriteQueryAsync(
        HttpResponse http, QueryService queries, Query query, string? actor, int jqTimeout, CancellationToken ct)
    {
        var response = await queries.ExecuteAsync(query, actor, ct);
        if (response.Status != Status.Success)
            return response;
        await JqEnvelope.WriteAsync(http, response, query.JqFilter, jqTimeout, ct);
        return Results.Empty;
    }

    private static string? BuildQueryJson(Entry taskEntry, Record record)
    {
        if (taskEntry.Payload?.Body is null) return null;
        var body = taskEntry.Payload.Body.Value;
        if (body.ValueKind == JsonValueKind.String) return null;

        JsonNode? rootNode = JsonNode.Parse(body.GetRawText());
        JsonObject? queryNode = null;
        if (rootNode is JsonObject obj)
        {
            var schema = taskEntry.Payload.SchemaShortname;
            // "report" wraps the Query under `query`; "api" wraps it under
            // `request_body` (alongside `end_point`/`verb` metadata that
            // describes which dmart endpoint the saved request targets).
            //
            // Schema mismatches (e.g. schema="api" but `request_body` missing
            // or not an object) fall through to the default branch — the root
            // is treated as the Query. Downstream deserialization will then
            // fail with INVALID_DATA, which surfaces a `JsonException` message
            // to the operator. This is intentional: keeping the unwrap lenient
            // means a malformed entry produces the same caller-visible error
            // shape as any other bad task body, rather than a new bespoke
            // failure mode for the schema-dispatch path.
            if (string.Equals(schema, "report", StringComparison.OrdinalIgnoreCase) &&
                obj["query"] is JsonObject reportQuery)
            {
                queryNode = (JsonObject)reportQuery.DeepClone();
            }
            else if (string.Equals(schema, "api", StringComparison.OrdinalIgnoreCase) &&
                     obj["request_body"] is JsonObject apiRequestBody)
            {
                queryNode = (JsonObject)apiRequestBody.DeepClone();
            }
            else
            {
                queryNode = (JsonObject)obj.DeepClone();
            }
        }
        if (queryNode is null) return null;

        if (queryNode.TryGetPropertyValue("query_subpath", out var querySubpath) &&
            !queryNode.ContainsKey("subpath"))
        {
            queryNode["subpath"] = querySubpath?.DeepClone();
            queryNode.Remove("query_subpath");
        }

        if (record.Attributes is not null)
        {
            if (queryNode["search"] is JsonValue searchValue &&
                searchValue.TryGetValue<string>(out var search))
            {
                foreach (var (key, value) in record.Attributes)
                {
                    if (key is "offset" or "limit" or "from_date" or "to_date") continue;
                    search = search.Replace($"${key}", AttributeToString(value), StringComparison.Ordinal);
                }
                queryNode["search"] = UnresolvedSearchParam.Replace(search, "").Trim();
            }

            CopyOverride(record.Attributes, queryNode, "offset");
            CopyOverride(record.Attributes, queryNode, "limit");
            CopyOverride(record.Attributes, queryNode, "from_date");
            CopyOverride(record.Attributes, queryNode, "to_date");
        }

        return queryNode.ToJsonString(DmartJsonContext.Default.Options);
    }

    private static void CopyOverride(Dictionary<string, object> attrs, JsonObject node, string key)
    {
        if (!attrs.TryGetValue(key, out var value) || value is null) return;
        node[key] = value switch
        {
            JsonElement el => JsonNode.Parse(el.GetRawText()),
            int i => i,
            long l => l,
            double d => d,
            bool b => b,
            string s => s,
            _ => value.ToString(),
        };
    }

    private static string AttributeToString(object? value) => value switch
    {
        null => "",
        JsonElement { ValueKind: JsonValueKind.String } el => el.GetString() ?? "",
        JsonElement el => el.GetRawText().Trim('"'),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

}
