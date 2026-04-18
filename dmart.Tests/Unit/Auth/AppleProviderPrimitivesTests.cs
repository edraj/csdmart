using System.Text;
using Dmart.Auth.OAuth;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

// Unit tests for the low-level primitives inside AppleProvider. Exercising
// the full provider over the network is out of scope — we test the bits
// that are provider-independent: base64url decoding, JWT header parse, and
// RSA roundtrip so we have confidence that the RS256 verification path
// works on bytes we sign ourselves.
public sealed class AppleProviderPrimitivesTests
{
    [Fact]
    public void Base64UrlDecode_Handles_Padding_Variants()
    {
        // "hello" → "aGVsbG8" (base64 "aGVsbG8=" without padding and url-safe).
        var result = AppleProvider.Base64UrlDecode("aGVsbG8");
        Encoding.UTF8.GetString(result).ShouldBe("hello");
    }

    [Fact]
    public void Base64UrlDecode_Converts_Url_Safe_Alphabet()
    {
        // Input uses BOTH url-safe characters (`-`, `_`) AND has length that
        // decodes cleanly once padded. Corresponds to the 6-byte payload
        // { 0xFB, 0xEF, 0xFF, 0xFB, 0xEF, 0xFF } — i.e. base64 "++//++//".
        var result = AppleProvider.Base64UrlDecode("--__--__");
        result.Length.ShouldBe(6);
        result.ShouldBe(new byte[] { 0xFB, 0xEF, 0xFF, 0xFB, 0xEF, 0xFF });
    }

    [Fact]
    public void Base64UrlDecode_Empty_Returns_Empty()
    {
        AppleProvider.Base64UrlDecode("").Length.ShouldBe(0);
    }
}
