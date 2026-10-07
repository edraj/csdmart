using Dmart.Config;
using Dmart.Models.Api;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.Public;

// Python: POST /public/excute/{task_type}/{space} — executes a saved query task
// (same as managed but unauthenticated, limited to query type only).
public static class ExecuteTaskHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/excute/{task_type}/{space_name}", async (
            string task_type, string space_name,
            HttpRequest req, EntryService entries, QueryService queryService,
            HttpContext http, IOptions<DmartSettings> settings,
            CancellationToken ct) =>
        {
            // Mirror /public/query: resolve the saved query, then execute +
            // render it applying the query's own top-level jq_filter.
            var resolved = await Dmart.Api.Managed.ExecuteTaskHandler.ResolveFromBodyAsync(
                task_type, space_name, req, entries, "anonymous", ct);
            if (!resolved.IsOk)
                // Collapse every resolution failure to one uniform response on
                // the anonymous path (V-19). The managed route keeps the
                // specific code/message for operators, but here distinct errors
                // — task absent (404) vs present-but-not-a-Query (400 invalid
                // Query) vs unknown task type — were an existence/shape oracle
                // and leaked framework type names. One generic answer removes it.
                return (object?)Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST,
                    "task not found", ErrorTypes.Request);
            return await Dmart.Api.Managed.ExecuteTaskHandler.ExecuteAndWriteQueryAsync(
                http.Response, queryService, resolved.Value!, "anonymous", settings.Value.JqTimeout, ct);
        })
            .Accepts<ExecuteTaskBody>("application/json")
            .Produces<Response>();
}
