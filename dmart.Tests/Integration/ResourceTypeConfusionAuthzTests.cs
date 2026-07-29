using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
// EntryService now re-derives the locator from existing.ResourceType before
// calling PermissionService, for both UpdateAsync and MoveAsync.
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
