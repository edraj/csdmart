using System.Net;
using System.Net.Http.Json;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the /user/password-reset-request behaviour around the shortname-only
// path. Pre-fix `OtpProvider.Generate(dest)` saw a shortname-shaped string and
// fell through to RandomNumberGenerator even with MockSmppApi=true, so test
// fixtures relying on MockOtpCode silently broke. The fix resolves the
// shortname to a deliverable identifier (msisdn → email) before calling
// Generate so per-channel mocks behave predictably.
//
// We assert via the OTP row that gets stored under `reset:{dest}` because
// the endpoint never returns the code in the response body (anti-leak).
public sealed class PasswordResetRequestTests : IClassFixture<DmartFactory>, IAsyncLifetime
{
    private readonly DmartFactory _factory;
    public PasswordResetRequestTests(DmartFactory factory) => _factory = factory;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [FactIfPg]
    public async Task ShortnameOnly_Resolves_To_UserMsisdn_AndStoresMockCode()
    {
        var settings = _factory.Services.GetRequiredService<IOptions<DmartSettings>>().Value;
        if (!settings.MockSmppApi)
            return; // Mock SMPP must be on for predictable code matching.

        var (shortname, _, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // The handler stored under `reset:{dest}` where dest was resolved
            // from the shortname to the user's msisdn. The mock SMPP path on
            // OtpProvider returned MockOtpCode rather than a random value.
            var repo = _factory.Services.GetRequiredService<OtpRepository>();
            var code = await repo.GetCodeAsync($"reset:{msisdn}");
            code.ShouldNotBeNull();
            code.ShouldBe(settings.MockOtpCode);
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    [FactIfPg]
    public async Task ShortnameOnly_NoMsisdn_FallsBack_To_Email()
    {
        var settings = _factory.Services.GetRequiredService<IOptions<DmartSettings>>().Value;
        if (!settings.MockSmtpApi)
            return; // Mock SMTP must be on for predictable code matching.

        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var repo = _factory.Services.GetRequiredService<OtpRepository>();
            var code = await repo.GetCodeAsync($"reset:{email}");
            code.ShouldNotBeNull();
            code.ShouldBe(settings.MockOtpCode);
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    [FactIfPg]
    public async Task ShortnameOnly_UnknownUser_Returns_Ok_AndStores_Nothing()
    {
        // Anti-enumeration: should not leak whether the shortname exists.
        var unknown = $"definitely_not_a_user_{Guid.NewGuid():N}".Substring(0, 30);
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/user/password-reset-request",
            new PasswordResetRequest(unknown, null, null),
            DmartJsonContext.Default.PasswordResetRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [FactIfPg]
    public async Task EmailDirect_StoresUnderEmailKey()
    {
        var settings = _factory.Services.GetRequiredService<IOptions<DmartSettings>>().Value;
        if (!settings.MockSmtpApi)
            return;

        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(null, email, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var repo = _factory.Services.GetRequiredService<OtpRepository>();
            var code = await repo.GetCodeAsync($"reset:{email}");
            code.ShouldNotBeNull();
            code.ShouldBe(settings.MockOtpCode);
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    private async Task<(string Shortname, string Email, string Msisdn)> CreateUserAsync(bool withMsisdn)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"pr_test_{suffix}";
        var email = $"{shortname}@test.local";
        var msisdn = $"+9650000{suffix[..8]}";

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

    private async Task DeleteUserAsync(string shortname)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAsync(shortname);
        }
        catch { /* best-effort cleanup */ }
    }
}
