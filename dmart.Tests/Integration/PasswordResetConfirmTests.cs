using System.Net;
using System.Net.Http.Json;
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

// /user/password-reset-confirm verifies the OTP minted by /user/otp-request
// purpose=reset and writes a new password hash. Pinned here:
//   1. Purpose isolation — a reset OTP is not consumable via /user/login's
//      OTP path, which verifies at the login purpose.
//   2. Brute-force lockout — wrong OTPs count against the failed-attempt
//      counter.
// Identifier is one of {Shortname, Email, Msisdn}, same shape as the
// request half.
public sealed class PasswordResetConfirmTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PasswordResetConfirmTests(DmartFactory factory) => _factory = factory;

    private const string ValidPassword = "NewPass1234";

    private static SendOTPRequest ResetReq(string shortname)
        => new(Msisdn: null, Email: null, Shortname: shortname, Purpose: OtpPurpose.Reset);

    [FactIfPg]
    public async Task HappyPath_Request_Then_Confirm_Updates_Password()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();

            // 1. Mint a reset OTP for the user.
            var reqResp = await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(shortname), DmartJsonContext.Default.SendOTPRequest);
            reqResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Codes are stored hashed, so the server-issued value can't be read
            // back. Seed a known code (superseding the random one — IssueAsync
            // kills the predecessor, same as a real resend) and confirm with it.
            const string knownOtp = "246813";
            await SeedResetOtpAsync(msisdn, knownOtp);

            // 2. Confirm with the correct OTP + a valid new password.
            var confirmResp = await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                    Otp: knownOtp, Password: ValidPassword),
                DmartJsonContext.Default.PasswordResetConfirm);
            confirmResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // The user row's password hash must verify against the new password.
            var users = _factory.Services.GetRequiredService<UserRepository>();
            var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
            var updated = await users.GetByShortnameAsync(shortname);
            updated.ShouldNotBeNull();
            hasher.Verify(ValidPassword, updated!.Password!).ShouldBeTrue();

            // The OTP must be consumed — a replay of the same code must fail.
            (await LatestActiveResetHashAsync(msisdn)).ShouldBeNull();
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task Reset_Otp_Is_Not_Consumable_Via_Login_OtpPath()
    {
        // Purpose isolation: the reset OTP lives at (dest, reset);
        // /user/login's OTP path verifies at (dest, login), so the reset code
        // must not authenticate a login.
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var reqResp = await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(shortname), DmartJsonContext.Default.SendOTPRequest);
            reqResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // Seed a known reset code so we can try to misuse it via login.
            const string knownOtp = "135792";
            await SeedResetOtpAsync(msisdn, knownOtp);

            // Attempt to log in with the reset code via the OTP path. Login
            // verifies at the login purpose — no row there, so it must fail.
            var loginResp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(null, null, msisdn, null, knownOtp),
                DmartJsonContext.Default.UserLoginRequest);
            loginResp.IsSuccessStatusCode.ShouldBeFalse(
                "a reset-purpose OTP must not authenticate a login");

            // The reset OTP must still be live (login's failed verify ran at
            // the unrelated login purpose and didn't touch the reset row).
            (await LatestActiveResetHashAsync(msisdn)).ShouldNotBeNullOrEmpty();
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task SecondRequest_Within_Cooldown_Is_SilentlyOk()
    {
        // Anti-enumeration: the cooldown branch returns 200 Ok silently so a
        // paired-request attacker can't distinguish "known user, just-issued
        // OTP" (cooldown hit) from "unknown user" (early return).
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var first = await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(shortname), DmartJsonContext.Default.SendOTPRequest);
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
            var firstCode = await LatestActiveResetHashAsync(msisdn);
            firstCode.ShouldNotBeNullOrEmpty();

            // Second call within the default 60s cooldown — must return 200
            // Ok AND must NOT refresh the stored code (otherwise the cooldown
            // is observable via the OTP value changing).
            var second = await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(shortname), DmartJsonContext.Default.SendOTPRequest);
            second.StatusCode.ShouldBe(HttpStatusCode.OK);

            var secondCode = await LatestActiveResetHashAsync(msisdn);
            secondCode.ShouldBe(firstCode, "cooldown must be a true no-op — same OTP code persists");
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task WrongOtp_Returns_OtpInvalid()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(shortname), DmartJsonContext.Default.SendOTPRequest);

            // Submit a definitely-wrong 6-digit code.
            var resp = await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                    Otp: "000000", Password: ValidPassword),
                DmartJsonContext.Default.PasswordResetConfirm);
            // OTP_INVALID → HTTP 400 (FailedResponseFilter default for OTP codes).
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await resp.Content.ReadAsStringAsync();
            body.ShouldContain("code mismatch or expired");

            // A single wrong guess is under MaxOtpVerifyAttempts — the code
            // stays live for another try (consume happens only on success).
            (await LatestActiveResetHashAsync(msisdn)).ShouldNotBeNullOrEmpty();
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task UnknownIdentifier_Returns_OtpInvalid()
    {
        var client = _factory.CreateClient();
        var unknown = $"ghost_user_{Guid.NewGuid():N}".Substring(0, 24);

        var resp = await client.PostAsJsonAsync("/user/password-reset-confirm",
            new PasswordResetConfirm(Shortname: unknown, Email: null, Msisdn: null,
                Otp: "123456", Password: ValidPassword),
            DmartJsonContext.Default.PasswordResetConfirm);
        // Uniform OTP_INVALID error → HTTP 400 — endpoint doesn't leak which leg failed.
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("code mismatch or expired");
    }

    [FactIfPg]
    public async Task WeakPassword_Returns_InvalidPasswordRules()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(shortname), DmartJsonContext.Default.SendOTPRequest);
            // Password-rule failure runs before the OTP probe, so the exact code
            // is irrelevant — seed a known one to satisfy "an OTP is pending".
            const string knownOtp = "112358";
            await SeedResetOtpAsync(msisdn, knownOtp);

            // 4 chars, no digit, no uppercase — fails the regex on multiple counts.
            var resp = await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                    Otp: knownOtp, Password: "weak"),
                DmartJsonContext.Default.PasswordResetConfirm);
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await resp.Content.ReadAsStringAsync();
            body.ShouldContain("password does not meet complexity rules");

            // Password-rule failure runs before the OTP probe, so the reset
            // row must still be live.
            (await LatestActiveResetHashAsync(msisdn)).ShouldNotBeNullOrEmpty();
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task Confirm_With_No_Pending_Otp_Returns_OtpInvalid()
    {
        // Known user but no reset OTP was ever requested for them — the
        // (dest, reset) row doesn't exist, so confirm must fail uniformly.
        // Pins the contract that confirm can never succeed without a paired
        // request.
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            // Sanity: no OTP exists for this user.
            (await LatestActiveResetHashAsync(msisdn)).ShouldBeNull();

            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                    Otp: "123456", Password: ValidPassword),
                DmartJsonContext.Default.PasswordResetConfirm);
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await resp.Content.ReadAsStringAsync();
            body.ShouldContain("code mismatch or expired");
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task BruteForce_Locks_Account_After_MaxFailedAttempts()
    {
        // Wrong OTPs count against the same failed-attempt counter /user/login
        // uses (MaxFailedLoginAttempts, default 5). Without this guarantee a
        // distributed attacker could exhaust the 10^6 6-digit keyspace inside
        // the 300s TTL by hitting the endpoint from many IPs.
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            await client.PostAsJsonAsync("/user/otp-request",
                ResetReq(shortname), DmartJsonContext.Default.SendOTPRequest);

            // Submit 4 wrong OTPs — each should return OTP_INVALID (HTTP 400)
            // and the account stays active (counter < 5).
            for (int i = 0; i < 4; i++)
            {
                var bad = await client.PostAsJsonAsync("/user/password-reset-confirm",
                    new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                        Otp: "000000", Password: ValidPassword),
                    DmartJsonContext.Default.PasswordResetConfirm);
                bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"attempt {i + 1} should be OTP_INVALID");
            }

            // 5th wrong OTP trips the lockout — server returns USER_ACCOUNT_LOCKED
            // (HTTP 401) on the same call that flipped IsActive=false.
            var lockResp = await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: shortname, Email: null, Msisdn: null,
                    Otp: "000000", Password: ValidPassword),
                DmartJsonContext.Default.PasswordResetConfirm);
            lockResp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var lockBody = await lockResp.Content.ReadAsStringAsync();
            lockBody.ShouldContain("Account has been locked");

            // Confirm the lock landed in the DB.
            var users = _factory.Services.GetRequiredService<UserRepository>();
            var locked = await users.GetByShortnameAsync(shortname);
            locked.ShouldNotBeNull();
            locked!.IsActive.ShouldBeFalse();
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    // ---- helpers ----

    // Returns the code_hash of the latest LIVE (non-consumed, non-expired)
    // reset row for `dest`, or null when none is pending. Codes are stored
    // hashed, so this is the HMAC — used for presence/consumed assertions and
    // for the cooldown no-op check (the deterministic hash is identical iff
    // the underlying code is unchanged).
    private async Task<string?> LatestActiveResetHashAsync(string dest)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command(
            "SELECT code_hash FROM otps WHERE identifier = $1 AND purpose = $2 " +
            "AND consumed_at IS NULL ORDER BY created_at DESC LIMIT 1");
        DbParams.Add(cmd, dest);
        DbParams.Add(cmd, OtpPurpose.Reset);
        var raw = await cmd.ExecuteScalarAsync();
        return raw is null or DBNull ? null : (string)raw;
    }

    // Seeds a KNOWN reset code at (dest, reset). Because codes are stored
    // hashed, a test can't read back a server-issued code to submit it — it
    // issues its own (superseding any predecessor, like a real resend) and
    // submits that, exercising the real verify+consume path.
    private async Task SeedResetOtpAsync(string dest, string code)
    {
        var repo = _factory.Services.GetRequiredService<OtpRepository>();
        await repo.IssueAsync(dest, OtpPurpose.Reset, code, DateTime.UtcNow.AddMinutes(5));
    }

    private async Task<(string Shortname, string Email, string Msisdn)> CreateUserAsync(bool withMsisdn)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"pc_test_{suffix}";
        var email = $"{shortname}@test.local";
        var msisdn = $"9647{Random.Shared.Next(100_000_000, 999_999_999)}";

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
            Email = email,
            Msisdn = withMsisdn ? msisdn : null,
            // Pre-set a known starting password so the happy-path test can
            // assert that confirm changed it.
            Password = hasher.Hash("OriginalPass1"),
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

            // Destinations are unique per test, so deleting by identifier
            // only touches our own rows (all purposes, all states).
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
