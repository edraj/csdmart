using System.Net.Http.Json;
using System.Text;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// /user/create verifies the registration OTP(s) with a capped
// verify-and-consume at the register purpose, so a stored code can't be
// replayed after registration.
public sealed class UserCreateConsumesOtpTests : IClassFixture<DmartFactory>
{
    private const string Otp = "123456";
    private const string ValidPassword = "Testtest1234";
    private readonly DmartFactory _factory;
    public UserCreateConsumesOtpTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Create_With_Email_Otp_Consumes_The_Code()
    {
        var factory = OtpRequired();
        var otpRepo = factory.Services.GetRequiredService<OtpRepository>();
        var email = $"otpc_{Guid.NewGuid():N}"[..16] + "@x.yz";
        await otpRepo.IssueAsync(email, OtpPurpose.Register, Otp, DateTime.UtcNow.AddMinutes(5));

        var body = "{\"attributes\":{\"email\":\"" + email + "\",\"password\":\"" + ValidPassword
            + "\",\"email_otp\":\"" + Otp + "\"}}";
        var resp = await factory.CreateClient().PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Success);
        var shortname = result.Records![0].Shortname;

        try
        {
            (await otpRepo.VerifyAndConsumeAsync(email, OtpPurpose.Register, Otp, 0))
                .ShouldBeFalse("the email OTP must be consumed by registration — a replay must fail");
        }
        finally { await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname); }
    }

    [FactIfPg]
    public async Task Create_With_Msisdn_Otp_Consumes_The_Code()
    {
        var factory = OtpRequired();
        var otpRepo = factory.Services.GetRequiredService<OtpRepository>();
        var msisdn = $"9647{Random.Shared.Next(100_000_000, 999_999_999)}";
        await otpRepo.IssueAsync(msisdn, OtpPurpose.Register, Otp, DateTime.UtcNow.AddMinutes(5));

        var body = "{\"attributes\":{\"msisdn\":\"" + msisdn + "\",\"password\":\"" + ValidPassword
            + "\",\"msisdn_otp\":\"" + Otp + "\"}}";
        var resp = await factory.CreateClient().PostAsync("/user/create",
            new StringContent(body, Encoding.UTF8, "application/json"));
        var result = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        result!.Status.ShouldBe(Status.Success);
        var shortname = result.Records![0].Shortname;

        try
        {
            (await otpRepo.VerifyAndConsumeAsync(msisdn, OtpPurpose.Register, Otp, 0))
                .ShouldBeFalse("the msisdn OTP must be consumed by registration — a replay must fail");
        }
        finally { await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname); }
    }

    private WebApplicationFactory<Program> OtpRequired() =>
        _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s =>
            {
                s.IsRegistrable = true;
                s.IsOtpForCreateRequired = true;
            })));
}
