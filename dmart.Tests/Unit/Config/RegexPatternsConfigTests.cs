using Dmart.Config;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Config;

// Unit tests for RegexPatternsConfig — the loader for `.dmart/regex.json`
// (per-channel email/msisdn format overrides). Verifies the built-in
// defaults, the override file shape documented in config.env.sample, and
// that bad input fails closed: an invalid override pattern falls back to
// the default, and a catastrophically-backtracking override reports
// "format is invalid" instead of surfacing RegexMatchTimeoutException as
// a 500 to the caller.
public class RegexPatternsConfigTests : IDisposable
{
    private readonly string _tmpFile = Path.Combine(
        Path.GetTempPath(),
        $"dmart-regex-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tmpFile)) File.Delete(_tmpFile);
        GC.SuppressFinalize(this);
    }

    private RegexPatternsConfig Build(string path)
    {
        var settings = new DmartSettings { RegexConfigPath = path };
        return new RegexPatternsConfig(
            Options.Create(settings), NullLogger<RegexPatternsConfig>.Instance);
    }

    // Point at a guaranteed-missing file so the built-in defaults apply —
    // never the empty path, which would resolve to the developer's real
    // ~/.dmart/regex.json.
    private RegexPatternsConfig Defaults() => Build("/nonexistent/dmart-regex.json");

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("first.last+tag@sub.domain.co")]
    [InlineData("USER_1%x-y@Example.COM")]
    public void Default_Email_Accepts_Standard_Shapes(string email)
        => Defaults().ValidateEmailFormat(email).ShouldBeNull();

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]          // no TLD
    [InlineData("a@b.c")]        // 1-char TLD
    [InlineData("a b@x.yz")]     // whitespace in local part
    [InlineData("@x.yz")]        // empty local part
    public void Default_Email_Rejects_Malformed_Shapes(string email)
        => Defaults().ValidateEmailFormat(email).ShouldNotBeNull();

    [Theory]
    [InlineData("+9647812345678")]
    [InlineData("9647812345678")]
    [InlineData("123456")]           // minimum length, no plus
    [InlineData("+123456789012345")] // 15 digits — E.164 max
    public void Default_Msisdn_Accepts_E164_Shapes(string msisdn)
        => Defaults().ValidateMsisdnFormat(msisdn).ShouldBeNull();

    [Theory]
    [InlineData("12345")]             // below the 6-digit floor shared with OtpProvider.IsMsisdn
    [InlineData("1234567890123456")]  // 16 digits — above the E.164 max
    [InlineData("+96478a2345678")]    // non-digit
    [InlineData("++9647812345678")]   // double plus
    public void Default_Msisdn_Rejects_Malformed_Shapes(string msisdn)
        => Defaults().ValidateMsisdnFormat(msisdn).ShouldNotBeNull();

    [Fact]
    public void Null_And_Empty_Values_Skip_Validation()
    {
        // Absent values are "not provided", not "malformed" — required-ness
        // is the caller's concern (e.g. UserService's registration chain).
        var cfg = Defaults();
        cfg.ValidateEmailFormat(null).ShouldBeNull();
        cfg.ValidateEmailFormat("").ShouldBeNull();
        cfg.ValidateMsisdnFormat(null).ShouldBeNull();
        cfg.ValidateMsisdnFormat("").ShouldBeNull();
    }

    [Fact]
    public void Override_File_Replaces_Default_Pattern_Per_Channel()
    {
        File.WriteAllText(_tmpFile, """{"msisdn": "^9647\\d{9}$"}""");
        var cfg = Build(_tmpFile);

        // Override applies to msisdn: Iraqi-local shape only.
        cfg.ValidateMsisdnFormat("9647123456789").ShouldBeNull();
        cfg.ValidateMsisdnFormat("+9647123456789").ShouldNotBeNull("override drops the optional '+'");
        cfg.ValidateMsisdnFormat("123456").ShouldNotBeNull("override requires the 9647 prefix");
        // Email key absent → default email pattern still applies.
        cfg.ValidateEmailFormat("user@example.com").ShouldBeNull();
        cfg.ValidateEmailFormat("not-an-email").ShouldNotBeNull();
    }

    [Fact]
    public void Invalid_Override_Pattern_Falls_Back_To_Default()
    {
        File.WriteAllText(_tmpFile, """{"email": "["}""");
        var cfg = Build(_tmpFile);
        cfg.ValidateEmailFormat("user@example.com").ShouldBeNull();
        cfg.ValidateEmailFormat("not-an-email").ShouldNotBeNull();
    }

    [Fact]
    public void Malformed_Json_Falls_Back_To_Defaults()
    {
        File.WriteAllText(_tmpFile, "{ this is not valid json");
        var cfg = Build(_tmpFile);
        cfg.ValidateEmailFormat("user@example.com").ShouldBeNull();
        cfg.ValidateMsisdnFormat("+9647812345678").ShouldBeNull();
    }

    [Fact]
    public void Catastrophic_Override_Times_Out_As_Invalid_Not_Exception()
    {
        // A ReDoS-prone override must not turn into an unhandled
        // RegexMatchTimeoutException (→ HTTP 500) at validation time. The
        // 100ms match timeout fires; the value is reported as invalid.
        File.WriteAllText(_tmpFile, """{"email": "^(a+)+$"}""");
        var cfg = Build(_tmpFile);
        var pathological = new string('a', 64) + "!";
        cfg.ValidateEmailFormat(pathological).ShouldNotBeNull();
    }
}
