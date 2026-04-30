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

// Python-parity tests for RequestType.assign (serve_request_assign in
// backend/api/managed/utils.py:739).
//
// Contract:
//   1. owner_shortname MUST be in record.attributes; absent → MISSING_DATA (4xx).
//   2. Target user must exist in management/users; absent → OBJECT_NOT_FOUND.
//   3. Permission check uses the explicit `assign` action (NOT `update`).
//   4. owner_shortname IS actually persisted on the entry — this is the bug the
//      C# port had: assign accepted the request but silently dropped the field.
public sealed class AssignOwnershipTests : IClassFixture<DmartFactory>
{
    private const string Password = "Test1234";
    private readonly DmartFactory _factory;

    public AssignOwnershipTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Assign_Updates_OwnerShortname_When_Caller_Has_Assign_Permission()
    {
        // The headline test: assign actually transfers ownership.
        // Note: super_admin's super_manager permission doesn't include the
        // `assign` action (matches Python — verify via
        // sample/spaces/management/permissions/.dm/super_manager/meta.permission.json).
        // So we set up a dedicated permission that explicitly grants assign.
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var space = Unique("asnh_sp");
        var perm = Unique("asnh_p");
        var role = Unique("asnh_r");
        var caller = Unique("asnh_c");
        var newOwner = Unique("asnh_no");
        var sn = Unique("asnh_e");
        var now = DateTime.UtcNow;

        try
        {
            await spaces.UpsertAsync(BuildSpace(space, now));
            await access.UpsertPermissionAsync(BuildPermission(perm,
                subpaths: new() { [space] = new() { "items" } },
                actions: new() { "view", "assign" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(role, perm));
            await CreateUserRow(users, hasher, caller, new() { role });
            await CreateUserRow(users, hasher, newOwner);
            // Seed an entry owned by the caller.
            await entries.UpsertAsync(BuildContent(space, "/items", sn, now, owner: caller));
            await access.InvalidateAllCachesAsync();

            var (client, _) = await LoginAs(caller);
            var resp = await PostManaged(client, RequestType.Assign, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = "/items",
                Shortname = sn,
                Attributes = new() { ["owner_shortname"] = newOwner },
            });
            resp.StatusCode.ShouldBe(HttpStatusCode.OK,
                $"assign by caller with assign permission must succeed. body: {await resp.Content.ReadAsStringAsync()}");

            var refreshed = await entries.GetAsync(space, "/items", sn, ResourceType.Content);
            refreshed.ShouldNotBeNull();
            refreshed!.OwnerShortname.ShouldBe(newOwner,
                $"owner_shortname must be persisted after assign — was '{refreshed.OwnerShortname}'");
        }
        finally
        {
            try { await entries.DeleteAsync(space, "/items", sn, ResourceType.Content); } catch { }
            try { await users.DeleteAllSessionsAsync(caller); } catch { }
            try { await users.DeleteAsync(caller); } catch { }
            try { await users.DeleteAsync(newOwner); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task Assign_Without_OwnerShortname_Returns_MISSING_DATA()
    {
        // Python parity: serve_request_assign raises MISSING_DATA when
        // attributes lacks owner_shortname.
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var space = "test";
        var subpath = "/itest";
        var sn = Unique("asn_miss");
        var now = DateTime.UtcNow;

        try
        {
            await entries.UpsertAsync(BuildContent(space, subpath, sn, now, owner: "dmart"));

            var resp = await PostManaged(client, RequestType.Assign, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = sn,
                // No owner_shortname.
                Attributes = new() { ["displayname"] = "no owner field" },
            });
            resp.IsSuccessStatusCode.ShouldBeFalse(
                $"assign without owner_shortname must fail. Got {(int)resp.StatusCode}");

            var raw = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(raw);
            // Aggregate envelope: failures land in error.info[0].failed[0].error_code.
            var failedArr = doc.RootElement
                .GetProperty("error")
                .GetProperty("info")[0]
                .GetProperty("failed");
            var code = failedArr[0].GetProperty("error_code").GetInt32();
            code.ShouldBe(InternalErrorCode.MISSING_DATA,
                $"per-record error_code must be MISSING_DATA (202). Got {code}. body: {raw}");
        }
        finally
        {
            try { await entries.DeleteAsync(space, subpath, sn, ResourceType.Content); } catch { }
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task Assign_With_Empty_OwnerShortname_Returns_MISSING_DATA()
    {
        // Defense in depth: empty string should be treated like missing.
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var space = "test";
        var subpath = "/itest";
        var sn = Unique("asn_empty");
        var now = DateTime.UtcNow;

        try
        {
            await entries.UpsertAsync(BuildContent(space, subpath, sn, now, owner: "dmart"));

            var resp = await PostManaged(client, RequestType.Assign, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = sn,
                Attributes = new() { ["owner_shortname"] = "" },
            });
            resp.IsSuccessStatusCode.ShouldBeFalse(
                $"assign with empty owner_shortname must fail. Got {(int)resp.StatusCode}");
        }
        finally
        {
            try { await entries.DeleteAsync(space, subpath, sn, ResourceType.Content); } catch { }
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task Assign_With_Nonexistent_Target_User_Returns_OBJECT_NOT_FOUND()
    {
        // Python parity: serve_request_assign loads the target user from
        // management/users; raises if not present.
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var space = "test";
        var subpath = "/itest";
        var sn = Unique("asn_ghost");
        var now = DateTime.UtcNow;

        try
        {
            await entries.UpsertAsync(BuildContent(space, subpath, sn, now, owner: "dmart"));

            var ghost = Unique("ghost_user");
            var resp = await PostManaged(client, RequestType.Assign, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = sn,
                Attributes = new() { ["owner_shortname"] = ghost },
            });
            resp.IsSuccessStatusCode.ShouldBeFalse(
                $"assign to non-existent user must fail. Got {(int)resp.StatusCode}");

            var raw = await resp.Content.ReadAsStringAsync();
            raw.Contains("not found").ShouldBeTrue(
                $"error message should indicate target user is missing. body: {raw}");
        }
        finally
        {
            try { await entries.DeleteAsync(space, subpath, sn, ResourceType.Content); } catch { }
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task Assign_Without_AssignAction_Permission_Is_Denied_Even_With_Update_Permission()
    {
        // The whole point of the action distinction: a user with `update` but
        // no `assign` cannot transfer ownership. If this test fails, the
        // action override didn't actually flow into the permission walk.
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var space = Unique("asn_sp");
        var perm = Unique("asn_p");
        var role = Unique("asn_r");
        var user = Unique("asn_u");
        var newOwner = Unique("asn_no");
        var sn = Unique("asn_e");
        var now = DateTime.UtcNow;

        try
        {
            await spaces.UpsertAsync(BuildSpace(space, now));
            // Permission has update + view but NOT assign.
            await access.UpsertPermissionAsync(BuildPermission(perm,
                subpaths: new() { [space] = new() { "items" } },
                actions: new() { "view", "update" },  // no "assign"
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(role, perm));
            await CreateUserRow(users, hasher, user, new() { role });
            await CreateUserRow(users, hasher, newOwner);
            await entries.UpsertAsync(BuildContent(space, "/items", sn, now, owner: user));
            await access.InvalidateAllCachesAsync();

            var (client, _) = await LoginAs(user);
            var resp = await PostManaged(client, RequestType.Assign, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = "/items",
                Shortname = sn,
                Attributes = new() { ["owner_shortname"] = newOwner },
            });
            resp.IsSuccessStatusCode.ShouldBeFalse(
                $"assign without 'assign' action permission must be denied. Got {(int)resp.StatusCode}");

            // Verify ownership was NOT changed.
            var refreshed = await entries.GetAsync(space, "/items", sn, ResourceType.Content);
            refreshed!.OwnerShortname.ShouldBe(user,
                "owner must remain unchanged when assign is denied");
        }
        finally
        {
            try { await entries.DeleteAsync(space, "/items", sn, ResourceType.Content); } catch { }
            try { await users.DeleteAllSessionsAsync(user); } catch { }
            try { await users.DeleteAsync(user); } catch { }
            try { await users.DeleteAsync(newOwner); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task Assign_With_AssignAction_Permission_Succeeds_For_Limited_User()
    {
        // Mirror of the above: user with `assign` action permission CAN
        // transfer. Without this check, we can't tell the action gate from
        // a generic "limited user can never assign" rule.
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var space = Unique("asn2_sp");
        var perm = Unique("asn2_p");
        var role = Unique("asn2_r");
        var user = Unique("asn2_u");
        var newOwner = Unique("asn2_no");
        var sn = Unique("asn2_e");
        var now = DateTime.UtcNow;

        try
        {
            await spaces.UpsertAsync(BuildSpace(space, now));
            // Permission grants assign explicitly.
            await access.UpsertPermissionAsync(BuildPermission(perm,
                subpaths: new() { [space] = new() { "items" } },
                actions: new() { "view", "assign" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(role, perm));
            await CreateUserRow(users, hasher, user, new() { role });
            await CreateUserRow(users, hasher, newOwner);
            await entries.UpsertAsync(BuildContent(space, "/items", sn, now, owner: user));
            await access.InvalidateAllCachesAsync();

            var (client, _) = await LoginAs(user);
            var resp = await PostManaged(client, RequestType.Assign, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = "/items",
                Shortname = sn,
                Attributes = new() { ["owner_shortname"] = newOwner },
            });
            resp.StatusCode.ShouldBe(HttpStatusCode.OK,
                $"user with 'assign' action permission must succeed. body: {await resp.Content.ReadAsStringAsync()}");

            var refreshed = await entries.GetAsync(space, "/items", sn, ResourceType.Content);
            refreshed!.OwnerShortname.ShouldBe(newOwner,
                "owner must transfer when caller has the assign action");
        }
        finally
        {
            try { await entries.DeleteAsync(space, "/items", sn, ResourceType.Content); } catch { }
            try { await users.DeleteAllSessionsAsync(user); } catch { }
            try { await users.DeleteAsync(user); } catch { }
            try { await users.DeleteAsync(newOwner); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task Assign_Does_Not_Touch_Acl_Or_Other_Restricted_Fields()
    {
        // Assign opens the restricted-fields gate for owner_shortname only.
        // ACL is also a restricted field; sneaking it via assign must NOT
        // mutate the entry's ACL.
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var space = Unique("asnk_sp");
        var perm = Unique("asnk_p");
        var role = Unique("asnk_r");
        var caller = Unique("asnk_c");
        var newOwner = Unique("asnk_no");
        var sn = Unique("asnk_e");
        var now = DateTime.UtcNow;

        try
        {
            await spaces.UpsertAsync(BuildSpace(space, now));
            await access.UpsertPermissionAsync(BuildPermission(perm,
                subpaths: new() { [space] = new() { "items" } },
                actions: new() { "view", "assign" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(role, perm));
            await CreateUserRow(users, hasher, caller, new() { role });
            await CreateUserRow(users, hasher, newOwner);
            // Seed an entry owned by the caller, with a known ACL.
            var seedAcl = new List<AclEntry>
            {
                new() { UserShortname = "alice", AllowedActions = new() { "view" } },
            };
            await entries.UpsertAsync(new Entry
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = sn,
                SpaceName = space,
                Subpath = "/items",
                OwnerShortname = caller,
                ResourceType = ResourceType.Content,
                IsActive = true,
                Acl = seedAcl,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await access.InvalidateAllCachesAsync();

            var (client, _) = await LoginAs(caller);
            // Try to sneak an ACL change via the assign call.
            var resp = await PostManaged(client, RequestType.Assign, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = "/items",
                Shortname = sn,
                Attributes = new()
                {
                    ["owner_shortname"] = newOwner,
                    // Attacker tries to add themselves to ACL via the assign window.
                    ["acl"] = new List<Dictionary<string, object>>
                    {
                        new()
                        {
                            ["user_shortname"] = caller,
                            ["allowed_actions"] = new List<string> { "view", "update", "delete" },
                        },
                    },
                },
            });
            resp.StatusCode.ShouldBe(HttpStatusCode.OK,
                $"assign with extra fields should succeed but ignore them. body: {await resp.Content.ReadAsStringAsync()}");

            var refreshed = await entries.GetAsync(space, "/items", sn, ResourceType.Content);
            refreshed!.OwnerShortname.ShouldBe(newOwner, "owner transferred");
            // ACL should still hold ONLY the original alice entry — assign must
            // not double as an ACL editor.
            var aclHasCaller = refreshed.Acl?.Any(e => e.UserShortname == caller) ?? false;
            aclHasCaller.ShouldBeFalse(
                "assign must NOT mutate ACL — that's update_acl's job. " +
                $"ACL after assign: {string.Join(",", refreshed.Acl?.Select(a => a.UserShortname) ?? Array.Empty<string>())}");
        }
        finally
        {
            try { await entries.DeleteAsync(space, "/items", sn, ResourceType.Content); } catch { }
            try { await users.DeleteAllSessionsAsync(caller); } catch { }
            try { await users.DeleteAsync(caller); } catch { }
            try { await users.DeleteAsync(newOwner); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // ====================================================================
    // helpers
    // ====================================================================

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..24];

    private static Permission BuildPermission(
        string shortname,
        Dictionary<string, List<string>> subpaths,
        List<string> actions,
        List<string>? resourceTypes = null)
    => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/permissions",
        OwnerShortname = "dmart",
        IsActive = true,
        Subpaths = subpaths,
        Actions = actions,
        ResourceTypes = resourceTypes ?? new(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Role BuildRole(string shortname, params string[] permissions)
    => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/roles",
        OwnerShortname = "dmart",
        IsActive = true,
        Permissions = new(permissions),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Space BuildSpace(string shortname, DateTime now)
    => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/",
        OwnerShortname = "dmart",
        IsActive = true,
        Languages = new() { Language.En },
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Entry BuildContent(string space, string subpath, string shortname,
        DateTime now, string owner)
    => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = space,
        Subpath = subpath,
        OwnerShortname = owner,
        ResourceType = ResourceType.Content,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static Task CreateUserRow(UserRepository users, PasswordHasher hasher,
        string shortname, List<string>? roles = null)
        => users.UpsertAsync(new User
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

    private async Task<(HttpClient Client, string Token)> LoginAs(string shortname)
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(shortname, null, null, Password, null);
        var resp = await client.PostAsJsonAsync("/user/login", login,
            DmartJsonContext.Default.UserLoginRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, $"login for '{shortname}' failed: {raw}");
        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"no access_token: {raw}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    private static Task<HttpResponseMessage> PostManaged(HttpClient client,
        RequestType rt, string space, Record record)
    {
        var req = new Request
        {
            RequestType = rt,
            SpaceName = space,
            Records = new() { record },
        };
        return client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
    }
}
