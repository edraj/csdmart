using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Managed;

// Mirrors POST /managed/apply-alteration/{space}/{alteration_name}.
//
// dmart's "alteration" resource is a saved-instruction record. Its payload.body has
// the shape:
//   {
//     "target_query": { ...Query... },          // entries to modify
//     "patch":        { ...attributes patch... } // applied to each match
//   }
//
// Apply walks the query results, calls EntryService.UpdateAsync on each, and reports
// per-entry success/failure.
public static class AlterationHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/apply-alteration/{space}/{alteration_name}",
            async Task<Response> (string space, string alteration_name,
                                  EntryService entryService, QueryService queries,
                                  HttpContext http, CancellationToken ct) =>
            {
                var actor = http.Actor();
                // Read-gated load through EntryService (which also re-gates on the
                // row's REAL resource_type): an alteration the caller cannot read
                // must not be executable by them either.
                var alteration = await entryService.GetAsync(
                    new Locator(ResourceType.Alteration, space, "/alterations", alteration_name), actor, ct);
                if (alteration is null)
                {
                    // dmart projects sometimes store alterations as Content under /alterations.
                    alteration = await entryService.GetAsync(
                        new Locator(ResourceType.Content, space, "/alterations", alteration_name), actor, ct);
                }
                if (alteration?.Payload?.Body is null)
                    return Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST,
                        $"alteration '{alteration_name}' not found", ErrorTypes.Request);

                var bodyJson = JsonSerializer.Serialize(alteration.Payload.Body!.Value, DmartJsonContext.Default.JsonElement);
                using var doc = JsonDocument.Parse(bodyJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("target_query", out var qEl) || qEl.ValueKind != JsonValueKind.Object)
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "alteration body missing target_query", ErrorTypes.Request);
                if (!root.TryGetProperty("patch", out var patchEl) || patchEl.ValueKind != JsonValueKind.Object)
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "alteration body missing patch", ErrorTypes.Request);

                Query? query;
                try
                {
                    query = JsonSerializer.Deserialize(qEl.GetRawText(), DmartJsonContext.Default.Query);
                }
                catch (JsonException)
                {
                    return Response.Fail(InternalErrorCode.INVALID_DATA, "invalid request body", ErrorTypes.Request);
                }
                if (query is null)
                    return Response.Fail(InternalErrorCode.MISSING_DATA, "target_query empty", ErrorTypes.Request);

                var patchDict = JsonElementToDict(patchEl);

                // The saved target_query is operator data, but it executes on behalf
                // of THIS caller: pin it to the alteration's own space and run it
                // through QueryService so the caller's row ACL and the MaxQueryLimit
                // clamp apply. The repository's actor-less overload is
                // server-unrestricted and must never see a caller-chosen query —
                // it let anyone with `create` on any /alterations enumerate (and
                // probe payload values of) every entry in every space.
                if (string.IsNullOrEmpty(query.SpaceName))
                    query = query with { SpaceName = space };
                else if (!string.Equals(query.SpaceName, space, StringComparison.Ordinal))
                    return Response.Fail(InternalErrorCode.NOT_ALLOWED,
                        "alteration target_query must target the alteration's own space", ErrorTypes.Request);

                var queried = await queries.ExecuteAsync(query, actor, ct);
                if (queried.Status != Status.Success) return queried;
                var matches = queried.Records ?? new List<Record>();
                var updated = 0;
                var failed = new List<Dictionary<string, object>>();

                // Every row here passed the caller's own read ACL, so naming the
                // ones that then fail to update discloses nothing they could not
                // already see.
                foreach (var entry in matches)
                {
                    var locator = new Locator(entry.ResourceType, space, entry.Subpath, entry.Shortname);
                    var result = await entryService.UpdateAsync(locator, patchDict, actor, ct);
                    if (result.IsOk) updated++;
                    else failed.Add(new()
                    {
                        ["shortname"] = entry.Shortname,
                        ["subpath"] = entry.Subpath,
                        ["error"] = result.ErrorMessage ?? "unknown",
                        ["code"] = result.ErrorCode,
                    });
                }

                return Response.Ok(attributes: new()
                {
                    ["alteration"] = alteration_name,
                    ["matched"] = matches.Count,
                    ["updated"] = updated,
                    ["failed_count"] = failed.Count,
                    ["failed"] = failed,
                });
            });

    // Convert a JsonElement object into a Dictionary<string, object> for the patch.
    // JSON null is represented as a JsonElement of ValueKind.Null so the dict
    // stays type-safe (no `null!` escape hatch) and ApplyPatch can detect the
    // intent to unset a field.
    private static Dictionary<string, object> JsonElementToDict(JsonElement el)
    {
        var dict = new Dictionary<string, object>();
        foreach (var prop in el.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? (object)"",
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? l : (object)prop.Value.GetDouble(),
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                // Pass JSON null through as the JsonElement itself — callers
                // already handle JsonElement with ValueKind.Null via FlattenAttrs.
                JsonValueKind.Null   => prop.Value.Clone(),
                _                    => prop.Value.Clone(),  // arrays/objects → JsonElement
            };
        }
        return dict;
    }
}
