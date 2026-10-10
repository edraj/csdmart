using System.Text;
using System.Text.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Json;
using Dmart.Services;
using Dmart.Tests.Infrastructure;
using Dmart.Utils;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Per-service delegation (USER_SERVICE_GRANTERS): who may grant `mail` versus
// `gitea`, on the model of a role's grantable_by. The actor here may create
// and update users in management/users, scoped so it is NOT a global admin;
// its role is listed as a granter of `mail` and of nothing else.
public sealed class ServiceDelegationTests(DmartFactory factory) : IClassFixture<DmartFactory>, IAsyncLifetime
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..6];
    private string Role => "svcgrant_role_" + _suffix;
    private string Perm => "svcgrant_perm_" + _suffix;
    private WebApplicationFactory<Program> _host = null!;

    public async Task InitializeAsync()
    {
        if (!DmartFactory.HasPg) return;
        _host = factory.WithWebHostBuilder(b => b.ConfigureServices(s => s.Configure<DmartSettings>(o =>
        {
            o.UserServices = "mail,matrix,gitea";
            o.UserServiceGranters = $"mail:{Role}";
        })));
        var access = _host.Services.GetRequiredService<AccessRepository>();
        await access.UpsertPermissionAsync(new Permission
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = Perm, SpaceName = "management", Subpath = "/permissions",
            OwnerShortname = "dmart", IsActive = true,
            Subpaths = new() { ["management"] = new() { "users" } },
            ResourceTypes = new() { "user" },
            Actions = new() { "view", "query", "create", "update" },
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });
        await access.UpsertRoleAsync(new Role
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = Role, SpaceName = "management", Subpath = "/roles",
            OwnerShortname = "dmart", IsActive = true, Permissions = new() { Perm },
            CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });
        await access.InvalidateAllCachesAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is null) return;
        var access = _host.Services.GetRequiredService<AccessRepository>();
        try { await access.DeleteRoleAsync(Role); } catch { }
        try { await access.DeletePermissionAsync(Perm); } catch { }
        await access.InvalidateAllCachesAsync();
        await _host.DisposeAsync();
    }

    private static async Task<(bool Ok, string Raw)> ManagedAsync(HttpClient client, string requestType, string shortname, string attributesJson)
    {
        var body = $$"""
            {"space_name":"management","request_type":"{{requestType}}","records":[
              {"resource_type":"user","subpath":"users","shortname":"{{shortname}}","attributes":{{attributesJson}}}]}
            """;
        var resp = await client.PostAsync("/managed/request", new StringContent(body, Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        return (JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response)!.Status == Status.Success, raw);
    }

    [FactIfPg]
    public async Task A_Delegate_Grants_Only_The_Services_Its_Role_Is_Listed_For()
    {
        var users = _host.Services.GetRequiredService<UserRepository>();
        var perms = _host.Services.GetRequiredService<PermissionService>();
        var actor = await factory.CreateLoggedInUserAsync(_host, roles: new() { Role });
        var target = "svcg_a_" + _suffix;
        try
        {
            (await perms.IsGlobalAdminAsync(actor.Shortname)).ShouldBeFalse("the test needs a non-admin actor");

            var (ok, raw) = await ManagedAsync(actor.Client, "create", target, """{"is_active":true,"services":["gitea"]}""");
            ok.ShouldBeFalse(raw);
            raw.ShouldContain("not permitted to grant or revoke service: gitea");
            (await users.GetByShortnameAsync(target)).ShouldBeNull();

            (ok, raw) = await ManagedAsync(actor.Client, "create", target, """{"is_active":true,"services":["mail"]}""");
            ok.ShouldBeTrue(raw);
            (await users.GetByShortnameAsync(target))!.Services.ShouldBe(new[] { "mail" });
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(_host.Services, target);
            await actor.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Only_The_Change_Is_Checked_So_Other_Services_Ride_Along()
    {
        var users = _host.Services.GetRequiredService<UserRepository>();
        var admin = await factory.CreateLoggedInUserAsync(_host);
        var actor = await factory.CreateLoggedInUserAsync(_host, roles: new() { Role });
        var target = "svcg_b_" + _suffix;
        try
        {
            // An admin gave the user gitea, which the delegate cannot grant.
            (await ManagedAsync(admin.Client, "create", target, """{"is_active":true,"services":["gitea"]}""")).Ok.ShouldBeTrue();

            // Turning mail on and off leaves gitea in the list without needing it.
            var (ok, raw) = await ManagedAsync(actor.Client, "update", target, """{"services":["gitea","mail"]}""");
            ok.ShouldBeTrue(raw);
            (ok, raw) = await ManagedAsync(actor.Client, "update", target, """{"services":["gitea"]}""");
            ok.ShouldBeTrue(raw);
            // An update that does not mention services is not a change at all.
            (ok, raw) = await ManagedAsync(actor.Client, "update", target, """{"displayname":{"en":"Renamed"}}""");
            ok.ShouldBeTrue(raw);

            // Revoking gitea is a change to gitea, and refused.
            (ok, raw) = await ManagedAsync(actor.Client, "update", target, """{"services":[]}""");
            ok.ShouldBeFalse(raw);
            raw.ShouldContain("not permitted to grant or revoke service: gitea");
            (await users.GetByShortnameAsync(target))!.Services.ShouldBe(new[] { "gitea" });

            // The global admin is not limited by the list.
            (await ManagedAsync(admin.Client, "update", target, """{"services":["matrix"]}""")).Ok.ShouldBeTrue();
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(_host.Services, target);
            await actor.Cleanup();
            await admin.Cleanup();
        }
    }
}
