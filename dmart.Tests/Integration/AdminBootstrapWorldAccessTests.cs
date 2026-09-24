using Dmart.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Granting public access needs three rows that only work together: the
// `anonymous` user, a real `world` role row for it to resolve through, and the
// `world` permission that ResolvePermissionsAsync folds in. Bootstrap ships all
// three so operators don't hand-roll them, but ships them INERT — `world` has
// no subpaths, so it matches no space and grants nothing until scoped.
//
// The two invariants under test: the triple exists after a fresh boot, and
// bootstrap never widens or resets an operator's scope on an existing row.
public class AdminBootstrapWorldAccessTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public AdminBootstrapWorldAccessTests(DmartFactory factory) => _factory = factory;

    private (AccessRepository Access, UserRepository Users, AdminBootstrap Bootstrap) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<AccessRepository>(),
            sp.GetRequiredService<UserRepository>(),
            sp.GetServices<IHostedService>().OfType<AdminBootstrap>().Single());
    }

    [FactIfPg]
    public async Task Bootstrap_Creates_Inert_World_Permission_When_Missing()
    {
        var (access, _, bootstrap) = Resolve();
        var snapshot = await access.GetPermissionAsync("world");
        try
        {
            await access.DeletePermissionAsync("world");
            await bootstrap.StartAsync(default);

            var world = await access.GetPermissionAsync("world");
            world.ShouldNotBeNull("bootstrap must provision the world permission");
            world!.Subpaths.ShouldBeEmpty(
                "world must ship INERT — an empty subpaths map matches no space, so a fresh " +
                "deployment grants nothing publicly until an operator scopes it");
            world.Actions.ShouldBe(new List<string> { "view", "query" }, ignoreOrder: true);
            world.Conditions.ShouldContain("is_active");
            world.IsActive.ShouldBeTrue();
            world.SpaceName.ShouldBe("management");
            world.Subpath.ShouldBe("/permissions");
        }
        finally
        {
            if (snapshot is not null) await access.UpsertPermissionAsync(snapshot);
        }
    }

    // The seeded resource types decide what a space exposes the moment an
    // operator scopes it. Anything here that leaks identity or the authz model
    // turns "make /archive public" into "publish the user list".
    [FactIfPg]
    public async Task Bootstrap_World_Permission_Excludes_Identity_And_Authz_Resource_Types()
    {
        var (access, _, bootstrap) = Resolve();
        var snapshot = await access.GetPermissionAsync("world");
        try
        {
            await access.DeletePermissionAsync("world");
            await bootstrap.StartAsync(default);

            var world = await access.GetPermissionAsync("world");
            world.ShouldNotBeNull();
            foreach (var forbidden in new[]
                     { "user", "group", "role", "permission", "acl", "log", "history" })
            {
                world!.ResourceTypes.ShouldNotContain(forbidden,
                    $"a scoped world permission must never expose '{forbidden}' to anonymous callers");
            }
            world!.ResourceTypes.ShouldContain("content");
            world.ResourceTypes.ShouldContain("folder");
        }
        finally
        {
            if (snapshot is not null) await access.UpsertPermissionAsync(snapshot);
        }
    }

    [FactIfPg]
    public async Task Bootstrap_Creates_World_Role_Holding_World_Permission()
    {
        var (access, _, bootstrap) = Resolve();
        var snapshot = await access.GetRoleAsync("world");
        try
        {
            await access.DeleteRoleAsync("world");
            await bootstrap.StartAsync(default);

            var role = await access.GetRoleAsync("world");
            role.ShouldNotBeNull(
                "anonymous resolves world only via a real role row — ResolvePermissionsAsync " +
                "requires roles.Count > 0 before folding the world permission in");
            role!.Permissions.ShouldContain("world");
            role.IsActive.ShouldBeTrue();
            role.SpaceName.ShouldBe("management");
            role.Subpath.ShouldBe("/roles");
        }
        finally
        {
            if (snapshot is not null) await access.UpsertRoleAsync(snapshot);
        }
    }

    // A bootstrapped anonymous row must not become a login vector: no password
    // (LoginAsync bails on an empty stored hash) and no msisdn/email (the OTP
    // path derives its destination from user.Msisdn and bails when empty).
    [FactIfPg]
    public async Task Bootstrap_Creates_Credential_Less_Anonymous_User()
    {
        var (_, users, bootstrap) = Resolve();
        var snapshot = await users.GetByShortnameAsync("anonymous");
        try
        {
            await users.DeleteAsync("anonymous");
            await bootstrap.StartAsync(default);

            var anon = await users.GetByShortnameAsync("anonymous");
            anon.ShouldNotBeNull("bootstrap must provision the anonymous user");
            anon!.Roles.ShouldContain("world");
            anon.Password.ShouldBeNullOrEmpty("anonymous must carry no password — it is not a login");
            anon.Msisdn.ShouldBeNullOrEmpty("an msisdn would open the OTP login path");
            anon.Email.ShouldBeNullOrEmpty("an email would open the OTP login path");
            anon.Type.ShouldBe(UserType.Web);
            anon.SpaceName.ShouldBe("management");
            anon.Subpath.ShouldBe("/users");
        }
        finally
        {
            if (snapshot is not null) await users.UpsertAsync(snapshot);
        }
    }

    // The whole point of shipping this inert: the operator's scope is theirs.
    // A restart that silently reset subpaths would revoke public access; one
    // that widened them would publish more than was asked for.
    [FactIfPg]
    public async Task Bootstrap_Never_Touches_Existing_World_Permission_Scope()
    {
        var (access, _, bootstrap) = Resolve();
        var snapshot = await access.GetPermissionAsync("world");
        try
        {
            var seeded = (snapshot ?? new Permission
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = "world",
                SpaceName = "management",
                Subpath = "/permissions",
                OwnerShortname = "dmart",
                IsActive = true,
                CreatedAt = TimeUtils.Now(),
                UpdatedAt = TimeUtils.Now(),
            }) with
            {
                Subpaths = new() { ["archive"] = new() { "__all_subpaths__" } },
                Actions = new() { "view" },
            };
            await access.UpsertPermissionAsync(seeded);

            await bootstrap.StartAsync(default);

            var world = await access.GetPermissionAsync("world");
            world.ShouldNotBeNull();
            world!.Subpaths.ShouldContainKey("archive");
            world.Subpaths["archive"].ShouldContain("__all_subpaths__");
            world.Actions.ShouldBe(new List<string> { "view" },
                "bootstrap must not widen an operator's narrowed action list");
        }
        finally
        {
            if (snapshot is not null) await access.UpsertPermissionAsync(snapshot);
            else await access.DeletePermissionAsync("world");
        }
    }

    [FactIfPg]
    public async Task Bootstrap_Never_Touches_Existing_Anonymous_User()
    {
        var (_, users, bootstrap) = Resolve();
        var snapshot = await users.GetByShortnameAsync("anonymous");
        try
        {
            var seeded = (snapshot ?? new User
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = "anonymous",
                SpaceName = "management",
                Subpath = "/users",
                OwnerShortname = "dmart",
                Type = UserType.Web,
                IsActive = false,
                CreatedAt = TimeUtils.Now(),
                UpdatedAt = TimeUtils.Now(),
            }) with
            {
                Roles = new() { "operator_attached_role" },
            };
            await users.UpsertAsync(seeded);

            await bootstrap.StartAsync(default);

            var anon = await users.GetByShortnameAsync("anonymous");
            anon.ShouldNotBeNull();
            anon!.Roles.ShouldContain("operator_attached_role");
        }
        finally
        {
            if (snapshot is not null) await users.UpsertAsync(snapshot);
            else await users.DeleteAsync("anonymous");
        }
    }
}
