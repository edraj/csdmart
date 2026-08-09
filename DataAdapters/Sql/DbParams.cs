using System.Data.Common;
using System.Text;
using Dmart.QueryGrammar;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// Binds parameters for positional `$N` SQL against either provider.
//
// The emitted SQL text is shared between the two backends — `$1` is a valid
// placeholder in both — but the BINDING is not, and the difference is not
// optional:
//
//   * Npgsql treats `$N` as positional and requires NAMELESS parameters, bound
//     in order. Naming one "$1" makes Npgsql classify it as a named parameter,
//     match nothing, and send zero parameters — the server then rejects the
//     bind with 08P01 "supplies 0 parameters, but prepared statement requires
//     N". Verified against PostgreSQL 18, not assumed.
//   * Microsoft.Data.Sqlite treats `$1` as a NAME and binds by it, so the
//     parameter must carry that exact name.
//
// One helper absorbing that difference is what lets repositories keep a single
// SQL string. Dispatch is on the concrete command type: both providers are
// statically referenced, so nothing here is reflection-based and Native AOT
// sees through it.
public static class DbParams
{
    /// <summary>
    /// Appends a parameter and returns the <c>$N</c> placeholder that refers to
    /// it. N is derived from the command's current parameter count, so callers
    /// bind in the same order the SQL reads.
    /// </summary>
    public static string Add(DbCommand cmd, object? value, SqlValueKind kind = SqlValueKind.Inferred)
    {
        var index = cmd.Parameters.Count + 1;
        var placeholder = "$" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (cmd is SqliteCommand)
        {
            var sp = new SqliteParameter(placeholder, ToSqliteStorage(value, kind));
            cmd.Parameters.Add(sp);
        }
        else
        {
            // Nameless: Npgsql binds these by position.
            cmd.Parameters.Add(PostgresDialect.CreateParameter(
                new SqlParam(null, value ?? DBNull.Value, kind)));
        }
        return placeholder;
    }

    /// <summary>Appends several parameters in order, returning their placeholders.</summary>
    public static string[] AddAll(DbCommand cmd, params object?[] values)
    {
        var result = new string[values.Length];
        for (var i = 0; i < values.Length; i++) result[i] = Add(cmd, values[i]);
        return result;
    }

    // Projects a CLR value onto one of SQLite's four storage classes, using the
    // formats SqliteValues pins. Every conversion here has a correctness reason,
    // not just a type reason — see SqliteValues for why the formats are what
    // they are.
    internal static object ToSqliteStorage(object? value, SqlValueKind kind) => value switch
    {
        null or DBNull => DBNull.Value,

        // Canonical lowercase hyphenated text. Left to the provider, a Guid
        // becomes a 16-byte BLOB, and a database holding both representations
        // compares unequal for the same UUID with nothing to signal it.
        Guid g => SqliteValues.FromGuid(g),

        // Fixed-width local wall-clock, so string ordering matches chronological
        // ordering and a plain B-tree serves ORDER BY / BETWEEN.
        DateTime dt => SqliteValues.FromDateTime(dt),

        bool b => SqliteValues.FromBoolean(b),

        // query_policies is the only array column; it is stored as a JSON array
        // and read back with json_each.
        string[] arr => ToJsonArray(arr),

        // Everything else — string, numeric, byte[] — maps directly onto TEXT,
        // INTEGER/REAL or BLOB.
        _ => value,
    };

    // Hand-built rather than serialized. System.Text.Json without a
    // source-generated context is reflection-based, which this project
    // forbids (JsonSerializerIsReflectionEnabledByDefault=false), and a
    // whole JsonSerializerContext for a string array is more machinery than
    // the four lines it replaces.
    private static string ToJsonArray(string[] values)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"');
            foreach (var c in values[i])
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Control characters must be escaped for the result to
                        // be valid JSON that json_each will parse.
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4",
                            System.Globalization.CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
        return sb.Append(']').ToString();
    }

    // Convenience for the many repository sites that bind a text[] of policies.
    public static string AddPolicies(DbCommand cmd, IEnumerable<string> policies)
        => Add(cmd, policies as string[] ?? policies.ToArray(), SqlValueKind.TextArray);

    // Convenience for jsonb/JSON-text columns.
    public static string AddJson(DbCommand cmd, string? json)
        => Add(cmd, (object?)json ?? DBNull.Value, SqlValueKind.Json);

    // Kept for call sites that already hold an NpgsqlDbType-shaped intent.
    internal static NpgsqlDbType TextArrayType => NpgsqlDbType.Array | NpgsqlDbType.Text;
}
