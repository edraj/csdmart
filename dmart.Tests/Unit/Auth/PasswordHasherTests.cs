using Dmart.Auth;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

public class PasswordHasherTests
{
    private readonly PasswordHasher _h = new();

    [Fact]
    public void Hash_Then_Verify_Round_Trips()
    {
        var hash = _h.Hash("hunter22hunter");
        _h.Verify("hunter22hunter", hash).ShouldBeTrue();
    }

    [Fact]
    public void Verify_Wrong_Password_Returns_False()
    {
        var hash = _h.Hash("hunter22hunter");
        _h.Verify("wrong", hash).ShouldBeFalse();
    }

    [Fact]
    public void Two_Hashes_Of_Same_Password_Have_Different_Salts()
    {
        var a = _h.Hash("password");
        var b = _h.Hash("password");
        a.ShouldNotBe(b);
        _h.Verify("password", a).ShouldBeTrue();
        _h.Verify("password", b).ShouldBeTrue();
    }

    [Fact]
    public void Verify_Garbage_Hash_Returns_False()
    {
        _h.Verify("password", "this-is-not-a-valid-hash").ShouldBeFalse();
        _h.Verify("password", "").ShouldBeFalse();
    }
}
