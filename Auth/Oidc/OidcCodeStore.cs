using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Dmart.Auth.Oidc;

// Authorization codes for the OIDC provider. In memory, single use, 60 seconds:
// a code only has to survive one redirect and one back-channel request, and a
// restart in between costs the user one more click. Separate from
// OAuthCodeStore (the MCP flow), which carries no nonce or auth time.
public sealed class OidcCodeStore
{
    public sealed record Grant(
        string UserShortname,
        string ClientId,
        string RedirectUri,
        string Scope,
        string? Nonce,
        string? CodeChallenge,
        long AuthTime,
        DateTime ExpiresAt);

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private const int SweepAbove = 1024;
    private readonly ConcurrentDictionary<string, Grant> _codes = new(StringComparer.Ordinal);

    public string Issue(string userShortname, string clientId, string redirectUri, string scope,
        string? nonce, string? codeChallenge, long authTime)
    {
        if (_codes.Count > SweepAbove) RemoveExpired();
        var code = OidcSigningKey.Base64Url(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new Grant(userShortname, clientId, redirectUri, scope, nonce, codeChallenge, authTime,
            DateTime.UtcNow + Ttl);
        return code;
    }

    // The grant, if the code exists, is unexpired, was issued to this client
    // for this redirect URI, and the PKCE verifier matches the challenge it was
    // issued with. The code is spent whatever the outcome, so a wrong guess at
    // the verifier cannot be retried.
    public Grant? Consume(string code, string clientId, string redirectUri, string? codeVerifier)
    {
        if (!_codes.TryRemove(code, out var g)) return null;
        if (g.ExpiresAt < DateTime.UtcNow || g.ClientId != clientId || g.RedirectUri != redirectUri) return null;
        if (g.CodeChallenge is not null
            && (string.IsNullOrEmpty(codeVerifier) || OAuthCodeStore.S256Challenge(codeVerifier) != g.CodeChallenge))
            return null;
        return g;
    }

    private void RemoveExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var (code, g) in _codes)
            if (g.ExpiresAt < now) _codes.TryRemove(code, out _);
    }
}
