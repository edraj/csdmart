using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Services;

namespace Dmart.Api.User;

public static class ProfileHandler
{
    // The one attachment shortname /user/profile carries. Python pins the same
    // literal (api/user/router.py:589, filter_shortnames=["avatar"]); it is a
    // convention rather than a schema constraint, so it lives here as a name
    // instead of being spelled inline at the filter.
    private const string AvatarShortname = "avatar";

    public static void Map(RouteGroupBuilder g)
    {
        // GET /user/profile — Python returns records: [Record] with user
        // attributes. The tsdmart SDK reads data.records[0].attributes.
        g.MapGet("/profile", async (HttpContext http, UserService svc,
            DataAdapters.Sql.AccessRepository access,
            DataAdapters.Sql.AttachmentRepository attachmentRepo,
            CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);
            var user = await svc.GetByShortnameAsync(actor, ct);
            if (user is null)
                return Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "user missing", ErrorTypes.Db);

            // Python: attributes["permissions"] = await db.get_user_permissions(shortname)
            // Resolves user → roles → permissions into a dict keyed by
            // "space:subpath:resource_type" with allowed_actions, conditions, etc.
            var permissions = await access.GenerateUserPermissionsAsync(actor, ct);

            // Python parity (dmart/api/user/router.py:563-587): optional fields
            // are added only when truthy. StripNulls drops null + "" — matches
            // Python's `if user.X:` guard and response_model_exclude_none.
            var attrs = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["email"] = user.Email,
                ["msisdn"] = user.Msisdn,
                ["displayname"] = user.Displayname,
                ["description"] = user.Description,
                ["language"] = user.Language,
                ["type"] = user.Type.ToString().ToLowerInvariant(),
                ["roles"] = user.Roles,
                ["groups"] = user.Groups,
                ["is_email_verified"] = user.IsEmailVerified,
                ["is_msisdn_verified"] = user.IsMsisdnVerified,
                ["force_password_change"] = user.ForcePasswordChange,
                ["payload"] = user.Payload,
                ["permissions"] = permissions,
            };
            // The caller's avatar, grouped by resource_type — the same shape
            // /managed/entry returns, so a client that already renders
            // `record.attachments` needs no second call.
            //
            // AVATAR ONLY, matching Python (api/user/router.py:589 fetches
            // `filter_shortnames=["avatar"]`). Returning every attachment
            // instead looked like a harmless superset and is not:
            //
            //   * `ListForParentAsync` orders `created_at DESC`, so the
            //     conventional `attachments.media[0]` stops being the avatar
            //     the moment the user has a NEWER media attachment — an
            //     uploaded document, an ID scan. The profile then renders that
            //     file as the user's picture.
            //   * `AttachmentMapper.ToEntryRecord` emits each attachment's
            //     `payload` AND `body`, and this endpoint has no
            //     `retrieve_attachments` flag to opt out of. Every profile
            //     read would ship the body of every json/comment attachment on
            //     the row, unbounded, on the hottest authenticated route.
            //
            // NOT read-gated per attachment, unlike Api/Managed/EntryHandler.
            // That gate is right there, where the parent may belong to someone
            // else. Here the row IS the caller's, and this handler already
            // returns their email, msisdn, payload and full permission map
            // ungated — refusing them their own avatar while handing over
            // their contact details does not hold together. It would also
            // refuse in the ordinary case: PermissionService.CanAsync returns
            // false outright when the actor holds no role permissions
            // (PermissionService.cs — `if (perms.Count == 0) return false;`),
            // and AdminBootstrap provisions the implicit `logged_in` role with
            // an empty permission list, so a self-registered user would never
            // see their own avatar.
            var children = await attachmentRepo.ListForParentAsync(
                user.SpaceName, user.Subpath, user.Shortname, ct);
            var avatars = children
                .Where(a => string.Equals(a.Shortname, AvatarShortname, StringComparison.Ordinal))
                .ToList();
            // Left null when empty so DefaultIgnoreCondition.WhenWritingNull
            // keeps the key off the wire entirely, rather than emitting `{}`
            // for the strip middleware to clean up afterwards.
            var attachments = avatars.Count == 0
                ? null
                : avatars
                    .GroupBy(a => DataAdapters.Sql.JsonbHelpers.EnumMember(a.ResourceType))
                    .ToDictionary(
                        grp => grp.Key,
                        grp => grp.Select(a => AttachmentMapper.ToEntryRecord(a)).ToList());

            var profileRecord = new Record
            {
                ResourceType = Dmart.Models.Enums.ResourceType.User,
                Shortname = user.Shortname,
                Subpath = "/users",
                Attributes = AttrHelper.StripNulls(attrs),
                Attachments = attachments,
            };
            return Response.Ok(new[] { profileRecord });
        });

        g.MapPost("/profile", async (HttpRequest req, HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);

            // Python parity: set_user_profile(profile: core.Record, ...) — the
            // POST body is a Record envelope where every field the handler
            // cares about (password, old_password, email, displayname, ...)
            // lives inside record.attributes. Parse the body once as a raw
            // JSON document, then promote record.attributes to `patch` when
            // the envelope shape is present; otherwise treat the whole doc
            // as the patch (keeps legacy flat-body callers working).
            Dictionary<string, object>? patch;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return Response.Fail(InternalErrorCode.INVALID_DATA, "body must be a JSON object", ErrorTypes.Request);

                if (root.TryGetProperty("attributes", out var attrsEl)
                    && attrsEl.ValueKind == JsonValueKind.Object)
                {
                    patch = JsonSerializer.Deserialize(
                        attrsEl.GetRawText(), DmartJsonContext.Default.DictionaryStringObject);
                }
                else
                {
                    patch = JsonSerializer.Deserialize(
                        root.GetRawText(), DmartJsonContext.Default.DictionaryStringObject);
                }
            }
            catch (JsonException ex)
            {
                return Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, ErrorTypes.Request);
            }

            if (patch is null)
                return Response.Fail(InternalErrorCode.INVALID_DATA, "missing body", ErrorTypes.Request);

            // Python threads `auth_token` into set_user_profile so a
            // firebase_token update lands on the caller's session row only.
            // Pull from Authorization bearer first, fall back to cookie.
            var sessionToken = TryExtractSessionToken(http);
            var result = await svc.UpdateProfileAsync(actor, patch, sessionToken, ct);
            return result.IsOk
                ? Response.Ok(attributes: new() { ["shortname"] = result.Value!.Shortname })
                : Response.Fail(result.ErrorCode, result.ErrorMessage!, result.ErrorType ?? "request");
        })
        .Accepts<ProfileUpdateBody>("application/json")
        .Produces<Response>();

        g.MapPost("/delete", async (HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);
            // Self-delete: the target and the action-maker are the same account.
            // force: true — self-delete has no flag to pass one, and refusing
            // to let someone delete their own account because they created
            // records would make the endpoint useless to exactly the users who
            // have one.
            var result = await svc.DeleteUserAsync(actor, actor, dryRun: false, force: true, ct);
            return result.IsOk
                ? Response.Ok()
                : Response.Fail(result.ErrorCode, result.ErrorMessage!, result.ErrorType ?? ErrorTypes.Request);
        });

        // POST /user/validate_password — Python verifies against stored hash,
        // requires authentication. Returns {valid: bool}.
        g.MapPost("/validate_password", async (HttpRequest req, HttpContext http, UserService svc, CancellationToken ct) =>
        {
            var actor = http.Actor();
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED, "login required", ErrorTypes.Auth);

            Dictionary<string, object>? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(req.Body, DmartJsonContext.Default.DictionaryStringObject, ct);
            }
            catch (JsonException ex)
            {
                return Response.Fail(InternalErrorCode.INVALID_DATA, ex.Message, ErrorTypes.Request);
            }
            var password = body?.TryGetValue("password", out var pw) == true ? pw?.ToString() : null;
            if (string.IsNullOrEmpty(password))
                return Response.Fail(InternalErrorCode.MISSING_DATA, "password required", ErrorTypes.Request);

            var valid = await svc.ValidatePasswordAsync(actor, password, ct);
            if (!valid)
            {
                // A wrong guess here must cost the same as a wrong guess at
                // /user/login: this endpoint runs the identical Argon2id
                // verify against the identical hash, so without the counter it
                // is an unmetered password oracle for anyone holding a stolen
                // session token (e.g. checking a candidate before using it to
                // step up through POST /user/profile's old_password gate).
                var user = await svc.GetByShortnameAsync(actor, ct);
                if (user is not null)
                    await svc.RecordFailedAttemptAsync(user, ct);
            }
            return Response.Ok(attributes: new() { ["valid"] = valid });
        })
        .Accepts<ValidatePasswordBody>("application/json")
        .Produces<Response>()
        // Rate-limited like the other credential-handling routes: each call is
        // a full Argon2id verify (m=102400 → 100 MiB per in-flight request),
        // so an unthrottled caller is a memory-exhaustion lever as well.
        .RequireRateLimiting("auth-by-ip");

        // GET /user/check-existing — Python parity: short-circuit on first
        // conflict. Iteration order matches Python dict: shortname → msisdn →
        // email. Returns {"unique": true} when all free, else
        // {"unique": false, "field": "<name>"}.
        g.MapGet("/check-existing", async (
            string? shortname, string? email, string? msisdn,
            UserRepository users, CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(shortname)
                && await users.GetByShortnameAsync(shortname, ct) is not null)
            {
                return Response.Ok(attributes: new()
                {
                    ["unique"] = false,
                    ["field"] = "shortname",
                });
            }
            if (!string.IsNullOrEmpty(msisdn)
                && await users.GetByMsisdnAsync(msisdn, ct) is not null)
            {
                return Response.Ok(attributes: new()
                {
                    ["unique"] = false,
                    ["field"] = "msisdn",
                });
            }
            if (!string.IsNullOrEmpty(email)
                && await users.GetByEmailAsync(email, ct) is not null)
            {
                return Response.Ok(attributes: new()
                {
                    ["unique"] = false,
                    ["field"] = "email",
                });
            }

            return Response.Ok(attributes: new() { ["unique"] = true });
        })
        // Anonymous by design (signup forms probe it before a session
        // exists), which also makes it a clean yes/no oracle over the whole
        // user table — shortname, msisdn AND email, one query-string away.
        // The rate limit is the only thing standing between it and a bulk
        // enumeration of the directory, so it gets the same "auth-by-ip"
        // bucket the credential routes use.
        .RequireRateLimiting("auth-by-ip");
    }

    // Extract the caller's access token so UserService can update the exact
    // session row they're authenticated under (Python parity — `auth_token`
    // threaded through set_user_profile). Authorization header wins; fall
    // back to the auth_token cookie issued by /user/login. Returns null when
    // neither source is present (e.g. during anonymous access).
    private static string? TryExtractSessionToken(HttpContext http)
    {
        var auth = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(auth))
        {
            const string bearer = "Bearer ";
            if (auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
                return auth.Substring(bearer.Length).Trim();
        }
        var cookie = http.Request.Cookies["auth_token"];
        return string.IsNullOrEmpty(cookie) ? null : cookie;
    }
}
