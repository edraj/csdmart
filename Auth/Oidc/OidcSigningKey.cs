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
            if (!File.Exists(path) && Create(rsa, path))
                log.LogInformation("OIDC: created a new signing key in {File}", path);
            else
                rsa.ImportFromPem(File.ReadAllText(path));
            if (rsa.KeySize < 2048)
                throw new CryptographicException($"the OIDC signing key in {path} is {rsa.KeySize} bits; 2048 is the minimum");
            // A public key alone loads without complaint and fails the first
            // sign-in; say so now.
            try { _ = rsa.ExportParameters(includePrivateParameters: true); }
            catch (CryptographicException ex)
            {
                throw new CryptographicException($"{path} holds no RSA private key; the OIDC provider signs with it", ex);
            }
            if (!OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
                log.LogWarning("OIDC: the signing key {File} is readable by other users; chmod 600 it", path);

            var p = rsa.ExportParameters(includePrivateParameters: false);
            _n = Base64Url(p.Modulus!);
            _e = Base64Url(p.Exponent!);
            // RFC 7638: the required members, lexicographically, no whitespace.
            _kid = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes($$"""{"e":"{{_e}}","kty":"RSA","n":"{{_n}}"}""")));
            _rsa = rsa;
        }
    }

    // Writes a new key to `path`, whole or not at all: to a private temporary
    // file first, then renamed into place. False when another process got
    // there first (two hosts starting at once); the caller reads its key.
    private static bool Create(RSA rsa, string path)
    {
        rsa.KeySize = 2048;
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var file = new FileStream(temp, options))
            using (var writer = new StreamWriter(file))
                writer.Write(rsa.ExportPkcs8PrivateKeyPem());
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
        finally
        {
            File.Delete(temp);
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
            static bool Is(JsonElement o, string name, string? expected)
                => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == expected;
            if (h.ValueKind != JsonValueKind.Object || !Is(h, "alg", "RS256") || !Is(h, "kid", _kid) || !Is(h, "typ", type))
                return null;
            if (!_rsa!.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]),
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return null;
            return JsonDocument.Parse(FromBase64Url(parts[1]));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException)
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
