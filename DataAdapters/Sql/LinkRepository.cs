using Npgsql;

namespace Dmart.DataAdapters.Sql;

// dmart's URL shortener table is `urlshorts` (SQLAlchemy lowercased class name).
public sealed class LinkRepository(Db db)
{
    public async Task<string> CreateAsync(string url, CancellationToken ct = default)
    {
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray()).Substring(0, 10).ToLowerInvariant();
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO urlshorts (uuid, token_uuid, url, timestamp)
            VALUES (gen_random_uuid(), $1, $2, NOW())
            """, conn);
        cmd.Parameters.Add(new() { Value = token });
        cmd.Parameters.Add(new() { Value = url });
        await cmd.ExecuteNonQueryAsync(ct);
        return token;
    }

    public async Task<string?> ResolveAsync(string token, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT url FROM urlshorts WHERE token_uuid = $1", conn);
        cmd.Parameters.Add(new() { Value = token });
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
