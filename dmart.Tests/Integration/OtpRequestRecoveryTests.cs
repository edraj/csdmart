using System.Net.Http.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Two ways POST /user/otp-request could strand a user who had done nothing
// wrong. Both answered a silent 200, so neither was visible from the wire —
// these assert through the repository instead.
public sealed class OtpRequestRecoveryTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OtpRequestRecoveryTests(DmartFactory factory) => _factory = factory;

    // The daily cap is keyed on the destination and the endpoint is anonymous,
    // so anyone who knows a msisdn can spend its whole budget. Counted across
    // all purposes that also closed `reset` — locking the victim out of the
    // one flow that recovers an account, for 24 hours, silently. Reset draws
    // on its own budget now.
    [FactIfPg]
    public async Task Flooding_Login_Requests_Cannot_Close_Account_Recovery()
    {
        const int cap = 2;
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s =>
            {
                s.MaxOtpRequestsPerDay = cap;
                s.AllowOtpResendAfter = 0;   // isolate the cap from the cooldown
            })));

        var msisdn = NewMsisdn();
        var shortname = await SeedUserAsync(host, msisdn);
        var repo = host.Services.GetRequiredService<OtpRepository>();
        try
        {
            // Spend the destination's whole login budget, as an attacker would.
            for (var i = 0; i < cap; i++)
            {
                var flood = await RequestAsync(host, msisdn, OtpPurpose.Login);
                flood.Status.ShouldBe(Status.Success);
            }
            (await CountAsync(repo, msisdn, OtpPurpose.Login)).ShouldBe(cap);

            // Login is now capped — the abuse control still works.
            (await RequestAsync(host, msisdn, OtpPurpose.Login)).Status.ShouldBe(Status.Success);
            (await CountAsync(repo, msisdn, OtpPurpose.Login))
                .ShouldBe(cap, "the login budget must still be enforced");

            // …but the victim can still start a password reset.
            (await RequestAsync(host, msisdn, OtpPurpose.Reset)).Status.ShouldBe(Status.Success);
            (await CountAsync(repo, msisdn, OtpPurpose.Reset))
                .ShouldBe(1, "account recovery must not be deniable by flooding another purpose");
        }
        finally { await CleanupAsync(shortname, msisdn); }
    }

    // The mirror image, and the reason the split has to cut both ways: a
    // reserve that only protects reset would just move the cheap attack to
    // flooding reset, which would then take sign-in down with it.
    [FactIfPg]
    public async Task Flooding_Reset_Requests_Cannot_Close_Sign_In()
    {
        const int cap = 2;
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s =>
            {
                s.MaxOtpRequestsPerDay = cap;
                s.AllowOtpResendAfter = 0;
            })));

        var msisdn = NewMsisdn();
        var shortname = await SeedUserAsync(host, msisdn);
        var repo = host.Services.GetRequiredService<OtpRepository>();
        try
        {
            for (var i = 0; i < cap; i++)
                (await RequestAsync(host, msisdn, OtpPurpose.Reset)).Status.ShouldBe(Status.Success);
            (await CountAsync(repo, msisdn, OtpPurpose.Reset)).ShouldBe(cap);

            // Reset is capped…
            (await RequestAsync(host, msisdn, OtpPurpose.Reset)).Status.ShouldBe(Status.Success);
            (await CountAsync(repo, msisdn, OtpPurpose.Reset)).ShouldBe(cap);

            // …and sign-in is untouched by it.
            (await RequestAsync(host, msisdn, OtpPurpose.Login)).Status.ShouldBe(Status.Success);
            (await CountAsync(repo, msisdn, OtpPurpose.Login))
                .ShouldBe(1, "a reset flood must not deny the victim a login code");
        }
        finally { await CleanupAsync(shortname, msisdn); }
    }

    // Locking persists IsActive=false; only IsLockedAsync clears it once the
    // cool-down elapses. Reading IsUsable directly meant the account stayed
    // silently un-OTP-able forever after — and for a password-less user, whose
    // only credential IS the OTP, nothing on any path would have unlocked it.
    [FactIfPg]
    public async Task A_Locked_Account_Can_Get_A_Code_Once_The_Cooldown_Expires()
    {
        var settings = _factory.Services.GetRequiredService<IOptions<DmartSettings>>().Value;
        var maxAttempts = settings.MaxFailedLoginAttempts;
        var cooldown = settings.LockoutCooldownSeconds;
        if (maxAttempts <= 0 || cooldown <= 0) return;   // lockout disabled by config

        var msisdn = NewMsisdn();
        var shortname = await SeedUserAsync(_factory, msisdn);
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var repo = _factory.Services.GetRequiredService<OtpRepository>();
        try
        {
            // Built through the repository's own writers, not by upserting a
            // hand-made User: UpsertAsync does not carry last_failed_login, so
            // assembling the state that way yields attempt_count set with no
            // timestamp anchor — which IsLockedAsync correctly reads as "still
            // locked" and the test would then pass or fail for the wrong reason.
            var stale = DateTime.Now.AddSeconds(-(cooldown + 60));
            for (var i = 0; i < maxAttempts; i++)
                await users.IncrementAttemptAsync(shortname, stale);

            // …and the deactivation the lockout itself persists.
            var locked = (await users.GetByShortnameAsync(shortname)).ShouldNotBeNull();
            locked.AttemptCount.ShouldBe(maxAttempts);
            locked.LastFailedLogin.ShouldNotBeNull();
            await users.UpsertAsync(locked with { IsActive = false });

            (await RequestAsync(_factory, msisdn, OtpPurpose.Login)).Status.ShouldBe(Status.Success);

            (await CountAsync(repo, msisdn, OtpPurpose.Login)).ShouldBe(1,
                "the cool-down has expired, so the account must be able to receive a code again");
        }
        finally { await CleanupAsync(shortname, msisdn); }
    }

    // ====================================================================

    private static async Task<Response> RequestAsync(
        WebApplicationFactory<Program> host, string msisdn, string purpose)
    {
        var resp = await host.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: purpose),
            DmartJsonContext.Default.SendOTPRequest);
        return (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!;
    }

    private static Task<int> CountAsync(OtpRepository repo, string dest, string purpose)
        => repo.CountIssuedSinceAsync(dest, DateTime.Now.AddHours(-24), default, purpose);

    private static string NewMsisdn() => $"9647{Random.Shared.Next(100_000_000, 999_999_999)}";

    private static async Task<string> SeedUserAsync(
        WebApplicationFactory<Program> host, string msisdn)
    {
        var shortname = $"otprec_{Guid.NewGuid():N}"[..16];
        await host.Services.GetRequiredService<UserRepository>().UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname, SpaceName = "management", Subpath = "/users",
            OwnerShortname = shortname, IsActive = true,
            Msisdn = msisdn, IsMsisdnVerified = true,
            Type = UserType.Web, Language = Language.En,
            Roles = new(), Groups = new(),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return shortname;
    }

    private async Task CleanupAsync(string shortname, string msisdn)
    {
        try
        {
            await _factory.Services.GetRequiredService<UserRepository>().DeleteAsync(shortname);
            var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
            await using var conn = await db.OpenAsync();
            await using var cmd = conn.Command("DELETE FROM otps WHERE identifier = $1");
            DbParams.Add(cmd, msisdn);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort */ }
    }
}
