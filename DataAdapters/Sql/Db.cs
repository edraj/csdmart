using Dmart.Config;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Thin connection factory. Singleton — opens a fresh NpgsqlConnection per call so we
// don't share state across handlers. Npgsql does its own pooling under the hood.
//
// Construction is non-throwing so the host can boot when PostgreSQL isn't configured
// (useful for tests, smoke checks, and graceful degradation). The connection-string
// requirement is enforced at OpenAsync time and surfaces as a clean exception in the
// failing handler rather than a DI resolution failure at startup.
public sealed class Db(IOptions<DmartSettings> settings)
{
    private readonly string? _conn = settings.Value.PostgresConnection;

    public bool IsConfigured => !string.IsNullOrEmpty(_conn);

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_conn))
            throw new InvalidOperationException("Dmart:PostgresConnection not configured");
        var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        return c;
    }
}
