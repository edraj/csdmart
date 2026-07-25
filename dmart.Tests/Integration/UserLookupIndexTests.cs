using Dmart.DataAdapters.Sql;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the plain lookup indexes on `users`:
//   idx_users_email_lower   (expression btree on lower(email))
//   idx_users_msisdn
//
// These exist SEPARATELY from the unique indexes pinned by
// UserUniqueColumnConstraintTests, because those can't be relied on to
// serve lookups:
//   * idx_users_email_lower_unique carries `email <> ''` in its partial
//     predicate, and the planner cannot prove that from GetByEmailAsync's
//     `LOWER(email) = LOWER($1)` clause (the predicate tests `email`, the
//     query tests `lower(email)`), so that index never serves the lookup;
//   * both unique indexes are SKIPPED by SqlSchema's guarded DO block when
//     legacy duplicate rows exist — leaving no email/msisdn index at all.
//
// If a refactor drops one of these, nothing breaks functionally — every
// OTP/OAuth login that resolves a user by email or msisdn just silently
// degrades to a sequential scan over the users table. These tests fail
// loud instead.
public sealed class UserLookupIndexTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserLookupIndexTests(DmartFactory factory) => _factory = factory;

    private async Task<string?> IndexDefAsync(string indexName)
    {
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT indexdef FROM pg_indexes
             WHERE schemaname = current_schema()
               AND tablename = 'users'
               AND indexname = $1
            """, conn);
        cmd.Parameters.Add(new() { Value = indexName });
        return await cmd.ExecuteScalarAsync() as string;
    }

    [FactIfPg]
    public async Task Email_Lookup_Index_Exists_On_Lower_Email()
    {
        var def = await IndexDefAsync("idx_users_email_lower");
        def.ShouldNotBeNull(customMessage:
            "idx_users_email_lower is missing — GetByEmailAsync seq-scans users");
        // Must be the expression form, matching the LOWER(email) = LOWER($1)
        // lookup; an index on the raw column would not serve that query.
        def.ShouldContain("lower(email");
        // Must NOT be partial: a WHERE predicate on `email` is unprovable
        // from a clause on `lower(email)` and would make the index dead
        // weight for the lookup (exactly the unique-index gap this fills).
        def.ShouldNotContain("WHERE");
    }

    [FactIfPg]
    public async Task Msisdn_Lookup_Index_Exists()
    {
        var def = await IndexDefAsync("idx_users_msisdn");
        def.ShouldNotBeNull(customMessage:
            "idx_users_msisdn is missing — GetByMsisdnAsync seq-scans users");
        def.ShouldContain("(msisdn)");
        // Non-partial on purpose: generic parameter plans can't prove a
        // `msisdn <> ''` predicate from `msisdn = $1`, and the partial
        // unique index is skipped entirely on DBs with legacy duplicates.
        def.ShouldNotContain("WHERE");
    }
}
