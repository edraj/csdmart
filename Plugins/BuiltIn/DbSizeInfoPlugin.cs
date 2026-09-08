using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Microsoft.Data.Sqlite;

namespace Dmart.Plugins.BuiltIn;

// Port of dmart/backend/plugins/db_size_info/plugin.py. An API plugin that
// mounts GET /db_size_info/ and returns a per-table size list sourced from
// pg_total_relation_size for every public.* table, ordered largest first.
//
// Whether SQLite can answer the same question depends on how dmart was
// linked, so it is asked rather than assumed. Per-table byte sizes come from
// the `dbstat` virtual table, a compile-time option (SQLITE_ENABLE_DBSTAT_VTAB)
// that the SQLitePCLRaw e_sqlite3 build is NOT compiled with — but the static
// musl artifact links Alpine's SQLite, which IS. This used to hardcode the
// former, so the artifact that could answer refused to, and the refusal named
// a build it was not running.
//
// When dbstat is absent the SQLite path still says what is unavailable and
// returns the one size it can measure exactly: the whole database file. That
// also keeps the "PostgresConnection not configured" error from escaping,
// which is what used to happen when the wrong factory was opened.
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

        // Ask the build in front of us. On a static artifact this returns real
        // per-table rows and the endpoint behaves as it does on PostgreSQL.
        var rows = await TryDbStatAsync(conn, ct);
        if (rows is not null)
        {
            return Response.Ok(attributes: new()
            {
                ["status"] = "success",
                ["data"] = rows,
                // Kept alongside the breakdown: dbstat accounts for pages that
                // belong to a table or index, so its sum is not the file size.
                // The difference is freelist and overhead, and a caller
                // comparing the two should be able to see both.
                ["database_size"] = Pretty(total),
                ["database_size_bytes"] = total,
                ["database_used"] = Pretty(used),
                ["database_used_bytes"] = used,
            });
        }

        return Response.Ok(attributes: new()
        {
            ["status"] = "failed",
            ["error"] = "per-table sizes are unavailable: they require the dbstat virtual "
                      + "table (SQLITE_ENABLE_DBSTAT_VTAB), which this SQLite build was not "
                      + "compiled with. The whole-database size is reported instead.",
            ["database_size"] = Pretty(total),
            ["database_size_bytes"] = total,
            ["database_used"] = Pretty(used),
            ["database_used_bytes"] = used,
        });
    }

    // Per-table sizes from dbstat, or null when this build has no dbstat.
    //
    // Attempted rather than probed. The obvious probe — pragma_module_list —
    // is itself behind a compile-time option, so a negative answer from it
    // would not distinguish "no dbstat" from "no introspection pragmas", and
    // would refuse the query on a build that could have served it. Running the
    // query is the only test that cannot be wrong.
    private static async Task<List<object>?> TryDbStatAsync(
        System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT name, SUM(pgsize) AS bytes
            FROM dbstat
            GROUP BY name
            ORDER BY bytes DESC
            """;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<object>();
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new Dictionary<string, object>
                {
                    ["table_name"] = reader.GetString(0),
                    ["pretty_size"] = Pretty(reader.GetInt64(1)),
                });
            }
            return rows;
        }
        catch (SqliteException)
        {
            // "no such table: dbstat" on a build without the option. Any other
            // SqliteException here means the query itself is wrong, which is a
            // bug rather than a capability difference — but distinguishing them
            // by message text would be worse than treating both as "cannot
            // answer", since the fallback below is still correct either way.
            return null;
        }
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
