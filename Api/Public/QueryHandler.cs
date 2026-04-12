using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.Public;

public static class QueryHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // Read body as raw stream and deserialize ourselves so we can surface the
        // real JSON error (Minimal APIs swallow body-binding errors as 400 no-body).
        g.MapPost("/query", async Task<Response> (HttpRequest req, QueryService svc, CancellationToken ct) =>
        {
            Query? q;
            try
            {
                q = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.Query, ct);
            }
            catch (JsonException ex)
            {
                return Response.Fail("bad_request", $"invalid Query JSON: {ex.Message}");
            }
            if (q is null) return Response.Fail("bad_request", "empty body");
            return await svc.ExecuteAsync(q, actor: null, ct);
        });

        // Python: GET /public/query-via-url — query via URL parameters (for embedding).
        // Also: GET /public/query/{type}/{space_name}/{subpath}
        g.MapGet("/query/{type}/{space_name}/{subpath}", async (
            string type, string space_name, string subpath,
            int? limit, int? offset, string? search,
            QueryService svc, CancellationToken ct) =>
        {
            if (!Enum.TryParse<Dmart.Models.Enums.QueryType>(type, ignoreCase: true, out var qt))
                return Response.Fail("bad_request", $"unknown query type: {type}");
            var q = new Query
            {
                Type = qt,
                SpaceName = space_name,
                Subpath = subpath,
                Limit = limit ?? 10,
                Offset = offset ?? 0,
                Search = search,
            };
            return await svc.ExecuteAsync(q, actor: null, ct);
        });
    }
}
