using System.Net.Http;
using Dmart.Auth;
using Dmart.Config;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Logging;
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
        // Generate-path tests don't exercise message rendering, so a minimal
        // (unloaded) LanguageLoader is sufficient — Get returns null and the
        // Send path falls back to the hardcoded English literal.
        var languages = new LanguageLoader(NullLogger<LanguageLoader>.Instance);
        return new OtpProvider(opts, sms, smtp, languages, NullLogger<OtpProvider>.Instance);
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

    // ---- RenderMessage / language overlay ----

    // Builds an OtpProvider whose LanguageLoader has Load() called against
    // the embedded language resources — covers the production happy path.
    private static OtpProvider BuildWithLoadedLanguages()
    {
        var s = new DmartSettings();
        var opts = Options.Create(s);
        var sms = new SmsSender(new NoOpHttpClientFactory(), opts, NullLogger<SmsSender>.Instance);
        var smtp = new SmtpSender(opts, NullLogger<SmtpSender>.Instance);
        var languages = new LanguageLoader(NullLogger<LanguageLoader>.Instance);
        languages.Load();
        return new OtpProvider(opts, sms, smtp, languages, NullLogger<OtpProvider>.Instance);
    }

    [Fact]
    public void RenderMessage_English_Uses_Loaded_Template()
    {
        var otp = BuildWithLoadedLanguages();
        otp.RenderMessage(Language.En, "654321").ShouldBe("Your OTP code is 654321");
    }

    [Fact]
    public void RenderMessage_Arabic_Uses_Loaded_Template()
    {
        // Pinned to the exact wording shipped in languages/arabic.json. If
        // operators want a different message, they override at
        // ~/.dmart/languages/arabic.json (LanguageLoader strategy 3).
        var otp = BuildWithLoadedLanguages();
        otp.RenderMessage(Language.Ar, "987654").ShouldBe("رمز التحقق الخاص بك هو 987654");
    }

    [Fact]
    public void RenderMessage_Kurdish_Uses_Loaded_Template()
    {
        var otp = BuildWithLoadedLanguages();
        otp.RenderMessage(Language.Ku, "112233").ShouldBe("کۆدی پشتڕاستکردنەوەکەت 112233 ە");
    }

    [Fact]
    public void RenderMessage_Falls_Back_To_English_Literal_When_Languages_Empty()
    {
        // Unloaded LanguageLoader → Get returns null → fallback literal kicks
        // in. The hardcoded English literal is intentional: a misconfigured
        // deployment must still send a usable OTP, not an empty body.
        var otp = Build(new DmartSettings());
        otp.RenderMessage(Language.Ar, "424242").ShouldBe("Your OTP code is 424242");
    }

    [Fact]
    public void RenderMessage_Falls_Back_To_English_When_Locale_Lacks_Key()
    {
        // French / Turkish locale files don't ship `otp_message`. LanguageLoader.Get
        // falls back to the English entry — pin that contract here so an
        // operator who adds Fr/Tr without otp_message gets an English OTP
        // instead of a literal "{code}" or empty string.
        var otp = BuildWithLoadedLanguages();
        otp.RenderMessage(Language.Fr, "424242").ShouldBe("Your OTP code is 424242");
    }

    // ---- RenderSubject / email subject localization ----
    //
    // The email subject used to be the hardcoded literal "OTP" at the
    // SendEmailAsync call site, so operators had no way to brand it or serve
    // it per the recipient's language. It now resolves `otp_email_subject`
    // through the same LanguageLoader path as the body, which makes it
    // overridable at ~/.dmart/languages/<locale>.json (strategy 3).

    [Fact]
    public void RenderSubject_English_Uses_Loaded_Template()
    {
        var otp = BuildWithLoadedLanguages();
        otp.RenderSubject(Language.En).ShouldBe("Your Verification Code");
    }

    [Fact]
    public void RenderSubject_Arabic_Uses_Loaded_Template()
    {
        var otp = BuildWithLoadedLanguages();
        otp.RenderSubject(Language.Ar).ShouldBe("\u0631\u0645\u0632 \u0627\u0644\u062a\u062d\u0642\u0642");
    }

    [Fact]
    public void RenderSubject_Kurdish_Uses_Loaded_Template()
    {
        var otp = BuildWithLoadedLanguages();
        otp.RenderSubject(Language.Ku).ShouldBe("\u06a9\u06c6\u062f\u06cc \u067e\u0634\u062a\u0695\u0627\u0633\u062a\u06a9\u0631\u062f\u0646\u06d5\u0648\u06d5");
    }

    [Fact]
    public void RenderSubject_Falls_Back_To_Literal_When_Languages_Empty()
    {
        // Same contract as RenderMessage: a misconfigured deployment must
        // still send a subject line, not an empty one (blank subjects are
        // spam-filter bait).
        var otp = Build(new DmartSettings());
        otp.RenderSubject(Language.Ar).ShouldBe("OTP");
    }

    [Fact]
    public void RenderSubject_Falls_Back_To_English_When_Locale_Lacks_Key()
    {
        // Fr/Tr locale files don't ship otp_email_subject — LanguageLoader.Get
        // falls back to the English entry rather than returning null.
        var otp = BuildWithLoadedLanguages();
        otp.RenderSubject(Language.Fr).ShouldBe("Your Verification Code");
    }

    // Pins the wiring, not just the renderer: SendAsync must hand the
    // localized subject to SmtpSender rather than the old "OTP" literal.
    // MOCK_SMTP_API short-circuits delivery and logs "<to>: <subject>", so a
    // capturing logger observes the subject without a live SMTP gateway.
    [Fact]
    public async Task SendAsync_Email_Passes_Localized_Subject_To_Smtp()
    {
        var s = new DmartSettings { MockSmtpApi = true };
        var opts = Options.Create(s);
        var smtpLog = new CapturingLogger<SmtpSender>();
        var sms = new SmsSender(new NoOpHttpClientFactory(), opts, NullLogger<SmsSender>.Instance);
        var smtp = new SmtpSender(opts, smtpLog);
        var languages = new LanguageLoader(NullLogger<LanguageLoader>.Instance);
        languages.Load();
        var otp = new OtpProvider(opts, sms, smtp, languages, NullLogger<OtpProvider>.Instance);

        await otp.SendAsync("a@b.co", "123456", Language.Ar);

        smtpLog.Messages.ShouldContain(m => m.Contains("\u0631\u0645\u0632 \u0627\u0644\u062a\u062d\u0642\u0642", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
