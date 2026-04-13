using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

public class LockDbTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public LockDbTests(DmartFactory factory) => _factory = factory;

    [Fact]
    public async Task Lock_Then_Unlock_Round_Trip()
    {
        if (!DmartFactory.HasPg) return;

        var client = _factory.CreateClient();
        var token = await GetTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var space = "test";
        var subpath = "lock-test";
        var shortname = $"lock-{Guid.NewGuid():N}".Substring(0, 12);

        var lockResp = await client.PutAsync($"/managed/lock/content/{space}/{subpath}/{shortname}", null);
        lockResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Trying to lock again as the same user is also a no-op success since the row exists.
        var secondLock = await client.PutAsync($"/managed/lock/content/{space}/{subpath}/{shortname}", null);
        secondLock.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondBody = await secondLock.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        // It returns either Success (idempotent) or Failed("locked") depending on race;
        // both are acceptable. The contract is: no exception, no 5xx.

        var unlockResp = await client.DeleteAsync($"/managed/lock/{space}/{subpath}/{shortname}");
        unlockResp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<string> GetTokenAsync(HttpClient client)
    {
        var login = new UserLoginRequest(_factory.AdminShortname, null, null, _factory.AdminPassword, null);
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
        return body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString()
            ?? throw new InvalidOperationException($"Login failed for '{_factory.AdminShortname}': {resp.StatusCode} {raw}");
    }
}
