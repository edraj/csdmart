using Dmart.Utils;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Utils;

public class HashUtilsTests
{
    [Fact]
    public void Sha256_Hex_Length_Is_64()
    {
        HashUtils.Sha256Hex("hello").Length.ShouldBe(64);
    }

    [Fact]
    public void Sha256_Is_Deterministic()
    {
        HashUtils.Sha256Hex("hello").ShouldBe(HashUtils.Sha256Hex("hello"));
    }

    [Fact]
    public void Sha256_Hello_Matches_Known_Vector()
    {
        // Known SHA-256 of "hello"
        HashUtils.Sha256Hex("hello")
            .ShouldBe("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824");
    }
}
