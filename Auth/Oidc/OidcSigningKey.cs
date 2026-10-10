using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Auth.Oidc;

// The OIDC provider's signing key (docs/oidc-provider.md): one RSA key, kept as
// PKCS#8 PEM in OidcSigningKeyFile and created there (mode 0600) the first time
// the provider starts. It signs ID tokens and the provider's access tokens with
// RS256, the one algorithm every relying party must accept.
//
// RS256 because dmart's own session JWTs are HS256 with a shared secret, which
// a relying party could only verify by holding that secret, and with it mint
// sessions on dmart. An asymmetric key lets it verify without being able to
// sign.
//
// The key id is the RFC 7638 thumbprint, so it changes exactly when the key
// does. Rotation is replacing the file and restarting; tokens signed with the
// old key stop verifying, which their short lifetime (OidcTokenSeconds) bounds.
public sealed class OidcSigningKey(IOptions<DmartSettings> settings, ILogger<OidcSigningKey> log) : IDisposable
{
    private readonly Lock _gate = new();
    private RSA? _rsa;
    private string? _kid;
    private string? _n;
    private string? _e;

    public string KeyId { get { EnsureLoaded(); return _kid!; } }

    // Loads the key, creating it first if the file does not exist. Called at
    // startup when the provider is on, so a key that cannot be read stops the
    // host instead of failing the first login.
    public void EnsureLoaded()
    {
        if (_rsa is not null) return;
        lock (_gate)
        {
            if (_rsa is not null) return;
            var path = settings.Value.OidcSigningKeyFile;
            var rsa = RSA.Create();
            if (File.Exists(path))
            {
                rsa.ImportFromPem(File.ReadAllText(path));
            }
            else
            {
                rsa.KeySize = 2048;
                // CreateNew: two hosts starting at once must not both write
                // a key; the loser fails here and reads the winner's on retry.
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(path, options))
                using (var writer = new StreamWriter(file))
                    writer.Write(rsa.ExportPkcs8PrivateKeyPem());
                log.LogInformation("OIDC: created a new signing key in {File}", path);
            }
            if (rsa.KeySize < 2048)
                throw new CryptographicException($"the OIDC signing key in {path} is {rsa.KeySize} bits; 2048 is the minimum");

            var p = rsa.ExportParameters(includePrivateParameters: false);
            _n = Base64Url(p.Modulus!);
            _e = Base64Url(p.Exponent!);
            // RFC 7638: the required members, lexicographically, no whitespace.
            _kid = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes($$"""{"e":"{{_e}}","kty":"RSA","n":"{{_n}}"}""")));
            _rsa = rsa;
        }
    }

    // A compact JWS over `payload` (UTF-8 JSON). `type` is the header's typ:
    // "JWT" for ID tokens, "at+jwt" for access tokens (RFC 9068), so one can
    // never be passed off as the other.
    public string Sign(ReadOnlySpan<byte> payload, string type)
    {
        EnsureLoaded();
        var header = Base64Url(Encoding.UTF8.GetBytes($$"""{"alg":"RS256","typ":"{{type}}","kid":"{{_kid}}"}"""));
        var signingInput = header + "." + Base64Url(payload);
        var signature = _rsa!.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    // Verifies one of this provider's own tokens: signature, key id and typ.
    // Returns the payload, or null. Expiry and audience are the caller's to
    // check: they depend on what the token is being used for.
    public JsonDocument? Verify(string token, string type)
    {
        EnsureLoaded();
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            using var header = JsonDocument.Parse(FromBase64Url(parts[0]));
            var h = header.RootElement;
            if (h.ValueKind != JsonValueKind.Object
                || !h.TryGetProperty("alg", out var alg) || alg.GetString() != "RS256"
                || !h.TryGetProperty("kid", out var kid) || kid.GetString() != _kid
                || !h.TryGetProperty("typ", out var typ) || typ.GetString() != type)
                return null;
            if (!_rsa!.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return null;
            return JsonDocument.Parse(FromBase64Url(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    // The public key as a JWK Set (RFC 7517).
    public void WriteJwks(Utf8JsonWriter w)
    {
        EnsureLoaded();
        w.WriteStartObject();
        w.WriteStartArray("keys");
        w.WriteStartObject();
        w.WriteString("kty", "RSA");
        w.WriteString("use", "sig");
        w.WriteString("alg", "RS256");
        w.WriteString("kid", _kid);
        w.WriteString("n", _n);
        w.WriteString("e", _e);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String((s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s });
    }

    public void Dispose() => _rsa?.Dispose();
}
