using System.Security.Cryptography;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Auth;

public sealed class OtpProvider(
    IOptions<DmartSettings> settings,
    SmsSender sms,
    SmtpSender smtp,
    LanguageLoader languages,
    ILogger<OtpProvider> log)
{
    public string Generate(string destination)
    {
        // Per-channel mock: only short-circuit when the channel that will
        // actually deliver this code is mocked. A half-mocked setup (e.g.
        // MockSmtpApi=true with a real SMS gateway) must still mint real
        // random codes for the live channel.
        var s = settings.Value;
        if (IsMsisdn(destination) && s.MockSmppApi)
        {
            log.LogWarning("OTP SMS mock active — returning configured MockOtpCode");
            return s.MockOtpCode;
        }
        if (IsEmail(destination) && s.MockSmtpApi)
        {
            log.LogWarning("OTP SMTP mock active — returning configured MockOtpCode");
            return s.MockOtpCode;
        }
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    public async Task SendAsync(string destination, string code,
        Language language = Language.En, CancellationToken ct = default)
    {
        // Dispatch:
        //   msisdn-shaped destination → SEND_SMS_OTP_API (configured) or log.
        //   email-shaped destination  → SMTP gateway (configured) or log.
        //   anything else             → log only.
        var body = RenderMessage(language, code);
        if (IsMsisdn(destination))
        {
            var sent = await sms.SendOtpAsync(destination, body,
                language: JsonbHelpers.EnumMember(language), ct);
            if (sent) return;
        }
        else if (IsEmail(destination))
        {
            // Python parity: email_send_otp() — HTML body containing the code.
            // Wrap the localized template in the same HTML shell Python uses,
            // so "Your OTP code is 123456" / "رمز التحقق…" both render bold.
            var html = $"<p>{System.Net.WebUtility.HtmlEncode(body)}</p>";
            var sent = await smtp.SendEmailAsync(destination, RenderSubject(language), html, ct);
            if (sent) return;
        }

        // Delivery failed or no gateway is configured. Record that at Warning
        // (no secret), and emit the code ONLY at Debug — production runs at
        // Information+ so the live OTP never lands in production logs, while a
        // dev with no gateway can still retrieve it by enabling Debug logging.
        log.LogWarning("OTP for {Destination} not delivered (no gateway configured or send failed)", destination);
        log.LogDebug("OTP code for {Destination}: {Code}", destination, code);
    }

    // Resolves `otp_message` from the loaded languages and substitutes the
    // `{code}` placeholder. Falls back to the historical English literal when
    // the key isn't loaded — mirrors Python's send_otp() which does the same
    // dictionary lookup with a hard-coded fallback. Operators override the
    // template by dropping a JSON file at ~/.dmart/languages/<lang>.json with
    // an `otp_message` key (LanguageLoader strategy 3).
    //
    // Internal so the unit suite can pin the rendering contract without
    // standing up the full SMS / SMTP send pipeline.
    internal string RenderMessage(Language language, string code)
    {
        const string fallback = "Your OTP code is {code}";
        var template = languages.Get(language, "otp_message") ?? fallback;
        var ttlMinutes = (settings.Value.OtpTokenTtl / 60).ToString();
        return template
            .Replace("{code}", code, StringComparison.Ordinal)
            .Replace("{otp_ttl}", ttlMinutes, StringComparison.Ordinal);
    }

    // Email subject counterpart to RenderMessage. Resolves `otp_email_subject`
    // through the same LanguageLoader path so the subject is served in the
    // recipient's language and stays operator-overridable at
    // ~/.dmart/languages/<locale>.json without a rebuild. Falls back to the
    // historical "OTP" literal — a blank subject is spam-filter bait, so a
    // deployment with no language files must still get a usable one.
    //
    // IsNullOrWhiteSpace, not `?? "OTP"`: the override path is a JSON file an
    // operator edits by hand, where the natural way to say "I don't want a
    // subject" is `"otp_email_subject": ""`, not omitting the key. A
    // null-only guard sends that straight through as a blank Subject header —
    // exactly the case the fallback exists to prevent.
    //
    // No {code} substitution: putting the OTP in the subject line leaks it to
    // lock-screen notification previews and mail-server logs.
    internal string RenderSubject(Language language)
    {
        var subject = languages.Get(language, "otp_email_subject");
        return string.IsNullOrWhiteSpace(subject) ? "OTP" : subject;
    }

    // Lightweight email heuristic — good enough for dispatch routing; the OTP
    // flow validates the full address format upstream when the user registered.
    private static bool IsEmail(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;
        var at = destination.IndexOf('@');
        return at > 0 && at < destination.Length - 1 && destination.IndexOf('.', at) > at;
    }

    // Phone-number heuristic: +<digits> or pure digits of length 6+. Matches
    // Python's User.msisdn regex behaviour for typical E.164 inputs.
    private static bool IsMsisdn(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return false;
        var s = destination.StartsWith('+') ? destination[1..] : destination;
        if (s.Length < 6) return false;
        foreach (var c in s) if (!char.IsDigit(c)) return false;
        return true;
    }
}
