using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Narrows #96: /managed/request may set an INITIAL password when CREATING a user
// (an admin provisioning an account), but must never CHANGE one on update — an
// admin form that loaded the stored $argon2id hash would otherwise post it back
// and have it re-hashed, locking the user out. A rotation flows only through the
// OTP password-reset and self-service /user/profile.
//
// On create the password is optional: absent/empty keeps the account passwordless
// with force_password_change=true, exactly as before this was reintroduced.
public sealed class ManagedUserPasswordTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ManagedUserPasswordTests(DmartFactory factory) => _factory = factory;

    private static string CreateBody(string shortname, string? extraAttrs = null) =>
        "{\"space_name\":\"management\",\"request_type\":\"create\",\"records\":[{" +
        "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
        "\"attributes\":{\"is_active\":true,\"email\":\"" + shortname + "@x.yz\"" +
        (extraAttrs is null ? "" : "," + extraAttrs) + "}}]}";

    private static string Shortname() => $"itestpw{Guid.NewGuid():N}"[..14];

    private static async Task<(Response? Result, string Raw)> PostAsync(
        DmartFactory.TestUser admin, string body)
    {
        var resp = await admin.Client.PostAsync("/managed/request",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var raw = await resp.Content.ReadAsStringAsync();
        return (JsonSerializer.Deserialize(raw, DmartJsonContext.Default.Response), raw);
    }

    // Drills into the aggregate failure envelope for the first failed record's
    // error_code, which is where /managed/request puts the actual reason.
    private static int? PerRecordErrorCode(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("error", out var err)
            || !err.TryGetProperty("info", out var info)
            || info.GetArrayLength() == 0
            || !info[0].TryGetProperty("failed", out var failed)
            || failed.GetArrayLength() == 0
            || !failed[0].TryGetProperty("error_code", out var code))
            return null;
        return code.GetInt32();
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_With_Password_Is_Hashed_And_Verifiable()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        var shortname = Shortname();
        try
        {
            var (result, raw) = await PostAsync(admin,
                CreateBody(shortname, "\"password\":\"Provision1234\""));

            result!.Status.ShouldBe(Status.Success, $"create with a valid password must succeed; got: {raw}");
            var created = await users.GetByShortnameAsync(shortname);
            created.ShouldNotBeNull();
            created!.Password.ShouldNotBeNull("an admin-supplied password must be persisted");
            created.Password!.StartsWith("$argon2id$", StringComparison.Ordinal)
                .ShouldBeTrue("the password must be stored hashed, never in clear");
            hasher.Verify("Provision1234", created.Password!)
                .ShouldBeTrue("the stored hash must verify against the supplied plaintext");
            created.ForcePasswordChange.ShouldBeFalse(
                "an admin-supplied password with no force_password_change flag is a real password");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_With_Password_Never_Echoes_It_Back()
    {
        // The create response round-trips the record's attributes. User.Password is
        // [JsonIgnore], but the echo is built from the REQUEST attributes, so guard
        // that neither the plaintext nor a hash leaks into the response body.
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = Shortname();
        try
        {
            var (result, raw) = await PostAsync(admin,
                CreateBody(shortname, "\"password\":\"Provision1234\""));

            result!.Status.ShouldBe(Status.Success, $"create must succeed; got: {raw}");
            raw.Contains("Provision1234", StringComparison.Ordinal)
                .ShouldBeFalse("the create response must not echo the plaintext password");
            raw.Contains("$argon2id$", StringComparison.Ordinal)
                .ShouldBeFalse("the create response must not leak the password hash");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_With_Weak_Password_Is_Rejected()
    {
        // "weakpass" clears the 8-char floor but has no digit and no uppercase, so
        // it fails PasswordRules — the same policy /user/profile and the OTP reset
        // enforce. The gate runs before the upsert, so nothing is persisted.
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = Shortname();
        try
        {
            var (result, raw) = await PostAsync(admin,
                CreateBody(shortname, "\"password\":\"weakpass\""));

            result!.Status.ShouldBe(Status.Failed, $"a weak password must be rejected; got: {raw}");
            // Aggregate envelope: /managed/request always reports SOMETHING_WRONG at
            // the top level and carries the real code at
            // error.info[0].failed[0].error_code (same drill-down as
            // AssignOwnershipTests / RolePermissionRequestTests).
            PerRecordErrorCode(raw).ShouldBe(InternalErrorCode.INVALID_PASSWORD_RULES,
                $"the per-record error_code must be INVALID_PASSWORD_RULES; got: {raw}");
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
    public async Task ManagedRequest_Create_User_With_Password_Honors_ForcePasswordChange()
    {
        // Ticking the flag alongside a password means "this is a temporary handover
        // credential" — the user must rotate it at first login.
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = Shortname();
        try
        {
            var (result, raw) = await PostAsync(admin,
                CreateBody(shortname, "\"password\":\"Temp12345\",\"force_password_change\":true"));

            result!.Status.ShouldBe(Status.Success, $"create must succeed; got: {raw}");
            var created = await users.GetByShortnameAsync(shortname);
            created.ShouldNotBeNull();
            created!.Password.ShouldNotBeNull();
            created.ForcePasswordChange.ShouldBeTrue(
                "force_password_change must be honored when an initial password is supplied");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_Without_Password_Succeeds_With_No_Password()
    {
        // Control: the password is optional. Omitting it preserves the pre-existing
        // behavior — a passwordless user who obtains one via OTP / password reset.
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = Shortname();
        try
        {
            var (result, raw) = await PostAsync(admin, CreateBody(shortname));

            result!.Status.ShouldBe(Status.Success, $"create without a password must succeed; got: {raw}");
            var created = await users.GetByShortnameAsync(shortname);
            created.ShouldNotBeNull();
            created!.Password.ShouldBeNull("omitting the password must leave the account passwordless");
            created.ForcePasswordChange.ShouldBeTrue(
                "a passwordless user must set a password at first login");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_Empty_Password_Is_Treated_As_Absent()
    {
        // Pins the wire contract relied on by seeded API samples (and by the cxb
        // form, whose blank input serializes away): `"password": ""` must behave
        // exactly like omitting the attribute, not fail the rules check.
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = Shortname();
        try
        {
            var (result, raw) = await PostAsync(admin, CreateBody(shortname, "\"password\":\"\""));

            result!.Status.ShouldBe(Status.Success, $"an empty password must not fail the rules; got: {raw}");
            var created = await users.GetByShortnameAsync(shortname);
            created.ShouldNotBeNull();
            created!.Password.ShouldBeNull();
            created.ForcePasswordChange.ShouldBeTrue();
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Create_User_ForcePasswordChange_False_Is_Overridden_When_Passwordless()
    {
        // With no password to fall back on, an explicit `force_password_change: false`
        // is still overridden — the account has no credential, so the flag cannot be
        // cleared. It IS honored once a password accompanies it (test above).
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var shortname = Shortname();
        try
        {
            var (result, raw) = await PostAsync(admin,
                CreateBody(shortname, "\"force_password_change\":false"));

            result!.Status.ShouldBe(Status.Success, $"create must succeed; got: {raw}");
            var created = await users.GetByShortnameAsync(shortname);
            created.ShouldNotBeNull();
            created!.ForcePasswordChange.ShouldBeTrue(
                "a passwordless create must override an explicit force_password_change:false");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }

    [FactIfPg]
    public async Task ManagedRequest_Update_User_With_Password_Is_Rejected_And_Hash_Unchanged()
    {
        var admin = await _factory.CreateLoggedInUserAsync();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        var shortname = Shortname();
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
                Language = Language.En,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            var body =
                "{\"space_name\":\"management\",\"request_type\":\"update\",\"records\":[{" +
                "\"resource_type\":\"user\",\"subpath\":\"users\",\"shortname\":\"" + shortname + "\"," +
                "\"attributes\":{\"password\":\"Hijack1234\"}}]}";
            var (result, raw) = await PostAsync(admin, body);

            result!.Status.ShouldBe(Status.Failed, $"update with a password must be rejected; got: {raw}");
            raw.ShouldContain("password cannot be changed");
            (await users.GetByShortnameAsync(shortname))!.Password
                .ShouldBe(originalHash, "a rejected update must not touch the stored password hash");
        }
        finally
        {
            await admin.Cleanup();
            try { await users.DeleteAsync(shortname); } catch { }
        }
    }
}
