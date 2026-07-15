using Dmart.DataAdapters.Sql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// EXPLAIN-pins that UserRepository's identifier lookups can use the
// partial indexes SqlSchema defines on users:
//
//   idx_users_email_lower_unique  ON users (lower(email)) WHERE email IS NOT NULL AND email <> ''
//   idx_users_msisdn_unique       ON users (msisdn)       WHERE msisdn IS NOT NULL AND msisdn <> ''
//
// Postgres uses a partial index only when the query's WHERE clause
// PROVABLY implies the index predicate. `LOWER(email) = LOWER($1)` alone
// cannot prove `email <> ''`, so a lookup without an explicit
// `AND email <> ''` clause silently degrades to a sequential scan of the
// whole users table. Auth is the hot path (login, OTP, uniqueness
// checks): in production this was ~1.3s per lookup and hours of daily DB
// time before it was pinned here.
//
// Probes run against a TEMP TABLE named `users` — pg_temp shadows the
// real table for the probe session only — carrying the exact index DDL a
// fresh install gets, so the tests hold regardless of which historical
// index variants the shared dev database has, and never touch real rows.
//
// Each lookup is asserted under a forced GENERIC plan (PREPARE +
// plan_cache_mode=force_generic_plan), the strictest case: parameter
// values are unknown at plan time, so nothing can be proven from them.
// dmart issues unnamed statements today (custom plans), but Npgsql
// auto-prepare, a raw PostgresConnection override, or PgBouncer can all
// flip a deployment to generic plans — the query text alone must carry
// the proof.
public sealed class UserLookupIndexPlanTests
{
    // The DDL the probe replicates, asserted verbatim against
    // SqlSchema.CreateAll below. If Probe_Index_Ddl_Matches_Schema fails,
    // the schema changed: update the probe AND re-check that
    // UserRepository's lookup fragments still imply the new predicates.
    private const string EmailIndexDdl =
        "ON users (lower(email)) WHERE email IS NOT NULL AND email <> ''";
    private const string MsisdnIndexDdl =
        "ON users (msisdn) WHERE msisdn IS NOT NULL AND msisdn <> ''";

    private static async Task<NpgsqlConnection> OpenProbeAsync()
    {
        // Pooling=false: the probe shadows `users` with a temp table and
        // sets enable_seqscan=off — none of that may leak back into a pool.
        var conn = new NpgsqlConnection(DmartFactory.PgConn + ";Pooling=false");
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            CREATE TEMP TABLE users (shortname text, email text, msisdn text, filler text);
            INSERT INTO users
            SELECT 'u'||g, 'user'||g||'@example.com', '96478'||g, repeat('x', 100)
            FROM generate_series(1, 5000) g;
            CREATE UNIQUE INDEX idx_users_email_lower_unique {EmailIndexDdl};
            CREATE UNIQUE INDEX idx_users_msisdn_unique {MsisdnIndexDdl};
            CREATE UNIQUE INDEX idx_probe_shortname ON users (shortname);
            ANALYZE users;
            SET enable_seqscan = off;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    // Custom plan: parameter values are bound, mirroring the unnamed
    // statements Npgsql sends by default.
    private static async Task<string> ExplainAsync(NpgsqlConnection conn, string sql, params string?[] args)
    {
        await using var cmd = new NpgsqlCommand("EXPLAIN " + sql, conn);
        foreach (var a in args)
            cmd.Parameters.Add(new() { Value = (object?)a ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return await ReadPlanAsync(cmd);
    }

    // Generic plan: parameter values are opaque at plan time.
    private static async Task<string> ExplainGenericAsync(
        NpgsqlConnection conn, string paramTypes, string sql, string executeArgs)
    {
        await using (var prep = new NpgsqlCommand(
            $"SET plan_cache_mode = force_generic_plan; PREPARE probe_stmt ({paramTypes}) AS {sql}", conn))
            await prep.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand($"EXPLAIN EXECUTE probe_stmt({executeArgs})", conn);
        return await ReadPlanAsync(cmd);
    }

    private static async Task<string> ReadPlanAsync(NpgsqlCommand cmd)
    {
        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join("\n", lines);
    }

    private static void AssertUsesIndex(string plan, string index) =>
        plan.ShouldContain(index, customMessage:
            $"Planner could not use {index} — this lookup will sequentially scan the users table. Plan:\n{plan}");

    [FactIfPg]
    public void Probe_Index_Ddl_Matches_Schema()
    {
        SqlSchema.CreateAll.ShouldContain(EmailIndexDdl,
            customMessage: "users email index DDL changed — update this probe and re-verify UserRepository.EmailLookupWhere implies the new predicate");
        SqlSchema.CreateAll.ShouldContain(MsisdnIndexDdl,
            customMessage: "users msisdn index DDL changed — update this probe and re-verify UserRepository.MsisdnLookupWhere implies the new predicate");
    }

    [FactIfPg]
    public async Task Email_Lookup_Uses_Index_Under_Custom_Plan()
    {
        await using var conn = await OpenProbeAsync();
        var plan = await ExplainAsync(conn,
            $"SELECT 1 FROM users WHERE {UserRepository.EmailLookupWhere}",
            "user100@example.com");
        AssertUsesIndex(plan, "idx_users_email_lower_unique");
    }

    [FactIfPg]
    public async Task Email_Lookup_Uses_Index_Under_Generic_Plan()
    {
        await using var conn = await OpenProbeAsync();
        var plan = await ExplainGenericAsync(conn, "text",
            $"SELECT 1 FROM users WHERE {UserRepository.EmailLookupWhere}",
            "'user100@example.com'");
        AssertUsesIndex(plan, "idx_users_email_lower_unique");
    }

    [FactIfPg]
    public async Task Msisdn_Lookup_Uses_Index_Under_Generic_Plan()
    {
        await using var conn = await OpenProbeAsync();
        var plan = await ExplainGenericAsync(conn, "text",
            $"SELECT 1 FROM users WHERE {UserRepository.MsisdnLookupWhere}",
            "'964785000'");
        AssertUsesIndex(plan, "idx_users_msisdn_unique");
    }

    [FactIfPg]
    public async Task Exists_Check_Uses_All_Three_Indexes_Under_Generic_Plan()
    {
        await using var conn = await OpenProbeAsync();
        var plan = await ExplainGenericAsync(conn, "text, text, text",
            $"SELECT 1 FROM users WHERE {UserRepository.ExistsWhere} LIMIT 1",
            "NULL, 'user100@example.com', '964785000'");
        AssertUsesIndex(plan, "idx_probe_shortname");
        AssertUsesIndex(plan, "idx_users_email_lower_unique");
        AssertUsesIndex(plan, "idx_users_msisdn_unique");
    }
}
