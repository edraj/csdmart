using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dmart.Config;
using Dmart.Models.Json;
using Microsoft.Extensions.Options;

namespace Dmart.Auth.Oidc;

// One relying party in OidcClientsFile (docs/oidc-provider.md). `set`, not
// `init`: with init-only properties the source-generated reader drops the
// initializers (ModelDefaultsTests). An unknown key is an error, not ignored:
// `redirect_uri` for `redirect_uris`, or `service` for `services`, would
// otherwise load as a client with no redirect URIs, or with no service gate.
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record OidcClientConfig
{
    public string ClientId { get; set; } = "";
    // Omitted for a public client (a browser app or native app), which must
    // then use PKCE.
    public string? ClientSecret { get; set; }
    public string? Name { get; set; }
    public List<string> RedirectUris { get; set; } = [];
    // Directory services (docs/user-directory-fields.md), any one of which a
    // user must hold to sign in here: the gate Dex expressed as an LDAP filter
    // on authorizedService. Empty: any active user.
    public List<string> Services { get; set; } = [];
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record OidcClientsDocument
{
    public List<OidcClientConfig> Clients { get; set; } = [];
}

// The relying parties, read from OidcClientsFile at startup and again when the
// file's timestamp changes (checked at most every few seconds). A file that
// fails validation keeps the previous list in service, and logs why; at
// startup it stops the host.
public sealed partial class OidcClients(IOptions<DmartSettings> settings, ILogger<OidcClients> log)
{
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private Dictionary<string, OidcClientConfig>? _clients;
    private DateTime _stamp;
    private DateTime _checkedAt;

    public void EnsureLoaded() => _ = Current();

    public OidcClientConfig? Find(string? clientId)
        => clientId is not null && Current().TryGetValue(clientId, out var c) ? c : null;

    public static bool IsPublic(OidcClientConfig client) => string.IsNullOrEmpty(client.ClientSecret);

    // Constant-time in the secret's content: both sides are hashed first, so
    // neither the comparison nor a length check leaks anything about it.
    public static bool SecretMatches(OidcClientConfig client, string? presented)
        => client.ClientSecret is { Length: > 0 } secret && presented is not null
           && CryptographicOperations.FixedTimeEquals(
               SHA256.HashData(Encoding.UTF8.GetBytes(secret)), SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

    // The user's directory services satisfy the client's gate.
    public static bool Admits(OidcClientConfig client, IReadOnlyCollection<string> userServices)
        => client.Services.Count == 0 || client.Services.Any(userServices.Contains);

    private Dictionary<string, OidcClientConfig> Current()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (_clients is not null && now - _checkedAt < RecheckEvery) return _clients;
            _checkedAt = now;
            var path = settings.Value.OidcClientsFile;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_clients is not null && stamp == _stamp) return _clients;
            try
            {
                _clients = Load(path);
                _stamp = stamp;
                log.LogInformation("OIDC: {Count} relying part(ies) from {File}", _clients.Count, path);
            }
            // Anything at all: a reload that fails must leave the clients in
            // service, not take the sign-in page down with it.
            catch (Exception ex) when (_clients is not null)
            {
                log.LogError(ex, "OIDC: {File} did not load; keeping the previous {Count} client(s)", path, _clients.Count);
            }
            return _clients;
        }
    }

    internal static Dictionary<string, OidcClientConfig> Load(string path)
    {
        var doc = JsonSerializer.Deserialize(File.ReadAllText(path), DmartJsonContext.Default.OidcClientsDocument)
            ?? throw new InvalidDataException("the clients file is empty");
        var result = new Dictionary<string, OidcClientConfig>(StringComparer.Ordinal);
        // An explicit null deserializes as null whatever the initializer says.
        foreach (var c in doc.Clients ?? throw new InvalidDataException("clients is required"))
        {
            if (c is null) throw new InvalidDataException("a client entry is null");
            if (c.ClientId is null || !ClientIdPattern().IsMatch(c.ClientId))
                throw new InvalidDataException($"client_id '{c.ClientId}' must be 1-128 of letters, digits, '.', '_' and '-'");
            if (!result.TryAdd(c.ClientId, c))
                throw new InvalidDataException($"client_id '{c.ClientId}' is listed twice");
            if (c.ClientSecret is { Length: < 16 })
                throw new InvalidDataException($"{c.ClientId}: client_secret must be at least 16 characters, or absent for a public client");
            if (c.RedirectUris is not { Count: > 0 })
                throw new InvalidDataException($"{c.ClientId}: redirect_uris is required");
            c.Services ??= [];
            foreach (var uri in c.RedirectUris)
            {
                // Exact-match only, and never somewhere a code would leak to:
                // https, or http on the loopback interface (native apps).
                if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.Fragment)
                    || !(u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback)))
                    throw new InvalidDataException(
                        $"{c.ClientId}: redirect_uri '{uri}' must be an absolute https URL (http only on loopback), without a fragment");
            }
            foreach (var service in c.Services)
                if (!Utils.DirectoryFields.IsValidService(service))
                    throw new InvalidDataException($"{c.ClientId}: '{service}' is not a valid service name");
        }
        return result;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex ClientIdPattern();
}
