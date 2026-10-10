using System.Security.Cryptography;
using System.Text;

namespace Dmart.Auth;

// Password hashes carried over from an LDAP directory by `dmart import-ldif`,
// in the RFC 2307 / OpenLDAP {SCHEME} form, so imported users keep their
// passwords:
//
//   {SSHA} {SHA} {SSHA256} {SHA256} {SSHA512} {SHA512}
//       base64(digest(password + salt) + salt); unsalted forms have no salt
//   {ARGON2}$argon2id$...   OpenLDAP's argon2 module: a PHC string dmart's own
//       verifier reads once the prefix is gone (PasswordHasher handles it)
//
// Verification only. These are one round of SHA: cheap to compute, so cheap
// to brute-force if they leak. They are never written by dmart, and the first
// successful login or bind replaces them with Argon2id
// (UserService.RehashIfNeededAsync), whatever PasswordRehashOnLogin says.
internal static class LegacyLdapHash
{
    public const string Argon2Prefix = "{ARGON2}";

    private static (HashAlgorithmName Algorithm, int Length, bool Salted)? Scheme(string encoded)
    {
        var close = encoded.IndexOf('}', StringComparison.Ordinal);
        if (!encoded.StartsWith('{') || close < 0) return null;
        return encoded[1..close].ToUpperInvariant() switch
        {
            "SSHA" => (HashAlgorithmName.SHA1, 20, true),
            "SHA" => (HashAlgorithmName.SHA1, 20, false),
            "SSHA256" => (HashAlgorithmName.SHA256, 32, true),
            "SHA256" => (HashAlgorithmName.SHA256, 32, false),
            "SSHA512" => (HashAlgorithmName.SHA512, 64, true),
            "SHA512" => (HashAlgorithmName.SHA512, 64, false),
            _ => null,
        };
    }

    // A {SHA*} hash this class verifies itself. {ARGON2} is not one: its
    // payload is a native hash.
    public static bool IsSha(string? encoded) => encoded is not null && Scheme(encoded) is not null;

    // Anything stored in an LDAP form, which a login should replace.
    public static bool IsLegacy(string? encoded)
        => IsSha(encoded) || (encoded?.StartsWith(Argon2Prefix, StringComparison.OrdinalIgnoreCase) ?? false);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350",
        Justification = "Verifies {SSHA}/{SHA} hashes imported from LDAP so their owners can sign in once; dmart never writes them, and the first sign-in replaces them with Argon2id.")]
    public static bool Verify(string password, string encoded)
    {
        if (Scheme(encoded) is not { } scheme || password.Length == 0) return false;
        byte[] payload;
        try { payload = Convert.FromBase64String(encoded[(encoded.IndexOf('}', StringComparison.Ordinal) + 1)..].Trim()); }
        catch (FormatException) { return false; }
        if (payload.Length < scheme.Length || (!scheme.Salted && payload.Length != scheme.Length)) return false;

        var salt = payload.AsSpan(scheme.Length);
        var input = new byte[Encoding.UTF8.GetByteCount(password) + salt.Length];
        var written = Encoding.UTF8.GetBytes(password, input);
        salt.CopyTo(input.AsSpan(written));
        var actual = scheme.Algorithm == HashAlgorithmName.SHA1 ? SHA1.HashData(input)
            : scheme.Algorithm == HashAlgorithmName.SHA256 ? SHA256.HashData(input)
            : SHA512.HashData(input);
        CryptographicOperations.ZeroMemory(input);
        return CryptographicOperations.FixedTimeEquals(actual, payload.AsSpan(0, scheme.Length));
    }
}
