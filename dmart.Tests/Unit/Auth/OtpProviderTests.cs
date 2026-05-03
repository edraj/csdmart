using System.Net.Http;
using Dmart.Auth;
using Dmart.Config;
using Dmart.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

// OtpProvider.Generate routes the mock-code short-circuit per delivery channel:
// the destination shape (msisdn vs email) decides which mock flag applies.
// These tests pin that contract so a regression like "MockSmtpApi=true also
// mocked SMS-delivered OTPs" doesn't reappear silently.
//
// The Send pipeline (HTTP / SMTP) isn't exercised here — Generate doesn't call
// it — so the senders can be constructed with a no-op IHttpClientFactory.
public class OtpProviderTests
{
    private static OtpProvider Build(DmartSettings s)
    {
        var opts = Options.Create(s);
        var sms = new SmsSender(new NoOpHttpClientFactory(), opts, NullLogger<SmsSender>.Instance);
        var smtp = new SmtpSender(opts, NullLogger<SmtpSender>.Instance);
        return new OtpProvider(opts, sms, smtp, NullLogger<OtpProvider>.Instance);
    }

    [Fact]
    public void MsisdnDestination_With_MockSmppApi_Returns_MockCode()
    {
        var otp = Build(new DmartSettings { MockSmppApi = true, MockSmtpApi = false, MockOtpCode = "111222" });
        otp.Generate("+96599887766").ShouldBe("111222");
    }

    [Fact]
    public void EmailDestination_With_MockSmtpApi_Returns_MockCode()
    {
        var otp = Build(new DmartSettings { MockSmppApi = false, MockSmtpApi = true, MockOtpCode = "999000" });
        otp.Generate("a@b.c").ShouldBe("999000");
    }

    // The pre-fix bug: MockSmtpApi=true short-circuited SMS-channel OTPs too.
    // After the fix, the SMS channel only honours MockSmppApi.
    [Fact]
    public void MsisdnDestination_When_Only_SmtpMocked_Returns_RealCode()
    {
        var otp = Build(new DmartSettings { MockSmppApi = false, MockSmtpApi = true, MockOtpCode = "111222" });
        var code = otp.Generate("+96599887766");
        code.Length.ShouldBe(6);
        code.ShouldNotBe("111222");
        foreach (var c in code) char.IsDigit(c).ShouldBeTrue();
    }

    [Fact]
    public void EmailDestination_When_Only_SmppMocked_Returns_RealCode()
    {
        var otp = Build(new DmartSettings { MockSmppApi = true, MockSmtpApi = false, MockOtpCode = "999000" });
        var code = otp.Generate("a@b.c");
        code.Length.ShouldBe(6);
        code.ShouldNotBe("999000");
    }

    // Shortname-shaped destination matches neither IsMsisdn nor IsEmail. Both
    // mocks active or not, Generate should return a real random code — callers
    // that care about predictable mock codes (password-reset-request) must
    // resolve the shortname to a deliverable identifier before calling.
    [Fact]
    public void ShortnameLikeDestination_With_BothMocks_Returns_RealCode()
    {
        var otp = Build(new DmartSettings { MockSmppApi = true, MockSmtpApi = true, MockOtpCode = "777888" });
        var code = otp.Generate("alice");
        code.Length.ShouldBe(6);
        code.ShouldNotBe("777888");
    }

    [Fact]
    public void NoMocks_Returns_SixDigitRandom()
    {
        var otp = Build(new DmartSettings { MockSmppApi = false, MockSmtpApi = false });
        var seen = new HashSet<string>();
        for (var i = 0; i < 8; i++) seen.Add(otp.Generate("+96599887766"));
        // Effectively no chance of collision across 8 calls of cryptographic random.
        seen.Count.ShouldBeGreaterThan(1);
        foreach (var c in seen) c.Length.ShouldBe(6);
    }

    private sealed class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
