using System.Text.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;

namespace Dmart.Api.Managed;

// /managed/request — dmart's unified CRUD endpoint. The body is a Request envelope
// containing a list of Records. We dispatch each record to the right repository
// based on its resource_type:
//
//   * User                                  → UserRepository (users table)
//   * Role                                  → AccessRepository (roles table)
//   * Permission                            → AccessRepository (permissions table)
//   * Space                                 → SpaceRepository (spaces table)
//   * Comment/Reply/Reaction/Media/Json/    → AttachmentRepository (attachments table)
//     Share/Lock/DataAsset/Relationship/
//     Alteration
//   * everything else (Content/Folder/      → EntryRepository via EntryService
//     Schema/Ticket/...)
public static class RequestHandler
{
    public static void Map(RouteGroupBuilder g) =>
        g.MapPost("/request",
            async Task<Response> (Request req, EntryService entries, UserRepository users,
                                  AccessRepository access, SpaceRepository spaces,
                                  AttachmentRepository attachments, PasswordHasher hasher,
                                  HttpContext http, CancellationToken ct) =>
            {
                var actor = http.User.Identity?.Name ?? "anonymous";
                var responses = new List<Record>();

                foreach (var rec in req.Records)
                {
                    var result = req.RequestType switch
                    {
                        RequestType.Create =>
                            await DispatchCreateAsync(rec, req.SpaceName, actor,
                                entries, users, access, spaces, attachments, hasher, ct),
                        RequestType.Update or RequestType.Patch =>
                            await DispatchUpdateAsync(rec, req.SpaceName, actor,
                                entries, users, access, spaces, attachments, hasher, ct),
                        RequestType.Delete =>
                            await DispatchDeleteAsync(rec, req.SpaceName, actor,
                                entries, users, access, spaces, attachments, ct),
                        RequestType.Move =>
                            await DispatchMoveAsync(rec, req.SpaceName, actor, entries, ct),
                        _ =>
                            ((Response Response, Record UpdatedRecord))(Response.Fail(InternalErrorCode.NOT_SUPPORTED_TYPE,
                                $"{req.RequestType} not supported", "request"),
                             rec),
                    };

                    if (result.Response.Status != Status.Success) return result.Response;
                    responses.Add(result.UpdatedRecord);
                }
                return Response.Ok(responses);
            });

    // ============================================================================
    // CREATE
    // ============================================================================

    private static async Task<(Response Response, Record UpdatedRecord)> DispatchCreateAsync(
        Record rec, string space, string actor,
        EntryService entries, UserRepository users, AccessRepository access,
        SpaceRepository spaces, AttachmentRepository attachments, PasswordHasher hasher,
        CancellationToken ct)
    {
        switch (rec.ResourceType)
        {
            case ResourceType.User:
                return await CreateUserAsync(rec, actor, users, hasher, ct);
            case ResourceType.Role:
                return await CreateRoleAsync(rec, space, actor, access, ct);
            case ResourceType.Permission:
                return await CreatePermissionAsync(rec, space, actor, access, ct);
            case ResourceType.Space:
                return await CreateSpaceAsync(rec, actor, spaces, ct);
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
                return await CreateAttachmentAsync(rec, space, actor, attachments, ct);
            default:
                return await CreateEntryAsync(rec, space, actor, entries, ct);
        }
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreateEntryAsync(
        Record rec, string space, string actor, EntryService entries, CancellationToken ct)
    {
        var entry = MaterializeEntry(rec, space, actor);
        var result = await entries.CreateAsync(entry, actor, ct);
        return result.IsOk
            ? (Response.Ok(), rec with { Uuid = result.Value!.Uuid })
            : (Response.Fail(result.ErrorCode!, result.ErrorMessage!, "request"), rec);
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreateUserAsync(
        Record rec, string actor, UserRepository users, PasswordHasher hasher, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var existing = await users.GetByShortnameAsync(rec.Shortname, ct);
        if (existing is not null)
            return (Response.Fail(InternalErrorCode.SHORTNAME_ALREADY_EXIST,
                $"user {rec.Shortname} already exists", "request"), rec);

        var rolesList = ExtractStringList(attrs, "roles");
        var groupsList = ExtractStringList(attrs, "groups");
        var languageStr = attrs.TryGetValue("language", out var lObj) ? ConvertToString(lObj) : null;
        var typeStr = attrs.TryGetValue("type", out var tObj) ? ConvertToString(tObj) : null;
        var passwordRaw = attrs.TryGetValue("password", out var pObj) ? ConvertToString(pObj) : null;

        var user = new Dmart.Models.Core.User
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = "management",
            Subpath = "/" + rec.Subpath.TrimStart('/'),
            OwnerShortname = actor,
            Email = attrs.TryGetValue("email", out var e) ? ConvertToString(e) : null,
            Msisdn = attrs.TryGetValue("msisdn", out var m) ? ConvertToString(m) : null,
            Password = string.IsNullOrEmpty(passwordRaw) ? null : hasher.Hash(passwordRaw),
            Roles = rolesList ?? new(),
            Groups = groupsList ?? new(),
            Type = ParseUserType(typeStr),
            Language = ParseLanguage(languageStr),
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            IsEmailVerified = attrs.TryGetValue("is_email_verified", out var iev) && IsTruthy(iev),
            IsMsisdnVerified = attrs.TryGetValue("is_msisdn_verified", out var imv) && IsTruthy(imv),
            ForcePasswordChange = attrs.TryGetValue("force_password_change", out var fpc) && IsTruthy(fpc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user, ct);
        return (Response.Ok(), rec with { Uuid = user.Uuid });
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreateRoleAsync(
        Record rec, string space, string actor, AccessRepository access, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var role = new Role
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = space,
            Subpath = "/" + rec.Subpath.TrimStart('/'),
            OwnerShortname = actor,
            Permissions = ExtractStringList(attrs, "permissions") ?? new(),
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await access.UpsertRoleAsync(role, ct);
        return (Response.Ok(), rec with { Uuid = role.Uuid });
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreatePermissionAsync(
        Record rec, string space, string actor, AccessRepository access, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var perm = new Permission
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = space,
            Subpath = "/" + rec.Subpath.TrimStart('/'),
            OwnerShortname = actor,
            ResourceTypes = ExtractStringList(attrs, "resource_types") ?? new(),
            Actions = ExtractStringList(attrs, "actions") ?? new(),
            Conditions = ExtractStringList(attrs, "conditions") ?? new(),
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await access.UpsertPermissionAsync(perm, ct);
        return (Response.Ok(), rec with { Uuid = perm.Uuid });
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreateSpaceAsync(
        Record rec, string actor, SpaceRepository spaces, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var existing = await spaces.GetAsync(rec.Shortname, ct);
        if (existing is not null)
            return (Response.Fail(InternalErrorCode.ALREADY_EXIST_SPACE_NAME,
                $"space {rec.Shortname} already exists", "request"), rec);

        var space = new Space
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = rec.Shortname,    // self-referential — every space row's space_name = shortname
            Subpath = "/",
            OwnerShortname = actor,
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            HideSpace = attrs.TryGetValue("hide_space", out var hs) ? IsTruthy(hs) : null,
            IndexingEnabled = attrs.TryGetValue("indexing_enabled", out var ie) && IsTruthy(ie),
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await spaces.UpsertAsync(space, ct);
        return (Response.Ok(), rec with { Uuid = space.Uuid });
    }

    private static async Task<(Response Response, Record UpdatedRecord)> CreateAttachmentAsync(
        Record rec, string space, string actor, AttachmentRepository attachments, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        var attachment = new Attachment
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = space,
            Subpath = "/" + rec.Subpath.TrimStart('/'),
            ResourceType = rec.ResourceType,
            OwnerShortname = actor,
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            Body = attrs.TryGetValue("body", out var b) ? ConvertToString(b) : null,
            State = attrs.TryGetValue("state", out var s) ? ConvertToString(s) : null,
            CreatedAt = DateTime.UtcNow,
        };
        await attachments.UpsertAsync(attachment, ct);
        return (Response.Ok(), rec with { Uuid = attachment.Uuid });
    }

    // ============================================================================
    // UPDATE
    // ============================================================================

    private static async Task<(Response Response, Record UpdatedRecord)> DispatchUpdateAsync(
        Record rec, string space, string actor,
        EntryService entries, UserRepository users, AccessRepository access,
        SpaceRepository spaces, AttachmentRepository attachments, PasswordHasher hasher,
        CancellationToken ct)
    {
        var locator = new Locator(rec.ResourceType, space, rec.Subpath, rec.Shortname);

        switch (rec.ResourceType)
        {
            case ResourceType.User:
            {
                var existing = await users.GetByShortnameAsync(rec.Shortname, ct);
                if (existing is null)
                    return (Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "user not found", "request"), rec);
                var attrs = rec.Attributes ?? new();
                var passwordRaw = attrs.TryGetValue("password", out var p) ? ConvertToString(p) : null;
                var updated = existing with
                {
                    Email = attrs.TryGetValue("email", out var e) ? ConvertToString(e) : existing.Email,
                    Msisdn = attrs.TryGetValue("msisdn", out var m) ? ConvertToString(m) : existing.Msisdn,
                    Password = string.IsNullOrEmpty(passwordRaw) ? existing.Password : hasher.Hash(passwordRaw),
                    Roles = ExtractStringList(attrs, "roles") ?? existing.Roles,
                    Groups = ExtractStringList(attrs, "groups") ?? existing.Groups,
                    IsActive = attrs.TryGetValue("is_active", out var ia) ? !IsExplicitlyFalse(ia) : existing.IsActive,
                    IsEmailVerified = attrs.TryGetValue("is_email_verified", out var iev) ? IsTruthy(iev) : existing.IsEmailVerified,
                    IsMsisdnVerified = attrs.TryGetValue("is_msisdn_verified", out var imv) ? IsTruthy(imv) : existing.IsMsisdnVerified,
                    UpdatedAt = DateTime.UtcNow,
                };
                await users.UpsertAsync(updated, ct);
                return (Response.Ok(), rec with { Uuid = updated.Uuid });
            }
            case ResourceType.Space:
            {
                var existing = await spaces.GetAsync(rec.Shortname, ct);
                if (existing is null)
                    return (Response.Fail(InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "space not found", "request"), rec);
                var attrs = rec.Attributes ?? new();
                var updated = existing with
                {
                    HideSpace = attrs.TryGetValue("hide_space", out var hs) ? IsTruthy(hs) : existing.HideSpace,
                    IsActive = attrs.TryGetValue("is_active", out var ia) ? !IsExplicitlyFalse(ia) : existing.IsActive,
                    UpdatedAt = DateTime.UtcNow,
                };
                await spaces.UpsertAsync(updated, ct);
                return (Response.Ok(), rec with { Uuid = updated.Uuid });
            }
            default:
            {
                var result = await entries.UpdateAsync(locator, rec.Attributes ?? new(), actor, ct);
                return result.IsOk
                    ? (Response.Ok(), rec with { Uuid = result.Value!.Uuid })
                    : (Response.Fail(result.ErrorCode!, result.ErrorMessage!, "request"), rec);
            }
        }
    }

    // ============================================================================
    // DELETE
    // ============================================================================

    private static async Task<(Response Response, Record UpdatedRecord)> DispatchDeleteAsync(
        Record rec, string space, string actor,
        EntryService entries, UserRepository users, AccessRepository access,
        SpaceRepository spaces, AttachmentRepository attachments, CancellationToken ct)
    {
        var locator = new Locator(rec.ResourceType, space, rec.Subpath, rec.Shortname);

        switch (rec.ResourceType)
        {
            case ResourceType.User:
                await users.DeleteAsync(rec.Shortname, ct);
                return (Response.Ok(), rec);
            case ResourceType.Space:
                await spaces.DeleteAsync(rec.Shortname, ct);
                return (Response.Ok(), rec);
            case ResourceType.Comment:
            case ResourceType.Reply:
            case ResourceType.Reaction:
            case ResourceType.Media:
            case ResourceType.Json:
            case ResourceType.Share:
            case ResourceType.Relationship:
            case ResourceType.Alteration:
            case ResourceType.DataAsset:
            {
                // Look up by (space, subpath, shortname) and delete by uuid
                var existing = await attachments.GetAsync(space, "/" + rec.Subpath.TrimStart('/'), rec.Shortname, ct);
                if (existing is null) return (Response.Ok(), rec); // already gone
                if (Guid.TryParse(existing.Uuid, out var u))
                    await attachments.DeleteAsync(u, ct);
                return (Response.Ok(), rec);
            }
            default:
            {
                var result = await entries.DeleteAsync(locator, actor, ct);
                return result.IsOk
                    ? (Response.Ok(), rec)
                    : (Response.Fail(result.ErrorCode!, result.ErrorMessage!, "request"), rec);
            }
        }
    }

    // ============================================================================
    // MOVE
    // ============================================================================

    private static async Task<(Response Response, Record UpdatedRecord)> DispatchMoveAsync(
        Record rec, string space, string actor, EntryService entries, CancellationToken ct)
    {
        var attrs = rec.Attributes ?? new();
        if (attrs is null
            || !TryGetString(attrs, "dest_subpath", out var destSubpath)
            || !TryGetString(attrs, "dest_shortname", out var destShortname))
            return (Response.Fail(InternalErrorCode.MISSING_DESTINATION_OR_SHORTNAME,
                "move requires dest_subpath and dest_shortname", "request"), rec);

        var locator = new Locator(rec.ResourceType, space, rec.Subpath, rec.Shortname);
        var to = new Locator(rec.ResourceType, space, destSubpath!, destShortname!);
        var result = await entries.MoveAsync(locator, to, actor, ct);
        return result.IsOk
            ? (Response.Ok(), rec with { Subpath = to.Subpath, Shortname = to.Shortname })
            : (Response.Fail(result.ErrorCode!, result.ErrorMessage!, "request"), rec);
    }

    // ============================================================================
    // helpers
    // ============================================================================

    private static Entry MaterializeEntry(Record rec, string spaceName, string actor)
    {
        var attrs = rec.Attributes ?? new();
        // dmart stores schema_shortname inside the Payload object, not as an Entry column.
        Payload? payload = null;
        if (attrs.TryGetValue("payload", out var pRaw) && pRaw is JsonElement p && p.ValueKind == JsonValueKind.Object)
        {
            payload = new Payload
            {
                ContentType = ParseContentType(p.TryGetProperty("content_type", out var ct) ? ct.GetString() : null),
                SchemaShortname = p.TryGetProperty("schema_shortname", out var ss) ? ss.GetString() : null,
                Body = p.TryGetProperty("body", out var b) ? b.Clone() : null,
            };
        }
        else if (attrs.TryGetValue("schema_shortname", out var sc))
        {
            payload = new Payload { SchemaShortname = ConvertToString(sc) };
        }

        return new Entry
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = spaceName,
            Subpath = "/" + rec.Subpath.TrimStart('/'),
            ResourceType = rec.ResourceType,
            OwnerShortname = actor,
            Slug = attrs.TryGetValue("slug", out var s) ? ConvertToString(s) : null,
            Displayname = attrs.TryGetValue("displayname", out var dn) ? ParseTranslation(dn) : null,
            Description = attrs.TryGetValue("description", out var de) ? ParseTranslation(de) : null,
            Tags = ExtractStringList(attrs, "tags") ?? new(),
            IsActive = !attrs.TryGetValue("is_active", out var ia) || !IsExplicitlyFalse(ia),
            State = attrs.TryGetValue("state", out var st) ? ConvertToString(st) : null,
            WorkflowShortname = attrs.TryGetValue("workflow_shortname", out var wf) ? ConvertToString(wf) : null,
            Payload = payload,
        };
    }

    private static ContentType ParseContentType(string? value)
        => value is null ? ContentType.Json
            : Enum.TryParse<ContentType>(value, true, out var ct) ? ct
            : value switch
            {
                "json" => ContentType.Json,
                "text" => ContentType.Text,
                "markdown" => ContentType.Markdown,
                "html" => ContentType.Html,
                _ => ContentType.Json,
            };

    private static UserType ParseUserType(string? code) => code?.ToLowerInvariant() switch
    {
        "mobile" => UserType.Mobile,
        "bot"    => UserType.Bot,
        _        => UserType.Web,
    };

    private static Language ParseLanguage(string? code) => code?.ToLowerInvariant() switch
    {
        "ar" or "arabic"  => Language.Ar,
        "ku" or "kurdish" => Language.Ku,
        "fr" or "french"  => Language.Fr,
        "tr" or "turkish" => Language.Tr,
        _                 => Language.En,
    };

    private static Translation? ParseTranslation(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                return new Translation(
                    En: el.TryGetProperty("en", out var en) ? en.GetString() : null,
                    Ar: el.TryGetProperty("ar", out var ar) ? ar.GetString() : null,
                    Ku: el.TryGetProperty("ku", out var ku) ? ku.GetString() : null);
            }
            if (el.ValueKind == JsonValueKind.String)
                return new Translation(En: el.GetString());
        }
        return new Translation(En: value.ToString());
    }

    private static List<string>? ExtractStringList(Dictionary<string, object> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var raw)) return null;
        if (raw is null) return null;
        if (raw is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in el.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            return list;
        }
        if (raw is List<string> stringList) return stringList;
        if (raw is IEnumerable<object> objs)
            return objs.Select(o => o?.ToString() ?? "").ToList();
        return null;
    }

    private static string? ConvertToString(object? v) => v switch
    {
        null => null,
        string s => s,
        JsonElement el => el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null   => null,
            _                    => el.GetRawText(),
        },
        _ => v.ToString(),
    };

    private static bool IsTruthy(object? v) => v switch
    {
        true => true,
        bool b => b,
        JsonElement el => el.ValueKind == JsonValueKind.True,
        _ => false,
    };

    private static bool IsExplicitlyFalse(object? v) => v switch
    {
        false => true,
        bool b => !b,
        JsonElement el => el.ValueKind == JsonValueKind.False,
        _ => false,
    };

    private static bool TryGetString(Dictionary<string, object> attrs, string key, out string? value)
    {
        value = null;
        if (!attrs.TryGetValue(key, out var raw)) return false;
        value = ConvertToString(raw);
        return value is not null;
    }
}
