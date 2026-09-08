using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Regression guard for the resource-type-confusion authorization bypass.
//
// EntryRepository.GetAsync deliberately retries WITHOUT the resource_type
// filter when the typed lookup misses — the entries uniqueness key is
// (shortname, space_name, subpath), so resource_type is redundant for identity.
// That fallback is fine on its own, but EntryService gated the write on the
// CLIENT-DECLARED locator.Type rather than the loaded row's real type. An actor
// holding update on resource_types:["content"] could therefore declare
// resource_type "content", be handed a "schema" row, pass the permission walk,
// and overwrite it — the upsert preserves the row's true type, so the write
// really landed on the schema (and flushed the schema cache with it).
//
// The rule, applied at every gate below: authorize against the resource_type of
// the row actually in hand, never the one the caller declared. EntryService does
// that for Get/Update/Delete/Move; the attachment paths in RequestHandler,
// PayloadHandler and McpTools do it for their untyped lookups; and the two
// attachment create paths refuse an address already occupied by a different type
// rather than upserting over it (the upsert rewrites resource_type too, so
// overwrite-in-place is idempotency only while the types match).
public sealed class ResourceTypeConfusionAuthzTests : IClassFixture<DmartFactory>
{
    private const string Password = "Test1234";
    private readonly DmartFactory _factory;

    public ResourceTypeConfusionAuthzTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Content_Only_Grant_Cannot_Update_A_Schema_Row_By_Declaring_Content()
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var editor = Unique("rtc_editor");
        var role = Unique("rtc_role");
        var perm = Unique("rtc_perm");
        var space = Unique("rtc_space");
        var subpath = "/schema";
        var schemaShortname = Unique("rtc_schema");
        var now = DateTime.UtcNow;

        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = space,
            SpaceName = "management",
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });

        // The grant is deliberately generous on ACTIONS but scoped to the
        // "content" resource type only — the exact shape a "content editor"
        // role would carry in production.
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = perm,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [space] = new() { PermissionService.AllSubpathsMw } },
            ResourceTypes = new() { "content" },
            Actions = new() { "view", "query", "create", "update", "delete" },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = role,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { perm },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await CreateUserAsync(users, hasher, editor, new() { role });

        // The victim row: a SCHEMA the content editor must not be able to touch.
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = schemaShortname,
            SpaceName = space,
            Subpath = subpath,
            ResourceType = ResourceType.Schema,
            OwnerShortname = "dmart",
            IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object> { ["type"] = "object" },
                    DmartJsonContext.Default.DictionaryStringObject),
            },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.InvalidateAllCachesAsync();

        try
        {
            var client = await LoginAsAsync(editor);

            // Declare "content" while targeting the schema row. The typed
            // lookup misses, the untyped fallback finds the schema.
            var body = new Request
            {
                RequestType = RequestType.Update,
                SpaceName = space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Content,
                        Subpath = subpath,
                        Shortname = schemaShortname,
                        Attributes = new() { ["displayname"] = new Dictionary<string, object> { ["en"] = "pwned" } },
                    },
                },
            };
            var resp = await client.PostAsJsonAsync("/managed/request", body, DmartJsonContext.Default.Request);
            var raw = await resp.Content.ReadAsStringAsync();

            resp.StatusCode.ShouldNotBe(HttpStatusCode.OK,
                $"content-only grant must not update a schema row via resource-type confusion. Body: {raw}");

            // And the row must be untouched — still a schema, still owned by dmart.
            var after = await entries.GetAsync(space, subpath, schemaShortname);
            after.ShouldNotBeNull();
            after!.ResourceType.ShouldBe(ResourceType.Schema);
            after.Displayname?.En.ShouldNotBe("pwned");
        }
        finally
        {
            try { await users.DeleteAllSessionsAsync(editor); } catch { }
            try { await users.DeleteAsync(editor); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await entries.DeleteAsync(space, subpath, schemaShortname, ResourceType.Schema); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // The delete leg of the same bypass, and the destructive one.
    //
    // Declaring `folder` against a NON-folder row used to pass the gate on a
    // folder-scoped grant, and the folder branch then ran the subtree cascade.
    // The entries row itself survives — DeleteFolderTreeWithDependentsOnceAsync
    // guards it with `AND resource_type = 'folder'` — but the histories, locks
    // and attachments predicates beside it match on PATH alone, so the victim's
    // audit trail and every attachment it owned were deleted anyway and the call
    // reported success. No `force` needed: the non-empty guard counts entries
    // under a path that a content row has none of.
    [FactIfPg]
    public async Task Folder_Only_Grant_Cannot_Cascade_Delete_A_Content_Row_By_Declaring_Folder()
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        var histories = _factory.Services.GetRequiredService<HistoryRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var actor = Unique("rtc_folderer");
        var role = Unique("rtc_frole");
        var perm = Unique("rtc_fperm");
        var space = Unique("rtc_fspace");
        const string subpath = "/docs";
        var victim = Unique("rtc_victim");
        var now = DateTime.UtcNow;

        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = space,
            SpaceName = "management",
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });

        // Scoped to "folder" only — the shape a "space librarian" role carries.
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = perm,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [space] = new() { PermissionService.AllSubpathsMw } },
            ResourceTypes = new() { "folder" },
            Actions = new() { "view", "query", "create", "update", "delete" },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = role,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { perm },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await CreateUserAsync(users, hasher, actor, new() { role });

        // The victim: a CONTENT entry the folder grant must not reach, carrying
        // the two things the cascade would have destroyed without touching the
        // entries row — an attachment and a history row.
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = victim,
            SpaceName = space,
            Subpath = subpath,
            ResourceType = ResourceType.Content,
            OwnerShortname = "dmart",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await attachments.UpsertAsync(new Attachment
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "att1",
            SpaceName = space,
            // Attachments hang at "{parent subpath}/{parent shortname}".
            Subpath = $"{subpath}/{victim}",
            ResourceType = ResourceType.Comment,
            OwnerShortname = "dmart",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await histories.AppendAsync(space, subpath, victim, "dmart", null,
            new Dictionary<string, object> { ["state"] = "seeded" });
        await access.InvalidateAllCachesAsync();

        try
        {
            var client = await LoginAsAsync(actor);

            var body = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = space,
                Records = new()
                {
                    new Record
                    {
                        // The lie: a content row named as a folder.
                        ResourceType = ResourceType.Folder,
                        Subpath = subpath,
                        Shortname = victim,
                        Attributes = new(),
                    },
                },
            };
            var resp = await client.PostAsJsonAsync("/managed/request", body, DmartJsonContext.Default.Request);
            var raw = await resp.Content.ReadAsStringAsync();

            resp.StatusCode.ShouldNotBe(HttpStatusCode.OK,
                $"folder-only grant must not delete a content row via resource-type confusion. Body: {raw}");

            // The entries row was never the exposed part — these two were.
            (await attachments.ListForParentAsync(space, subpath, victim))
                .Count.ShouldBe(1, "the victim's attachments must survive a refused delete");
            (await histories.ListAsync(space, subpath, victim))
                .Count.ShouldBe(1, "the victim's history must survive a refused delete");
            (await entries.GetAsync(space, subpath, victim)).ShouldNotBeNull();
        }
        finally
        {
            try { await users.DeleteAllSessionsAsync(actor); } catch { }
            try { await users.DeleteAsync(actor); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await attachments.DeleteUnderSubpathAsync(space, $"{subpath}/{victim}"); } catch { }
            try { await entries.DeleteAsync(space, subpath, victim, ResourceType.Content); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // =========================================================================
    // The READ leg of the same bypass.
    //
    // UpdateAsync/DeleteAsync/MoveAsync were taught to re-derive the locator
    // from the loaded row; EntryService.GetAsync was not. The untyped fallback
    // that powers the write bypass therefore also handed the row's CONTENT to
    // a caller whose grant does not cover its type — the route's
    // {resource_type} segment is caller-controlled, so naming "content" was
    // enough to read a schema, a ticket, or any other row at that address.
    // =========================================================================
    [FactIfPg]
    public async Task Content_Only_Grant_Cannot_Read_A_Schema_Row_By_Declaring_Content()
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var reader = Unique("rtc_reader");
        var role = Unique("rtc_rrole");
        var perm = Unique("rtc_rperm");
        var space = Unique("rtc_rspace");
        const string subpath = "/schema";
        var schemaShortname = Unique("rtc_rschema");
        const string Marker = "rtc_marker_do_not_leak";
        var now = DateTime.UtcNow;

        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = space,
            SpaceName = "management",
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });

        // Read-only content grant — no reach into `schema` by any route.
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = perm,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [space] = new() { PermissionService.AllSubpathsMw } },
            ResourceTypes = new() { "content" },
            Actions = new() { "view", "query" },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = role,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { perm },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await CreateUserAsync(users, hasher, reader, new() { role });

        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = schemaShortname,
            SpaceName = space,
            Subpath = subpath,
            ResourceType = ResourceType.Schema,
            OwnerShortname = "dmart",
            IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object> { ["title"] = Marker },
                    DmartJsonContext.Default.DictionaryStringObject),
            },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.InvalidateAllCachesAsync();

        try
        {
            var client = await LoginAsAsync(reader);

            // Control: naming the row's REAL type is correctly refused, which is
            // what makes the next call a bypass rather than a legitimate read.
            var honest = await client.GetAsync(
                $"/managed/entry/schema/{space}/schema/{schemaShortname}?retrieve_json_payload=true");
            honest.StatusCode.ShouldNotBe(HttpStatusCode.OK,
                "a content-only grant must not read a schema row that names itself honestly");

            var resp = await client.GetAsync(
                $"/managed/entry/content/{space}/schema/{schemaShortname}?retrieve_json_payload=true");
            var raw = await resp.Content.ReadAsStringAsync();

            resp.StatusCode.ShouldNotBe(HttpStatusCode.OK,
                $"content-only grant must not read a schema row by declaring content. Body: {raw}");
            raw.Contains(Marker, StringComparison.Ordinal).ShouldBeFalse(
                "the schema's payload must not reach a caller with no schema grant");
        }
        finally
        {
            try { await users.DeleteAllSessionsAsync(reader); } catch { }
            try { await users.DeleteAsync(reader); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await entries.DeleteAsync(space, subpath, schemaShortname, ResourceType.Schema); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // The MCP surface reaches the same gate with the same caller-supplied type.
    // dmart_download takes `resource_type` straight from the tool arguments, so
    // it is the entry-read bypass again with a JSON-RPC envelope around it.
    [FactIfPg]
    public async Task Content_Only_Grant_Cannot_Download_A_Schema_Payload_Over_Mcp()
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var reader = Unique("rtc_mcpr");
        var role = Unique("rtc_mrole");
        var perm = Unique("rtc_mperm");
        var space = Unique("rtc_mspace");
        const string subpath = "/schema";
        var schemaShortname = Unique("rtc_mschema");
        const string Marker = "rtc_mcp_marker_do_not_leak";
        var now = DateTime.UtcNow;

        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = space,
            SpaceName = "management",
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = perm,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [space] = new() { PermissionService.AllSubpathsMw } },
            ResourceTypes = new() { "content" },
            Actions = new() { "view", "query" },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = role,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { perm },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await CreateUserAsync(users, hasher, reader, new() { role });
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = schemaShortname,
            SpaceName = space,
            Subpath = subpath,
            ResourceType = ResourceType.Schema,
            OwnerShortname = "dmart",
            IsActive = true,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonSerializer.SerializeToElement(
                    new Dictionary<string, object> { ["title"] = Marker },
                    DmartJsonContext.Default.DictionaryStringObject),
            },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.InvalidateAllCachesAsync();

        try
        {
            var client = await LoginAsAsync(reader);
            var body = $$"""
                {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
                  "name":"dmart_download",
                  "arguments":{
                    "space_name":"{{space}}",
                    "subpath":"{{subpath}}",
                    "shortname":"{{schemaShortname}}",
                    "resource_type":"content"
                  }
                 }
                }
                """;
            var resp = await client.PostAsync("/mcp",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();

            raw.Contains(Marker, StringComparison.Ordinal).ShouldBeFalse(
                $"dmart_download must not serve a schema payload to a content-only grant. Body: {raw}");
        }
        finally
        {
            try { await users.DeleteAllSessionsAsync(reader); } catch { }
            try { await users.DeleteAsync(reader); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await entries.DeleteAsync(space, subpath, schemaShortname, ResourceType.Schema); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // =========================================================================
    // The attachment legs.
    //
    // Worse than the entries legs on two counts: AttachmentRepository.GetAsync
    // has no typed overload at all (the lookup is ALWAYS untyped, so no probe
    // has to miss first), and the delete goes by uuid with no type guard beside
    // it. Every attachment path below gated on rec.ResourceType / the route's
    // {resource_type} while holding a row of a different type.
    // =========================================================================

    [FactIfPg]
    public async Task Comment_Only_Grant_Cannot_Read_Media_Bytes_By_Declaring_Comment()
    {
        var f = await SetupAttachmentFixtureAsync();
        try
        {
            var client = await LoginAsAsync(f.Actor);
            var path = $"{f.ParentSubpath.TrimStart('/')}/{f.Parent}/{f.Att}.png";

            // Control: the honest request is refused.
            var honest = await client.GetAsync($"/managed/payload/media/{f.Space}/{path}");
            honest.StatusCode.ShouldNotBe(HttpStatusCode.OK,
                "a comment-only grant must not read media bytes that name themselves honestly");

            var resp = await client.GetAsync($"/managed/payload/comment/{f.Space}/{path}");
            var raw = await resp.Content.ReadAsStringAsync();

            resp.StatusCode.ShouldNotBe(HttpStatusCode.OK,
                $"comment-only grant must not read media bytes by declaring comment. Body: {raw}");
            raw.Contains(MediaMarker, StringComparison.Ordinal).ShouldBeFalse(
                "the media bytes must not reach the caller");
        }
        finally { await CleanupAttachmentFixtureAsync(f); }
    }

    [FactIfPg]
    public async Task Comment_Only_Grant_Cannot_Update_A_Media_Attachment_By_Declaring_Comment()
    {
        var f = await SetupAttachmentFixtureAsync();
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        try
        {
            var client = await LoginAsAsync(f.Actor);
            var body = new Request
            {
                RequestType = RequestType.Update,
                SpaceName = f.Space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Comment,
                        Subpath = f.AttSubpath,
                        Shortname = f.Att,
                        Attributes = new() { ["displayname"] = new Dictionary<string, object> { ["en"] = "pwned" } },
                    },
                },
            };
            var resp = await client.PostAsJsonAsync("/managed/request", body, DmartJsonContext.Default.Request);
            var raw = await resp.Content.ReadAsStringAsync();

            resp.StatusCode.ShouldNotBe(HttpStatusCode.OK,
                $"comment-only grant must not update a media attachment. Body: {raw}");

            var after = await attachments.GetAsync(f.Space, f.AttSubpath, f.Att);
            after.ShouldNotBeNull();
            after!.ResourceType.ShouldBe(ResourceType.Media);
            after.Displayname?.En.ShouldNotBe("pwned");
        }
        finally { await CleanupAttachmentFixtureAsync(f); }
    }

    [FactIfPg]
    public async Task Comment_Only_Grant_Cannot_Delete_A_Media_Attachment_By_Declaring_Comment()
    {
        var f = await SetupAttachmentFixtureAsync();
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        try
        {
            var client = await LoginAsAsync(f.Actor);
            var body = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = f.Space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Comment,
                        Subpath = f.AttSubpath,
                        Shortname = f.Att,
                        Attributes = new(),
                    },
                },
            };
            var resp = await client.PostAsJsonAsync("/managed/request", body, DmartJsonContext.Default.Request);
            var raw = await resp.Content.ReadAsStringAsync();

            resp.StatusCode.ShouldNotBe(HttpStatusCode.OK,
                $"comment-only grant must not delete a media attachment. Body: {raw}");

            (await attachments.GetAsync(f.Space, f.AttSubpath, f.Att))
                .ShouldNotBeNull("the media attachment must survive a refused delete");
        }
        finally { await CleanupAttachmentFixtureAsync(f); }
    }

    // The create leg. Attachments upsert on (shortname, space_name, subpath)
    // with `resource_type = EXCLUDED.resource_type` in the SET list, so
    // "create a comment here" silently overwrote the media row in place — type,
    // bytes and all — on a gate that only ever saw "comment".
    [FactIfPg]
    public async Task Comment_Only_Grant_Cannot_Clobber_A_Media_Attachment_By_Creating_A_Comment()
    {
        var f = await SetupAttachmentFixtureAsync();
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        try
        {
            var client = await LoginAsAsync(f.Actor);
            var body = new Request
            {
                RequestType = RequestType.Create,
                SpaceName = f.Space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Comment,
                        Subpath = f.AttSubpath,
                        Shortname = f.Att,
                        Attributes = new() { ["body"] = "pwned" },
                    },
                },
            };
            var resp = await client.PostAsJsonAsync("/managed/request", body, DmartJsonContext.Default.Request);
            var raw = await resp.Content.ReadAsStringAsync();

            resp.StatusCode.ShouldNotBe(HttpStatusCode.OK,
                $"comment-only grant must not overwrite a media attachment by creating a comment. Body: {raw}");

            var after = await attachments.GetAsync(f.Space, f.AttSubpath, f.Att);
            after.ShouldNotBeNull();
            after!.ResourceType.ShouldBe(ResourceType.Media,
                "the row's resource_type must not be rewritten by a create it never authorized");
            after.Media.ShouldNotBeNull("the media bytes must survive");
        }
        finally { await CleanupAttachmentFixtureAsync(f); }
    }

    // -------------------------------------------------------------------------
    // Shared fixture for the attachment legs: a space, a parent content entry, a
    // MEDIA attachment hanging off it, and an actor whose grant covers every
    // action but ONLY the `comment` resource type.
    // -------------------------------------------------------------------------
    private const string MediaMarker = "rtc-secret-media-bytes";

    private sealed record AttFixture(
        string Actor, string Role, string Perm, string Space,
        string ParentSubpath, string AttSubpath, string Parent, string Att);

    private async Task<AttFixture> SetupAttachmentFixtureAsync()
    {
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var f = new AttFixture(
            Actor: Unique("rtc_commenter"),
            Role: Unique("rtc_crole"),
            Perm: Unique("rtc_cperm"),
            Space: Unique("rtc_cspace"),
            ParentSubpath: "/docs",
            AttSubpath: "",
            Parent: Unique("rtc_parent"),
            Att: Unique("rtc_att"));
        f = f with { AttSubpath = $"{f.ParentSubpath}/{f.Parent}" };
        var now = DateTime.UtcNow;

        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = f.Space,
            SpaceName = "management",
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = f.Perm,
            SpaceName = "management",
            Subpath = "/permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [f.Space] = new() { PermissionService.AllSubpathsMw } },
            ResourceTypes = new() { "comment" },
            Actions = new() { "view", "query", "create", "update", "delete" },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = f.Role,
            SpaceName = "management",
            Subpath = "/roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { f.Perm },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await CreateUserAsync(users, hasher, f.Actor, new() { f.Role });

        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = f.Parent,
            SpaceName = f.Space,
            Subpath = f.ParentSubpath,
            ResourceType = ResourceType.Content,
            OwnerShortname = "dmart",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await attachments.UpsertAsync(new Attachment
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = f.Att,
            SpaceName = f.Space,
            Subpath = f.AttSubpath,
            ResourceType = ResourceType.Media,
            OwnerShortname = "dmart",
            IsActive = true,
            Media = Encoding.UTF8.GetBytes(MediaMarker),
            Payload = new Payload { ContentType = ContentType.ImagePng },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await access.InvalidateAllCachesAsync();
        return f;
    }

    private async Task CleanupAttachmentFixtureAsync(AttFixture f)
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();

        try { await users.DeleteAllSessionsAsync(f.Actor); } catch { }
        try { await users.DeleteAsync(f.Actor); } catch { }
        try { await access.DeleteRoleAsync(f.Role); } catch { }
        try { await access.DeletePermissionAsync(f.Perm); } catch { }
        try { await attachments.DeleteUnderSubpathAsync(f.Space, f.AttSubpath); } catch { }
        try { await entries.DeleteAsync(f.Space, f.ParentSubpath, f.Parent, ResourceType.Content); } catch { }
        try { await spaces.DeleteAsync(f.Space); } catch { }
        await access.InvalidateAllCachesAsync();
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    private static async Task CreateUserAsync(
        UserRepository users, PasswordHasher hasher, string shortname, List<string>? roles = null)
    {
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = hasher.Hash(Password),
            Type = UserType.Web,
            Language = Language.En,
            Roles = roles ?? new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    private async Task<HttpClient> LoginAsAsync(string shortname)
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(shortname, null, null, Password, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, raw);

        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"Login failed for '{shortname}': {raw}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
