using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Auth.OAuth;

// Validates Google-issued ID tokens via Google's public `tokeninfo` endpoint
// and exchanges authorization codes via `oauth2.googleapis.com/token`. Two
// flows:
//
//   * Mobile: client has an id_token obtained via Google Sign-In SDK.
//     ValidateIdTokenAsync POSTs the token to tokeninfo; Google decodes +
//     signature-checks it server-side and returns the claims to us.
//   * Web callback: ExchangeCodeForIdTokenAsync POSTs the authorization
//     code + our client_secret to the token endpoint; Google responds with
//     an id_token which we then pass through ValidateIdTokenAsync.
//
// Both paths funnel into an OAuthUserInfo that the resolver translates into
// a dmart User.
public sealed class GoogleProvider(
    IHttpClientFactory httpFactory,
    IOptions<DmartSettings> settings,
    ILogger<GoogleProvider> log)
{
    private const string TokenInfoUrl = "https://oauth2.googleapis.com/tokeninfo";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Value.GoogleClientId);

    // Validates an id_token against Google's tokeninfo endpoint. Verifies
    // `aud == GoogleClientId`. Returns null on any failure — callers surface
    // that as "invalid google id token" without leaking details.
    public async Task<OAuthUserInfo?> ValidateIdTokenAsync(string idToken, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(idToken)) return null;

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        try
        {
            using var resp = await http.GetAsync(
                $"{TokenInfoUrl}?id_token={Uri.EscapeDataString(idToken)}", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                log.LogWarning("google tokeninfo {Status}: {Body}", (int)resp.StatusCode, Trim(err, 200));
                return null;
            }
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            // Audience must match our registered client id, else someone is
            // presenting a token issued for a different app.
            var aud = GetString(root, "aud");
            if (!string.Equals(aud, settings.Value.GoogleClientId, StringComparison.Ordinal))
            {
                log.LogWarning("google tokeninfo aud mismatch: got {Got}, expected {Expected}",
                    aud, settings.Value.GoogleClientId);
                return null;
            }

            var sub = GetString(root, "sub");
            if (string.IsNullOrEmpty(sub)) return null;

            return new OAuthUserInfo(
                Provider: "google",
                ProviderId: sub,
                Email: GetString(root, "email"),
                FirstName: GetString(root, "given_name"),
                LastName: GetString(root, "family_name"),
                PictureUrl: GetString(root, "picture"));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "google tokeninfo threw");
            return null;
        }
    }

    // Authorization-code exchange for the web callback flow. Returns the
    // id_token from the response, which the caller then runs through
    // ValidateIdTokenAsync for consistent audience checks.
    public async Task<string?> ExchangeCodeForIdTokenAsync(string code, CancellationToken ct = default)
    {
        var s = settings.Value;
        if (string.IsNullOrWhiteSpace(s.GoogleClientId) ||
            string.IsNullOrWhiteSpace(s.GoogleClientSecret) ||
            string.IsNullOrWhiteSpace(s.GoogleOauthCallback))
            return null;

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = s.GoogleClientId,
                ["client_secret"] = s.GoogleClientSecret,
                ["redirect_uri"] = s.GoogleOauthCallback,
                ["grant_type"] = "authorization_code",
            });
            using var resp = await http.PostAsync(TokenUrl, form, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("google token exchange {Status}: {Body}",
                    (int)resp.StatusCode, Trim(await resp.Content.ReadAsStringAsync(ct), 200));
                return null;
            }
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            return GetString(doc.RootElement, "id_token");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "google token exchange threw");
            return null;
        }
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
