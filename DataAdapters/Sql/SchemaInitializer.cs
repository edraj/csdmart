using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Runs once on startup; creates tables if they don't exist. Idempotent.
public sealed class SchemaInitializer(Db db, ILogger<SchemaInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        if (!db.IsConfigured) return;

        // Use a PostgreSQL advisory lock so parallel test hosts (xUnit creates
        // multiple WebApplicationFactory instances) don't deadlock on concurrent
        // CREATE TABLE / ALTER TABLE statements against the same tables.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await using var conn = await db.OpenAsync(ct);
                // Advisory lock 1 — serializes schema init across connections.
                await using (var lk = new NpgsqlCommand("SELECT pg_advisory_lock(1)", conn))
                    await lk.ExecuteNonQueryAsync(ct);
                try
                {
                    await using var cmd = new NpgsqlCommand(SqlSchema.CreateAll, conn);
                    await cmd.ExecuteNonQueryAsync(ct);
                    log.LogInformation("dmart schema ready");
                }
                finally
                {
                    await using var ul = new NpgsqlCommand("SELECT pg_advisory_unlock(1)", conn);
                    await ul.ExecuteNonQueryAsync(ct);
                }
                return; // success
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == "40P01") // deadlock
            {
                if (attempt == 2) log.LogWarning("schema init deadlock after 3 attempts — continuing");
                else await Task.Delay(200 * (attempt + 1), ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "schema initialization failed — continuing without DB");
                return;
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
