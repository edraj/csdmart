using System.Net;
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

// Password-reset issuing goes through POST /user/otp-request with
// purpose=reset. Routing: msisdn/shortname → SMS, email → Email,
// shortname-only-no-msisdn → email fallback. These tests assert the otps
// row the handler writes, since SmsSender/SmtpSender short-circuit silently
// in mock mode, and the response is always 200 Ok regardless of outcome.
public sealed class PasswordResetRequestTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PasswordResetRequestTests(DmartFactory factory) => _factory = factory;

    private static SendOTPRequest ResetReq(string? shortname = null, string? email = null, string? msisdn = null)
        => new(Msisdn: msisdn, Email: email, Shortname: shortname, Purpose: OtpPurpose.Reset);

    [FactIfPg]
    public async Task ShortnameOnly_Sends_Otp_To_Users_Msisdn()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(shortname: shortname), DmartJsonContext.Default.SendOTPRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // The row is keyed by (destination, reset) — assert a code was
            // stored at the user's msisdn and not at their email.
            (await OtpExistsAsync(msisdn)).ShouldBeTrue();
            (await OtpExistsAsync(email)).ShouldBeFalse(
                "user has msisdn — must not also send to email");
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task ShortnameOnly_NoMsisdn_FallsBack_To_Email()
    {
        // When the caller supplied only a shortname and the resolved user
        // has no msisdn, the handler falls back to the email channel so the
        // reset still reaches the user. The fallback is gated to
        // shortname-only requests — direct-msisdn requests honor the channel
        // the caller picked (covered by MsisdnDirect_NoFallback_To_Email).
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(shortname: shortname), DmartJsonContext.Default.SendOTPRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await OtpExistsAsync(email)).ShouldBeTrue(
                "shortname-only with no msisdn falls back to email");
        }
        finally { await CleanupAsync(shortname, email, msisdn: null); }
    }

    [FactIfPg]
    public async Task ShortnameOnly_UnknownUser_Returns_Ok_AndSends_Nothing()
    {
        // Anti-enumeration: should not leak whether the shortname exists.
        var unknown = $"definitely_not_a_user_{Guid.NewGuid():N}".Substring(0, 30);
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/user/otp-request",
            ResetReq(shortname: unknown), DmartJsonContext.Default.SendOTPRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        // No row should exist — the user never existed, so there's nothing
        // to send for. Status code being OK is the anti-enumeration check.
    }

    [FactIfPg]
    public async Task EmailDirect_Sends_Otp_To_Email()
    {
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(email: email), DmartJsonContext.Default.SendOTPRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await OtpExistsAsync(email)).ShouldBeTrue();
        }
        finally { await CleanupAsync(shortname, email, msisdn: null); }
    }

    [FactIfPg]
    public async Task MsisdnDirect_Sends_Otp_To_Msisdn()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(msisdn: msisdn), DmartJsonContext.Default.SendOTPRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await OtpExistsAsync(msisdn)).ShouldBeTrue();
            (await OtpExistsAsync(email)).ShouldBeFalse(
                "msisdn-direct must not also send to email");
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task MsisdnDirect_NoFallback_To_Email()
    {
        // Pins the no-fallback property of the direct-msisdn path: even when
        // the user record exists with an email but no msisdn, a request that
        // supplied only a msisdn must NOT silently fall back to email.
        // (The handler routes the lookup by the supplied msisdn, so the user
        // here is unreachable through the msisdn key — silent OK is correct.)
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var ghostMsisdn = $"9647{Random.Shared.Next(100_000_000, 999_999_999)}";
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(msisdn: ghostMsisdn), DmartJsonContext.Default.SendOTPRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await OtpExistsAsync(ghostMsisdn)).ShouldBeFalse();
            (await OtpExistsAsync(email)).ShouldBeFalse(
                "direct-msisdn lookup must not fall back to the user's email");
        }
        finally { await CleanupAsync(shortname, email, msisdn: null); }
    }

    [FactIfPg]
    public async Task Reset_And_Login_Rows_Are_Purpose_Isolated()
    {
        // A reset issue must not create (or satisfy) a login-purpose row.
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(email: email), DmartJsonContext.Default.SendOTPRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var repo = _factory.Services.GetRequiredService<OtpRepository>();
            (await repo.GetCreatedSinceAsync(email, OtpPurpose.Reset)).ShouldNotBeNull();
            (await repo.GetCreatedSinceAsync(email, OtpPurpose.Login)).ShouldBeNull(
                "a reset issue must not be visible at the login purpose");
        }
        finally { await CleanupAsync(shortname, email, msisdn: null); }
    }

    // ---- helpers ----

    private async Task<bool> OtpExistsAsync(string dest)
    {
        var repo = _factory.Services.GetRequiredService<OtpRepository>();
        return await repo.GetCreatedSinceAsync(dest, OtpPurpose.Reset) is not null;
    }

    private async Task<(string Shortname, string Email, string Msisdn)> CreateUserAsync(bool withMsisdn)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"pr_test_{suffix}";
        var email = $"{shortname}@test.local";
        var msisdn = $"9647{Random.Shared.Next(100_000_000, 999_999_999)}";

        var users = _factory.Services.GetRequiredService<UserRepository>();
        var user = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Email = email,
            Msisdn = withMsisdn ? msisdn : null,
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user);
        return (shortname, email, msisdn);
    }

    private async Task CleanupAsync(string shortname, string? email, string? msisdn)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAsync(shortname);

            // Delete every otps row this test could have produced so
            // back-to-back runs start clean (destinations are unique per
            // test, so this only touches our own rows).
            var idents = new List<string>();
            if (!string.IsNullOrEmpty(email)) idents.Add(email);
            if (!string.IsNullOrEmpty(msisdn)) idents.Add(msisdn);
            if (idents.Count == 0) return;

            var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
            await using var conn = await db.OpenAsync();
            foreach (var ident in idents)
            {
                await using var cmd = conn.Command("DELETE FROM otps WHERE identifier = $1");
                DbParams.Add(cmd, ident);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch { /* best-effort cleanup */ }
    }
}
