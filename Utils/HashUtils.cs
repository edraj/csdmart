using System.Security.Cryptography;
using System.Text;

namespace Dmart.Utils;

public static class HashUtils
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> dst = stackalloc byte[32];
        SHA256.HashData(bytes, dst);
        return Convert.ToHexString(dst).ToLowerInvariant();
    }

    public static string Sha256Hex(string s) => Sha256Hex(Encoding.UTF8.GetBytes(s));
}
