using System.Security.Cryptography;

namespace Dmart.DataAdapters.Sql;

// dmart's URL shortener table is `urlshorts` (SQLAlchemy lowercased class name).
// The `timestamp` column holds the link's EXPIRY (local wall-clock, matching
// the rest of dmart's timezone-less storage); ResolveAsync refuses anything
// past it. Tokens are 128-bit CSPRNG so they can't be brute-force enumerated.
//
// Backend-neutral: the connection arrives as a DbConnection from whichever
// factory DATABASE_DRIVER selected, parameters bind through DbParams, and the
// one construct the two engines spell differently — the row's uuid default —
// is supplied by the caller rather than by SQL. PostgreSQL's
// gen_random_uuid() has no SQLite equivalent, and generating the value here
// is what the rest of the codebase already does anyway.
public sealed class LinkRepository(IDbConnectionFactory db)
{
    // 16 random bytes → 32 lowercase hex chars (128 bits). Replaces the old
    // 8–10 hex-char (32–40 bit) tokens, which were small enough to enumerate.
    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public async Task<string> CreateAsync(string url, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var token = NewToken();
        var expiresAt = TimeUtils.Now().Add(ttl ?? TimeSpan.FromHours(24));
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO urlshorts (uuid, token_uuid, url, timestamp)
            VALUES ($1, $2, $3, $4)
            """;
        DbParams.AddAll(cmd, Guid.NewGuid(), token, url, expiresAt);
        await cmd.ExecuteNonQueryAsync(ct);
        return token;
    }

    public async Task CreateWithTokenAsync(string token, string url, DateTime expiresAt, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // ON CONFLICT ... DO UPDATE with EXCLUDED is spelled identically by both
        // engines. Served by idx_urlshorts_token_uuid, which both schemas
        // declare — without a matching unique index the conflict target is a
        // hard error rather than a slow path.
        cmd.CommandText = """
            INSERT INTO urlshorts (uuid, token_uuid, url, timestamp)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (token_uuid) DO UPDATE SET url = EXCLUDED.url, timestamp = EXCLUDED.timestamp
            """;
        DbParams.AddAll(cmd, Guid.NewGuid(), token, url, expiresAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> ResolveAsync(string token, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        // Compare against a C# wall-clock value (naive, the same basis the
        // expiry was written with) rather than SQL NOW()/CURRENT_TIMESTAMP, to
        // avoid a session-timezone-dependent cast on PostgreSQL and a
        // UTC-vs-local mismatch on SQLite. Served by idx_urlshorts_token_uuid.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT url FROM urlshorts WHERE token_uuid = $1 AND timestamp > $2";
        DbParams.AddAll(cmd, token, TimeUtils.Now());
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (string)result;
    }
}
