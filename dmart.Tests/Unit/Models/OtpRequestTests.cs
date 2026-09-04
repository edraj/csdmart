using Dmart.Models.Api;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Models;

// OTP DTOs: the unified SendOTPRequest (purpose + one identifier) and the
// OtpPurpose closed set that gates it.
public class OtpRequestTests
{
    [Fact]
    public void SendOtp_Valid_Msisdn()
    {
        var req = new SendOTPRequest("+96599887766", null, Purpose: OtpPurpose.Login);
        req.Msisdn.ShouldBe("+96599887766");
        req.Email.ShouldBeNull();
        req.Purpose.ShouldBe("login");
    }

    [Fact]
    public void SendOtp_Valid_Email()
    {
        var req = new SendOTPRequest(null, "a@b.c", Purpose: OtpPurpose.Reset);
        req.Email.ShouldBe("a@b.c");
    }

    [Fact]
    public void SendOtp_Both_Fields_Construct_But_Handler_Rejects()
    {
        // The record itself is permissive; /user/otp-request enforces
        // exactly-one-identifier (verified in OtpRequestGateTests).
        var req = new SendOTPRequest("+96599887766", "a@b.c", Purpose: OtpPurpose.Login);
        (req.Msisdn ?? req.Email).ShouldBe("+96599887766");
    }

    [Fact]
    public void SendOtp_Purpose_Defaults_To_Null()
    {
        // Purpose is required at the API level; the record leaves it null so
        // the handler owns the rejection (and its error shape).
        var req = new SendOTPRequest(null, null);
        req.Purpose.ShouldBeNull();
        OtpPurpose.IsValid(req.Purpose).ShouldBeFalse();
    }

    [Theory]
    [InlineData("login")]
    [InlineData("reset")]
    [InlineData("register")]
    [InlineData("verify-contact")]
    public void OtpPurpose_Accepts_The_Closed_Set(string purpose)
        => OtpPurpose.IsValid(purpose).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Login")]        // case-sensitive on purpose — one spelling
    [InlineData("pwd-reset")]    // not one of the four defined purposes
    [InlineData("anything-else")]
    public void OtpPurpose_Rejects_Everything_Else(string? purpose)
        => OtpPurpose.IsValid(purpose).ShouldBeFalse();
}
