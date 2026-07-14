using System.Net.Http.Json;
using System.Text;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Self-registration (/user/create) honors the `tags` attribute — Python
// parity (Meta.from_record passes every Metas-base attribute) and consistency
// with the admin create path, which already accepted tags. Values go through
// the canonical AttrHelper parser, so they arrive trimmed with empty entries
// dropped.
public sealed class UserRegistrationTagsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserRegistrationTagsTests(DmartFactory factory) => _factory = factory;

    private (HttpClient Client, IServiceProvider Services) OtpOff()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.IsOtpForCreateRequired = false)));
        return (factory.CreateClient(), factory.Services);
    }

    [FactIfPg]
    public async Task Registration_Persists_Tags_Trimmed_And_Filtered()
    {
        var (client, services) = OtpOff();
        var users = services.GetRequiredService<UserRepository>();
        var email = "tags_" + Guid.NewGuid().ToString("N")[..6] + "@x.yz";
        var body = "{\"attributes\":{\"email\":\"" + email + "\",\"password\":\"Testtest1234\"," +
                   "\"tags\":[\"vip\",\" beta \",\"\"]}}";

        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Success, $"got: {result.Error?.Message}");
        var shortname = result.Records![0].Shortname;
        try
        {
            var created = await users.GetByShortnameAsync(shortname);
            created.ShouldNotBeNull();
            // Tags must be honored, trimmed, and empty entries dropped.
            created!.Tags.ShouldBe(new[] { "vip", "beta" });
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(services, shortname);
        }
    }

    [FactIfPg]
    public async Task Registration_Without_Tags_Yields_Empty_List()
    {
        var (client, services) = OtpOff();
        var users = services.GetRequiredService<UserRepository>();
        var email = "notags_" + Guid.NewGuid().ToString("N")[..6] + "@x.yz";
        var body = "{\"attributes\":{\"email\":\"" + email + "\",\"password\":\"Testtest1234\"}}";

        var resp = await client.PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Success);
        var shortname = result.Records![0].Shortname;
        try
        {
            (await users.GetByShortnameAsync(shortname))!.Tags.ShouldBeEmpty();
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(services, shortname);
        }
    }
}
