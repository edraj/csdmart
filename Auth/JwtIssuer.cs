using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Auth;

// Hand-rolled HS256 JWT — fully AOT-safe (no reflection, no JwtSecurityTokenHandler).
// The JWT payload is built directly with Utf8JsonWriter so we don't depend on
// source-gen JSON metadata for any value types (string[]/long/etc.) at runtime.
// Microsoft.AspNetCore.Authentication.JwtBearer can validate these as long as it's
// configured with the same SymmetricSecurityKey.
public sealed class JwtIssuer(IOptions<DmartSettings> settings)
{
    private readonly DmartSettings _s = settings.Value;

    public string IssueAccess(string subject, IEnumerable<string>? roles = null)
        => Sign(subject, roles, TimeSpan.FromMinutes(_s.JwtAccessMinutes));

    public string IssueRefresh(string subject)
        => Sign(subject, null, TimeSpan.FromDays(_s.JwtRefreshDays));

    private string Sign(string subject, IEnumerable<string>? roles, TimeSpan lifetime)
    {
        var now = DateTimeOffset.UtcNow;

        using var payloadStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(payloadStream))
        {
            writer.WriteStartObject();
            writer.WriteString("sub", subject);
            writer.WriteString("iss", _s.JwtIssuer);
            writer.WriteString("aud", _s.JwtAudience);
            writer.WriteNumber("iat", now.ToUnixTimeSeconds());
            writer.WriteNumber("exp", now.Add(lifetime).ToUnixTimeSeconds());
            writer.WriteString("jti", Guid.NewGuid().ToString("n"));
            if (roles is not null)
            {
                writer.WriteStartArray("roles");
                foreach (var r in roles) writer.WriteStringValue(r);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }

        // Hand-crafted header keeps us off any JsonContext for header fields too.
        const string headerJson = """{"alg":"HS256","typ":"JWT"}""";
        var encodedHeader  = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var encodedPayload = Base64UrlEncode(payloadStream.ToArray());
        var signingInput   = $"{encodedHeader}.{encodedPayload}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_s.JwtSecret));
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        var encodedSignature = Base64UrlEncode(signature);
        return $"{signingInput}.{encodedSignature}";
    }

    public static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
