using System.Text.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the audit-history side-effect of UserService.UpdateProfileAsync:
//   - successful profile changes write one histories row with a {old, new} diff
//   - the language field uses the wire form ("english"), not the C# enum name ("en")
//   - secrets and bookkeeping (password hash, attempt_count, updated_at) never appear
//   - a no-op update (patch that resolves to zero diff) skips the history append
//     entirely so we don't pollute the table with empty rows
public sealed class UserProfileHistoryTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserProfileHistoryTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task UpdateProfile_RealChange_Writes_History_With_Wire_Language_And_OldNew_Shape()
    {
        var sp = _factory.Services;
        _factory.CreateClient();
        var users = sp.GetRequiredService<UserRepository>();
        var svc = sp.GetRequiredService<UserService>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var (shortname, _) = await CreateUserAsync(language: Language.En);
        try
        {
            // Change two top-level fields plus a translation. Both kinds must
            // surface in the diff, and language must use the wire format.
            // Translation is wrapped as a JsonElement because ParseTranslation
            // only accepts JsonElement / string.
            var patch = new Dictionary<string, object>
            {
                ["language"] = "ar",
                ["displayname"] = JsonSerializer.SerializeToElement(new { en = "Test User" }),
            };
            var result = await svc.UpdateProfileAsync(shortname, patch);
            result.IsOk.ShouldBeTrue(result.ErrorMessage);

            var histResp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = "management",
                Subpath = "/users",
                FilterShortnames = new() { shortname },
                Limit = 100,
            }, _factory.AdminShortname);

            histResp.Status.ShouldBe(Status.Success);
            histResp.Records.ShouldNotBeNull();
            histResp.Records!.Count.ShouldBe(1);

            var diff = (JsonElement)histResp.Records[0].Attributes!["diff"]!;
            diff.ValueKind.ShouldBe(JsonValueKind.Object);

            // Every diff entry must be exactly {old, new}.
            foreach (var prop in diff.EnumerateObject())
            {
                prop.Value.ValueKind.ShouldBe(JsonValueKind.Object);
                var keys = prop.Value.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
                keys.ShouldBe(new[] { "new", "old" });
            }

            // Language must be the wire form ("english"/"arabic"), not "en"/"ar".
            diff.TryGetProperty("language", out var langDiff).ShouldBeTrue();
            langDiff.GetProperty("old").GetString().ShouldBe("english");
            langDiff.GetProperty("new").GetString().ShouldBe("arabic");

            // Translation field flattens per-locale.
            diff.TryGetProperty("displayname.en", out var dnDiff).ShouldBeTrue();
            dnDiff.GetProperty("new").GetString().ShouldBe("Test User");
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    [FactIfPg]
    public async Task UpdateProfile_NoOp_Patch_Does_Not_Write_History_Row()
    {
        // A patch that resolves to no actual field changes (every key already
        // matches the stored value) must NOT create a histories row. Without
        // the empty-diff guard the table would gain a {} entry per touch.
        var sp = _factory.Services;
        _factory.CreateClient();
        var svc = sp.GetRequiredService<UserService>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var (shortname, _) = await CreateUserAsync(language: Language.En);
        try
        {
            // Re-state the existing language — same value, no real change.
            var patch = new Dictionary<string, object> { ["language"] = "en" };
            var result = await svc.UpdateProfileAsync(shortname, patch);
            result.IsOk.ShouldBeTrue(result.ErrorMessage);

            var histResp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = "management",
                Subpath = "/users",
                FilterShortnames = new() { shortname },
                Limit = 100,
            }, _factory.AdminShortname);

            histResp.Status.ShouldBe(Status.Success);
            histResp.Records.ShouldNotBeNull();
            histResp.Records!.Count.ShouldBe(0);
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    [FactIfPg]
    public async Task UpdateProfile_Password_And_Bookkeeping_Excluded_From_Diff()
    {
        // Password hash, attempt_count, updated_at must never appear in the
        // history diff — they're either secrets or pure noise.
        var sp = _factory.Services;
        _factory.CreateClient();
        var users = sp.GetRequiredService<UserRepository>();
        var svc = sp.GetRequiredService<UserService>();
        var qsvc = sp.GetRequiredService<QueryService>();

        var (shortname, _) = await CreateUserAsync(language: Language.En, password: "OldPass1234!");
        try
        {
            // Stage force_password_change so the password change bypasses
            // the old_password requirement.
            var u = (await users.GetByShortnameAsync(shortname))!;
            await users.UpsertAsync(u with { ForcePasswordChange = true, UpdatedAt = DateTime.UtcNow });

            var patch = new Dictionary<string, object> { ["password"] = "NewPass1234!" };
            var result = await svc.UpdateProfileAsync(shortname, patch);
            result.IsOk.ShouldBeTrue(result.ErrorMessage);

            var histResp = await qsvc.ExecuteAsync(new Query
            {
                Type = QueryType.History,
                SpaceName = "management",
                Subpath = "/users",
                FilterShortnames = new() { shortname },
                Limit = 100,
            }, _factory.AdminShortname);

            histResp.Status.ShouldBe(Status.Success);
            histResp.Records.ShouldNotBeNull();
            // force_password_change flipped from true to false, so we expect
            // exactly one diff entry (the flag), not the password hash.
            histResp.Records!.Count.ShouldBe(1);

            var diff = (JsonElement)histResp.Records[0].Attributes!["diff"]!;
            diff.TryGetProperty("password", out _).ShouldBeFalse(
                "password hash must not appear in the user history diff");
            diff.TryGetProperty("attempt_count", out _).ShouldBeFalse();
            diff.TryGetProperty("updated_at", out _).ShouldBeFalse();
            // The flag flip is the legitimate audit signal.
            diff.TryGetProperty("force_password_change", out var fpcDiff).ShouldBeTrue();
            fpcDiff.GetProperty("old").GetBoolean().ShouldBeTrue();
            fpcDiff.GetProperty("new").GetBoolean().ShouldBeFalse();
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    // ---- helpers ----

    private async Task<(string Shortname, string Email)> CreateUserAsync(
        Language language, string? password = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"hist_{suffix}";
        var address = $"{shortname}@test.local";

        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        var user = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Email = address,
            IsEmailVerified = true,
            Password = password is null ? null : hasher.Hash(password),
            Roles = new() { },
            Groups = new(),
            Type = UserType.Web,
            Language = language,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user);
        return (shortname, address);
    }

    private async Task DeleteUserAsync(string shortname)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAsync(shortname);
            // Clean up any histories rows we created for this user.
            // Best-effort — not all schemas expose a delete-by-shortname.
        }
        catch { /* best-effort cleanup */ }
    }
}
