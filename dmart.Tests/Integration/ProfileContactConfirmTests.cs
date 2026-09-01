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

// POST /user/profile is the only way to confirm the contact already on your
// row: `email` (== stored) plus `email_otp` consumes a verify-contact code and
// flips is_email_verified.
//
// "== stored" cannot be an ordinal comparison. The supplied value is lowercased
// on the way in, while the stored column keeps whatever case it was written
// with — RequestHandler keeps the admin's spelling when provisioning,
// OAuthUserResolver keeps the provider's. So for any address carrying an
// uppercase letter the guard was always false, and its owner could never
// confirm the contact they already hold: their own address came back "email
// does not match the stored address; use new_email to change it", and
// new_email would have been a lie.
//
// Same defect the reset flow had (see OtpHandler.EmailDest and
// PasswordResetConfirmTests); this is the other endpoint that compares a
// normalised input against a raw stored value.
public class ProfileContactConfirmTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ProfileContactConfirmTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Mixed_Case_Stored_Email_Can_Be_Confirmed_With_Its_Otp()
    {
        var (shortname, email) = await CreateUserAsync();
        try
        {
            var svc = _factory.Services.GetRequiredService<UserService>();
            var users = _factory.Services.GetRequiredService<UserRepository>();

            // /otp-request stores a verify-contact code under the lowercased
            // address; seed the same destination with a known value.
            const string knownOtp = "778899";
            await _factory.Services.GetRequiredService<OtpRepository>()
                .IssueAsync(email.ToLowerInvariant(), OtpPurpose.VerifyContact,
                    knownOtp, DateTime.UtcNow.AddMinutes(5));

            var result = await svc.UpdateProfileAsync(shortname, new Dictionary<string, object>
            {
                ["email"] = email.ToLowerInvariant(),
                ["email_otp"] = knownOtp,
            });

            result.IsOk.ShouldBeTrue(result.ErrorMessage);
            (await users.GetByShortnameAsync(shortname))!.IsEmailVerified
                .ShouldBeTrue("confirming the stored address must flip is_email_verified");
        }
        finally { await CleanupAsync(shortname, email); }
    }

    // Without an OTP the same shape is a documented no-op — it must not be
    // rejected as a mismatch either, which is what the ordinal compare did.
    [FactIfPg]
    public async Task Mixed_Case_Stored_Email_Is_Not_Rejected_As_A_Mismatch()
    {
        var (shortname, email) = await CreateUserAsync();
        try
        {
            var svc = _factory.Services.GetRequiredService<UserService>();

            var result = await svc.UpdateProfileAsync(shortname,
                new Dictionary<string, object> { ["email"] = email.ToLowerInvariant() });

            result.IsOk.ShouldBeTrue(result.ErrorMessage);
        }
        finally { await CleanupAsync(shortname, email); }
    }

    // An address that really is different must still be refused — the fix
    // widens the comparison to ignore case, not to accept anything.
    [FactIfPg]
    public async Task A_Genuinely_Different_Email_Is_Still_Rejected()
    {
        var (shortname, email) = await CreateUserAsync();
        try
        {
            var svc = _factory.Services.GetRequiredService<UserService>();

            var result = await svc.UpdateProfileAsync(shortname,
                new Dictionary<string, object> { ["email"] = "someone.else@example.com" });

            result.IsOk.ShouldBeFalse("a different address must require new_email");
        }
        finally { await CleanupAsync(shortname, email); }
    }

    private async Task<(string Shortname, string Email)> CreateUserAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var shortname = $"pcc_{suffix}";
        // Stored with uppercase on purpose — the ordinary state for a row
        // provisioned by an admin or created from an OAuth profile.
        var email = $"PCC_{suffix}@Example.COM";

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
