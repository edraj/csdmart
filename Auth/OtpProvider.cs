using System.Security.Cryptography;
using Dmart.Config;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Auth;

public sealed class OtpProvider(
    IOptions<DmartSettings> settings,
    SmsSender sms,
    ILogger<OtpProvider> log)
{
    public string Generate()
    {
        // In mock mode, return the configured mock code (for dev/testing).
        var s = settings.Value;
        if (s.MockSmtpApi || s.MockSmppApi)
        {
            log.LogWarning("OTP mock mode active — returning configured MockOtpCode");
            return s.MockOtpCode;
        }
        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
    }

    public async Task SendAsync(string destination, string code, CancellationToken ct = default)
    {
        // Dispatch:
        //   msisdn-shaped destination → SEND_SMS_OTP_API (configured) or log.
        //   anything else → log only (email delivery is still a TODO — the
        //   SMTP gateway hasn't been ported from Python yet).
        if (IsMsisdn(destination))
        {
            var sent = await sms.SendOtpAsync(destination, $"Your OTP code is {code}", language: null, ct);
            if (sent) return;
        }

        // Fallback: log so developers can retrieve the code from server logs.
        log.LogInformation("OTP for {Destination}: {Code} (delivery not implemented or SMS gateway unavailable)",
            destination, code);
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
