using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

public sealed class ForceDeleteEndpointTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ForceDeleteEndpointTests(DmartFactory factory) => _factory = factory;

    private static Request Del(string space, ResourceType rt, string subpath, string sn, bool force) => new()
    {
        RequestType = RequestType.Delete, SpaceName = space, Force = force,
        Records = new() { new Record { ResourceType = rt, Subpath = subpath, Shortname = sn } },
    };

    [FactIfPg]
    public async Task NonEmpty_Folder_Without_Force_Fails_And_With_Force_Lists_Deleted()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var client = caller.Client;
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

        // No force → logical failure, folder still present. (Assert on the parsed
        // envelope, not the HTTP code: per-record failures are wrapped in a
        // top-level SOMETHING_WRONG aggregate; the HTTP status mapping is not
        // asserted here to avoid coupling to it.)
        var noForce = await client.PostAsJsonAsync("/managed/request",
            Del(space, ResourceType.Folder, "/", folder, force: false), DmartJsonContext.Default.Request);
        var noForceBody = JsonSerializer.Deserialize(await noForce.Content.ReadAsStringAsync(), DmartJsonContext.Default.Response)!;
        noForceBody.Status.ShouldBe(Status.Failed);
        (await client.GetAsync($"/managed/entry/folder/{space}/{folder}")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Force → success, response lists the deleted records.
        var forced = await client.PostAsJsonAsync("/managed/request",
            Del(space, ResourceType.Folder, "/", folder, force: true), DmartJsonContext.Default.Request);
        forced.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize(await forced.Content.ReadAsStringAsync(), DmartJsonContext.Default.Response)!;
        var deleted = (body.Records!.Single().Attributes!["deleted"] as JsonElement?)!.Value;
        deleted.EnumerateArray().Select(e => e.GetString()).ShouldContain($"{space}/{folder}/c1");
        (await client.GetAsync($"/managed/entry/folder/{space}/{folder}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await caller.Cleanup();
    }

    [FactIfPg]
    public async Task Force_False_Delete_Returns_Deleted_List()
    {
        var caller = await _factory.CreateLoggedInUserAsync();
        var client = caller.Client;
        var space = "test";
        var sn = $"itest_{Guid.NewGuid():N}"[..16];
        (await client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = space,
            Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = "/itest", Shortname = sn } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync("/managed/request",
            Del(space, ResourceType.Content, "/itest", sn, force: false), DmartJsonContext.Default.Request);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize(await resp.Content.ReadAsStringAsync(), DmartJsonContext.Default.Response)!;
        var deleted = (body.Records!.Single().Attributes!["deleted"] as JsonElement?)!.Value;
        deleted.EnumerateArray().Select(e => e.GetString()).ShouldContain($"test/itest/{sn}");
        await caller.Cleanup();
    }

    [FactIfPg]
    public async Task DeleteUser_With_Records_NoForce_Fails_ForceCascades()
    {
        var admin = await _factory.CreateLoggedInUserAsync();         // super_admin
        var victim = await _factory.CreateLoggedInUserAsync();        // will own an entry
        var sn = $"v{Guid.NewGuid():N}"[..12];
        (await victim.Client.PostAsJsonAsync("/managed/request", new Request
        {
            RequestType = RequestType.Create, SpaceName = "test",
            Records = new() { new Record { ResourceType = ResourceType.Content, Subpath = "/itest", Shortname = sn } },
        }, DmartJsonContext.Default.Request)).EnsureSuccessStatusCode();

        // force=false → friendly failure, user still present.
        var noForce = await admin.Client.PostAsJsonAsync("/managed/request",
            Del("management", ResourceType.User, "/users", victim.Shortname, force: false),
            DmartJsonContext.Default.Request);
        var noForceBody = JsonSerializer.Deserialize(await noForce.Content.ReadAsStringAsync(), DmartJsonContext.Default.Response)!;
        noForceBody.Status.ShouldBe(Status.Failed);
        var users = _factory.Services.GetRequiredService<Dmart.DataAdapters.Sql.UserRepository>();
        (await users.GetByShortnameAsync(victim.Shortname)).ShouldNotBeNull();

        // force=true → user + entry gone, response lists deleted records.
        var forced = await admin.Client.PostAsJsonAsync("/managed/request",
            Del("management", ResourceType.User, "/users", victim.Shortname, force: true),
            DmartJsonContext.Default.Request);
        forced.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize(await forced.Content.ReadAsStringAsync(), DmartJsonContext.Default.Response)!;
        var deleted = (body.Records!.Single().Attributes!["deleted"] as JsonElement?)!.Value;
        deleted.EnumerateArray().Select(e => e.GetString()).ShouldContain($"test/itest/{sn}");
        (await users.GetByShortnameAsync(victim.Shortname)).ShouldBeNull();
        await admin.Cleanup();
    }
}
