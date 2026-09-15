using Dmart.QueryGrammar;
using Npgsql;
using NpgsqlTypes;

namespace Dmart.DataAdapters.Sql;

// Materializes the query grammar's provider-neutral SqlParam into a concrete
// NpgsqlParameter.
//
// This is the PostgreSQL half of the driver seam. Dmart.QueryGrammar builds SQL
// text and *describes* the values it bound; it no longer references any database
// package. Each backend supplies the translation from that description to its
// own parameter type.
//
// The construction below is deliberately verbatim — it reproduces exactly what
// SearchExpressionParser.ParamCtx.Add used to do inline, including which
// constructor overload is used in each case. That matters more than it looks:
//
//   * Setting NpgsqlDbType BEFORE Value (the two-arg ctor does this) keeps
//     Npgsql from pre-inferring text and then refusing the cast. Required for
//     jsonb containment params (`payload @> $n`) and booleans.
//   * An untagged parameter is NOT the same as one tagged Text. Npgsql infers
//     `unknown` for the former, which PostgreSQL resolves per-context — that is
//     what lets a single bound string work against both a text column and a
//     timestamp cast. Tagging it Text would change the server-side resolution
//     without changing one character of SQL. SqlValueKind.Inferred exists to
//     preserve that distinction, and SqlEmissionGoldenTests pins it.
public static class PostgresDialect
{
    public static NpgsqlParameter CreateParameter(SqlParam p)
    {
        var value = Naive(p.Value);
        return p.Kind switch
        {
            SqlValueKind.Inferred => p.Name is null
                ? new NpgsqlParameter { Value = value }
                : new NpgsqlParameter(p.Name, value),
            _ => p.Name is null
                ? new NpgsqlParameter { NpgsqlDbType = ToNpgsqlDbType(p.Kind), Value = value }
                : new NpgsqlParameter(p.Name, ToNpgsqlDbType(p.Kind)) { Value = value },
        };
    }

    /// <summary>
    /// Strips the Kind off a bound <see cref="DateTime"/> so it binds as the
    /// naive wall-clock dmart stores.
    /// </summary>
    /// <remarks>
    /// dmart's time model is naive LOCAL: every timestamp column is `timestamp
    /// without time zone`, TimeUtils.Now() is DateTime.Now, and
    /// Db.ApplyHostTimezone pins the session TimeZone to the host so SQL NOW()
    /// reads the same clock. The one thing that escaped that model was the
    /// binding itself — Npgsql infers the PostgreSQL type from DateTime.Kind,
    /// and Kind=Utc infers `timestamptz`, which the server then converts into
    /// the column through that pinned session zone. Measured, binding the same
    /// 12:00 wall-clock with the session on Asia/Baghdad:
    ///
    ///     Unspecified -> 12:00      Local -> 12:00      Utc -> 15:00
    ///
    /// SQLite has no such behaviour (SqliteValues.FromDateTime writes the
    /// components verbatim), so the two backends disagreed about what a
    /// Kind=Utc value means — silently, and by exactly the host's offset.
    ///
    /// SpecifyKind keeps the clock components and only relabels the Kind, the
    /// same thing TimeUtils.Naive does for the same reason. Local and
    /// Unspecified already stored verbatim, so this is a no-op for everything
    /// the application produces; it only stops a stray Kind=Utc from being
    /// reinterpreted. Pinned by
    /// DatabaseClockTests.Every_DateTimeKind_Stores_The_Same_Wall_Clock.
    /// </remarks>
    private static object? Naive(object? value)
        => value is DateTime { Kind: not DateTimeKind.Unspecified } dt
            ? TimeUtils.Naive(dt)
            : value;

    private static NpgsqlDbType ToNpgsqlDbType(SqlValueKind kind) => kind switch
    {
        SqlValueKind.Json => NpgsqlDbType.Jsonb,
        SqlValueKind.Boolean => NpgsqlDbType.Boolean,
        SqlValueKind.TextArray => NpgsqlDbType.Array | NpgsqlDbType.Text,
        SqlValueKind.KeyValueMap => NpgsqlDbType.Hstore,
        // Inferred is handled by the caller (it must not set a type at all);
        // reaching here means a new SqlValueKind was added without deciding how
        // PostgreSQL should type it. Fail loudly rather than binding it as text.
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "No PostgreSQL type mapping for this SqlValueKind."),
    };
}
