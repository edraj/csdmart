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
                return (object?)Response.Fail(resolved.ErrorCode, resolved.ErrorMessage!,
                    resolved.ErrorType ?? ErrorTypes.Request, resolved.Info);
            return await Dmart.Api.Managed.ExecuteTaskHandler.ExecuteAndWriteQueryAsync(
                http.Response, queryService, resolved.Value!, "anonymous", settings.Value.JqTimeout, ct);
        })
            .Accepts<ExecuteTaskBody>("application/json")
            .Produces<Response>();
}
