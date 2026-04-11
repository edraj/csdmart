using Dmart.Models.Api;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Models;

// Mirrors dmart's pytests/api_user_models_erros_test.py — error envelope shape.
public class ErrorTests
{
    [Fact]
    public void Invalid_Otp_Error_Has_Type_Code_Message()
    {
        var err = new Error(Type: "auth", Code: InternalErrorCode.OTP_INVALID, Message: "code mismatch", Info: null);
        err.Type.ShouldBe("auth");
        err.Code.ShouldBe(InternalErrorCode.OTP_INVALID);
        err.Message.ShouldBe("code mismatch");
        err.Info.ShouldBeNull();
    }

    [Fact]
    public void Expired_Otp_Error_Has_Expected_Properties()
    {
        var info = new List<Dictionary<string, object>>
        {
            new() { ["after_seconds"] = 600 },
        };
        var err = new Error("auth", InternalErrorCode.OTP_EXPIRED, "code expired", info);
        err.Code.ShouldBe(InternalErrorCode.OTP_EXPIRED);
        err.Info.ShouldNotBeNull();
        err.Info!.Count.ShouldBe(1);
        err.Info[0]["after_seconds"].ShouldBe(600);
    }

    [Fact]
    public void Response_Fail_Builds_Failed_Status_With_Error()
    {
        // String overload maps "invalid_otp" → InternalErrorCode.OTP_INVALID.
        var resp = Response.Fail("invalid_otp", "code mismatch", "auth");
        resp.Status.ShouldBe(Status.Failed);
        resp.Error.ShouldNotBeNull();
        resp.Error!.Code.ShouldBe(InternalErrorCode.OTP_INVALID);
        resp.Error.Type.ShouldBe("auth");
    }

    [Fact]
    public void Response_Fail_Accepts_Int_Code_Directly()
    {
        var resp = Response.Fail(InternalErrorCode.LOCKED_ENTRY, "already locked");
        resp.Status.ShouldBe(Status.Failed);
        resp.Error!.Code.ShouldBe(InternalErrorCode.LOCKED_ENTRY);  // == 31
        resp.Error.Code.ShouldBe(31);
    }

    [Fact]
    public void Response_Ok_Builds_Success_With_Records()
    {
        var resp = Response.Ok(records: new List<Record>(), attributes: new() { ["total"] = 0 });
        resp.Status.ShouldBe(Status.Success);
        resp.Error.ShouldBeNull();
        resp.Records.ShouldNotBeNull();
        resp.Attributes!["total"].ShouldBe(0);
    }
}
