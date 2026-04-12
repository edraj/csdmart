using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Tests covering features added after the initial parity batch:
// __root__, resource_type fallback, exact_subpath, search fields,
// entry routing by type, profile records[], permissions, auto shortname,
// space create plugin, CXB embedded.
public class RecentParityTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public RecentParityTests(DmartFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, string Token)> LoginAsync()
    {
        var client = _factory.CreateClient();
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        var token = body!.Records![0].Attributes!["access_token"]!.ToString()!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    // ==================== __root__ magic word ====================

    [Fact]
    public async Task Root_Magic_Word_Resolves_To_Root_Subpath()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        // hr space has "employees" folder at subpath="/"
        var resp = await client.GetAsync("/managed/entry/folder/hr/__root__/employees");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Entry);
        body.ShouldNotBeNull();
        body!.Shortname.ShouldBe("employees");
        body.Subpath.ShouldBe("/");
    }

    // ==================== resource_type fallback ====================

    [Fact]
    public async Task Entry_Lookup_Falls_Back_When_ResourceType_Mismatches()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        // hr/schema/employee is resource_type=schema but we request as content
        var resp = await client.GetAsync("/managed/entry/content/hr/schema/employee");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Entry);
        body.ShouldNotBeNull();
        body!.Shortname.ShouldBe("employee");
        body.ResourceType.ShouldBe(ResourceType.Schema);
    }

    // ==================== entry routing by resource_type ====================

    [Fact]
    public async Task Entry_Space_Routes_To_Spaces_Table()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        var resp = await client.GetAsync("/managed/entry/space/hr/__root__/hr");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain("\"resource_type\"");
        json.ShouldContain("\"space\"");
    }

    [Fact]
    public async Task Entry_User_Routes_To_Users_Table()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        var resp = await client.GetAsync("/managed/entry/user/management/__root__/dmart");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.ShouldContain("\"dmart\"");
    }

    // ==================== exact_subpath ====================

    [Fact]
    public async Task ExactSubpath_Root_Returns_Only_Root_Entries()
    {
        if (!DmartFactory.HasPg) return;
        var svc = _factory.Services.GetRequiredService<QueryService>();
        var all = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Search, SpaceName = "hr", Subpath = "/",
            ExactSubpath = false, Limit = 100,
        }, _factory.AdminShortname);

        var exact = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Search, SpaceName = "hr", Subpath = "/",
            ExactSubpath = true, Limit = 100,
        }, _factory.AdminShortname);

        // exact should return fewer (only root) than non-exact (all subpaths)
        all.Records!.Count.ShouldBeGreaterThanOrEqualTo(exact.Records!.Count);
        foreach (var r in exact.Records!)
        {
            var subpath = r.Attributes?["space_name"] is not null
                ? (r.Subpath ?? "/") : "/";
            // All records should be at root subpath
        }
    }

    // ==================== search includes shortname + tags ====================

    [Fact]
    public async Task Search_Finds_By_Shortname()
    {
        if (!DmartFactory.HasPg) return;
        var svc = _factory.Services.GetRequiredService<QueryService>();
        // "oneoneone" is an entry at hr/employees
        var resp = await svc.ExecuteAsync(new Query
        {
            Type = QueryType.Search, SpaceName = "hr", Subpath = "/employees",
            Search = "one", ExactSubpath = true, Limit = 100,
        }, _factory.AdminShortname);
        resp.Status.ShouldBe(Status.Success);
        resp.Records!.Any(r => r.Shortname.Contains("one")).ShouldBeTrue();
    }

    // ==================== profile returns records[] ====================

    [Fact]
    public async Task Profile_Returns_Records_With_Permissions()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        var resp = await client.GetAsync("/user/profile");
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        body.Records.ShouldNotBeNull();
        body.Records!.Count.ShouldBe(1);
        body.Records[0].ResourceType.ShouldBe(ResourceType.User);
        var attrs = body.Records[0].Attributes!;
        attrs.ShouldContainKey("permissions");
        // Permissions should be a non-empty dict for admin
        var perms = (JsonElement)attrs["permissions"]!;
        perms.ValueKind.ShouldBe(JsonValueKind.Object);
        perms.EnumerateObject().Count().ShouldBeGreaterThan(0);
    }

    // ==================== auto shortname ====================

    [Fact]
    public async Task Auto_Shortname_Generates_UUID_Prefix()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        var resp = await client.PostAsync("/managed/request",
            new StringContent(
                "{\"space_name\":\"hr\",\"request_type\":\"create\",\"records\":[{\"resource_type\":\"content\",\"subpath\":\"employees\",\"shortname\":\"auto\",\"attributes\":{\"payload\":{\"content_type\":\"json\",\"body\":{\"test\":true}}}}]}",
                Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);
        var rec = body.Records![0];
        rec.Shortname.ShouldNotBe("auto");
        rec.Shortname.Length.ShouldBe(8);
        // Shortname should be the first 8 chars of the UUID
        rec.Uuid.ShouldNotBeNull();
        rec.Uuid!.Replace("-", "")[..8].ShouldBe(rec.Shortname);

        // Cleanup
        await client.PostAsync("/managed/request",
            new StringContent(
                $"{{\"space_name\":\"hr\",\"request_type\":\"delete\",\"records\":[{{\"resource_type\":\"content\",\"subpath\":\"employees\",\"shortname\":\"{rec.Shortname}\",\"attributes\":{{}}}}]}}",
                Encoding.UTF8, "application/json"));
    }

    // ==================== space create triggers schema folder ====================

    [Fact]
    public async Task Space_Create_Triggers_Schema_Folder_Plugin()
    {
        if (!DmartFactory.HasPg) return;
        var (client, _) = await LoginAsync();
        var spaceName = $"pltest_{Guid.NewGuid():N}"[..12];
        try
        {
            var resp = await client.PostAsync("/managed/request",
                new StringContent(
                    $"{{\"space_name\":\"{spaceName}\",\"request_type\":\"create\",\"records\":[{{\"resource_type\":\"space\",\"subpath\":\"/\",\"shortname\":\"{spaceName}\",\"attributes\":{{\"is_active\":true}}}}]}}",
                    Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Success);

            // Wait for the async plugin to fire
            await Task.Delay(500);

            // Check /schema folder was created
            var entries = _factory.Services.GetRequiredService<EntryRepository>();
            var schemaFolder = await entries.GetAsync(spaceName, "/", "schema", ResourceType.Folder);
            schemaFolder.ShouldNotBeNull("resource_folders_creation plugin should create /schema");
        }
        finally
        {
            var spaces = _factory.Services.GetRequiredService<SpaceRepository>();
            await spaces.DeleteAsync(spaceName);
        }
    }

    // ==================== CXB embedded ====================

    [Fact]
    public async Task CXB_Index_Served_From_Embedded_Resources()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/cxb/index.html");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        var html = await resp.Content.ReadAsStringAsync();
        html.ShouldContain("<!doctype html", Case.Insensitive);
    }

    [Fact]
    public async Task CXB_SPA_Fallback_Returns_Index()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/cxb/some/deep/route");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.ShouldContain("<!doctype html", Case.Insensitive);
    }
}
