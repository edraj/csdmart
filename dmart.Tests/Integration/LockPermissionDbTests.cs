using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
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

// Covers the permission gate on the lock/unlock endpoints:
//   - lock requires the `lock` action (strict, like create/update/delete);
//   - the lock holder can always release their own lock;
//   - the `unlock` action lets a non-holder force-release someone else's lock;
//   - unlocking with no live lock is an idempotent no-op success.
//
// Ticket locking additionally writes collaborators.processed_by via update and
// therefore also requires `update`; that interaction is exercised at the
// service/workflow level and is intentionally not re-tested here (ticket setup
// needs a workflow). ACL-granted lock uses the same CanAsync path as every other
// action, already covered by PermissionServiceIntegrationTests.
public class LockPermissionDbTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public LockPermissionDbTests(DmartFactory factory) => _factory = factory;

    private const string Space = "test";
    private const string Sub = "lockperm";

    private static string Rnd() => $"lp_{Guid.NewGuid():N}".Substring(0, 12);

    private static Request CreateContent(string shortname) => new()
    {
        RequestType = RequestType.Create,
        SpaceName = Space,
        Records = new()
        {
            new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = Sub,
                Shortname = shortname,
                Attributes = new() { ["displayname"] = "lock permission probe" },
            },
        },
    };

    private static Request DeleteContent(string shortname) => new()
    {
        RequestType = RequestType.Delete,
        SpaceName = Space,
        Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = Sub, Shortname = shortname } },
    };

    // A logged-in user whose only role grants exactly `actions` on the whole
    // `test` space for content + ticket resource types. Used to isolate the
    // effect of granting/withholding the `lock` / `unlock` actions.
    private async Task<DmartFactory.TestUser> UserWithActionsAsync(params string[] actions)
    {
        _factory.CreateClient(); // force host construction so AdminBootstrap + tables are ready
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var permName = $"itest_lkperm_{suffix}";
        var roleName = $"itest_lkrole_{suffix}";

        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = permName,
            SpaceName = "management",
            Subpath = "permissions",
            OwnerShortname = "dmart",
            IsActive = true,
            Subpaths = new() { [Space] = new() { PermissionService.AllSubpathsMw } },
            Actions = actions.ToList(),
            ResourceTypes = new() { "content", "ticket" },
            Conditions = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = roleName,
            SpaceName = "management",
            Subpath = "roles",
            OwnerShortname = "dmart",
            IsActive = true,
            Permissions = new() { permName },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await access.InvalidateAllCachesAsync();
        return await _factory.CreateLoggedInUserAsync(roles: new() { roleName });
    }

    [FactIfPg]
    public async Task Lock_Without_Lock_Action_Is_Denied()
    {
        // create/update/delete/view but NO lock.
        var user = await UserWithActionsAsync("view", "create", "update", "delete");
        var shortname = Rnd();
        try
        {
            (await user.Client.PostAsJsonAsync("/managed/request", CreateContent(shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var resp = await user.Client.PutAsync($"/managed/lock/content/{Space}/{Sub}/{shortname}", null);
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // NOT_ALLOWED -> 401
            (await resp.Content.ReadAsStringAsync()).ShouldContain("lock access");
        }
        finally
        {
            await user.Client.DeleteAsync($"/managed/lock/{Space}/{Sub}/{shortname}");
            await user.Client.PostAsJsonAsync("/managed/request", DeleteContent(shortname), DmartJsonContext.Default.Request);
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Lock_With_Lock_Action_Succeeds()
    {
        var user = await UserWithActionsAsync("view", "create", "update", "delete", "lock", "unlock");
        var shortname = Rnd();
        try
        {
            (await user.Client.PostAsJsonAsync("/managed/request", CreateContent(shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await user.Client.PutAsync($"/managed/lock/content/{Space}/{Sub}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await user.Client.DeleteAsync($"/managed/lock/{Space}/{Sub}/{shortname}");
            await user.Client.PostAsJsonAsync("/managed/request", DeleteContent(shortname), DmartJsonContext.Default.Request);
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Holder_Can_Unlock_Without_Unlock_Action()
    {
        // Has `lock` but deliberately NOT `unlock`.
        var user = await UserWithActionsAsync("view", "create", "update", "delete", "lock");
        var shortname = Rnd();
        try
        {
            (await user.Client.PostAsJsonAsync("/managed/request", CreateContent(shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await user.Client.PutAsync($"/managed/lock/content/{Space}/{Sub}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // The holder always releases their own lock, no `unlock` action needed.
            (await user.Client.DeleteAsync($"/managed/lock/{Space}/{Sub}/{shortname}"))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await user.Client.DeleteAsync($"/managed/lock/{Space}/{Sub}/{shortname}");
            await user.Client.PostAsJsonAsync("/managed/request", DeleteContent(shortname), DmartJsonContext.Default.Request);
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task ForceUnlock_By_User_With_Unlock_Action_Succeeds()
    {
        var owner = await _factory.CreateLoggedInUserAsync();   // super_admin holds the lock
        var forcer = await UserWithActionsAsync("unlock");      // non-holder with force capability
        var shortname = Rnd();
        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await owner.Client.PutAsync($"/managed/lock/content/{Space}/{Sub}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // A different user holding `unlock` force-releases the owner's lock.
            (await forcer.Client.DeleteAsync($"/managed/lock/{Space}/{Sub}/{shortname}"))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // The lock is gone — a lock-status query surfaces no holder.
            var q = new Query
            {
                Type = QueryType.Search,
                SpaceName = Space,
                Subpath = Sub,
                ExactSubpath = true,
                FilterShortnames = new() { shortname },
                RetrieveLockStatus = true,
                Limit = 10,
            };
            var resp = await owner.Client.PostAsJsonAsync("/managed/query", q, DmartJsonContext.Default.Query);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            var rec = body!.Records!.ShouldHaveSingleItem();
            (rec.Attributes is null || !rec.Attributes.ContainsKey("locked")).ShouldBeTrue();
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{Sub}/{shortname}");
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(shortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
            await forcer.Cleanup();
        }
    }

    [FactIfPg]
    public async Task ForceUnlock_Without_Unlock_Action_Is_Denied()
    {
        var owner = await _factory.CreateLoggedInUserAsync();  // holds the lock
        var other = await UserWithActionsAsync("view");        // non-holder, NO unlock
        var shortname = Rnd();
        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
            (await owner.Client.PutAsync($"/managed/lock/content/{Space}/{Sub}/{shortname}", null))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var resp = await other.Client.DeleteAsync($"/managed/lock/{Space}/{Sub}/{shortname}");
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // NOT_ALLOWED -> 401
        }
        finally
        {
            await owner.Client.DeleteAsync($"/managed/lock/{Space}/{Sub}/{shortname}");
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(shortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
            await other.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Unlock_With_No_Live_Lock_Is_Idempotent()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var shortname = Rnd();
        try
        {
            (await owner.Client.PostAsJsonAsync("/managed/request", CreateContent(shortname), DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // Never locked → unlock is a no-op success, not a failure.
            (await owner.Client.DeleteAsync($"/managed/lock/{Space}/{Sub}/{shortname}"))
                .StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await owner.Client.PostAsJsonAsync("/managed/request", DeleteContent(shortname), DmartJsonContext.Default.Request);
            await owner.Cleanup();
        }
    }
}
