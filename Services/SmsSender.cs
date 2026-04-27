using System.Net.Http.Headers;
using System.Text.Json;
using Dmart.Config;
using Dmart.Models.Json;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// Thin HTTP client wrapper around the configured SMS gateway endpoints.
// Mirrors Python's send_sms() / send_otp() in backend/api/user/service.py:
//   POST {endpoint}
//   Headers: Content-Type: application/json, auth-key: {smpp_auth_key}
//            [skel-accept-language: <lang>]         (OTP flow only)
//   Body:    {"msisdn": "<phone>", "text": "<message>"
//            [, "sender": "<SmsSender>"]}            (when configured)
//
// When the relevant endpoint URL or the auth key are empty the call is a
// no-op + warning log (Python does the same — features degrade gracefully
// when not configured). MOCK_SMPP_API=true short-circuits to a log entry
// without actually hitting the network so dev/CI doesn't spam a real gateway.
public sealed class SmsSender(
    IHttpClientFactory httpFactory,
    IOptions<DmartSettings> settings,
    ILogger<SmsSender> log)
{
    public Task<bool> SendOtpAsync(string msisdn, string text, string? language = null, CancellationToken ct = default)
        => SendAsync(settings.Value.SendSmsOtpApi, msisdn, text, language, ct);

    public Task<bool> SendAsync(string msisdn, string text, CancellationToken ct = default)
        => SendAsync(settings.Value.SendSmsApi, msisdn, text, language: null, ct);

    private async Task<bool> SendAsync(string endpoint, string msisdn, string text, string? language, CancellationToken ct)
    {
        var s = settings.Value;
        if (s.MockSmppApi)
        {
            log.LogWarning("MOCK_SMPP_API=true — not sending to {Msisdn}: {Text}", msisdn, text);
            return true;
        }
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(s.SmppAuthKey))
        {
            log.LogWarning("SMS gateway not configured (endpoint or SmppAuthKey blank) — dropping message to {Msisdn}", msisdn);
            return false;
        }

        var body = new Dictionary<string, object>
        {
            ["msisdn"] = msisdn,
            ["text"] = text,
        };
        // Python parity: `sms_sender` is optional. When set, the gateway sees
        // it as the from-name / shortcode shown to the recipient. Omit when
        // empty so gateways that reject unknown keys keep working.
        if (!string.IsNullOrWhiteSpace(s.SmsSender))
            body["sender"] = s.SmsSender;
        var json = JsonSerializer.Serialize(body, DmartJsonContext.Default.DictionaryStringObject);

        using var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("auth-key", s.SmppAuthKey);
        if (!string.IsNullOrWhiteSpace(language))
            req.Headers.TryAddWithoutValidation("skel-accept-language", language);

        try
        {
            using var resp = await client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                log.LogError("SMS gateway POST {Endpoint} failed: {Status} {Body}", endpoint, (int)resp.StatusCode, err);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SMS gateway POST {Endpoint} threw", endpoint);
            return false;
        }
    }
}
