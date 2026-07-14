using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Managed user UPDATE applies the email/msisdn format gate only to CHANGED
// values. Legacy rows can predate the format regex (or a stricter override);
// a full-record update that echoes the stored value back — the standard
// read-modify-write client pattern — must not be rejected over a field the
// caller didn't touch. New/changed values are still validated.
public sealed class ManagedUserUpdateFormatTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagedUserUpdateFormatTests(DmartFactory factory) => _factory = factory;

    // "@x.y" fails the default email regex (TLD must be 2+ chars); the
    // 5-digit msisdn fails the 6-digit floor. Both are seeded straight
    // through the repo, as legacy rows would be.
    private static User LegacyUser(string shortname) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/users",
        OwnerShortname = shortname,
        IsActive = true,
        Type = UserType.Web,
        Language = Language.En,
        Email = $"{shortname}@x.y",
        Msisdn = null,
        Roles = new(), Groups = new(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static string UpdateBody(string shortname, string attrsJson) =>
        "{\"space_name\":\"management\",\"request_type\":\"update\",\"records\":[{" +
        "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
        "\"attributes\":{" + attrsJson + "}}]}";

    private async Task<(Response Result, string Raw)> PostUpdateAsync(
        HttpClient client, string shortname, string attrsJson)
    {
        var resp = await client.PostAsync("/managed/request",
            new StringContent(UpdateBody(shortname, attrsJson), Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        return (JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response)!, raw);
    }

    [FactIfPg]
    public async Task Update_Echoing_Unchanged_Legacy_Email_Succeeds()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"legup_{Guid.NewGuid():N}"[..14];
        var legacy = LegacyUser(shortname);
        try
        {
            await users.UpsertAsync(legacy);

            var (result, raw) = await PostUpdateAsync(admin.Client, shortname,
                "\"email\":\"" + legacy.Email + "\",\"displayname\":{\"en\":\"Renamed\"}");

            result.Status.ShouldBe(Status.Success,
                $"echoing the stored (pre-regex) email back unchanged must not fail format validation; got: {raw}");
            var updated = await users.GetByShortnameAsync(shortname);
            updated!.Displayname?.En.ShouldBe("Renamed");
            updated.Email.ShouldBe(legacy.Email, "the legacy email itself is untouched");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task Update_Changing_Email_To_Invalid_Value_Is_Rejected()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"badup_{Guid.NewGuid():N}"[..14];
        try
        {
            await users.UpsertAsync(LegacyUser(shortname));

            var (result, raw) = await PostUpdateAsync(admin.Client, shortname,
                "\"email\":\"still not an email\"");

            result.Status.ShouldBe(Status.Failed, $"a CHANGED malformed email must be rejected; got: {raw}");
            // Managed responses wrap per-record errors under error.info.failed[].
            raw.ShouldContain("Email format is invalid");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task Update_Changing_Msisdn_To_Invalid_Value_Is_Rejected()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"badms_{Guid.NewGuid():N}"[..14];
        try
        {
            await users.UpsertAsync(LegacyUser(shortname));

            var (result, raw) = await PostUpdateAsync(admin.Client, shortname,
                "\"msisdn\":\"+96478abc678\"");

            result.Status.ShouldBe(Status.Failed, $"a CHANGED malformed msisdn must be rejected; got: {raw}");
            raw.ShouldContain("MSISDN format is invalid");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }
}
