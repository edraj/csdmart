using Dmart.Models.Api;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Models;

// Mirrors dmart's pytests/api_user_models_requests_test.py — login DTO shape checks.
public class UserLoginRequestTests
{
    [Fact]
    public void Valid_Shortname_And_Password_Roundtrips()
    {
        var req = new UserLoginRequest("alice", null, null, "hunter22hunter", null);
        req.Shortname.ShouldBe("alice");
        req.Password.ShouldBe("hunter22hunter");
        req.Email.ShouldBeNull();
        req.Msisdn.ShouldBeNull();
    }

    [Fact]
    public void Valid_Email_And_Password()
    {
        var req = new UserLoginRequest(null, "a@b.c", null, "hunter22hunter", null);
        req.Email.ShouldBe("a@b.c");
        req.Shortname.ShouldBeNull();
    }

    [Fact]
    public void Valid_Msisdn_And_Password()
    {
        var req = new UserLoginRequest(null, null, "+96599887766", "hunter22hunter", null);
        req.Msisdn.ShouldBe("+96599887766");
    }

    [Fact]
    public void All_Identity_Fields_Null_Is_Constructable_But_Logically_Invalid()
    {
        // Mirrors dmart's "missing fields" test — record can construct, but the
        // login service treats it as a not-found.
        var req = new UserLoginRequest(null, null, null, "hunter22hunter", null);
        (req.Shortname is null && req.Email is null && req.Msisdn is null).ShouldBeTrue();
    }

    [Fact]
    public void Missing_Password_Is_Allowed_By_Dto()
    {
        // dmart's Python model requires password; our C# DTO carries password as nullable
        // because OAuth and OTP login paths don't supply it. The service-layer rejection
        // is tested in UserServiceTests via the integration suite.
        var req = new UserLoginRequest("alice", null, null, null, null);
        req.Password.ShouldBeNull();
    }
}
