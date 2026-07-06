using System;
using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Counterpart to ManagedUserPasswordRejectedTests. That suite pins the SECURE
// DEFAULT (IsPasswordUpdatableByOtherUser=false → a password on /managed/request
// is rejected). This suite pins the OPT-IN behaviour: with the flag ON, an
// authorized admin MAY set another user's password via /managed/request, the
// password is validated against PasswordRules and persisted Argon2-hashed, and
// force_password_change is cleared. Reads persisted state through the shared
// Postgres (both hosts point at the same DB).
public sealed class ManagedUserPasswordUpdatableWhenEnabledTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagedUserPasswordUpdatableWhenEnabledTests(DmartFactory factory) => _factory = factory;

    // Flag-on host: IsPasswordUpdatableByOtherUser = true.
    private WebApplicationFactory<Program> PwUpdatableHost() =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s => s.IsPasswordUpdatableByOtherUser = true)));

    private static string CreateBody(string shortname, string? password) =>
        "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{" +
        "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
        "\"attributes\":{\"is_active\":true,\"email\":\"" + shortname + "@x.y\"" +
        (password is null ? "" : ",\"password\":\"" + password + "\"") + "}}]}";

    [FactIfPg]
    public async Task ManagedRequest_Create_User_With_Password_Is_Accepted_When_Flag_On()
    {
        var host = PwUpdatableHost();
        var admin = await _factory.CreateLoggedInUserAsync(host: host);
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        var shortname = $"itestpw{Guid.NewGuid():N}"[..14];
        try
        {
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(CreateBody(shortname, "Provision1234"), Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Success, $"create with a password must succeed when the flag is on; got: {raw}");
            var created = await users.GetByShortnameAsync(shortname);
            created.ShouldNotBeNull();
            created!.Password.ShouldNotBeNull("the admin-provisioned password must be persisted");
            hasher.Verify("Provision1234", created.Password!).ShouldBeTrue("the stored hash must verify against the supplied password");
            created.ForcePasswordChange.ShouldBeFalse("a provisioned password means there is nothing to force-change");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_With_Weak_Password_Is_Rejected_When_Flag_On()
    {
        // The opt-in path still enforces PasswordRules (8-64, >=1 digit, >=1 upper).
        var host = PwUpdatableHost();
        var admin = await _factory.CreateLoggedInUserAsync(host: host);
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = $"itestpw{Guid.NewGuid():N}"[..14];
        try
        {
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(CreateBody(shortname, "nouppercase123"), Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Failed, $"a password failing PasswordRules must be rejected; got: {raw}");
            raw.ShouldContain("password does not meet the required rules");
            (await users.GetByShortnameAsync(shortname))
                .ShouldBeNull("the user must not be persisted when the password is rejected");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Update_User_With_Password_Is_Accepted_When_Flag_On()
    {
        var host = PwUpdatableHost();
        var admin = await _factory.CreateLoggedInUserAsync(host: host);
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        var shortname = $"itestpw{Guid.NewGuid():N}"[..14];
        try
        {
            var originalHash = hasher.Hash("Original1234");
            await users.UpsertAsync(new User
            {
                Uuid = Guid.NewGuid().ToString(),
                Shortname = shortname,
                SpaceName = "management",
                Subpath = "/users",
                OwnerShortname = "dmart",
                IsActive = true,
                Password = originalHash,
                ForcePasswordChange = true,
                Language = Language.En,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            var body =
                "{\"space_name\":\"management\",\"request_type\":\"update\",\"records\":[{" +
                "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
                "\"attributes\":{\"password\":\"Rotated9876\"}}]}";
            var resp = await admin.Client.PostAsync("/managed/request",
                new StringContent(body, Encoding.UTF8, "application/json"));
            var raw = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response);

            result!.Status.ShouldBe(Status.Success, $"update with a password must succeed when the flag is on; got: {raw}");
            var updated = await users.GetByShortnameAsync(shortname);
            updated!.Password.ShouldNotBe(originalHash, "the stored hash must be replaced by the new password");
            hasher.Verify("Rotated9876", updated.Password!).ShouldBeTrue("the stored hash must verify against the new password");
            updated.ForcePasswordChange.ShouldBeFalse("a freshly admin-set password clears force_password_change");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }
}
