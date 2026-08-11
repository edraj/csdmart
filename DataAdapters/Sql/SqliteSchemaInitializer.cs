using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Dmart.DataAdapters.Sql;

// Creates and patches the SQLite schema. Idempotent — safe to re-run.
//
// The PostgreSQL initializer takes pg_advisory_lock(1) to serialize concurrent
// starts. There is no analogue here and none is needed: SQLite serializes
// writers at the database level, so a second process running CreateAll
// concurrently blocks on the write lock rather than racing. What it CAN hit is
// SQLITE_BUSY while waiting, which is why the whole thing runs through
// SqliteRetry.
//
// The PostgreSQL schema does its forward-compatibility patching in SQL
// (`ADD COLUMN IF NOT EXISTS`) and its conditional index creation in PL/pgSQL
// DO blocks. SQLite has neither, so both move here — see PatchColumnsAsync.
public sealed class SqliteSchemaInitializer(
    SqliteConnectionFactory factory,
    Microsoft.Extensions.Options.IOptions<Dmart.Config.DmartSettings> settings,
    ILogger<SqliteSchemaInitializer> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Both schema initializers are registered because the driver is not
        // known at registration time; each declines when it is not the one
        // selected. Creating a SQLite schema in a PostgreSQL deployment would
        // leave a stray database file nothing ever reads.
        if (!DatabaseDriverParser.TryParse(settings.Value.DatabaseDriver, out var driver)
            || driver != DatabaseDriver.Sqlite) return;

        var ct = cancellationToken;
        await SqliteRetry.ExecuteAsync(async token =>
        {
            await using var conn = await factory.OpenAsync(token);

            // One transaction: a half-created schema is worse than none, and
            // SQLite DDL is transactional (unlike some other engines).
            await using var tx = await conn.BeginTransactionAsync(token);
            try
            {
                await ExecAsync(conn, SqliteSchema.CreateAll, token);
                await PatchColumnsAsync(conn, token);
                await tx.CommitAsync(token);
            }
            catch
            {
                await tx.RollbackAsync(CancellationToken.None);
                throw;
            }
        }, ct);

        log.LogInformation("sqlite: schema ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // SQLite's ALTER TABLE has no IF NOT EXISTS, so probe pragma_table_info and
    // add only what is missing. This is the equivalent of the PostgreSQL
    // schema's ADD COLUMN IF NOT EXISTS block: it brings a database created by
    // an older build up to the columns the current SELECTs and INSERTs
    // reference.
    //
    // SQLite restricts what ALTER TABLE ADD COLUMN accepts — notably a NOT NULL
    // column must carry a non-null default, and a UNIQUE or PRIMARY KEY column
    // is rejected outright. Every definition in SqliteSchema.ExpectedColumns
    // satisfies that; a future entry that does not will fail loudly here rather
    // than silently skipping.
    private async Task PatchColumnsAsync(DbConnection conn, CancellationToken ct)
    {
        foreach (var group in SqliteSchema.ExpectedColumns.GroupBy(c => c.Table, StringComparer.Ordinal))
        {
            var existing = await GetColumnsAsync(conn, group.Key, ct);
            foreach (var (table, column, definition) in group)
            {
                if (existing.Contains(column)) continue;
                // Identifiers here are compile-time constants from
                // SqliteSchema.ExpectedColumns, never user input.
                await ExecAsync(conn, $"ALTER TABLE {table} ADD COLUMN {column} {definition}", ct);
                log.LogInformation("sqlite: added missing column {Table}.{Column}", table, column);
            }
        }
    }

    private static async Task<HashSet<string>> GetColumnsAsync(
        DbConnection conn, string table, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        // pragma_table_info is a table-valued function, so the table name binds
        // as a parameter rather than being interpolated.
        cmd.CommandText = "SELECT name FROM pragma_table_info($1)";
        var p = cmd.CreateParameter();
        p.ParameterName = "$1";
        p.Value = table;
        cmd.Parameters.Add(p);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
