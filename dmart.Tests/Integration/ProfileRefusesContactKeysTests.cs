using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// /user/profile used to own contact verification: `email` + `email_otp` to
// confirm the stored address, `new_email` + `email_otp` to change it. All six
// keys moved to POST /user/verify-contact, which owns every contact-plus-OTP
// operation.
//
// They are REFUSED here, not ignored. A client still sending `new_email` would
// otherwise get a 200 with no change and no clue — the failure mode that costs
// the most to diagnose, because everything looks like it worked. The error
// names the endpoint to use.
//
// `email` and `msisdn` are refused on VALUE rather than presence, because
// unlike the other four they are part of the profile representation: a caller
// who reads their profile, edits a display name and posts the Record back
// sends the stored address straight back. That echo is a no-op, not a request
// to change anything, and 400ing it would break the round-trip.
//
// The prefix went with them: `email` was ambiguous on this endpoint because it
// is also part of the profile representation, so a caller echoing it back on
// an unrelated edit had to be distinguishable from one asking for a change.
// /user/verify-contact has no representation to echo, so it takes plain
// `email`/`msisdn`.
public class ProfileRefusesContactKeysTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ProfileRefusesContactKeysTests(DmartFactory factory) => _factory = factory;

    [TheoryIfPg]
    [InlineData("email")]
    [InlineData("new_email")]
    [InlineData("email_otp")]
    [InlineData("msisdn")]
    [InlineData("new_msisdn")]
    [InlineData("msisdn_otp")]
    public async Task Every_Contact_Key_Is_Refused_When_It_Asks_For_A_Change(string key)
    {
        var (shortname, email) = await CreateUserAsync();
        try
        {
            var svc = _factory.Services.GetRequiredService<UserService>();

            var result = await svc.UpdateProfileAsync(shortname,
                new Dictionary<string, object> { [key] = "whatever" });

            result.IsOk.ShouldBeFalse($"'{key}' must not be silently ignored");
            result.ErrorMessage.ShouldContain(key,
                Case.Sensitive, "the error has to name the key the caller sent");
            result.ErrorMessage.ShouldContain("/user/verify-contact",
                Case.Sensitive, "and where to go instead, or it is not actionable");
        }
        finally { await CleanupAsync(shortname, email); }
    }

    // The read-modify-write round-trip: `email` echoed back unchanged is not a
    // contact change and must not fail the patch it rides along with. Case is
    // irrelevant to that — the stored column keeps whatever spelling it was
    // written with while lookups lower both sides.
    [TheoryIfPg]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_Unchanged_Email_Rides_Along_With_An_Ordinary_Edit(bool upperCased)
    {
        var (shortname, email) = await CreateUserAsync();
        try
        {
            var svc = _factory.Services.GetRequiredService<UserService>();
            var users = _factory.Services.GetRequiredService<UserRepository>();

            var result = await svc.UpdateProfileAsync(shortname, new Dictionary<string, object>
            {
                ["email"] = upperCased ? email.ToUpperInvariant() : email,
                ["language"] = "ar",
            });

            result.IsOk.ShouldBeTrue(result.ErrorMessage);
            var after = (await users.GetByShortnameAsync(shortname)).ShouldNotBeNull();
            after.Language.ShouldBe(Language.Ar);
            after.Email.ShouldBe(email, "an echo changes nothing, including the case");
        }
        finally { await CleanupAsync(shortname, email); }
    }

    // A null contact is the other shape a serialised profile can echo — it is
    // just as much a non-request as the address itself.
    [FactIfPg]
    public async Task A_Null_Contact_Key_Is_Not_A_Change()
    {
        var (shortname, email) = await CreateUserAsync();
        try
        {
            var svc = _factory.Services.GetRequiredService<UserService>();

            var result = await svc.UpdateProfileAsync(shortname,
                new Dictionary<string, object> { ["msisdn"] = null!, ["language"] = "ar" });

            result.IsOk.ShouldBeTrue(result.ErrorMessage);
        }
        finally { await CleanupAsync(shortname, email); }
    }

    // The refusal must not have cost the caller anything: the contact on the
    // row is untouched, and so are its verified flags.
    [FactIfPg]
    public async Task A_Refused_Patch_Changes_Nothing()
    {
        var (shortname, email) = await CreateUserAsync();
        try
        {
            var svc = _factory.Services.GetRequiredService<UserService>();
            var users = _factory.Services.GetRequiredService<UserRepository>();

            await svc.UpdateProfileAsync(shortname, new Dictionary<string, object>
            {
                ["new_email"] = "someone.else@example.com",
                ["language"] = "ar",
            });

            var after = (await users.GetByShortnameAsync(shortname)).ShouldNotBeNull();
            after.Email.ShouldBe(email);
            after.IsEmailVerified.ShouldBeFalse();
            after.Language.ShouldBe(Language.En,
                "the whole patch is refused, not partially applied");
        }
        finally { await CleanupAsync(shortname, email); }
    }

    // Everything else on the endpoint still works — this narrows /user/profile
    // to profile fields, it does not break it.
    [FactIfPg]
    public async Task An_Ordinary_Profile_Update_Still_Works()
    {
        var (shortname, email) = await CreateUserAsync();
        try
        {
            var svc = _factory.Services.GetRequiredService<UserService>();
            var users = _factory.Services.GetRequiredService<UserRepository>();

            var result = await svc.UpdateProfileAsync(shortname,
                new Dictionary<string, object> { ["language"] = "ar" });

            result.IsOk.ShouldBeTrue(result.ErrorMessage);
            (await users.GetByShortnameAsync(shortname))!.Language.ShouldBe(Language.Ar);
        }
        finally { await CleanupAsync(shortname, email); }
    }

    private async Task<(string Shortname, string Email)> CreateUserAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var shortname = $"prck_{suffix}";
        var email = $"prck_{suffix}@test.local";
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname, SpaceName = "management", Subpath = "/users",
            OwnerShortname = shortname, IsActive = true,
            Email = email, IsEmailVerified = false,
            Password = hasher.Hash("OldPass1234!"),
            Type = UserType.Web, Language = Language.En,
            Roles = new(), Groups = new(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return (shortname, email);
    }

    private async Task CleanupAsync(string shortname, string email)
    {
        try
        {
            await _factory.Services.GetRequiredService<UserRepository>().DeleteAsync(shortname);
            var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
            await using var conn = await db.OpenAsync();
            await using var cmd = conn.Command("DELETE FROM otps WHERE identifier = $1");
            DbParams.Add(cmd, email.ToLowerInvariant());
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort */ }
    }
}
