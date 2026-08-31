using System.Net.Http.Json;
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

// Gate rules for POST /user/otp-request:
//   * `purpose` must be one of the four defined values; anything else is
//     the only rejection the endpoint surfaces.
//   * Every well-formed request answers 200 Ok — locked accounts, disabled
//     registration and unknown users are all silent no-ops. Whether a code
//     was minted is asserted here through the repository, not the wire.
//   * One gate rule per purpose: login/reset are open to anonymous callers
//     but require an existing usable user; register is anonymous-allowed
//     only while is_registrable is on and the requested channel is enabled;
//     verify-contact requires a JWT.
public sealed class OtpRequestGateTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OtpRequestGateTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Missing_Or_Unknown_Purpose_Is_Rejected()
    {
        var client = _factory.CreateClient();

        var noPurpose = await client.PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: "9647811223344", Email: null),
            DmartJsonContext.Default.SendOTPRequest);
        var noPurposeBody = await noPurpose.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        noPurposeBody!.Status.ShouldBe(Status.Failed);
        noPurposeBody.Error!.Code.ShouldBe(InternalErrorCode.INVALID_DATA);

        var badPurpose = await client.PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: "9647811223344", Email: null, Purpose: "banana"),
            DmartJsonContext.Default.SendOTPRequest);
        var badPurposeBody = await badPurpose.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        badPurposeBody!.Status.ShouldBe(Status.Failed);
        badPurposeBody.Error!.Code.ShouldBe(InternalErrorCode.INVALID_DATA);
    }

    [FactIfPg]
    public async Task Anonymous_VerifyContact_Always_Silently_NoOps()
    {
        // verify-contact is JWT-only (it serves the authenticated profile
        // confirm/change flows). Anonymous callers get the same 200 Ok as
        // everyone, with nothing minted — even on a registrable deployment.
        var msisdn = NewMsisdn();
        var resp = await _factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.VerifyContact),
            DmartJsonContext.Default.SendOTPRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);

        (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.VerifyContact))
            .ShouldBeNull("verify-contact requires a JWT — anonymous must mint nothing");
    }

    [FactIfPg]
    public async Task Anonymous_Register_Silently_NoOps_When_Not_Registrable()
    {
        // is_registrable=false, no JWT: the wire answer is indistinguishable
        // from a successful issue, but no code may be minted.
        var factory = NotRegistrable();
        var msisdn = NewMsisdn();
        var resp = await factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.Register),
            DmartJsonContext.Default.SendOTPRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);

        (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.Register))
            .ShouldBeNull("no code may be minted when self-registration is off");
    }

    [FactIfPg]
    public async Task Anonymous_Register_Mints_When_Registrable()
    {
        // is_registrable=true (default), no JWT, brand-new msisdn → a code is
        // minted at the (destination, register) row.
        var msisdn = NewMsisdn();
        var resp = await _factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.Register),
            DmartJsonContext.Default.SendOTPRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);

        (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.Register))
            .ShouldNotBeNull("an OTP must be stored at the destination");
    }

    [FactIfPg]
    public async Task Purpose_Switch_Does_Not_Bypass_Cooldown()
    {
        // The resend cooldown anchors on the destination across ALL purposes:
        // after a register code is minted, an immediate login request for the
        // same msisdn must mint nothing — otherwise cycling purposes turns
        // the 60s cadence into one code per purpose, back-to-back.
        var msisdn = NewMsisdn();
        var shortname = await SeedUserAsync(_factory, msisdn: msisdn);
        try
        {
            var client = _factory.CreateClient();
            var first = await client.PostAsJsonAsync("/user/otp-request",
                new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.Register),
                DmartJsonContext.Default.SendOTPRequest);
            (await first.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);
            (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.Register)).ShouldNotBeNull();

            var second = await client.PostAsJsonAsync("/user/otp-request",
                new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.Login),
                DmartJsonContext.Default.SendOTPRequest);
            (await second.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);

            (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.Login))
                .ShouldBeNull("the register issue anchors the cooldown — no login code inside the window");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Register_Over_Disabled_Channel_Silently_NoOps()
    {
        // Registration open, but the msisdn channel disabled: a register OTP
        // over SMS must not be minted (mirrors /user/create's channel gate —
        // without this, a code could be issued for a channel registration
        // would then reject).
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s =>
                s.RegistrationEnabledChannels = "email")));
        var msisdn = NewMsisdn();
        var resp = await factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.Register),
            DmartJsonContext.Default.SendOTPRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);

        (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.Register))
            .ShouldBeNull("no code may be minted over a disabled registration channel");
    }

    [FactIfPg]
    public async Task Login_Purpose_For_Unknown_User_Still_NoOps_When_ImplicitRegistration_Off()
    {
        // EnableOtpImplicitRegistration defaults to false — an unresolved
        // msisdn at login purpose stays a silent no-op, same as today.
        var msisdn = NewMsisdn();
        var resp = await _factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.Login),
            DmartJsonContext.Default.SendOTPRequest);
        (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
            .Status.ShouldBe(Status.Success);

        (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.Login)).ShouldBeNull();
    }

    [FactIfPg]
    public async Task Login_Purpose_For_Unknown_User_Mints_When_ImplicitRegistration_On()
    {
        // With the flag on, an unresolved msisdn/email at login purpose is
        // gated like a register request instead of a flat no-op.
        var factory = WithImplicitRegistration();
        var msisdn = NewMsisdn();
        var resp = await factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.Login),
            DmartJsonContext.Default.SendOTPRequest);
        (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
            .Status.ShouldBe(Status.Success);

        (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.Login)).ShouldNotBeNull();
    }

    [FactIfPg]
    public async Task Login_Purpose_For_Unknown_User_Still_NoOps_When_Not_Registrable()
    {
        // Flag on, but registration itself is closed — the implicit path
        // inherits /user/create's own gate rather than bypassing it.
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s =>
            {
                s.EnableOtpImplicitRegistration = true;
                s.IsRegistrable = false;
            })));
        var msisdn = NewMsisdn();
        var resp = await factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.Login),
            DmartJsonContext.Default.SendOTPRequest);
        (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
            .Status.ShouldBe(Status.Success);

        (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.Login)).ShouldBeNull();
    }

    [FactIfPg]
    public async Task Login_Purpose_For_Unknown_Shortname_Stays_NoOp_Even_When_ImplicitRegistration_On()
    {
        // Shortname carries no contact to verify an OTP against for an
        // account that doesn't exist — the flag can't change that.
        var factory = WithImplicitRegistration();
        var unknown = $"otpgate_missing_{Guid.NewGuid():N}"[..20];
        var resp = await factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: null, Email: null, Shortname: unknown, Purpose: OtpPurpose.Login),
            DmartJsonContext.Default.SendOTPRequest);
        (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
            .Status.ShouldBe(Status.Success);
        // No destination exists for an unknown shortname, so nothing to
        // assert against the repository beyond the 200 Ok above.
    }

    [FactIfPg]
    public async Task Anonymous_Login_Purpose_Stays_Open_When_Not_Registrable()
    {
        // Login is a pre-auth flow: is_registrable must not gate it. The
        // msisdn maps to an existing user, so a login code is minted.
        var factory = NotRegistrable();
        var msisdn = NewMsisdn();
        var shortname = await SeedUserAsync(factory, msisdn: msisdn);
        try
        {
            var resp = await factory.CreateClient().PostAsJsonAsync("/user/otp-request",
                new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.Login),
                DmartJsonContext.Default.SendOTPRequest);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Success);

            (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.Login)).ShouldNotBeNull();
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Login_Purpose_For_Unknown_User_Silently_NoOps()
    {
        // Anti-enumeration: an unknown msisdn answers the same 200 Ok, with
        // nothing minted.
        var msisdn = NewMsisdn();
        var resp = await _factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.Login),
            DmartJsonContext.Default.SendOTPRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success);

        (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.Login)).ShouldBeNull();
    }

    [FactIfPg]
    public async Task Jwt_VerifyContact_Mints_Even_When_Not_Registrable()
    {
        // is_registrable=false but the caller presents a valid JWT → a
        // logged-in user may still verify/change a contact.
        var factory = NotRegistrable();
        var user = await _factory.CreateLoggedInUserAsync(factory);
        try
        {
            var msisdn = NewMsisdn();
            var resp = await user.Client.PostAsJsonAsync("/user/otp-request",
                new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.VerifyContact),
                DmartJsonContext.Default.SendOTPRequest);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Success);

            (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.VerifyContact)).ShouldNotBeNull();
        }
        finally { await user.Cleanup(); }
    }

    [FactIfPg]
    public async Task Jwt_Locked_Account_Silently_NoOps()
    {
        // A locked account must not mint an OTP even with a valid JWT: same
        // 200 Ok, nothing minted. The lock is the attempt-counter lock with
        // is_active=true (a deactivated account can't present a valid JWT),
        // and the session is left intact so the bearer token still validates.
        var user = await _factory.CreateLoggedInUserAsync();
        try
        {
            await SetAttemptCountAsync(user.Shortname, MaxAttempts());

            var msisdn = NewMsisdn();
            var resp = await user.Client.PostAsJsonAsync("/user/otp-request",
                new SendOTPRequest(Msisdn: msisdn, Email: null, Purpose: OtpPurpose.VerifyContact),
                DmartJsonContext.Default.SendOTPRequest);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Success);

            (await Repo().GetCreatedSinceAsync(msisdn, OtpPurpose.VerifyContact)).ShouldBeNull();
        }
        finally { await user.Cleanup(); }
    }

    // ---- helpers ----

    private static string NewMsisdn() => $"9647{Random.Shared.Next(100_000_000, 999_999_999)}";

    private OtpRepository Repo() => _factory.Services.GetRequiredService<OtpRepository>();

    private WebApplicationFactory<Program> NotRegistrable() =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.IsRegistrable = false)));

    private WebApplicationFactory<Program> WithImplicitRegistration() =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.EnableOtpImplicitRegistration = true)));

    private int MaxAttempts() =>
        _factory.Services.GetRequiredService<IOptions<Dmart.Config.DmartSettings>>()
            .Value.MaxFailedLoginAttempts;

    private async Task<string> SeedUserAsync(WebApplicationFactory<Program> host, string? msisdn = null)
    {
        var shortname = $"otpgate_{Guid.NewGuid():N}"[..16];
        var users = host.Services.GetRequiredService<UserRepository>();
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Msisdn = msisdn,
            IsMsisdnVerified = msisdn is not null,
            Type = UserType.Web,
            Language = Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return shortname;
    }

    private async Task SetAttemptCountAsync(string shortname, int count)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command("UPDATE users SET attempt_count = $1 WHERE shortname = $2");
        DbParams.Add(cmd, count);
        DbParams.Add(cmd, shortname);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DeleteUserAsync(string shortname)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAllSessionsAsync(shortname);
            await users.DeleteAsync(shortname);
        }
        catch { /* best effort */ }
    }
}
