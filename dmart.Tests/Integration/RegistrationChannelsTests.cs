using System.Net.Http.Json;
using System.Text;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Endpoint-level coverage for the REGISTRATION_ENABLED_CHANNELS setting and
// the RegexPatternsConfig format gates on self-registration (/user/create)
// and /user/otp-request.
//
// Channel gating applies to self-registration ONLY (see the setting's
// comment in DmartSettings) — /user/profile and /managed/request are
// deliberately exempt. Format gating applies everywhere.
public sealed class RegistrationChannelsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public RegistrationChannelsTests(DmartFactory factory) => _factory = factory;

    private WebApplicationFactoryFixture Configure(string channels) =>
        new(_factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s =>
            {
                s.IsOtpForCreateRequired = false;
                s.RegistrationEnabledChannels = channels;
            }))));

    // Small wrapper so each test reads as intent, not plumbing.
    private sealed class WebApplicationFactoryFixture(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory { get; } = factory;
        public HttpClient Client { get; } = factory.CreateClient();
    }

    private static StringContent CreateBody(string attrsJson) =>
        new("{\"attributes\":{" + attrsJson + "}}", Encoding.UTF8, "application/json");

    private static async Task<Response> PostCreateAsync(HttpClient client, string attrsJson)
    {
        var resp = await client.PostAsync("/user/create", CreateBody(attrsJson));
        return (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!;
    }

    private static async Task CleanupIfCreatedAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory, Response result)
    {
        var shortname = result.Records is { Count: > 0 } ? result.Records[0].Shortname : null;
        if (result.Status == Status.Success && !string.IsNullOrEmpty(shortname))
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname);
    }

    [FactIfPg]
    public async Task Disabled_Email_Channel_Rejects_Email_Registration()
    {
        var fx = Configure("msisdn");
        var email = "chdis_" + Guid.NewGuid().ToString("N")[..6] + "@x.yz";
        var result = await PostCreateAsync(fx.Client,
            "\"email\":\"" + email + "\",\"password\":\"Testtest1234\"");
        try
        {
            result.Status.ShouldBe(Status.Failed);
            result.Error!.Message.ShouldContain("Email registration is disabled");
        }
        finally { await CleanupIfCreatedAsync(fx.Factory, result); }
    }

    [FactIfPg]
    public async Task Disabled_Email_Channel_Still_Allows_Msisdn_Registration()
    {
        var fx = Configure("msisdn");
        var msisdn = $"+9647{Random.Shared.Next(10_000_000, 99_999_999)}";
        var result = await PostCreateAsync(fx.Client,
            "\"msisdn\":\"" + msisdn + "\",\"password\":\"Testtest1234\"");
        try
        {
            result.Status.ShouldBe(Status.Success,
                $"msisdn-only registration must stay valid when email is disabled; got: {result.Error?.Message}");
        }
        finally { await CleanupIfCreatedAsync(fx.Factory, result); }
    }

    [FactIfPg]
    public async Task Disabled_Msisdn_Channel_Rejects_Msisdn_Registration()
    {
        var fx = Configure("email");
        var msisdn = $"+9647{Random.Shared.Next(10_000_000, 99_999_999)}";
        var result = await PostCreateAsync(fx.Client,
            "\"msisdn\":\"" + msisdn + "\",\"password\":\"Testtest1234\"");
        try
        {
            result.Status.ShouldBe(Status.Failed);
            result.Error!.Message.ShouldContain("MSISDN registration is disabled");
        }
        finally { await CleanupIfCreatedAsync(fx.Factory, result); }
    }

    [FactIfPg]
    public async Task Both_Channels_Disabled_Closes_Self_Registration()
    {
        // An operator emptying REGISTRATION_ENABLED_CHANNELS means "no
        // self-registration" — it must NOT silently lift the "email or
        // msisdn required" gate and open contact-less, OTP-less signup.
        var fx = Configure("");
        var result = await PostCreateAsync(fx.Client, "\"password\":\"Testtest1234\"");
        try
        {
            result.Status.ShouldBe(Status.Failed,
                "with no channels enabled, registration must be closed — not contact-less");
            result.Error!.Message.ShouldContain("Register API is disabled");
        }
        finally { await CleanupIfCreatedAsync(fx.Factory, result); }
    }

    [FactIfPg]
    public async Task Malformed_Email_Rejected_On_Self_Registration()
    {
        var fx = Configure("email,msisdn");
        var result = await PostCreateAsync(fx.Client,
            "\"email\":\"not-an-email\",\"password\":\"Testtest1234\"");
        try
        {
            result.Status.ShouldBe(Status.Failed);
            result.Error!.Message.ShouldContain("Email format is invalid");
        }
        finally { await CleanupIfCreatedAsync(fx.Factory, result); }
    }

    [FactIfPg]
    public async Task Malformed_Msisdn_Rejected_On_Self_Registration()
    {
        var fx = Configure("email,msisdn");
        var result = await PostCreateAsync(fx.Client,
            "\"msisdn\":\"+96478abc678\",\"password\":\"Testtest1234\"");
        try
        {
            result.Status.ShouldBe(Status.Failed);
            result.Error!.Message.ShouldContain("MSISDN format is invalid");
        }
        finally { await CleanupIfCreatedAsync(fx.Factory, result); }
    }

    [FactIfPg]
    public async Task OtpRequest_Malformed_Msisdn_Rejected_Before_Dispatch()
    {
        // /otp-request sends to an arbitrary, unauthenticated destination —
        // format is validated before any lookup or OTP dispatch.
        var resp = await _factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: "+96478abc678", Email: null),
            DmartJsonContext.Default.SendOTPRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error!.Code.ShouldBe(InternalErrorCode.INVALID_DATA);
        body.Error.Message.ShouldContain("MSISDN format is invalid");
    }

    [FactIfPg]
    public async Task OtpRequest_Malformed_Email_Rejected_Before_Dispatch()
    {
        var resp = await _factory.CreateClient().PostAsJsonAsync("/user/otp-request",
            new SendOTPRequest(Msisdn: null, Email: "definitely not@an email"),
            DmartJsonContext.Default.SendOTPRequest);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error!.Code.ShouldBe(InternalErrorCode.INVALID_DATA);
        body.Error.Message.ShouldContain("Email format is invalid");
    }
}
