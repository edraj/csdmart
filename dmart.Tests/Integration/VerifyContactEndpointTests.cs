using System.Net.Http.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// POST /user/verify-contact — confirms a contact the caller's row ALREADY carries.
//
// Kept as its own route rather than folded into /user/profile: Python dmart
// exposes otp-confirm and this port is defined against Python's surface, and
// "POST a profile update" does not advertise "confirm my contact". The CHANGE
// flow (new_email/new_msisdn) still lives on /user/profile, where the field
// write belongs.
//
// The three properties that make it safe are all pinned below, because each
// was load-bearing in the original and easy to lose in a rewrite.
public sealed class VerifyContactEndpointTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public VerifyContactEndpointTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Confirms_The_Callers_Own_Email_And_Flips_The_Flag()
    {
        var (shortname, email, client) = await LoggedInUserAsync();
        try
        {
            await SeedAsync(email.ToLowerInvariant(), "121314");

            var resp = await client.PostAsJsonAsync("/user/verify-contact",
                new VerifyContactRequest("121314", Msisdn: null, Email: email.ToLowerInvariant()),
                DmartJsonContext.Default.VerifyContactRequest);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);

            body!.Status.ShouldBe(Status.Success, body.Error?.Message);
            (await Users().GetByShortnameAsync(shortname))!.IsEmailVerified.ShouldBeTrue();
        }
        finally { await CleanupAsync(shortname, email); }
    }

    // The stored column keeps whatever case it was written with, so the two
    // halves have to normalise identically — the same defect that made
    // password reset unrecoverable for these addresses.
    [FactIfPg]
    public async Task Works_For_A_Mixed_Case_Stored_Email()
    {
        var (shortname, email, client) = await LoggedInUserAsync(mixedCase: true);
        try
        {
            await SeedAsync(email.ToLowerInvariant(), "151617");

            var resp = await client.PostAsJsonAsync("/user/verify-contact",
                new VerifyContactRequest("151617", Msisdn: null, Email: email.ToLowerInvariant()),
                DmartJsonContext.Default.VerifyContactRequest);

            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);
            (await Users().GetByShortnameAsync(shortname))!.IsEmailVerified.ShouldBeTrue();
        }
        finally { await CleanupAsync(shortname, email); }
    }

    // Proving control of SOMEONE ELSE'S address must not verify the contact on
    // your row — that verification would prove nothing about it. And the
    // mismatch is rejected before the code is consumed, so a wrong request does
    // not spend a code the caller still needs.
    [FactIfPg]
    public async Task A_New_Address_Replaces_The_Old_One_And_Is_Verified()
    {
        var (shortname, email, client) = await LoggedInUserAsync();
        var fresh = $"moved_{Guid.NewGuid():N}"[..16] + "@test.local";
        try
        {
            await SeedAsync(fresh, "242526");

            var resp = await client.PostAsJsonAsync("/user/verify-contact",
                new VerifyContactRequest("242526", Msisdn: null, Email: fresh),
                DmartJsonContext.Default.VerifyContactRequest);

            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);

            var after = (await Users().GetByShortnameAsync(shortname)).ShouldNotBeNull();
            after.Email.ShouldBe(fresh, "proving control of a new address makes it yours");
            after.IsEmailVerified.ShouldBeTrue();
        }
        finally { await CleanupAsync(shortname, email); await CleanupAsync(null, fresh); }
    }

    // The collision guard the profile path carried, and which had to come with
    // the operation: without it a change could silently take an address that
    // already belongs to somebody else. Checked BEFORE the code is spent.
    [FactIfPg]
    public async Task An_Address_Owned_By_Someone_Else_Is_Refused_Without_Spending_The_Code()
    {
        var (shortname, email, client) = await LoggedInUserAsync();
        var (otherShortname, otherEmail, _) = await LoggedInUserAsync();
        try
        {
            await SeedAsync(otherEmail.ToLowerInvariant(), "272829");

            var resp = await client.PostAsJsonAsync("/user/verify-contact",
                new VerifyContactRequest("272829", Msisdn: null, Email: otherEmail.ToLowerInvariant()),
                DmartJsonContext.Default.VerifyContactRequest);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);

            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
            (await Users().GetByShortnameAsync(shortname))!.Email
                .ShouldBe(email, "a refused change must leave the address alone");
            (await Repo().VerifyAndConsumeAsync(otherEmail.ToLowerInvariant(),
                OtpPurpose.VerifyContact, "272829", 5))
                .ShouldBeTrue("the collision check runs before the code is spent");
        }
        finally
        {
            await CleanupAsync(shortname, email);
            await CleanupAsync(otherShortname, otherEmail);
        }
    }

    [FactIfPg]
    public async Task An_Address_With_No_Code_Issued_To_It_Is_Refused()
    {
        var (shortname, email, client) = await LoggedInUserAsync();
        var stranger = $"stranger_{Guid.NewGuid():N}"[..18] + "@test.local";
        try
        {
            // No code was ever issued to this address.
            var resp = await client.PostAsJsonAsync("/user/verify-contact",
                new VerifyContactRequest("181920", Msisdn: null, Email: stranger),
                DmartJsonContext.Default.VerifyContactRequest);

            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Failed);
            (await Users().GetByShortnameAsync(shortname))!.Email
                .ShouldBe(email, "no code, no change");
        }
        finally { await CleanupAsync(shortname, email); await CleanupAsync(null, stranger); }
    }

    // Anonymous callers cannot reach the OTP store at all. Purpose isolation
    // means the worst they could burn is a verify-contact code rather than a
    // login one, but there is no reason to let them burn anything.
    [FactIfPg]
    public async Task An_Anonymous_Caller_Is_Rejected_Before_The_Store_Is_Touched()
    {
        var dest = $"anon_{Guid.NewGuid():N}"[..14] + "@test.local";
        try
        {
            await SeedAsync(dest, "212223");

            var resp = await _factory.CreateClient().PostAsJsonAsync("/user/verify-contact",
                new VerifyContactRequest("212223", Msisdn: null, Email: dest),
                DmartJsonContext.Default.VerifyContactRequest);

            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Failed);
            (await Repo().VerifyAndConsumeAsync(dest, OtpPurpose.VerifyContact, "212223", 5))
                .ShouldBeTrue("an anonymous call must not have spent the code");
        }
        finally { await CleanupAsync(null, dest); }
    }

    // ====================================================================

    private UserRepository Users() => _factory.Services.GetRequiredService<UserRepository>();
    private OtpRepository Repo() => _factory.Services.GetRequiredService<OtpRepository>();

    private Task SeedAsync(string dest, string code) =>
        Repo().IssueAsync(dest, OtpPurpose.VerifyContact, code, DateTime.Now.AddMinutes(5));

    private async Task<(string Shortname, string Email, HttpClient Client)> LoggedInUserAsync(
        bool mixedCase = false)
    {
        var logged = await _factory.CreateLoggedInUserAsync();
        var user = (await Users().GetByShortnameAsync(logged.Shortname)).ShouldNotBeNull();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var email = mixedCase ? $"OC_{suffix}@Test.Local" : $"oc_{suffix}@test.local";
        await Users().UpsertAsync(user with { Email = email, IsEmailVerified = false });
        return (logged.Shortname, email, logged.Client);
    }

    private async Task CleanupAsync(string? shortname, string? dest)
    {
        try
        {
            if (shortname is not null) await Users().DeleteAsync(shortname);
            if (dest is null) return;
            var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
            await using var conn = await db.OpenAsync();
            await using var cmd = conn.Command("DELETE FROM otps WHERE identifier = $1");
            DbParams.Add(cmd, dest.ToLowerInvariant());
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort */ }
    }
}
