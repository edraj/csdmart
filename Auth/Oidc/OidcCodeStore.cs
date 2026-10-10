using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Dmart.Auth.Oidc;

// Authorization codes for the OIDC provider. In memory, single use, 60 seconds:
// a code only has to survive one redirect and one back-channel request, and a
// restart in between costs the user one more click. Separate from
// OAuthCodeStore (the MCP flow), which carries no nonce or auth time.
//
// In memory means ONE instance: a code issued by one dmart process cannot be
// redeemed at another, so a provider behind a load balancer needs sticky
// routing for /oidc/token or a single instance (docs/oidc-provider.md).
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
    // Live codes at most. Every one is a minute old at worst, so this is
    // 10,000 sign-ins a minute; past it a flood of authorize requests (each
    // cheap for its sender) would otherwise grow the table without bound.
    private const int MaxLive = 10_000;
    private readonly ConcurrentDictionary<string, Grant> _codes = new(StringComparer.Ordinal);

    // Null when MaxLive codes are outstanding.
    public string? Issue(string userShortname, string clientId, string redirectUri, string scope,
        string? nonce, string? codeChallenge, long authTime)
    {
        if (_codes.Count > SweepAbove) RemoveExpired();
        if (_codes.Count >= MaxLive) return null;
        var code = OidcSigningKey.Base64Url(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new Grant(userShortname, clientId, redirectUri, scope, nonce, codeChallenge, authTime,
            DateTime.UtcNow + Ttl);
        return code;
    }

    // The grant, if the code exists, is unexpired, was issued to this client
    // for this redirect URI, and the PKCE verifier matches the challenge it was
    // issued with. The code is spent whatever the outcome, so a wrong guess at
    // the verifier cannot be retried. A verifier for a code issued WITHOUT a
    // challenge is refused too (RFC 9700 §2.1.1): the client meant to use
    // PKCE, so the authorization request it sent was not the one that
    // arrived, which is what a downgrade looks like.
    public Grant? Consume(string code, string clientId, string redirectUri, string? codeVerifier)
    {
        if (!_codes.TryRemove(code, out var g)) return null;
        if (g.ExpiresAt < DateTime.UtcNow || g.ClientId != clientId || g.RedirectUri != redirectUri) return null;
        if (g.CodeChallenge is null) return string.IsNullOrEmpty(codeVerifier) ? g : null;
        if (string.IsNullOrEmpty(codeVerifier) || OAuthCodeStore.S256Challenge(codeVerifier) != g.CodeChallenge)
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
