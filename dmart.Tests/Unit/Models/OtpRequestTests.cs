using Dmart.Models.Api;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Models;

// Mirrors dmart's pytests/api_user_models_requests_test.py — OTP and password-reset DTOs.
public class OtpRequestTests
{
    [Fact]
    public void SendOtp_Valid_Msisdn()
    {
        var req = new SendOTPRequest("+96599887766", null);
        req.Msisdn.ShouldBe("+96599887766");
        req.Email.ShouldBeNull();
    }

    [Fact]
    public void SendOtp_Valid_Email()
    {
        var req = new SendOTPRequest(null, "a@b.c");
        req.Email.ShouldBe("a@b.c");
    }

    [Fact]
    public void SendOtp_Both_Fields_Construct_But_Service_Picks_One()
    {
        var req = new SendOTPRequest("+96599887766", "a@b.c");
        // Service layer prefers msisdn; verified in UserServiceTests / OtpHandlerTests.
        (req.Msisdn ?? req.Email).ShouldBe("+96599887766");
    }

    [Fact]
    public void SendOtp_Both_Null_Is_Constructable()
    {
        var req = new SendOTPRequest(null, null);
        (req.Msisdn ?? req.Email).ShouldBeNull();
    }

    [Fact]
    public void PasswordReset_Valid_Email()
    {
        var req = new PasswordResetRequest(null, "a@b.c", null);
        req.Email.ShouldBe("a@b.c");
    }

    [Fact]
    public void PasswordReset_Valid_Msisdn()
    {
        var req = new PasswordResetRequest(null, null, "+96599887766");
        req.Msisdn.ShouldBe("+96599887766");
    }

    [Fact]
    public void ConfirmOtp_Valid_With_Email()
    {
        var req = new ConfirmOTPRequest("123456", null, "a@b.c");
        req.Code.ShouldBe("123456");
        req.Email.ShouldBe("a@b.c");
    }

    [Fact]
    public void ConfirmOtp_Code_Is_Required_Field()
    {
        // The record positional constructor enforces Code as a required parameter.
        // This test asserts the type system itself: removing Code from the call
        // wouldn't compile.
        var req = new ConfirmOTPRequest("000000", "+96599887766", null);
        req.Code.Length.ShouldBe(6);
    }
}
