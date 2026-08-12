using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Microsoft.Data.Sqlite;

namespace Dmart.Plugins.BuiltIn;

// Port of dmart/backend/plugins/db_size_info/plugin.py. An API plugin that
// mounts GET /db_size_info/ and returns a per-table size list sourced from
// pg_total_relation_size for every public.* table, ordered largest first.
//
// SQLite cannot answer the same question. Per-table byte sizes come from the
// `dbstat` virtual table, which is a compile-time option
// (SQLITE_ENABLE_DBSTAT_VTAB) and is NOT enabled in the SQLitePCLRaw
// e_sqlite3 build this project ships — verified against the pinned 2.1.12
// native library, which answers "no such table: dbstat". Rather than leak
// that, or the "PostgresConnection not configured" that used to escape from
// opening the wrong factory, the SQLite path says what is unavailable and
// still returns the one size it CAN measure exactly: the whole database file.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
    Justification = "Audited: CommandText is compile-time SQL and PRAGMA names from a fixed literal set. No caller-supplied value reaches it; PRAGMA does not accept parameters.")]
public sealed class DbSizeInfoPlugin(IDbConnectionFactory db) : IApiPlugin
{
    public string Shortname => "db_size_info";

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.MapGet("/", async Task<Response> (CancellationToken ct) =>
        {
            const string sql = """
                SELECT table_name,
                       pg_size_pretty(pg_total_relation_size(quote_ident(table_name))) AS pretty_size
                FROM information_schema.tables
                WHERE table_schema = 'public'
                ORDER BY pg_total_relation_size(quote_ident(table_name)) DESC
                """;

            try
            {
                await using var conn = await db.OpenAsync(ct);
                if (conn is SqliteConnection) return await SqliteSizeAsync(conn, ct);

                await using var cmd = conn.Command(sql);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                var rows = new List<object>();
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new Dictionary<string, object>
                    {
                        ["table_name"] = reader.GetString(0),
                        ["pretty_size"] = reader.GetString(1),
                    });
                }
                return Response.Ok(attributes: new()
                {
                    ["status"] = "success",
                    ["data"] = rows,
                });
            }
            catch (Exception ex)
            {
                return Response.Ok(attributes: new()
                {
                    ["status"] = "failed",
                    ["error"] = ex.Message,
                });
            }
        });
    }

    // status "failed" is deliberate: the caller asked for a per-table
    // breakdown and is not getting one, so reporting success with a single
    // synthetic row would misrepresent whole-file bytes as a table's bytes.
    // The total is attached alongside because it is exact, costs two pragmas,
    // and is the closest honest answer to what the endpoint is for.
    private static async Task<Response> SqliteSizeAsync(
        System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        var pageCount = await PragmaAsync(conn, "page_count", ct);
        var pageSize = await PragmaAsync(conn, "page_size", ct);
        var freelist = await PragmaAsync(conn, "freelist_count", ct);

        var total = pageCount * pageSize;
        // Pages on the freelist are allocated in the file but hold no data —
        // the difference is what a VACUUM would reclaim.
        var used = (pageCount - freelist) * pageSize;

        return Response.Ok(attributes: new()
        {
            ["status"] = "failed",
            ["error"] = "per-table sizes are unavailable on the sqlite driver: they require the "
                      + "dbstat virtual table (SQLITE_ENABLE_DBSTAT_VTAB), which the bundled "
                      + "SQLite build does not include. The whole-database size is reported instead.",
            ["database_size"] = Pretty(total),
            ["database_size_bytes"] = total,
            ["database_used"] = Pretty(used),
            ["database_used_bytes"] = used,
        });
    }

    private static async Task<long> PragmaAsync(
        System.Data.Common.DbConnection conn, string name, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        // `name` is a compile-time literal from the three call sites above,
        // never caller input — PRAGMA takes no parameters.
        cmd.CommandText = $"PRAGMA {name}";
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is null or DBNull
            ? 0
            : Convert.ToInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Mirrors pg_size_pretty's units and 1024 divisor so the field means the
    // same thing on both backends.
    private static string Pretty(long bytes)
    {
        string[] units = ["bytes", "kB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0
            ? $"{bytes} {units[0]}"
            : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "{0:0.##} {1}", value, units[unit]);
    }
}
