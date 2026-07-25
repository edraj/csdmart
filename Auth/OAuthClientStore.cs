using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Dmart.Auth;

// In-memory registry of OAuth 2.1 dynamic-client-registration entries (RFC 7591).
// MCP clients (Claude Desktop, Cursor, Zed, etc.) register themselves at
// /oauth/register and get back a client_id they use for the subsequent
// authorize+token flow. All MCP clients are *public* clients — no
// client_secret is issued, PKCE is the confidentiality primitive.
//
// Restarts wipe the registry, which is fine: clients re-register on the next
// call. If we need durability later (to survive redeploys without the client
// having to re-register), persist to a DB table.
public sealed class OAuthClientStore
{
    public sealed record Client(
        string ClientId,
        IReadOnlyList<string> RedirectUris,
        string ClientName,
        DateTime CreatedAt);

    private readonly ConcurrentDictionary<string, Client> _clients = new();

    // Registration is unauthenticated and every accepted record is retained in
    // this process singleton until OAuthStoreSweeper evicts it 24h later. With
    // Kestrel's 50MB body limit and no caps, one anonymous caller could pin
    // hundreds of MB of strings by registering huge redirect_uris lists. Real
    // MCP clients register one or two loopback callbacks with a short name, so
    // these limits are generous by an order of magnitude.
    private const int MaxRedirectUris = 8;
    private const int MaxRedirectUriLength = 512;
    private const int MaxClientNameLength = 128;

    public Client Register(IEnumerable<string> redirectUris, string clientName)
    {
        var uris = redirectUris.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (uris.Count == 0)
            throw new ArgumentException("at least one redirect_uri required");
        if (uris.Count > MaxRedirectUris)
            throw new ArgumentException($"at most {MaxRedirectUris} redirect_uris allowed");
        if (clientName.Length > MaxClientNameLength)
            throw new ArgumentException($"client_name must be at most {MaxClientNameLength} characters");

        // Reject non-http(s) schemes at registration time. RFC 8252 and the
        // OAuth 2.1 draft require redirect URIs to be http(s) URLs (or custom
        // schemes for native apps — not supported here). Without this, a
        // client could register `javascript:alert(1)` or `data:text/html,...`
        // and the authorize flow would redirect the victim into that URL.
        foreach (var u in uris)
        {
            if (u.Length > MaxRedirectUriLength)
                throw new ArgumentException(
                    $"redirect_uri must be at most {MaxRedirectUriLength} characters");
            if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed))
                throw new ArgumentException($"redirect_uri is not an absolute URI: {u}");
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException($"redirect_uri must use http or https: {u}");
            if (!string.IsNullOrEmpty(parsed.Fragment))
                throw new ArgumentException($"redirect_uri must not carry a fragment: {u}");
        }

        var clientId = GenerateClientId();
        var client = new Client(clientId, uris, clientName, TimeUtils.Now());
        _clients[clientId] = client;
        return client;
    }

    public Client? Get(string clientId) =>
        _clients.TryGetValue(clientId, out var c) ? c : null;

    public bool ValidateRedirectUri(string clientId, string redirectUri)
    {
        if (!_clients.TryGetValue(clientId, out var c)) return false;
        foreach (var u in c.RedirectUris)
            if (string.Equals(u, redirectUri, StringComparison.Ordinal)) return true;
        return false;
    }

    // Removes clients registered more than `maxAge` ago. Called by
    // OAuthStoreSweeper so the dictionary doesn't grow unbounded from
    // abandoned MCP registrations. Real clients re-register on startup.
    public void RemoveOlderThan(TimeSpan maxAge)
    {
        var cutoff = TimeUtils.Now() - maxAge;
        foreach (var (key, client) in _clients)
        {
            if (client.CreatedAt < cutoff)
                _clients.TryRemove(key, out _);
        }
    }

    private static string GenerateClientId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return "mcp_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
