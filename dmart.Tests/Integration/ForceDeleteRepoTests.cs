using System.Net;
using System.Net.Http.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

public sealed class ForceDeleteRepoTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ForceDeleteRepoTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task DeleteFolderTree_Returns_Refs_For_Folder_And_Children()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var client = caller.Client;
        var repo = _factory.Services.GetRequiredService<EntryRepository>();
        var space = "test";
        var folder = $"f{Guid.NewGuid():N}"[..12];

        async Task Create(ResourceType rt, string subpath, string sn) =>
            (await client.PostAsJsonAsync("/managed/request", new Request
            {
                RequestType = RequestType.Create, SpaceName = space,
                Records = new() { new Record { ResourceType = rt, Subpath = subpath, Shortname = sn } },
            }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        await Create(ResourceType.Folder, "/", folder);
        await Create(ResourceType.Content, $"/{folder}", "c1");

        var refs = await repo.DeleteFolderTreeWithDependentsAsync(space, "/", folder);

        refs.Select(r => r.ToPath()).ShouldContain($"{space}/{folder}");
        refs.Select(r => r.ToPath()).ShouldContain($"{space}/{folder}/c1");
        await caller.Cleanup();
    }

    [FactIfPg]
    public async Task DeleteAsync_NonEmptyFolder_NoForce_Fails()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var client = caller.Client;
        var svc = _factory.Services.GetRequiredService<Dmart.Services.EntryService>();
        var space = "test";
        var folder = $"f{Guid.NewGuid():N}"[..12];

        async Task Create(ResourceType rt, string subpath, string sn) =>
            (await client.PostAsJsonAsync("/managed/request", new Request
            {
                RequestType = RequestType.Create, SpaceName = space,
                Records = new() { new Record { ResourceType = rt, Subpath = subpath, Shortname = sn } },
            }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        await Create(ResourceType.Folder, "/", folder);
        await Create(ResourceType.Content, $"/{folder}", "c1");

        var locator = new Locator(ResourceType.Folder, space, "/", folder);
        var res = await svc.DeleteAsync(locator, caller.Shortname, force: false);
        res.IsOk.ShouldBeFalse();
        res.ErrorCode.ShouldBe(Dmart.Models.Api.InternalErrorCode.CANNT_DELETE);

        // force=true succeeds and reports refs
        var forced = await svc.DeleteAsync(locator, caller.Shortname, force: true);
        forced.IsOk.ShouldBeTrue();
        forced.Value!.Select(r => r.ToPath()).ShouldContain($"{space}/{folder}/c1");
        await caller.Cleanup();
    }

    [FactIfPg]
    public async Task DeleteAsync_EmptyFolder_NoForce_Succeeds()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var client = caller.Client;
        var svc = _factory.Services.GetRequiredService<Dmart.Services.EntryService>();
        var space = "test";
        var folder = $"f{Guid.NewGuid():N}"[..12];
        (await client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = space,
            Records = new() { new Record { ResourceType = ResourceType.Folder, Subpath = "/", Shortname = folder } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        var res = await svc.DeleteAsync(new Locator(ResourceType.Folder, space, "/", folder), caller.Shortname, force: false);
        res.IsOk.ShouldBeTrue();
        res.Value!.Select(r => r.ToPath()).ShouldContain($"{space}/{folder}");
        await caller.Cleanup();
    }

    [FactIfPg]
    public async Task OwnsAnyRecords_True_When_User_Created_Entry()
    {
        var owner = await _factory.CreateLoggedInUserAsync();   // logged-in user owns nothing yet
        var users = _factory.Services.GetRequiredService<UserRepository>();
        (await users.OwnsAnyRecordsAsync(owner.Shortname)).ShouldBeFalse();

        (await owner.Client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = "test",
            Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = "/itest",
                Shortname = $"o{Guid.NewGuid():N}"[..12] } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        (await users.OwnsAnyRecordsAsync(owner.Shortname)).ShouldBeTrue();
        await owner.Cleanup();
    }

    [FactIfPg]
    public async Task OwnsSpaceAsync_Returns_True_For_Owned_Space_And_False_For_Unknown()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
        var spaceName = $"itest_owns_{Guid.NewGuid():N}"[..16];

        try
        {
            // False path — space does not exist yet.
            (await users.OwnsSpaceAsync(caller.Shortname, "no_such_space_xyz")).ShouldBeFalse();

            // Create a space owned by the logged-in test user.
            var createReq = new Request
            {
                RequestType = RequestType.Create,
                SpaceName = "management",
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Space,
                        Subpath = "/",
                        Shortname = spaceName,
                        Attributes = new() { ["is_active"] = true },
                    },
                },
            };
            var resp = await caller.Client.PostAsJsonAsync("/managed/request", createReq, DmartJsonContext.Default.Request);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

            // True path — caller now owns that space.
            (await users.OwnsSpaceAsync(caller.Shortname, spaceName)).ShouldBeTrue();

            // False path again — a different (non-existent) space name still returns false.
            (await users.OwnsSpaceAsync(caller.Shortname, "no_such_space_xyz")).ShouldBeFalse();
        }
        finally
        {
            try { await spaces.DeleteAsync(spaceName); } catch { }
            await caller.Cleanup();
        }
    }

    [FactIfPg]
    public async Task ForceDelete_Removes_User_And_Owned_Entries()
    {
        var owner = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var sn = $"e{Guid.NewGuid():N}"[..12];
        (await owner.Client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = "test",
            Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = "/itest", Shortname = sn } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        var deleted = await users.ForceDeleteAsync(owner.Shortname);

        (await users.GetByShortnameAsync(owner.Shortname)).ShouldBeNull();
        deleted.Select(r => r.ToPath()).ShouldContain($"test/itest/{sn}");
        // entry is gone — owner.Client returns 401 after user deletion (auth middleware
        // rejects requests for non-existent users, see FullParityTests), so verify via repo.
        (await entries.GetAsync("test", "/itest", sn, ResourceType.Content)).ShouldBeNull();
    }
}
