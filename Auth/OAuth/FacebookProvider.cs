using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Auth.OAuth;

// Facebook OAuth: validates access tokens against Facebook's Graph API and
// fetches user profile fields.
//
// Two-step validation for the mobile flow:
//   1. debug_token — verifies the token is valid AND was issued for our app
//      (app_id == FacebookClientId).
//   2. /me — once the token is known-good, pull email + name + picture.
//
// Web callback does the usual code-for-token exchange first via
// /oauth/access_token, then runs the same debug_token + /me pair.
public sealed class FacebookProvider(
    IHttpClientFactory httpFactory,
    IOptions<DmartSettings> settings,
    ILogger<FacebookProvider> log)
{
    private const string GraphRoot = "https://graph.facebook.com";
    private const string DebugTokenUrl = GraphRoot + "/debug_token";
    private const string MeUrl = GraphRoot + "/me";
    private const string AccessTokenUrl = GraphRoot + "/v18.0/oauth/access_token";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.Value.FacebookClientId) &&
        !string.IsNullOrWhiteSpace(settings.Value.FacebookClientSecret);

    // Validates an access token + returns the user's profile. Returns null
    // on any failure (invalid token, wrong app, profile fetch fails).
    public async Task<OAuthUserInfo?> ValidateAccessTokenAsync(string accessToken, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(accessToken)) return null;

        var s = settings.Value;
        // Facebook's "app token" for debug_token is literally "{id}|{secret}".
        var appToken = $"{s.FacebookClientId}|{s.FacebookClientSecret}";

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        try
        {
            // 1. debug_token.
            var debugUrl = $"{DebugTokenUrl}?input_token={Uri.EscapeDataString(accessToken)}&access_token={Uri.EscapeDataString(appToken)}";
            using (var resp = await http.GetAsync(debugUrl, ct))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    log.LogWarning("facebook debug_token {Status}: {Body}", (int)resp.StatusCode,
                        Trim(await resp.Content.ReadAsStringAsync(ct), 200));
                    return null;
                }
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Object)
                {
                    log.LogWarning("facebook debug_token: missing data envelope");
                    return null;
                }
                if (!(data.TryGetProperty("is_valid", out var iv) &&
                      iv.ValueKind == JsonValueKind.True))
                {
                    log.LogWarning("facebook debug_token: token not valid");
                    return null;
                }
                var appId = data.TryGetProperty("app_id", out var ai) && ai.ValueKind == JsonValueKind.String
                    ? ai.GetString() : null;
                if (!string.Equals(appId, s.FacebookClientId, StringComparison.Ordinal))
                {
                    log.LogWarning("facebook debug_token app_id mismatch: got {Got}, expected {Expected}",
                        appId, s.FacebookClientId);
                    return null;
                }
            }

            // 2. /me with the fields we care about.
            var meUrl = $"{MeUrl}?fields=id,email,first_name,last_name,picture.type(large)&access_token={Uri.EscapeDataString(accessToken)}";
            using (var resp = await http.GetAsync(meUrl, ct))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    log.LogWarning("facebook /me {Status}: {Body}", (int)resp.StatusCode,
                        Trim(await resp.Content.ReadAsStringAsync(ct), 200));
                    return null;
                }
                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;
                var id = GetString(root, "id");
                if (string.IsNullOrEmpty(id)) return null;

                string? picture = null;
                if (root.TryGetProperty("picture", out var picWrap) &&
                    picWrap.TryGetProperty("data", out var picData) &&
                    picData.TryGetProperty("url", out var picUrl) &&
                    picUrl.ValueKind == JsonValueKind.String)
                {
                    picture = picUrl.GetString();
                }

                return new OAuthUserInfo(
                    Provider: "facebook",
                    ProviderId: id,
                    Email: GetString(root, "email"),
                    FirstName: GetString(root, "first_name"),
                    LastName: GetString(root, "last_name"),
                    PictureUrl: picture);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "facebook validation threw");
            return null;
        }
    }

    // Code-for-access-token exchange for the web callback flow.
    public async Task<string?> ExchangeCodeForAccessTokenAsync(string code, CancellationToken ct = default)
    {
        var s = settings.Value;
        if (string.IsNullOrWhiteSpace(s.FacebookClientId) ||
            string.IsNullOrWhiteSpace(s.FacebookClientSecret) ||
            string.IsNullOrWhiteSpace(s.FacebookOauthCallback))
            return null;

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        try
        {
            var url = $"{AccessTokenUrl}?client_id={Uri.EscapeDataString(s.FacebookClientId)}" +
                      $"&client_secret={Uri.EscapeDataString(s.FacebookClientSecret)}" +
                      $"&redirect_uri={Uri.EscapeDataString(s.FacebookOauthCallback)}" +
                      $"&code={Uri.EscapeDataString(code)}";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return GetString(doc.RootElement, "access_token");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "facebook code exchange threw");
            return null;
        }
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
