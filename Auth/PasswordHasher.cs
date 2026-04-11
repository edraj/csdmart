using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Dmart.Auth;

// Argon2id matching dmart Python's utils/password_hashing.py exactly:
//   ph = PasswordHasher(memory_cost=102400, time_cost=3, parallelism=8)
//
// Output is the standard PHC string format that dmart Python's argon2-cffi
// PasswordHasher produces:
//   $argon2id$v=19$m=102400,t=3,p=8$<base64 salt>$<base64 hash>
//
// Salt and hash are base64 with padding stripped (dmart's argon2-cffi convention).
// Both sides can verify each other's hashes byte-for-byte.
public sealed class PasswordHasher
{
    private const int MemoryKb = 102_400;
    private const int Iterations = 3;
    private const int Parallelism = 8;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Argon2Version = 19;   // 0x13

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = ComputeArgon2id(password, salt, MemoryKb, Iterations, Parallelism, HashSize);
        return $"$argon2id$v={Argon2Version}$m={MemoryKb},t={Iterations},p={Parallelism}$" +
               $"{Base64NoPad(salt)}${Base64NoPad(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        // PHC string format: $argon2id$v=19$m=...,t=...,p=...$<salt>$<hash>
        // Split by '$' yields 6 parts (the leading '$' produces an empty first element).
        var parts = encoded.Split('$');
        if (parts.Length != 6) return false;
        if (parts[1] != "argon2id") return false;
        if (!parts[2].StartsWith("v=", StringComparison.Ordinal)) return false;

        var paramSegments = parts[3].Split(',');
        int m = 0, t = 0, p = 0;
        foreach (var seg in paramSegments)
        {
            var kv = seg.Split('=');
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "m": int.TryParse(kv[1], out m); break;
                case "t": int.TryParse(kv[1], out t); break;
                case "p": int.TryParse(kv[1], out p); break;
            }
        }
        if (m <= 0 || t <= 0 || p <= 0) return false;

        byte[] salt, expected;
        try
        {
            salt = Base64FromNoPad(parts[4]);
            expected = Base64FromNoPad(parts[5]);
        }
        catch
        {
            return false;
        }

        var actual = ComputeArgon2id(password, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] ComputeArgon2id(string password, byte[] salt, int memoryKb, int iterations, int parallelism, int outputLength)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            Iterations = iterations,
            MemorySize = memoryKb,
        };
        return argon2.GetBytes(outputLength);
    }

    private static string Base64NoPad(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] Base64FromNoPad(string s)
    {
        var pad = (4 - (s.Length % 4)) % 4;
        return Convert.FromBase64String(s + new string('=', pad));
    }
}
