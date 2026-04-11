using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.User;

public static class RegistrationHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/check-existing", async (string? shortname, string? email, string? msisdn,
                                            UserRepository users, CancellationToken ct) =>
        {
            var exists = await users.ExistsAsync(shortname, email, msisdn, ct);
            return Response.Ok(attributes: new() { ["exists"] = exists });
        });

        g.MapPost("/create", async (HttpRequest req, UserService svc, CancellationToken ct) =>
        {
            var body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            if (body is null) return Response.Fail("bad_request", "missing body");
            var shortname = body.TryGetValue("shortname", out var sn) ? sn?.ToString() ?? "" : "";
            var email = body.TryGetValue("email", out var e) ? e?.ToString() : null;
            var msisdn = body.TryGetValue("msisdn", out var m) ? m?.ToString() : null;
            var password = body.TryGetValue("password", out var p) ? p?.ToString() : null;
            var language = body.TryGetValue("language", out var l) ? l?.ToString() : null;
            var result = await svc.CreateAsync(shortname, email, msisdn, password, language, ct);
            return result.IsOk
                ? Response.Ok(attributes: new() { ["uuid"] = result.Value!.Uuid, ["shortname"] = result.Value.Shortname })
                : Response.Fail(result.ErrorCode!, result.ErrorMessage!);
        });

        g.MapPost("/validate_password", async (HttpRequest req) =>
        {
            var body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject);
            var password = body?.TryGetValue("password", out var p) == true ? p?.ToString() ?? "" : "";
            var valid = password.Length >= 8;
            return Response.Ok(attributes: new() { ["valid"] = valid, ["min_length"] = 8 });
        });
    }
}
