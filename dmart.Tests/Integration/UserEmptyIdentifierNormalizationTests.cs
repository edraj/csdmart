using System.Text;
using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Empty-string email/msisdn must be persisted as SQL NULL, not ''.
//
// The partial unique indexes on users (idx_users_email_lower_unique,
// idx_users_msisdn_unique) exclude NULLs but would treat '' as a real,
// collidable value — so without write-side normalization, the SECOND user
// saved with an empty email fails with a baffling 409 "resource with this
// email already exists". '' reaches the write path easily: admin UIs send
// `"email": ""` to mean "no email", and msisdn-only self-registration
// bodies often carry an empty email field.
public sealed class UserEmptyIdentifierNormalizationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserEmptyIdentifierNormalizationTests(DmartFactory factory) => _factory = factory;

    private static User NewUser(string shortname, string? email, string? msisdn) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/users",
        OwnerShortname = shortname,
        IsActive = true,
        Type = UserType.Web,
        Language = Language.En,
        Email = email,
        Msisdn = msisdn,
        Roles = new(), Groups = new(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [FactIfPg]
    public async Task Upsert_Persists_Empty_Email_And_Msisdn_As_Null()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"emptyid_{Guid.NewGuid():N}"[..16];
        try
        {
            await users.UpsertAsync(NewUser(shortname, email: "", msisdn: ""));
            var read = await users.GetByShortnameAsync(shortname);
            read.ShouldNotBeNull();
            read!.Email.ShouldBeNull("'' email must be normalized to NULL at the write boundary");
            read.Msisdn.ShouldBeNull("'' msisdn must be normalized to NULL at the write boundary");
        }
        finally
        {
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task Two_Users_With_Empty_Email_Coexist()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..10];
        var a = NewUser($"empt_a_{stamp}", email: "", msisdn: null);
        var b = NewUser($"empt_b_{stamp}", email: "", msisdn: null);
        try
        {
            await users.UpsertAsync(a);
            // Must not trip idx_users_email_lower_unique — '' is "absent",
            // not a shared identifier.
            await users.UpsertAsync(b);
            (await users.GetByShortnameAsync(a.Shortname)).ShouldNotBeNull();
            (await users.GetByShortnameAsync(b.Shortname)).ShouldNotBeNull();
        }
        finally
        {
            try { await users.DeleteAsync(a.Shortname); } catch { }
            try { await users.DeleteAsync(b.Shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task Two_Users_With_Empty_Msisdn_Coexist()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..10];
        var a = NewUser($"empm_a_{stamp}", email: null, msisdn: "");
        var b = NewUser($"empm_b_{stamp}", email: null, msisdn: "");
        try
        {
            await users.UpsertAsync(a);
            await users.UpsertAsync(b);
            (await users.GetByShortnameAsync(a.Shortname)).ShouldNotBeNull();
            (await users.GetByShortnameAsync(b.Shortname)).ShouldNotBeNull();
        }
        finally
        {
            try { await users.DeleteAsync(a.Shortname); } catch { }
            try { await users.DeleteAsync(b.Shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task Managed_Create_With_Empty_Email_Succeeds_Repeatedly()
    {
        // The wire-level version of the repo tests above: an admin creating
        // two users with `"email": ""` (the common "no email" form from
        // admin UIs) must not 409 on the second one.
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var first = $"empw_a_{stamp}";
        var second = $"empw_b_{stamp}";
        try
        {
            foreach (var shortname in new[] { first, second })
            {
                var body = "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{" +
                    "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
                    "\"attributes\":{\"is_active\":true,\"email\":\"\"}}]}";
                var resp = await admin.Client.PostAsync("/managed/request",
                    new StringContent(body, Encoding.UTF8, "application/json"));
                var raw = await resp.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);
                result!.Status.ShouldBe(Dmart.Models.Api.Status.Success,
                    $"create of {shortname} with empty email must succeed; got: {raw}");
                (await users.GetByShortnameAsync(shortname))!.Email
                    .ShouldBeNull("'' email must land as NULL");
            }
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(first); } catch { }
            try { await users.DeleteAsync(second); } catch { }
        }
    }
}
