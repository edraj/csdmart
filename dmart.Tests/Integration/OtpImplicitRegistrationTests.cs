using System.Net;
using System.Net.Http.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// EnableOtpImplicitRegistration: /user/login's OTP path creates an account
// for a direct msisdn/email identifier with no matching user, instead of
// failing, when the code verifies and the same gates /user/create uses
// (IsRegistrable + channel enabled) pass. Default is off — every test here
// that expects account creation opts in explicitly.
public sealed class OtpImplicitRegistrationTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OtpImplicitRegistrationTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Flag_Off_Unknown_Msisdn_Still_Fails_No_Account_Created()
    {
        var msisdn = NewMsisdn();
        const string code = "123456";
        await SeedOtpAsync(msisdn, code);

        var resp = await _factory.CreateClient().PostAsJsonAsync("/user/login",
            new UserLoginRequest(null, null, msisdn, null, Otp: code),
            DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await UserRepo().GetByMsisdnAsync(msisdn)).ShouldBeNull();
    }

    [FactIfPg]
    public async Task Flag_On_Valid_Otp_Unknown_Msisdn_Creates_Account_And_Logs_In()
    {
        var factory = WithImplicitRegistration();
        var msisdn = NewMsisdn();
        const string code = "246810";
        await SeedOtpAsync(msisdn, code, factory.Services);

        var resp = await factory.CreateClient().PostAsJsonAsync("/user/login",
            new UserLoginRequest(null, null, msisdn, null, Otp: code),
            DmartJsonContext.Default.UserLoginRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        try
        {
            resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            body!.Status.ShouldBe(Status.Success);
            body.Records![0].Attributes!.ShouldContainKey("access_token");
            // Presence is the signal: AuthHandler sets "created" only when
            // true, never "created": false, so an ordinary login response
            // never carries the key at all (see NormalLogin test below).
            body.Records[0].Attributes!.ShouldContainKey("created");

            var users = factory.Services.GetRequiredService<UserRepository>();
            var created = await users.GetByMsisdnAsync(msisdn);
            created.ShouldNotBeNull();
            created!.IsMsisdnVerified.ShouldBeTrue();
            created.IsActive.ShouldBeTrue();
            created.Password.ShouldBeNull("implicit registration never sets a password");
            created.ForcePasswordChange.ShouldBeTrue();
        }
        finally
        {
            var shortname = body?.Records is { Count: > 0 } ? body.Records[0].Shortname : null;
            if (!string.IsNullOrEmpty(shortname)) await DeleteUserAsync(shortname, factory.Services);
        }
    }

    [FactIfPg]
    public async Task Flag_On_Valid_Otp_Unknown_Email_Creates_Account()
    {
        var factory = WithImplicitRegistration();
        var email = $"implicit_{Guid.NewGuid():N}"[..20] + "@x.yz";
        const string code = "135791";
        await SeedOtpAsync(email, code, factory.Services);

        var resp = await factory.CreateClient().PostAsJsonAsync("/user/login",
            new UserLoginRequest(null, email, null, null, Otp: code),
            DmartJsonContext.Default.UserLoginRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        try
        {
            resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            var users = factory.Services.GetRequiredService<UserRepository>();
            var created = await users.GetByEmailAsync(email);
            created.ShouldNotBeNull();
            created!.IsEmailVerified.ShouldBeTrue();
            created.IsMsisdnVerified.ShouldBeFalse();
        }
        finally
        {
            var shortname = body?.Records is { Count: > 0 } ? body.Records[0].Shortname : null;
            if (!string.IsNullOrEmpty(shortname)) await DeleteUserAsync(shortname, factory.Services);
        }
    }

    [FactIfPg]
    public async Task Flag_On_Password_On_Request_Is_Ignored()
    {
        // The request may carry a Password (the DTO allows it alongside Otp);
        // implicit registration must not adopt it.
        var factory = WithImplicitRegistration();
        var msisdn = NewMsisdn();
        const string code = "975318";
        await SeedOtpAsync(msisdn, code, factory.Services);

        var resp = await factory.CreateClient().PostAsJsonAsync("/user/login",
            new UserLoginRequest(null, null, msisdn, "SomePassword1", Otp: code),
            DmartJsonContext.Default.UserLoginRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        try
        {
            resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            var users = factory.Services.GetRequiredService<UserRepository>();
            var created = await users.GetByMsisdnAsync(msisdn);
            created!.Password.ShouldBeNull();
            created.ForcePasswordChange.ShouldBeTrue();
        }
        finally
        {
            var shortname = body?.Records is { Count: > 0 } ? body.Records[0].Shortname : null;
            if (!string.IsNullOrEmpty(shortname)) await DeleteUserAsync(shortname, factory.Services);
        }
    }

    [FactIfPg]
    public async Task Flag_On_Wrong_Otp_Fails_No_Account_Created()
    {
        var factory = WithImplicitRegistration();
        var msisdn = NewMsisdn();
        await SeedOtpAsync(msisdn, "111111", factory.Services);

        var resp = await factory.CreateClient().PostAsJsonAsync("/user/login",
            new UserLoginRequest(null, null, msisdn, null, Otp: "000000"),
            DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await factory.Services.GetRequiredService<UserRepository>().GetByMsisdnAsync(msisdn)).ShouldBeNull();
    }

    [FactIfPg]
    public async Task Flag_On_Not_Registrable_Fails_Even_With_Valid_Otp()
    {
        // A code could exist despite registration being closed now (issued
        // earlier, or seeded directly, as here) — the redemption side must
        // enforce the same gate /user/create does, not just the issuing side.
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s =>
            {
                s.EnableOtpImplicitRegistration = true;
                s.IsRegistrable = false;
            })));
        var msisdn = NewMsisdn();
        const string code = "864209";
        await SeedOtpAsync(msisdn, code, factory.Services);

        var resp = await factory.CreateClient().PostAsJsonAsync("/user/login",
            new UserLoginRequest(null, null, msisdn, null, Otp: code),
            DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await factory.Services.GetRequiredService<UserRepository>().GetByMsisdnAsync(msisdn)).ShouldBeNull();
    }

    [FactIfPg]
    public async Task Flag_On_Disabled_Channel_Fails_Even_With_Valid_Otp()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s =>
            {
                s.EnableOtpImplicitRegistration = true;
                s.RegistrationEnabledChannels = "email";
            })));
        var msisdn = NewMsisdn();
        const string code = "702468";
        await SeedOtpAsync(msisdn, code, factory.Services);

        var resp = await factory.CreateClient().PostAsJsonAsync("/user/login",
            new UserLoginRequest(null, null, msisdn, null, Otp: code),
            DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await factory.Services.GetRequiredService<UserRepository>().GetByMsisdnAsync(msisdn)).ShouldBeNull();
    }

    [FactIfPg]
    public async Task Flag_On_Unknown_Shortname_Still_Fails()
    {
        // Shortname carries no contact to verify an OTP against — the flag
        // has nothing to act on.
        var factory = WithImplicitRegistration();
        var unknown = $"implicit_missing_{Guid.NewGuid():N}"[..24];

        var resp = await factory.CreateClient().PostAsJsonAsync("/user/login",
            new UserLoginRequest(unknown, null, null, null, Otp: "123456"),
            DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [FactIfPg]
    public async Task Ordinary_OtpLogin_For_Existing_User_Omits_Created()
    {
        // An OTP login for an ALREADY-existing account never carries
        // "created" — the field is specific to the implicit-registration
        // path, not a general "logged in via OTP" marker.
        var factory = WithImplicitRegistration();
        var msisdn = NewMsisdn();
        var shortname = await SeedUserAsync(factory, msisdn);
        const string code = "864213";
        await SeedOtpAsync(msisdn, code, factory.Services);
        try
        {
            var resp = await factory.CreateClient().PostAsJsonAsync("/user/login",
                new UserLoginRequest(null, null, msisdn, null, Otp: code),
                DmartJsonContext.Default.UserLoginRequest);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            body!.Records![0].Attributes!.ShouldNotContainKey("created");
        }
        finally { await DeleteUserAsync(shortname, factory.Services); }
    }

    // ---- helpers ----

    private async Task<string> SeedUserAsync(WebApplicationFactory<Program> host, string msisdn)
    {
        var shortname = $"implicit_existing_{Guid.NewGuid():N}"[..24];
        var users = host.Services.GetRequiredService<UserRepository>();
        await users.UpsertAsync(new Models.Core.User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Msisdn = msisdn,
            IsMsisdnVerified = true,
            Type = Models.Enums.UserType.Web,
            Language = Models.Enums.Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return shortname;
    }

    private static string NewMsisdn() => $"9647{Random.Shared.Next(100_000_000, 999_999_999)}";

    private WebApplicationFactory<Program> WithImplicitRegistration() =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.EnableOtpImplicitRegistration = true)));

    private UserRepository UserRepo() => _factory.Services.GetRequiredService<UserRepository>();

    // Seeds a KNOWN login-purpose code directly via the repository — bypasses
    // /otp-request so tests can exercise the redemption-side gate
    // independently of the issuing-side gate (they enforce the same rules,
    // but on different code paths).
    private async Task SeedOtpAsync(string identifier, string code, IServiceProvider? services = null)
    {
        var repo = (services ?? _factory.Services).GetRequiredService<OtpRepository>();
        await repo.IssueAsync(identifier, OtpPurpose.Login, code, DateTime.UtcNow.AddMinutes(5));
    }

    private async Task DeleteUserAsync(string shortname, IServiceProvider services)
    {
        try
        {
            var users = services.GetRequiredService<UserRepository>();
            await users.DeleteAllSessionsAsync(shortname);
            await users.DeleteAsync(shortname);
        }
        catch { /* best effort */ }
    }
}
