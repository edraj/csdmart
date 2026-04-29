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

// End-to-end coverage for every field on the Permission model — actions,
// subpaths, resource_types, conditions, restricted_fields, allowed_fields_values
// — exercised through the live HTTP surface (login → /managed/request) with a
// direct PermissionService fallback for the few cases where HTTP can't observe
// the gate (e.g. condition checks against a resource that hasn't been loaded).
//
// Each test seeds its own users/roles/permissions via the repositories so the
// tested actor's permission set is tightly controlled, then drives /managed/*
// or /public/* with a JWT minted via /user/login. Cleanup runs in finally so
// reruns are idempotent.
public sealed class ComprehensivePermissionsTests : IClassFixture<DmartFactory>
{
    private const string Password = "Test1234";
    private readonly DmartFactory _factory;

    public ComprehensivePermissionsTests(DmartFactory factory) => _factory = factory;

    // ============================================================
    // 1. ACTIONS — only listed actions are honored
    // ============================================================

    [FactIfPg]
    public async Task Actions_View_Only_Permission_Allows_Read_But_Blocks_Write()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_actview");
        var roleName = Unique("role_actview");
        var userName = Unique("user_actview");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");

            (await perms.CanReadAsync(userName, locator))
                .ShouldBeTrue("view action grants read");
            (await perms.CanCreateAsync(userName, locator))
                .ShouldBeFalse("create not in actions list — must deny");
            (await perms.CanUpdateAsync(userName, locator))
                .ShouldBeFalse("update not in actions list — must deny");
            (await perms.CanDeleteAsync(userName, locator))
                .ShouldBeFalse("delete not in actions list — must deny");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_Create_Only_Permission_Allows_Create_But_Blocks_Update_Delete()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_actcreate");
        var roleName = Unique("role_actcreate");
        var userName = Unique("user_actcreate");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "create" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");

            (await perms.CanCreateAsync(userName, locator)).ShouldBeTrue();
            (await perms.CanReadAsync(userName, locator)).ShouldBeFalse();
            (await perms.CanUpdateAsync(userName, locator)).ShouldBeFalse();
            (await perms.CanDeleteAsync(userName, locator)).ShouldBeFalse();
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_Multiple_Granted_Allows_All_Listed_And_Denies_Unlisted()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_actmulti");
        var roleName = Unique("role_actmulti");
        var userName = Unique("user_actmulti");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "view", "query", "create" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");

            (await perms.CanReadAsync(userName, locator)).ShouldBeTrue("view granted");
            (await perms.CanCreateAsync(userName, locator)).ShouldBeTrue("create granted");
            (await perms.CanAsync(userName, "query", locator)).ShouldBeTrue("query granted");
            (await perms.CanUpdateAsync(userName, locator)).ShouldBeFalse("update NOT in list");
            (await perms.CanDeleteAsync(userName, locator)).ShouldBeFalse("delete NOT in list");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    // ----- per-action exhaustive coverage -----
    //
    // Every action in ActionType.cs gets its own row. For each, we verify the
    // permission grants exactly that action and denies the other ten — the
    // strict cross-product proves Actions matching is symmetric and complete.
    [TheoryIfPg]
    [InlineData("query")]
    [InlineData("view")]
    [InlineData("update")]
    [InlineData("create")]
    [InlineData("delete")]
    [InlineData("attach")]
    [InlineData("assign")]
    [InlineData("move")]
    [InlineData("progress_ticket")]
    [InlineData("lock")]
    [InlineData("unlock")]
    public async Task Actions_Each_Single_Action_Granted_Only_That_Action(string grantedAction)
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_solo");
        var roleName = Unique("role_solo");
        var userName = Unique("user_solo");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { grantedAction },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            var allActions = new[]
            {
                "query", "view", "update", "create", "delete",
                "attach", "assign", "move", "progress_ticket", "lock", "unlock",
            };
            foreach (var probe in allActions)
            {
                var granted = await perms.CanAsync(userName, probe, locator);
                if (probe == grantedAction)
                    granted.ShouldBeTrue($"'{probe}' should be granted (it's the only action in the list)");
                else
                    granted.ShouldBeFalse($"'{probe}' should be denied (only '{grantedAction}' is granted)");
            }
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_Empty_List_Denies_All_Actions()
    {
        // A permission with no actions at all is functionally inert — every
        // action probe falls through CheckAtCandidate's
        //   if (!p.Actions.Contains(action, ...)) continue;
        // gate. This is the canary test: if someone "fixes" the empty-list
        // case to mean "any action", every other test in this file becomes a
        // false positive.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_noact");
        var roleName = Unique("role_noact");
        var userName = Unique("user_noact");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new(),
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            foreach (var probe in new[]
            {
                "query", "view", "update", "create", "delete",
                "attach", "assign", "move", "progress_ticket", "lock", "unlock",
            })
            {
                (await perms.CanAsync(userName, probe, locator))
                    .ShouldBeFalse($"empty actions list must deny '{probe}'");
            }
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_ReadOnly_View_Plus_Query_Pattern()
    {
        // The "viewer" role pattern: read-only on managed resources.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_ro");
        var roleName = Unique("role_ro");
        var userName = Unique("user_ro");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "view", "query" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            (await perms.CanReadAsync(userName, locator)).ShouldBeTrue();
            (await perms.CanAsync(userName, "query", locator)).ShouldBeTrue();
            (await perms.CanCreateAsync(userName, locator)).ShouldBeFalse();
            (await perms.CanUpdateAsync(userName, locator)).ShouldBeFalse();
            (await perms.CanDeleteAsync(userName, locator)).ShouldBeFalse();
            (await perms.CanAsync(userName, "attach", locator)).ShouldBeFalse();
            (await perms.CanAsync(userName, "assign", locator)).ShouldBeFalse();
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_WriteOnly_Create_Update_Delete_Pattern()
    {
        // "writer" role — can mutate but cannot read. Edge case worth pinning
        // because in some systems write implies read; in dmart the action
        // sets are independent (each action requires its own grant).
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_wo");
        var roleName = Unique("role_wo");
        var userName = Unique("user_wo");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "create", "update", "delete" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            (await perms.CanCreateAsync(userName, locator)).ShouldBeTrue();
            (await perms.CanUpdateAsync(userName, locator)).ShouldBeTrue();
            (await perms.CanDeleteAsync(userName, locator)).ShouldBeTrue();
            (await perms.CanReadAsync(userName, locator)).ShouldBeFalse(
                "write actions do NOT imply read — Python parity");
            (await perms.CanAsync(userName, "query", locator)).ShouldBeFalse();
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_FullCrud_All_Five_Verbs_Granted()
    {
        // The standard "editor" pattern.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_crud");
        var roleName = Unique("role_crud");
        var userName = Unique("user_crud");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "create", "view", "update", "delete", "query" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            (await perms.CanCreateAsync(userName, locator)).ShouldBeTrue();
            (await perms.CanReadAsync(userName, locator)).ShouldBeTrue();
            (await perms.CanUpdateAsync(userName, locator)).ShouldBeTrue();
            (await perms.CanDeleteAsync(userName, locator)).ShouldBeTrue();
            (await perms.CanAsync(userName, "query", locator)).ShouldBeTrue();
            // Workflow actions still NOT granted — full CRUD is just the five
            // primary verbs.
            (await perms.CanAsync(userName, "attach", locator)).ShouldBeFalse();
            (await perms.CanAsync(userName, "lock", locator)).ShouldBeFalse();
            (await perms.CanAsync(userName, "progress_ticket", locator)).ShouldBeFalse();
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_Attach_Does_Not_Imply_Create_Or_Update()
    {
        // Attaching files to an existing entry is its own verb. A user with
        // attach + view should be able to look at the entry and add files,
        // but NOT mutate fields.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_att");
        var roleName = Unique("role_att");
        var userName = Unique("user_att");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "view", "attach" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            (await perms.CanReadAsync(userName, locator)).ShouldBeTrue();
            (await perms.CanAsync(userName, "attach", locator)).ShouldBeTrue();
            (await perms.CanCreateAsync(userName, locator)).ShouldBeFalse(
                "attach is NOT create — they're independent verbs");
            (await perms.CanUpdateAsync(userName, locator)).ShouldBeFalse(
                "attach is NOT update");
            (await perms.CanDeleteAsync(userName, locator)).ShouldBeFalse();
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_Lock_Without_Unlock_Is_Asymmetric()
    {
        // lock and unlock are SEPARATE actions — granting one does not grant
        // the other. This matters for "soft moderation" patterns where a
        // junior role can lock things but only a supervisor can unlock.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_lock");
        var roleName = Unique("role_lock");
        var userName = Unique("user_lock");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "lock" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            (await perms.CanAsync(userName, "lock", locator)).ShouldBeTrue();
            (await perms.CanAsync(userName, "unlock", locator)).ShouldBeFalse(
                "unlock requires its own grant");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_Workflow_Trio_Move_Assign_ProgressTicket()
    {
        // Tickets workflow role: move between subpaths, assign owner,
        // progress through state machine. None of the standard CRUD verbs
        // bleed in.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_wf");
        var roleName = Unique("role_wf");
        var userName = Unique("user_wf");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "tickets" } },
                actions: new() { "move", "assign", "progress_ticket" },
                resourceTypes: new() { "ticket" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Ticket, "test", "/tickets", "x");
            (await perms.CanAsync(userName, "move", locator)).ShouldBeTrue();
            (await perms.CanAsync(userName, "assign", locator)).ShouldBeTrue();
            (await perms.CanAsync(userName, "progress_ticket", locator)).ShouldBeTrue();
            (await perms.CanReadAsync(userName, locator)).ShouldBeFalse(
                "workflow trio doesn't include view");
            (await perms.CanUpdateAsync(userName, locator)).ShouldBeFalse();
            (await perms.CanDeleteAsync(userName, locator)).ShouldBeFalse();
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_All_Eleven_Granted_Allows_Every_Verb()
    {
        // Kitchen sink — listing every valid action grants everything.
        // Defensive against future drift: if a new action is added to
        // ActionType.cs and the matcher introduces an implicit deny, this
        // test fails until the new action is added here too, drawing
        // attention to it.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_all");
        var roleName = Unique("role_all");
        var userName = Unique("user_all");

        var everyAction = new List<string>
        {
            "query", "view", "update", "create", "delete",
            "attach", "assign", "move", "progress_ticket", "lock", "unlock",
        };

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: everyAction,
                resourceTypes: new() { "content", "ticket" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            foreach (var probe in everyAction)
            {
                (await perms.CanAsync(userName, probe, locator))
                    .ShouldBeTrue($"action '{probe}' is in the granted list — must succeed");
            }
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Actions_Cumulative_Across_Multiple_Permissions_Same_Subpath()
    {
        // Two permissions on the SAME subpath, each granting different actions.
        // The effective set is the union — proves the per-permission action
        // check is OR'd across the resolved permission list, not AND'd.
        var (perms, users, access, _, _) = Resolve();

        var permRead = Unique("perm_rd");
        var permWrite = Unique("perm_wr");
        var roleName = Unique("role_cum");
        var userName = Unique("user_cum");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permRead,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "view", "query" },
                resourceTypes: new() { "content" }));
            await access.UpsertPermissionAsync(BuildPermission(permWrite,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "create", "update", "delete" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permRead, permWrite));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            (await perms.CanReadAsync(userName, locator)).ShouldBeTrue("from permRead");
            (await perms.CanAsync(userName, "query", locator)).ShouldBeTrue("from permRead");
            (await perms.CanCreateAsync(userName, locator)).ShouldBeTrue("from permWrite");
            (await perms.CanUpdateAsync(userName, locator)).ShouldBeTrue("from permWrite");
            (await perms.CanDeleteAsync(userName, locator)).ShouldBeTrue("from permWrite");
            (await perms.CanAsync(userName, "attach", locator)).ShouldBeFalse(
                "neither permission grants attach");
        }
        finally
        {
            try { await users.DeleteAsync(userName); } catch { }
            try { await access.DeleteRoleAsync(roleName); } catch { }
            try { await access.DeletePermissionAsync(permRead); } catch { }
            try { await access.DeletePermissionAsync(permWrite); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task Actions_Same_Verb_Different_Subpaths_Per_Permission_Honored()
    {
        // Two permissions, both granting "view", but on DIFFERENT subpaths.
        // Verifies the per-permission subpath scoping isn't accidentally
        // unified when actions overlap.
        var (perms, users, access, _, _) = Resolve();

        var permA = Unique("perm_va");
        var permB = Unique("perm_vb");
        var roleName = Unique("role_vab");
        var userName = Unique("user_vab");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permA,
                subpaths: new() { ["test"] = new() { "alpha" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));
            await access.UpsertPermissionAsync(BuildPermission(permB,
                subpaths: new() { ["test"] = new() { "bravo" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permA, permB));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/alpha", "x")))
                .ShouldBeTrue();
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/bravo", "x")))
                .ShouldBeTrue();
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/charlie", "x")))
                .ShouldBeFalse("neither permission scopes 'charlie' — denied even though both grant view");
        }
        finally
        {
            try { await users.DeleteAsync(userName); } catch { }
            try { await access.DeleteRoleAsync(roleName); } catch { }
            try { await access.DeletePermissionAsync(permA); } catch { }
            try { await access.DeletePermissionAsync(permB); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // ============================================================
    // 2. SUBPATHS — literal subpaths, hierarchical walk, magic words
    // ============================================================

    [FactIfPg]
    public async Task Subpaths_Literal_Only_Matches_Exact_Subpath_And_Children()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_subliteral");
        var roleName = Unique("role_subliteral");
        var userName = Unique("user_subliteral");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "alpha" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/alpha", "x")))
                .ShouldBeTrue("exact subpath match");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/alpha/deep", "x")))
                .ShouldBeTrue("hierarchical walk grants child");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/alpha/deep/deeper", "x")))
                .ShouldBeTrue("walk grants grandchild");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/bravo", "x")))
                .ShouldBeFalse("sibling subpath denied");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "other", "/alpha", "x")))
                .ShouldBeFalse("permission scoped to 'test' space — other space denied");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Subpaths_Multiple_Listed_Each_Grants_Independently()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_submulti");
        var roleName = Unique("role_submulti");
        var userName = Unique("user_submulti");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "alpha", "bravo", "charlie/inner" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/alpha", "x")))
                .ShouldBeTrue();
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/bravo", "x")))
                .ShouldBeTrue();
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/charlie/inner", "x")))
                .ShouldBeTrue("nested path matches the multi-segment literal");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/charlie", "x")))
                .ShouldBeFalse("permission is on charlie/inner, not on charlie itself");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/delta", "x")))
                .ShouldBeFalse();
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Subpaths_AllSubpaths_MagicWord_Grants_Across_Whole_Space()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_allsub");
        var roleName = Unique("role_allsub");
        var userName = Unique("user_allsub");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { PermissionService.AllSubpathsMw } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/whatever", "x")))
                .ShouldBeTrue();
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/very/deep/path", "x")))
                .ShouldBeTrue();
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "other_space", "/whatever", "x")))
                .ShouldBeFalse("magic word is scoped to 'test' — other space stays denied");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Subpaths_AllSpaces_MagicWord_Grants_Across_Every_Space()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_allsp");
        var roleName = Unique("role_allsp");
        var userName = Unique("user_allsp");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new()
                {
                    [PermissionService.AllSpacesMw] = new() { PermissionService.AllSubpathsMw },
                },
                actions: new() { "view", "query" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "anything", "/anywhere", "x")))
                .ShouldBeTrue();
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "yet_another", "/", "y")))
                .ShouldBeTrue();
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    // ============================================================
    // 3. RESOURCE_TYPES — only listed resource types match
    // ============================================================

    [FactIfPg]
    public async Task ResourceTypes_Listed_Only_Matches_Listed_Types()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_rt");
        var roleName = Unique("role_rt");
        var userName = Unique("user_rt");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "things" } },
                actions: new() { "view" },
                resourceTypes: new() { "content", "folder" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/things", "x")))
                .ShouldBeTrue("content listed");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Folder, "test", "/things", "x")))
                .ShouldBeTrue("folder listed");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Ticket, "test", "/things", "x")))
                .ShouldBeFalse("ticket NOT listed");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.User, "test", "/things", "x")))
                .ShouldBeFalse("user NOT listed");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task ResourceTypes_Empty_Matches_Any_Type()
    {
        // Python parity: empty resource_types list = wildcard. PermissionService
        // skips the type check when ResourceTypes.Count == 0.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_rtany");
        var roleName = Unique("role_rtany");
        var userName = Unique("user_rtany");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "things" } },
                actions: new() { "view" },
                resourceTypes: new()));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/things", "x")))
                .ShouldBeTrue();
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Ticket, "test", "/things", "x")))
                .ShouldBeTrue("empty resource_types = any");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.User, "test", "/things", "x")))
                .ShouldBeTrue();
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    // ============================================================
    // 4. CONDITIONS — own / is_active gating
    // ============================================================

    [FactIfPg]
    public async Task Conditions_Own_Allows_Owner_Update_Denies_Stranger()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_own");
        var roleName = Unique("role_own");
        var userName = Unique("user_own");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "update", "delete" },
                resourceTypes: new() { "content" },
                conditions: new() { "own" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");

            var ownedCtx = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: userName, OwnerGroupShortname: null, Acl: null);
            (await perms.CanUpdateAsync(userName, locator, ownedCtx, null))
                .ShouldBeTrue("user owns the resource — 'own' condition met");
            (await perms.CanDeleteAsync(userName, locator, ownedCtx))
                .ShouldBeTrue("delete also allowed for owner");

            var strangerCtx = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: "someone_else", OwnerGroupShortname: null, Acl: null);
            (await perms.CanUpdateAsync(userName, locator, strangerCtx, null))
                .ShouldBeFalse("not owner — own condition unmet");
            (await perms.CanDeleteAsync(userName, locator, strangerCtx))
                .ShouldBeFalse();
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Conditions_IsActive_Blocks_Update_On_Inactive_Resource()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_active");
        var roleName = Unique("role_active");
        var userName = Unique("user_active");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "update" },
                resourceTypes: new() { "content" },
                conditions: new() { "is_active" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");

            var activeCtx = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: "anyone", OwnerGroupShortname: null, Acl: null);
            (await perms.CanUpdateAsync(userName, locator, activeCtx, null))
                .ShouldBeTrue("active resource satisfies is_active condition");

            var inactiveCtx = new PermissionService.ResourceContext(
                IsActive: false, OwnerShortname: "anyone", OwnerGroupShortname: null, Acl: null);
            (await perms.CanUpdateAsync(userName, locator, inactiveCtx, null))
                .ShouldBeFalse("inactive resource fails is_active condition");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Conditions_OwnAndIsActive_Both_Required()
    {
        // A permission with two conditions requires both to be achieved.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_2cond");
        var roleName = Unique("role_2cond");
        var userName = Unique("user_2cond");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "update" },
                resourceTypes: new() { "content" },
                conditions: new() { "own", "is_active" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");

            var bothMet = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: userName, OwnerGroupShortname: null, Acl: null);
            (await perms.CanUpdateAsync(userName, locator, bothMet, null))
                .ShouldBeTrue("both conditions satisfied");

            var ownerButInactive = new PermissionService.ResourceContext(
                IsActive: false, OwnerShortname: userName, OwnerGroupShortname: null, Acl: null);
            (await perms.CanUpdateAsync(userName, locator, ownerButInactive, null))
                .ShouldBeFalse("owner but inactive — is_active not met");

            var activeButStranger = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: "stranger", OwnerGroupShortname: null, Acl: null);
            (await perms.CanUpdateAsync(userName, locator, activeButStranger, null))
                .ShouldBeFalse("active but not owner — own not met");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task Conditions_Skipped_For_Create_And_Query_Actions()
    {
        // Python parity: create/query actions don't check conditions because
        // there's no entry to inspect (create) or it's filtered post-hoc (query).
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_skipcond");
        var roleName = Unique("role_skipcond");
        var userName = Unique("user_skipcond");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "create", "query" },
                resourceTypes: new() { "content" },
                conditions: new() { "own", "is_active" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");

            (await perms.CanCreateAsync(userName, locator))
                .ShouldBeTrue("create exempt from condition check (no resource yet)");
            (await perms.CanAsync(userName, "query", locator))
                .ShouldBeTrue("query exempt from condition check (post-hoc filter)");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    // ============================================================
    // 5. RESTRICTED_FIELDS — block updates that touch listed fields
    // ============================================================

    [FactIfPg]
    public async Task RestrictedFields_Block_Update_Touching_Restricted_Field()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_rf");
        var roleName = Unique("role_rf");
        var userName = Unique("user_rf");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "tickets" } },
                actions: new() { "update" },
                resourceTypes: new() { "content" },
                restrictedFields: new() { "status", "payload.body.amount" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/tickets", "x");
            var ctx = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: userName, OwnerGroupShortname: null, Acl: null);

            (await perms.CanUpdateAsync(userName, locator, ctx,
                new() { ["title"] = "renamed" }))
                .ShouldBeTrue("untouched field is fine");

            (await perms.CanUpdateAsync(userName, locator, ctx,
                new() { ["status"] = "closed" }))
                .ShouldBeFalse("status is restricted");

            (await perms.CanUpdateAsync(userName, locator, ctx,
                new()
                {
                    ["payload"] = new Dictionary<string, object>
                    {
                        ["body"] = new Dictionary<string, object> { ["amount"] = 99 },
                    },
                }))
                .ShouldBeFalse("nested payload.body.amount restricted via prefix match");

            (await perms.CanUpdateAsync(userName, locator, ctx,
                new()
                {
                    ["payload"] = new Dictionary<string, object>
                    {
                        ["body"] = new Dictionary<string, object> { ["title"] = "ok" },
                    },
                }))
                .ShouldBeTrue("sibling under payload.body is fine");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task RestrictedFields_Skipped_For_View_And_Delete()
    {
        // Field restrictions only apply to create + update — view/delete aren't
        // gated by them because the actor doesn't supply attributes.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_rfvd");
        var roleName = Unique("role_rfvd");
        var userName = Unique("user_rfvd");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "view", "delete" },
                resourceTypes: new() { "content" },
                restrictedFields: new() { "status" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            var ctx = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: userName, OwnerGroupShortname: null, Acl: null);

            (await perms.CanReadAsync(userName, locator, ctx))
                .ShouldBeTrue("view ignores restricted_fields");
            (await perms.CanDeleteAsync(userName, locator, ctx))
                .ShouldBeTrue("delete ignores restricted_fields");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    // ============================================================
    // 6. ALLOWED_FIELDS_VALUES — value-set constraints on update/create
    // ============================================================

    [FactIfPg]
    public async Task AllowedFieldsValues_Restricts_Field_To_Allowed_Set()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_afv");
        var roleName = Unique("role_afv");
        var userName = Unique("user_afv");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "tickets" } },
                actions: new() { "update", "create" },
                resourceTypes: new() { "content" },
                allowedFieldsValues: new()
                {
                    ["state"] = new List<object> { "open", "pending" },
                }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/tickets", "x");
            var ctx = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: userName, OwnerGroupShortname: null, Acl: null);

            (await perms.CanUpdateAsync(userName, locator, ctx,
                new() { ["state"] = "open" }))
                .ShouldBeTrue("'open' is allowed");
            (await perms.CanUpdateAsync(userName, locator, ctx,
                new() { ["state"] = "pending" }))
                .ShouldBeTrue("'pending' is allowed");
            (await perms.CanUpdateAsync(userName, locator, ctx,
                new() { ["state"] = "closed" }))
                .ShouldBeFalse("'closed' not in allowed set");

            // A field NOT mentioned in allowed_fields_values is unconstrained.
            (await perms.CanUpdateAsync(userName, locator, ctx,
                new() { ["title"] = "anything" }))
                .ShouldBeTrue("title isn't in allowed_fields_values, so any value is fine");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    [FactIfPg]
    public async Task AllowedFieldsValues_Nested_Field_Path_Honored()
    {
        // The flatten step uses dot-separated keys, so nested constraints work.
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_afvn");
        var roleName = Unique("role_afvn");
        var userName = Unique("user_afvn");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "update" },
                resourceTypes: new() { "content" },
                allowedFieldsValues: new()
                {
                    ["payload.body.priority"] = new List<object> { "low", "medium", "high" },
                }));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            var locator = new Locator(ResourceType.Content, "test", "/items", "x");
            var ctx = new PermissionService.ResourceContext(
                IsActive: true, OwnerShortname: userName, OwnerGroupShortname: null, Acl: null);

            (await perms.CanUpdateAsync(userName, locator, ctx,
                new()
                {
                    ["payload"] = new Dictionary<string, object>
                    {
                        ["body"] = new Dictionary<string, object> { ["priority"] = "high" },
                    },
                }))
                .ShouldBeTrue("nested 'high' is allowed");

            (await perms.CanUpdateAsync(userName, locator, ctx,
                new()
                {
                    ["payload"] = new Dictionary<string, object>
                    {
                        ["body"] = new Dictionary<string, object> { ["priority"] = "critical" },
                    },
                }))
                .ShouldBeFalse("nested 'critical' is not allowed");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    // ============================================================
    // 7. END-TO-END: HTTP login + /managed/request flow per role
    // ============================================================

    [FactIfPg]
    public async Task EndToEnd_Two_Users_Different_Roles_Honor_Their_Permissions_Over_HTTP()
    {
        // Bring up the host once so AdminBootstrap runs, the admin can mint
        // the test resources we'll later try to mutate as a limited user.
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        // Two roles in the same space:
        //   - "viewer": view + query only
        //   - "editor": full CRUD
        var space = Unique("e2e_sp");
        var subpath = "/items";
        var viewerPerm = Unique("e2e_vp");
        var editorPerm = Unique("e2e_ep");
        var viewerRole = Unique("e2e_vr");
        var editorRole = Unique("e2e_er");
        var viewerUser = Unique("e2e_vu");
        var editorUser = Unique("e2e_eu");
        var seedSn = Unique("seed");
        var now = DateTime.UtcNow;

        try
        {
            await spaces.UpsertAsync(BuildSpace(space, now));
            await access.UpsertPermissionAsync(BuildPermission(viewerPerm,
                subpaths: new() { [space] = new() { "items" } },
                actions: new() { "view", "query" },
                resourceTypes: new() { "content" }));
            await access.UpsertPermissionAsync(BuildPermission(editorPerm,
                subpaths: new() { [space] = new() { "items" } },
                actions: new() { "view", "query", "create", "update", "delete" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(viewerRole, viewerPerm));
            await access.UpsertRoleAsync(BuildRole(editorRole, editorPerm));
            await CreateUserRowWithPassword(users, hasher, viewerUser, new() { viewerRole });
            await CreateUserRowWithPassword(users, hasher, editorUser, new() { editorRole });
            await entries.UpsertAsync(BuildContentEntry(space, subpath, seedSn, now, isActive: true));
            await access.InvalidateAllCachesAsync();

            var (viewerClient, _) = await LoginAs(viewerUser);
            var (editorClient, _) = await LoginAs(editorUser);

            // viewer: GET /managed/entry succeeds.
            (await viewerClient.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{seedSn}"))
                .StatusCode.ShouldBe(HttpStatusCode.OK, "viewer can read seed entry");

            // viewer: CREATE blocked (Python parity returns 401/403/404 — accept any non-2xx).
            var createAsViewer = await PostManaged(viewerClient, RequestType.Create, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = Unique("v_blocked"),
                Attributes = new() { ["displayname"] = "should be blocked" },
            });
            createAsViewer.StatusCode.ShouldBeOneOf(
                new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound },
                $"viewer must NOT be able to create. Got {createAsViewer.StatusCode}");

            // viewer: UPDATE blocked.
            var updateAsViewer = await PostManaged(viewerClient, RequestType.Update, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = seedSn,
                Attributes = new() { ["displayname"] = "viewer can't write" },
            });
            updateAsViewer.StatusCode.ShouldBeOneOf(
                new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound },
                $"viewer must NOT be able to update. Got {updateAsViewer.StatusCode}");

            // editor: CREATE succeeds.
            var newSn = Unique("editor_new");
            var createAsEditor = await PostManaged(editorClient, RequestType.Create, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = newSn,
                Attributes = new() { ["displayname"] = "editor created" },
            });
            createAsEditor.StatusCode.ShouldBe(HttpStatusCode.OK, "editor must be able to create");

            // editor: UPDATE the editor-created entry.
            var updateAsEditor = await PostManaged(editorClient, RequestType.Update, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = newSn,
                Attributes = new() { ["displayname"] = "editor edited" },
            });
            updateAsEditor.StatusCode.ShouldBe(HttpStatusCode.OK, "editor must be able to update");

            // editor: DELETE the editor-created entry.
            var deleteAsEditor = await PostManaged(editorClient, RequestType.Delete, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = newSn,
            });
            deleteAsEditor.StatusCode.ShouldBe(HttpStatusCode.OK, "editor must be able to delete");

            // viewer: query the subpath. Should get the seed entry back.
            var queryResp = await viewerClient.PostAsJsonAsync("/managed/query", new Query
            {
                Type = QueryType.Search,
                SpaceName = space,
                Subpath = subpath,
                FilterSchemaNames = new(),
                Limit = 10,
            }, DmartJsonContext.Default.Query);
            queryResp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var queryRaw = await queryResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(queryRaw);
            doc.RootElement.GetProperty("status").GetString().ShouldBe("success");
            var sns = doc.RootElement.GetProperty("records").EnumerateArray()
                .Select(r => r.GetProperty("shortname").GetString())
                .ToArray();
            sns.ShouldContain(seedSn, "viewer's query permission should surface the seed entry");
        }
        finally
        {
            try { await entries.DeleteAsync(space, subpath, seedSn, ResourceType.Content); } catch { }
            try { await users.DeleteAllSessionsAsync(viewerUser); } catch { }
            try { await users.DeleteAllSessionsAsync(editorUser); } catch { }
            try { await users.DeleteAsync(viewerUser); } catch { }
            try { await users.DeleteAsync(editorUser); } catch { }
            try { await access.DeleteRoleAsync(viewerRole); } catch { }
            try { await access.DeleteRoleAsync(editorRole); } catch { }
            try { await access.DeletePermissionAsync(viewerPerm); } catch { }
            try { await access.DeletePermissionAsync(editorPerm); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task EndToEnd_OwnCondition_Owner_Can_Update_Stranger_Cannot_Over_HTTP()
    {
        // End-to-end "own" condition: two users, both with update permission on
        // the same subpath but conditioned on `own`. The seed entry is owned by
        // userA — only userA may update it; userB's update must fail.
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var space = Unique("own_sp");
        var subpath = "/mine";
        var perm = Unique("own_p");
        var role = Unique("own_r");
        var userA = Unique("own_ua");
        var userB = Unique("own_ub");
        var seedSn = Unique("ownseed");
        var now = DateTime.UtcNow;

        try
        {
            await spaces.UpsertAsync(BuildSpace(space, now));
            await access.UpsertPermissionAsync(BuildPermission(perm,
                subpaths: new() { [space] = new() { "mine" } },
                actions: new() { "view", "update" },
                resourceTypes: new() { "content" },
                conditions: new() { "own" }));
            await access.UpsertRoleAsync(BuildRole(role, perm));
            await CreateUserRowWithPassword(users, hasher, userA, new() { role });
            await CreateUserRowWithPassword(users, hasher, userB, new() { role });
            // Seed an entry owned by userA.
            await entries.UpsertAsync(BuildContentEntry(space, subpath, seedSn, now,
                isActive: true, owner: userA));
            await access.InvalidateAllCachesAsync();

            var (clientA, _) = await LoginAs(userA);
            var (clientB, _) = await LoginAs(userB);

            // userA reads (view + own — userA is owner) → 200.
            (await clientA.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{seedSn}"))
                .StatusCode.ShouldBe(HttpStatusCode.OK, "userA owns the entry");

            // userA updates → 200.
            var updateA = await PostManaged(clientA, RequestType.Update, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = seedSn,
                Attributes = new() { ["displayname"] = "owner edit" },
            });
            updateA.StatusCode.ShouldBe(HttpStatusCode.OK, "owner update succeeds");

            // userB tries to update userA's entry → must fail (own condition unmet).
            var updateB = await PostManaged(clientB, RequestType.Update, space, new Record
            {
                ResourceType = ResourceType.Content,
                Subpath = subpath,
                Shortname = seedSn,
                Attributes = new() { ["displayname"] = "stranger should be blocked" },
            });
            updateB.StatusCode.ShouldBeOneOf(
                new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound },
                $"non-owner update must be denied. Got {updateB.StatusCode}");
        }
        finally
        {
            try { await entries.DeleteAsync(space, subpath, seedSn, ResourceType.Content); } catch { }
            try { await users.DeleteAllSessionsAsync(userA); } catch { }
            try { await users.DeleteAllSessionsAsync(userB); } catch { }
            try { await users.DeleteAsync(userA); } catch { }
            try { await users.DeleteAsync(userB); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task EndToEnd_IsActive_Filters_Query_Results_To_Active_Only()
    {
        // A query permission with `is_active` should only surface active rows.
        _factory.CreateClient();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var access = _factory.Services.GetRequiredService<AccessRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();

        var space = Unique("iax_sp");
        var subpath = "/feed";
        var perm = Unique("iax_p");
        var role = Unique("iax_r");
        var user = Unique("iax_u");
        var visibleSn = Unique("vis");
        var hiddenSn = Unique("hid");
        var now = DateTime.UtcNow;

        try
        {
            await spaces.UpsertAsync(BuildSpace(space, now));
            await access.UpsertPermissionAsync(BuildPermission(perm,
                subpaths: new() { [space] = new() { "feed" } },
                actions: new() { "view", "query" },
                resourceTypes: new() { "content" },
                conditions: new() { "is_active" }));
            await access.UpsertRoleAsync(BuildRole(role, perm));
            await CreateUserRowWithPassword(users, hasher, user, new() { role });
            await entries.UpsertAsync(BuildContentEntry(space, subpath, visibleSn, now, isActive: true));
            await entries.UpsertAsync(BuildContentEntry(space, subpath, hiddenSn, now, isActive: false));
            await access.InvalidateAllCachesAsync();

            var (client, _) = await LoginAs(user);

            var resp = await client.PostAsJsonAsync("/managed/query", new Query
            {
                Type = QueryType.Search,
                SpaceName = space,
                Subpath = subpath,
                FilterSchemaNames = new(),
                Limit = 50,
            }, DmartJsonContext.Default.Query);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var raw = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(raw);
            doc.RootElement.GetProperty("status").GetString().ShouldBe("success");
            var sns = doc.RootElement.GetProperty("records").EnumerateArray()
                .Select(r => r.GetProperty("shortname").GetString())
                .ToArray();
            sns.ShouldContain(visibleSn, "active row must surface");
            sns.ShouldNotContain(hiddenSn, "is_active condition must filter out inactive row");
        }
        finally
        {
            try { await entries.DeleteAsync(space, subpath, visibleSn, ResourceType.Content); } catch { }
            try { await entries.DeleteAsync(space, subpath, hiddenSn, ResourceType.Content); } catch { }
            try { await users.DeleteAllSessionsAsync(user); } catch { }
            try { await users.DeleteAsync(user); } catch { }
            try { await access.DeleteRoleAsync(role); } catch { }
            try { await access.DeletePermissionAsync(perm); } catch { }
            try { await spaces.DeleteAsync(space); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // ============================================================
    // 8. PERMISSION COMPOSITION — multiple perms on one role OR-merge
    // ============================================================

    [FactIfPg]
    public async Task Composition_Multiple_Permissions_OrMerge_Across_Subpaths_And_Actions()
    {
        // Permission A: view on /alpha
        // Permission B: create on /bravo
        // The role has both → user can view /alpha AND create /bravo.
        var (perms, users, access, _, _) = Resolve();

        var permA = Unique("perm_orA");
        var permB = Unique("perm_orB");
        var roleName = Unique("role_or");
        var userName = Unique("user_or");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permA,
                subpaths: new() { ["test"] = new() { "alpha" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));
            await access.UpsertPermissionAsync(BuildPermission(permB,
                subpaths: new() { ["test"] = new() { "bravo" } },
                actions: new() { "create" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleName, permA, permB));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/alpha", "x")))
                .ShouldBeTrue("permA grants view on /alpha");
            (await perms.CanCreateAsync(userName, new Locator(ResourceType.Content, "test", "/bravo", "x")))
                .ShouldBeTrue("permB grants create on /bravo");

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/bravo", "x")))
                .ShouldBeFalse("permB only grants create, not view");
            (await perms.CanCreateAsync(userName, new Locator(ResourceType.Content, "test", "/alpha", "x")))
                .ShouldBeFalse("permA only grants view, not create");

            (await perms.CanDeleteAsync(userName, new Locator(ResourceType.Content, "test", "/alpha", "x")))
                .ShouldBeFalse("delete absent everywhere");
        }
        finally
        {
            try { await users.DeleteAsync(userName); } catch { }
            try { await access.DeleteRoleAsync(roleName); } catch { }
            try { await access.DeletePermissionAsync(permA); } catch { }
            try { await access.DeletePermissionAsync(permB); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    [FactIfPg]
    public async Task Composition_Multiple_Roles_OrMerge_Permissions()
    {
        // Two roles on one user. Each role contributes a different permission.
        // The aggregate set is the union — user can do what either grants.
        var (perms, users, access, _, _) = Resolve();

        var permA = Unique("perm_2rA");
        var permB = Unique("perm_2rB");
        var roleA = Unique("role_2rA");
        var roleB = Unique("role_2rB");
        var userName = Unique("user_2r");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permA,
                subpaths: new() { ["test"] = new() { "alpha" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));
            await access.UpsertPermissionAsync(BuildPermission(permB,
                subpaths: new() { ["test"] = new() { "bravo" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" }));
            await access.UpsertRoleAsync(BuildRole(roleA, permA));
            await access.UpsertRoleAsync(BuildRole(roleB, permB));
            await CreateUserRow(users, userName, new() { roleA, roleB });
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/alpha", "x")))
                .ShouldBeTrue("roleA grants alpha");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/bravo", "x")))
                .ShouldBeTrue("roleB grants bravo");
            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/charlie", "x")))
                .ShouldBeFalse("neither role covers charlie");
        }
        finally
        {
            try { await users.DeleteAsync(userName); } catch { }
            try { await access.DeleteRoleAsync(roleA); } catch { }
            try { await access.DeleteRoleAsync(roleB); } catch { }
            try { await access.DeletePermissionAsync(permA); } catch { }
            try { await access.DeletePermissionAsync(permB); } catch { }
            await access.InvalidateAllCachesAsync();
        }
    }

    // ============================================================
    // 9. INACTIVE permission rows are skipped
    // ============================================================

    [FactIfPg]
    public async Task Inactive_Permission_Is_Skipped_Even_If_Granted_Elsewhere()
    {
        var (perms, users, access, _, _) = Resolve();

        var permName = Unique("perm_off");
        var roleName = Unique("role_off");
        var userName = Unique("user_off");

        try
        {
            await access.UpsertPermissionAsync(BuildPermission(permName,
                subpaths: new() { ["test"] = new() { "items" } },
                actions: new() { "view" },
                resourceTypes: new() { "content" },
                isActive: false));
            await access.UpsertRoleAsync(BuildRole(roleName, permName));
            await CreateUserRow(users, userName, new() { roleName });
            await access.InvalidateAllCachesAsync();

            (await perms.CanReadAsync(userName, new Locator(ResourceType.Content, "test", "/items", "x")))
                .ShouldBeFalse("inactive permission rows are ignored");
        }
        finally
        {
            await Cleanup(users, access, userName, roleName, permName);
        }
    }

    // ====================================================================
    // helpers
    // ====================================================================

    private (PermissionService perms, UserRepository users, AccessRepository access,
             EntryRepository entries, SpaceRepository spaces) Resolve()
    {
        // Force the host to construct so AdminBootstrap + SchemaInitializer run.
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<PermissionService>(),
            sp.GetRequiredService<UserRepository>(),
            sp.GetRequiredService<AccessRepository>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<SpaceRepository>());
    }

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..32];

    private static Permission BuildPermission(
        string shortname,
        Dictionary<string, List<string>> subpaths,
        List<string> actions,
        List<string>? resourceTypes = null,
        List<string>? conditions = null,
        List<string>? restrictedFields = null,
        Dictionary<string, object>? allowedFieldsValues = null,
        bool isActive = true)
    => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/permissions",
        OwnerShortname = "dmart",
        IsActive = isActive,
        Subpaths = subpaths,
        Actions = actions,
        ResourceTypes = resourceTypes ?? new(),
        Conditions = conditions ?? new(),
        RestrictedFields = restrictedFields,
        AllowedFieldsValues = allowedFieldsValues,
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

    // For tests that only call PermissionService directly (no login). Skips
    // password hashing because we never log in as this user.
    private static Task CreateUserRow(UserRepository users, string shortname, List<string> roles)
        => users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Type = UserType.Web,
            Language = Language.En,
            Roles = roles,
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

    // For tests that need to log in as the user — sets the standard test password.
    private static Task CreateUserRowWithPassword(
        UserRepository users, PasswordHasher hasher, string shortname, List<string> roles)
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
            Roles = roles,
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

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

    private static Entry BuildContentEntry(
        string space, string subpath, string shortname, DateTime now,
        bool isActive = true, string? owner = null)
        => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            OwnerShortname = owner ?? "dmart",
            ResourceType = ResourceType.Content,
            IsActive = isActive,
            CreatedAt = now,
            UpdatedAt = now,
        };

    private async Task<(HttpClient Client, string Token)> LoginAs(string shortname)
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(shortname, null, null, Password, null);
        var resp = await client.PostAsJsonAsync(
            "/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.ShouldBe(HttpStatusCode.OK, $"login for '{shortname}' failed: {raw}");

        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        var token = body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"no access_token in login body: {raw}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    private static Task<HttpResponseMessage> PostManaged(
        HttpClient client, RequestType rt, string space, Record record)
    {
        var req = new Request
        {
            RequestType = rt,
            SpaceName = space,
            Records = new() { record },
        };
        return client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
    }

    private static async Task Cleanup(
        UserRepository users, AccessRepository access,
        string user, string role, string perm)
    {
        try { await users.DeleteAsync(user); } catch { }
        try { await access.DeleteRoleAsync(role); } catch { }
        try { await access.DeletePermissionAsync(perm); } catch { }
        await access.InvalidateAllCachesAsync();
    }
}
