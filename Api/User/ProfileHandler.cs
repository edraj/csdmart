using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.User;

public static class ProfileHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/profile", async (HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name;
            if (actor is null) return Response.Fail("unauthorized", "login required");
            var user = await svc.GetByShortnameAsync(actor, ct);
            return user is null
                ? Response.Fail("not_found", "user missing")
                : Response.Ok(attributes: new()
                {
                    ["shortname"] = user.Shortname,
                    ["email"] = user.Email ?? (object)"",
                    ["msisdn"] = user.Msisdn ?? (object)"",
                    ["language"] = user.Language,
                    ["roles"] = user.Roles,
                    ["is_email_verified"] = user.IsEmailVerified,
                    ["is_msisdn_verified"] = user.IsMsisdnVerified,
                });
        });

        g.MapPost("/profile", async (HttpRequest req, HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name;
            if (actor is null) return Response.Fail("unauthorized", "login required");
            var patch = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            if (patch is null) return Response.Fail("bad_request", "missing body");
            var result = await svc.UpdateProfileAsync(actor, patch, ct);
            return result.IsOk
                ? Response.Ok(attributes: new() { ["shortname"] = result.Value!.Shortname })
                : Response.Fail(result.ErrorCode!, result.ErrorMessage!);
        });

        g.MapPost("/delete", async (HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name;
            if (actor is null) return Response.Fail("unauthorized", "login required");
            await svc.DeleteAsync(actor, ct);
            return Response.Ok();
        });

        // POST /user/reset — admin clears another user's failed-login attempt counter.
        // Body: { "shortname": "<target user>" }. Mirrors dmart's "reset" endpoint.
        g.MapPost("/reset", async (HttpRequest req, HttpContext http, UserRepository users, CancellationToken ct) =>
        {
            var actor = http.User.Identity?.Name;
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", "auth");

            Dictionary<string, object>? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            }
            catch (JsonException ex)
            {
                return Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, "request");
            }
            var target = body?.TryGetValue("shortname", out var sn) == true ? sn?.ToString() : null;
            if (string.IsNullOrEmpty(target))
                return Response.Fail(InternalErrorCode.MISSING_DATA, "shortname required", "request");

            var existing = await users.GetByShortnameAsync(target, ct);
            if (existing is null)
                return Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "user not found", "request");

            await users.ResetAttemptsAsync(target, ct);
            return Response.Ok(attributes: new() { ["shortname"] = target });
        });
    }
}
