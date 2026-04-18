using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Auth.OAuth;

// Validates Apple-issued ID tokens. Apple doesn't have a tokeninfo-style
// introspection endpoint — we verify the JWT's signature ourselves against
// Apple's published JWKS (JSON Web Key Set at
// https://appleid.apple.com/auth/keys).
//
// Flow:
//   1. Split the JWT into header.payload.signature.
//   2. Parse the header, extract `kid` and `alg` (must be RS256).
//   3. Fetch JWKS (cached for 1 hour — Apple rotates roughly weekly, so
//      hourly refresh is safe + cheap).
//   4. Find the JWK whose `kid` matches. Decode `n` and `e` from base64url,
//      construct an RSA public key from those components.
//   5. Verify the RS256 signature over the "header.payload" string.
//   6. Parse the payload, check `iss == https://appleid.apple.com`,
//      `aud == AppleClientId`, and `exp` in the future.
//
// Fully AOT-safe — no System.IdentityModel.Tokens.Jwt, no reflection.
public sealed class AppleProvider(
    IHttpClientFactory httpFactory,
    IOptions<DmartSettings> settings,
    ILogger<AppleProvider> log)
{
    private const string JwksUrl = "https://appleid.apple.com/auth/keys";
    private const string ExpectedIssuer = "https://appleid.apple.com";
    private static readonly TimeSpan JwksCacheTtl = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _jwksLock = new(1, 1);
    private Dictionary<string, RsaPublicKey>? _jwks;
    private DateTime _jwksFetchedAt = DateTime.MinValue;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Value.AppleClientId);

    public async Task<OAuthUserInfo?> ValidateIdTokenAsync(string idToken, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(idToken)) return null;

        var parts = idToken.Split('.');
        if (parts.Length != 3)
        {
            log.LogWarning("apple id_token: not a valid JWT (need 3 parts)");
            return null;
        }

        string headerJson, payloadJson;
        byte[] signature;
        try
        {
            headerJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[0]));
            payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            signature = Base64UrlDecode(parts[2]);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "apple id_token: base64url decode failed");
            return null;
        }

        string? kid, alg;
        using (var headerDoc = JsonDocument.Parse(headerJson))
        {
            kid = GetString(headerDoc.RootElement, "kid");
            alg = GetString(headerDoc.RootElement, "alg");
        }
        if (!string.Equals(alg, "RS256", StringComparison.Ordinal))
        {
            log.LogWarning("apple id_token: unsupported alg {Alg}", alg);
            return null;
        }
        if (string.IsNullOrEmpty(kid))
        {
            log.LogWarning("apple id_token: missing kid");
            return null;
        }

        var keyOpt = await GetJwkAsync(kid, ct);
        if (keyOpt is not RsaPublicKey key)
        {
            log.LogWarning("apple id_token: no JWKS entry for kid {Kid}", kid);
            return null;
        }

        // Verify RS256 over "header.payload" — the raw bytes of the signing input.
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = key.Modulus, Exponent = key.Exponent });
        var signatureValid = rsa.VerifyData(
            signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (!signatureValid)
        {
            log.LogWarning("apple id_token: signature invalid");
            return null;
        }

        // Payload claim checks.
        using var payload = JsonDocument.Parse(payloadJson);
        var root = payload.RootElement;

        var iss = GetString(root, "iss");
        if (!string.Equals(iss, ExpectedIssuer, StringComparison.Ordinal))
        {
            log.LogWarning("apple id_token: iss mismatch {Iss}", iss);
            return null;
        }

        // `aud` may be a string or an array — handle both.
        var audOk = false;
        if (root.TryGetProperty("aud", out var audEl))
        {
            if (audEl.ValueKind == JsonValueKind.String)
                audOk = audEl.GetString() == settings.Value.AppleClientId;
            else if (audEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in audEl.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && el.GetString() == settings.Value.AppleClientId)
                    { audOk = true; break; }
            }
        }
        if (!audOk)
        {
            log.LogWarning("apple id_token: aud mismatch");
            return null;
        }

        if (root.TryGetProperty("exp", out var expEl) && expEl.ValueKind == JsonValueKind.Number)
        {
            var exp = DateTimeOffset.FromUnixTimeSeconds(expEl.GetInt64());
            if (exp < DateTimeOffset.UtcNow)
            {
                log.LogWarning("apple id_token: expired at {Exp}", exp);
                return null;
            }
        }

        var sub = GetString(root, "sub");
        if (string.IsNullOrEmpty(sub)) return null;

        return new OAuthUserInfo(
            Provider: "apple",
            ProviderId: sub,
            Email: GetString(root, "email"),
            // Apple doesn't include the display name in the id_token — it's
            // only passed the first time the user signs in (separate `user`
            // param). Leave blank here; the resolver handles that gracefully.
            FirstName: null,
            LastName: null,
            PictureUrl: null);
    }

    // ---- JWKS cache ----

    private async Task<RsaPublicKey?> GetJwkAsync(string kid, CancellationToken ct)
    {
        var jwks = await EnsureJwksAsync(ct);
        return jwks?.TryGetValue(kid, out var key) == true ? key : null;
    }

    private async Task<Dictionary<string, RsaPublicKey>?> EnsureJwksAsync(CancellationToken ct)
    {
        if (_jwks is not null && (DateTime.UtcNow - _jwksFetchedAt) < JwksCacheTtl)
            return _jwks;

        await _jwksLock.WaitAsync(ct);
        try
        {
            if (_jwks is not null && (DateTime.UtcNow - _jwksFetchedAt) < JwksCacheTtl)
                return _jwks;

            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            using var resp = await http.GetAsync(JwksUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("apple JWKS fetch {Status}", (int)resp.StatusCode);
                return _jwks;  // keep the stale copy if we have one
            }
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var parsed = new Dictionary<string, RsaPublicKey>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("keys", out var keys) &&
                keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in keys.EnumerateArray())
                {
                    var kid = GetString(k, "kid");
                    var kty = GetString(k, "kty");
                    var n   = GetString(k, "n");
                    var e   = GetString(k, "e");
                    if (string.IsNullOrEmpty(kid) || kty != "RSA" ||
                        string.IsNullOrEmpty(n) || string.IsNullOrEmpty(e))
                        continue;
                    try
                    {
                        parsed[kid] = new RsaPublicKey(Base64UrlDecode(n), Base64UrlDecode(e));
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "apple JWKS: failed to decode key {Kid}", kid);
                    }
                }
            }
            _jwks = parsed;
            _jwksFetchedAt = DateTime.UtcNow;
            return _jwks;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "apple JWKS fetch threw");
            return _jwks;
        }
        finally { _jwksLock.Release(); }
    }

    // ---- helpers ----

    private readonly record struct RsaPublicKey(byte[] Modulus, byte[] Exponent);

    internal static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string? GetString(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
