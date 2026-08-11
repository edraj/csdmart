using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Dmart.Auth;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.QueryGrammar;

namespace Dmart.DataAdapters.Sql;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
    Justification = "Audited: CommandText is assembled from compile-time SQL, dialect-produced fragments and $N placeholders only. Every caller-supplied value is bound through DbParams, never concatenated.")]
public sealed class UserRepository(IDbConnectionFactory db, AuthzCacheRefresher refresher, SessionTokenHasher tokenHasher)
{
    private const string SelectAllColumns = """
        SELECT uuid, shortname, space_name, subpath, is_active, slug,
               displayname, description, tags, created_at, updated_at,
               owner_shortname, owner_group_shortname, payload,
               last_checksum_history, resource_type,
               password, roles, groups, acl, relationships,
               {TYPE_COLS}, email, msisdn, locked_to_device,
               is_email_verified, is_msisdn_verified, force_password_change,
               device_id, google_id, facebook_id, apple_id, social_avatar_url,
               attempt_count, last_login, notes, query_policies, last_failed_login,
               is_deleted, deleted_at
        FROM users
        """;

    // `type` and `language` are PostgreSQL ENUM columns and must be cast to
    // text to read them as strings; on SQLite they are already TEXT and the
    // cast is a syntax error. Same column list either way, so the two forms are
    // derived from one template rather than maintained separately.
    private static string SelectAll(DbConnection conn) =>
        SelectAllColumns.Replace("{TYPE_COLS}",
            conn is Microsoft.Data.Sqlite.SqliteConnection
                ? "type, language"
                : "type::text, language::text",
            StringComparison.Ordinal);

    // `type` and `language` are PostgreSQL ENUM columns, so the bound text has
    // to be cast to the enum type on insert. SQLite stores them as TEXT with a
    // CHECK constraint, where the cast is a syntax error.
    private static string EnumCasts(DbConnection conn, string sql) =>
        sql.Replace("{ENUM_CASTS}",
            conn is Microsoft.Data.Sqlite.SqliteConnection
                ? "$22,$23"
                : "$22::usertype,$23::language",
            StringComparison.Ordinal);

    // PostgreSQL needs the parameter cast so it can resolve the type of a bare
    // `$n IS NOT NULL`; SQLite has no such syntax and needs no hint.
    private static string ExistsWhereFor(DbConnection conn) =>
        conn is Microsoft.Data.Sqlite.SqliteConnection
            ? ExistsWhere.Replace("::text", "", StringComparison.Ordinal)
            : ExistsWhere;

    public async Task<User?> GetByShortnameAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        return await GetByShortnameAsync(shortname, conn, ct);
    }

    public async Task<User?> GetByShortnameAsync(string shortname, DbConnection conn, CancellationToken ct = default)
    {
        await using var cmd = conn.Command($"{SelectAllColumns} WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    // WHERE fragments for the identifier lookups below, named so
    // UserLookupIndexPlanTests can EXPLAIN-verify each one stays usable by
    // its partial index in SqlSchema (idx_users_email_lower_unique /
    // idx_users_msisdn_unique).
    //
    // The `<> ''` clauses look redundant but are load-bearing: those are
    // PARTIAL indexes whose predicates exclude '' rows, and Postgres uses
    // a partial index only when the query provably implies its predicate.
    // `LOWER(email) = LOWER($1)` alone cannot prove `email <> ''`, so
    // without the clause every lookup sequentially scans the users table.
    // '' never identifies a user (writes normalize '' to NULL —
    // NullIfEmptyIdentifier), so results are unchanged.
    internal const string EmailLookupWhere = "LOWER(email) = LOWER($1) AND email <> ''";
    internal const string MsisdnLookupWhere = "msisdn = $1 AND msisdn <> ''";
    internal const string ExistsWhere =
        "($1::text IS NOT NULL AND shortname = $1) " +
        "OR ($2::text IS NOT NULL AND LOWER(email) = LOWER($2) AND email <> '') " +
        "OR ($3::text IS NOT NULL AND msisdn = $3 AND msisdn <> '')";

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command($"{SelectAllColumns} WHERE {EmailLookupWhere}");
        DbParams.Add(cmd, email);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    public async Task<User?> GetByMsisdnAsync(string msisdn, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command($"{SelectAllColumns} WHERE {MsisdnLookupWhere}");
        DbParams.Add(cmd, msisdn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    // Look a user up by the provider id OAuth authenticated them with. This is
    // the identity the provider actually asserts, so it beats matching on email
    // (which the provider may or may not have verified) and on the synthetic
    // `{provider}_{id}` shortname (which BuildShortname sanitizes, so it does
    // not round-trip for ids carrying '-' or '.').
    //
    // One complete command per provider rather than interpolating the column
    // name into a shared string. The provider set is closed and known at compile
    // time, so there is no reason to assemble this text at runtime: each arm
    // below interpolates only `const` values, which the compiler folds into a
    // constant, so no dynamic SQL reaches the command (CA2100) — the same
    // property that makes the shortname/email lookups above safe. An
    // unrecognized provider has no query to run and resolves to "no match"
    // rather than to an unfiltered one.
    //
    // Each carries the same `<> ''` clause as the email/msisdn lookups so the
    // planner can use the partial index — see EmailLookupWhere.
    public async Task<User?> GetByProviderIdAsync(
        string provider, string providerId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(providerId)) return null;

        await using var conn = await db.OpenAsync(ct);
        await using var cmd = provider switch
        {
            "google" => conn.Command(
                $"{SelectAllColumns} WHERE google_id = $1 AND google_id <> ''"),
            "facebook" => conn.Command(
                $"{SelectAllColumns} WHERE facebook_id = $1 AND facebook_id <> ''"),
            "apple" => conn.Command(
                $"{SelectAllColumns} WHERE apple_id = $1 AND apple_id <> ''"),
            _ => null,
        };
        if (cmd is null) return null;

        DbParams.Add(cmd, providerId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    public async Task<bool> ExistsAsync(string? shortname, string? email, string? msisdn, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command($"SELECT 1 FROM users WHERE {ExistsWhereFor(conn)} LIMIT 1");
        DbParams.Add(cmd, (object?)shortname ?? DBNull.Value);
        DbParams.Add(cmd, (object?)email ?? DBNull.Value);
        DbParams.Add(cmd, (object?)msisdn ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task UpsertAsync(User u, CancellationToken ct = default)
    {
        // Same deadlock-retry posture as UpsertWithPriorAsync (see the
        // comment there). Concurrent users-table writes can trip the PG
        // deadlock detector; retry is the standard remediation.
        const int MaxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await db.OpenAsync(ct);
                await UpsertAsync(u, conn, ct);
                return;
            }
            catch (DbException ex) when (
                attempt < MaxAttempts && DbRetry.IsTransientContention(ex))
            {
#pragma warning disable CA5394 // Backoff jitter — randomness here is timing, not security.
                await Task.Delay(Random.Shared.Next(5, 25), ct);
#pragma warning restore CA5394
            }
        }
    }

    // The users INSERT, in ONE definition shared by the single-row upsert and
    // the batch restore. The clause that matters is
    //     password = COALESCE(EXCLUDED.password, users.password)
    // which preserves a stored hash when the incoming row carries none — the
    // case every pre-Parquet archive hits, because the zip export omits
    // passwords entirely. Sharing the text is what stops a second path from
    // quietly dropping it.
    private const string UserInsertColumns = """
            INSERT INTO users (uuid, shortname, space_name, subpath, is_active, slug,
                               displayname, description, tags, created_at, updated_at,
                               owner_shortname, owner_group_shortname, payload,
                               last_checksum_history, resource_type,
                               password, roles, groups, acl, relationships,
                               type, language, email, msisdn, locked_to_device,
                               is_email_verified, is_msisdn_verified, force_password_change,
                               device_id, google_id, facebook_id, apple_id, social_avatar_url,
                               attempt_count, last_login, notes, query_policies,
                               is_deleted, deleted_at)
        """;

    private const string UserConflictClause = """
            ON CONFLICT (shortname) DO UPDATE SET
                space_name = EXCLUDED.space_name,
                subpath = EXCLUDED.subpath,
                is_active = EXCLUDED.is_active,
                slug = EXCLUDED.slug,
                displayname = EXCLUDED.displayname,
                description = EXCLUDED.description,
                tags = EXCLUDED.tags,
                updated_at = EXCLUDED.updated_at,
                owner_shortname = EXCLUDED.owner_shortname,
                owner_group_shortname = EXCLUDED.owner_group_shortname,
                payload = EXCLUDED.payload,
                last_checksum_history = EXCLUDED.last_checksum_history,
                -- Preserve the stored hash when the caller passes Password=null.
                -- Same protection UpsertWithPriorCoreAsync gets — a partial
                -- update flow that loads-then-saves without explicitly carrying
                -- the password forward would otherwise silently wipe credentials.
                password = COALESCE(EXCLUDED.password, users.password),
                roles = EXCLUDED.roles,
                groups = EXCLUDED.groups,
                acl = EXCLUDED.acl,
                relationships = EXCLUDED.relationships,
                type = EXCLUDED.type,
                language = EXCLUDED.language,
                email = EXCLUDED.email,
                msisdn = EXCLUDED.msisdn,
                locked_to_device = EXCLUDED.locked_to_device,
                is_email_verified = EXCLUDED.is_email_verified,
                is_msisdn_verified = EXCLUDED.is_msisdn_verified,
                force_password_change = EXCLUDED.force_password_change,
                device_id = EXCLUDED.device_id,
                google_id = EXCLUDED.google_id,
                facebook_id = EXCLUDED.facebook_id,
                apple_id = EXCLUDED.apple_id,
                social_avatar_url = EXCLUDED.social_avatar_url,
                attempt_count = EXCLUDED.attempt_count,
                last_login = EXCLUDED.last_login,
                notes = EXCLUDED.notes,
                query_policies = EXCLUDED.query_policies,
                -- NEVER from EXCLUDED. Soft-delete state changes only via
                -- SoftDeleteAsync or a hard delete; every other writer (profile
                -- update, admin update, OAuth provisioning) must leave a
                -- deleted row deleted rather than resurrecting it as a side
                -- effect of an unrelated field change.
                is_deleted = users.is_deleted,
                deleted_at = users.deleted_at
        """;

    public async Task UpsertAsync(User u, DbConnection conn, CancellationToken ct = default)
    {
        // Populate query_policies deterministically on every write so the
        // row-level ACL filter (QueryHelper.AppendAclFilter) can match
        // patterns against it. See EntryRepository.UpsertAsync for the
        // full rationale — same pattern, same invariant.
        u = u with { QueryPolicies = Utils.QueryPolicies.Generate(u) };

        await using var cmd = conn.CreateCommand();
        var tuple = BindUserRow(cmd, u);
        cmd.CommandText = $"{UserInsertColumns}\nVALUES {tuple}\n{UserConflictClause}";

        await cmd.ExecuteNonQueryAsync(ct);
        // user.roles may have changed → clear the in-memory permission cache.
        await refresher.RefreshAsync(ct);
    }

    /// <summary>
    /// Binds one user's 38 columns and returns the VALUES tuple that reads them.
    /// </summary>
    /// <remarks>
    /// The single-row upsert and the batch restore both call this, so there is
    /// exactly ONE definition of how a user row is bound. That matters more
    /// here than anywhere else in the schema: the conflict clause carries
    /// <c>password = COALESCE(EXCLUDED.password, users.password)</c>, and a
    /// second hand-written binding that drifted from this one could feed a NULL
    /// password into a path that wrote it straight through — silently disabling
    /// every account it claimed to restore.
    ///
    /// Placeholders are taken from what DbParams.Add returns rather than
    /// hardcoded as $1..$38, which is what lets the same binding serve row N of
    /// a multi-row INSERT.
    /// </remarks>
    private static string BindUserRow(DbCommand cmd, User u)
    {
        var p = new string[40];
        var i = 0;
        p[i++] = DbParams.Add(cmd, Guid.Parse(u.Uuid));
        p[i++] = DbParams.Add(cmd, u.Shortname);
        p[i++] = DbParams.Add(cmd, u.SpaceName);
        p[i++] = DbParams.Add(cmd, u.Subpath);
        p[i++] = DbParams.Add(cmd, u.IsActive);
        p[i++] = DbParams.Add(cmd, (object?)u.Slug ?? DBNull.Value);
        p[i++] = AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Displayname));
        p[i++] = AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Description));
        p[i++] = AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(u.Tags));   // tags is NOT NULL
        p[i++] = DbParams.Add(cmd, u.CreatedAt == default ? TimeUtils.Now() : u.CreatedAt);
        // updated_at is stamped NOW, not carried from the model — existing
        // behaviour, preserved deliberately so the batch path is not a
        // behavioural change smuggled in alongside a performance one.
        p[i++] = DbParams.Add(cmd, TimeUtils.Now());
        p[i++] = DbParams.Add(cmd, u.OwnerShortname);
        p[i++] = DbParams.Add(cmd, (object?)u.OwnerGroupShortname ?? DBNull.Value);
        p[i++] = AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Payload));
        p[i++] = DbParams.Add(cmd, (object?)u.LastChecksumHistory ?? DBNull.Value);
        p[i++] = DbParams.Add(cmd, JsonbHelpers.EnumMember(u.ResourceType));
        p[i++] = DbParams.Add(cmd, (object?)u.Password ?? DBNull.Value);
        p[i++] = AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(u.Roles));   // roles is NOT NULL
        p[i++] = AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(u.Groups));  // groups is NOT NULL
        p[i++] = AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Acl));
        p[i++] = AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Relationships));
        // PG enum values: usertype='web'/'mobile'/'bot', language='ar'/'en'/'ku'/'fr'/'tr'.
        // Both match the C# enum member names lowercased (UserType.Web→"web", Language.En→"en").
        p[i++] = DbParams.Add(cmd, JsonbHelpers.EnumNameLower(u.Type));
        p[i++] = DbParams.Add(cmd, JsonbHelpers.EnumNameLower(u.Language));
        p[i++] = DbParams.Add(cmd, NullIfEmptyIdentifier(u.Email));
        p[i++] = DbParams.Add(cmd, NullIfEmptyIdentifier(u.Msisdn));
        p[i++] = DbParams.Add(cmd, u.LockedToDevice);
        p[i++] = DbParams.Add(cmd, u.IsEmailVerified);
        p[i++] = DbParams.Add(cmd, u.IsMsisdnVerified);
        p[i++] = DbParams.Add(cmd, u.ForcePasswordChange);
        p[i++] = DbParams.Add(cmd, (object?)u.DeviceId ?? DBNull.Value);
        p[i++] = DbParams.Add(cmd, (object?)u.GoogleId ?? DBNull.Value);
        p[i++] = DbParams.Add(cmd, (object?)u.FacebookId ?? DBNull.Value);
        p[i++] = DbParams.Add(cmd, (object?)u.AppleId ?? DBNull.Value);
        p[i++] = DbParams.Add(cmd, (object?)u.SocialAvatarUrl ?? DBNull.Value);
#pragma warning disable CA1508 // Analyzer limitation: int? boxed via (object?) cast IS null when source is null; the ?? is load-bearing.
        p[i++] = DbParams.Add(cmd, (object?)u.AttemptCount ?? DBNull.Value);
#pragma warning restore CA1508
        p[i++] = AddJsonb(cmd, JsonbHelpers.ToJsonb(u.LastLogin));
        p[i++] = DbParams.Add(cmd, (object?)u.Notes ?? DBNull.Value);
        p[i++] = DbParams.Add(cmd, u.QueryPolicies.ToArray(), SqlValueKind.TextArray);
        // Bound so INSERT works on a fresh row; the ON CONFLICT clause pins
        // both to the EXISTING values, so an upsert can never resurrect.
        p[i++] = DbParams.Add(cmd, u.IsDeleted);
        p[i++] = DbParams.Add(cmd, (object?)u.DeletedAt ?? DBNull.Value);

        // `type` and `language` are PostgreSQL ENUMs and need the cast; SQLite
        // stores them as TEXT, where the cast is a syntax error. Same rule as
        // EnumCasts, applied to this row's own placeholders.
        if (cmd is not Microsoft.Data.Sqlite.SqliteCommand)
        {
            p[21] += "::usertype";
            p[22] += "::language";
        }

        return "(" + string.Join(",", p) + ")";
    }

    /// <summary>
    /// Upserts many users in batched multi-row INSERTs. Returns rows affected.
    /// </summary>
    /// <remarks>
    /// Built from the SAME <see cref="UserInsertColumns"/>,
    /// <see cref="UserConflictClause"/> and row binding the single-row upsert
    /// uses, so the password-preserving COALESCE cannot be lost here — that is
    /// the whole reason this shares rather than restates.
    ///
    /// Batched at <see cref="RestoreBatchRows"/> because users are 38 columns
    /// wide and both drivers cap bound parameters: PostgreSQL at 65535, SQLite
    /// lower. 200 rows is 7,600 parameters, comfortably inside both — and the
    /// limit would otherwise only be hit on a LARGE restore, which is the worst
    /// time to discover it.
    ///
    /// One refresh at the end rather than per row: the cache invalidation is
    /// in-memory and idempotent, so doing it once is both cheaper and no less
    /// correct.
    /// </remarks>
    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: SQL is assembled from const literals plus generated placeholder names; every caller-supplied value binds through DbCommand.Parameters.")]
    public async Task<int> UpsertManyAsync(
        IReadOnlyList<User> users, CancellationToken ct = default)
    {
        if (users.Count == 0) return 0;

        var affected = 0;
        await using var conn = await db.OpenAsync(ct);

        for (var offset = 0; offset < users.Count; offset += RestoreBatchRows)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(RestoreBatchRows, users.Count - offset);

            await using var cmd = conn.CreateCommand();
            var tuples = new string[take];
            for (var i = 0; i < take; i++)
            {
                // query_policies is regenerated per row exactly as the
                // single-row path does — it is derived state, and a restore
                // that carried stale policies forward would leave rows
                // invisible to ACL-filtered reads.
                var u = users[offset + i];
                tuples[i] = BindUserRow(cmd, u with { QueryPolicies = Utils.QueryPolicies.Generate(u) });
            }

            cmd.CommandText =
                $"{UserInsertColumns}\nVALUES {string.Join(",", tuples)}\n{UserConflictClause}";
            affected += await cmd.ExecuteNonQueryAsync(ct);
        }

        await refresher.RefreshAsync(ct);
        return affected;
    }

    /// <summary>Users per multi-row INSERT. Bounded by each driver's parameter cap.</summary>
    internal static int RestoreBatchRows { get; set; } = 200;

    // True when the backend can report whether an upsert inserted or updated.
    // PostgreSQL exposes it through the xmax system column; SQLite has no
    // equivalent, so the caller derives the same answer from the in-transaction
    // read instead.
    private static bool ReturnsInsertedFlag(DbConnection conn)
        => conn is not Microsoft.Data.Sqlite.SqliteConnection;

    // Atomic prior-fetch + upsert for the native-plugin update_user path.
    // See EntryRepository.UpsertWithPriorAsync for the full rationale —
    // same pattern (SELECT FOR UPDATE, INSERT ON CONFLICT, RETURNING
    // xmax = 0) and the same residual race for concurrent inserts of a
    // brand-new shortname.
    //
    // Wraps the actual SQL work in a small deadlock-retry. Concurrent
    // UPSERTs on the users table can trip Postgres' deadlock detector
    // (SQLState 40P01) — common when several plugin hooks fire at once,
    // or when the integration test suite runs many test classes in
    // parallel against the same DB. PG explicitly designs these errors
    // to be retried by the application: the detector aborts one of the
    // colliding transactions to break the cycle, and the loser is
    // expected to back off briefly and try again. Bounded to 3 attempts
    // with a tiny randomised backoff to break symmetry.
    public async Task<(User? prior, bool inserted)> UpsertWithPriorAsync(User u, CancellationToken ct = default)
    {
        u = u with { QueryPolicies = Utils.QueryPolicies.Generate(u) };

        const int MaxAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await UpsertWithPriorCoreAsync(u, ct);
            }
            catch (DbException ex) when (
                attempt < MaxAttempts && DbRetry.IsTransientContention(ex))
            {
                // 40P01 = deadlock_detected, 40001 = serialization_failure.
                // Both are transient by design — back off briefly so the
                // colliding transaction has time to finish, then retry.
#pragma warning disable CA5394 // Backoff jitter — randomness here is timing, not security.
                await Task.Delay(Random.Shared.Next(5, 25), ct);
#pragma warning restore CA5394
            }
        }
    }

    private async Task<(User? prior, bool inserted)> UpsertWithPriorCoreAsync(User u, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // users' unique key is `shortname` only — that's also what the
        // ON CONFLICT below resolves on.
        User? prior = null;
        // PostgreSQL locks the incumbent row for the read-modify-write below.
        // SQLite has no row locks and needs none: Microsoft.Data.Sqlite begins
        // IMMEDIATE, so this transaction already holds the database write lock
        // and no other writer can interleave. Appending FOR UPDATE there would
        // simply be a syntax error.
        var lockClause = conn is Microsoft.Data.Sqlite.SqliteConnection ? "" : " FOR UPDATE";
        await using (var sel = conn.Command(
            $"{SelectAllColumns} WHERE shortname = $1{lockClause}", tx))
        {
            DbParams.Add(sel, u.Shortname);
            await using var reader = await sel.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) prior = Hydrate(reader);
        }

        await using var cmd = conn.Command("""
            INSERT INTO users (uuid, shortname, space_name, subpath, is_active, slug,
                               displayname, description, tags, created_at, updated_at,
                               owner_shortname, owner_group_shortname, payload,
                               last_checksum_history, resource_type,
                               password, roles, groups, acl, relationships,
                               type, language, email, msisdn, locked_to_device,
                               is_email_verified, is_msisdn_verified, force_password_change,
                               device_id, google_id, facebook_id, apple_id, social_avatar_url,
                               attempt_count, last_login, notes, query_policies,
                               is_deleted, deleted_at)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,
                    {ENUM_CASTS},$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,$34,$35,$36,$37,$38)
            ON CONFLICT (shortname) DO UPDATE SET
                space_name = EXCLUDED.space_name,
                subpath = EXCLUDED.subpath,
                is_active = EXCLUDED.is_active,
                slug = EXCLUDED.slug,
                displayname = EXCLUDED.displayname,
                description = EXCLUDED.description,
                tags = EXCLUDED.tags,
                updated_at = EXCLUDED.updated_at,
                owner_shortname = EXCLUDED.owner_shortname,
                owner_group_shortname = EXCLUDED.owner_group_shortname,
                payload = COALESCE(EXCLUDED.payload, users.payload),
                last_checksum_history = EXCLUDED.last_checksum_history,
                password = COALESCE(EXCLUDED.password, users.password),
                roles = EXCLUDED.roles,
                groups = EXCLUDED.groups,
                acl = EXCLUDED.acl,
                relationships = EXCLUDED.relationships,
                type = EXCLUDED.type,
                language = EXCLUDED.language,
                email = EXCLUDED.email,
                msisdn = EXCLUDED.msisdn,
                locked_to_device = EXCLUDED.locked_to_device,
                is_email_verified = EXCLUDED.is_email_verified,
                is_msisdn_verified = EXCLUDED.is_msisdn_verified,
                force_password_change = EXCLUDED.force_password_change,
                device_id = EXCLUDED.device_id,
                google_id = EXCLUDED.google_id,
                facebook_id = EXCLUDED.facebook_id,
                apple_id = EXCLUDED.apple_id,
                social_avatar_url = EXCLUDED.social_avatar_url,
                attempt_count = EXCLUDED.attempt_count,
                last_login = EXCLUDED.last_login,
                notes = EXCLUDED.notes,
                query_policies = EXCLUDED.query_policies,
                -- NEVER from EXCLUDED. Soft-delete state changes only via
                -- SoftDeleteAsync or a hard delete; every other writer (profile
                -- update, admin update, OAuth provisioning) must leave a
                -- deleted row deleted rather than resurrecting it as a side
                -- effect of an unrelated field change.
                is_deleted = users.is_deleted,
                deleted_at = users.deleted_at
            """ + (ReturnsInsertedFlag(conn) ? "\n            RETURNING (xmax = 0) AS inserted" : ""), tx);

        DbParams.Add(cmd, Guid.Parse(u.Uuid));
        DbParams.Add(cmd, u.Shortname);
        DbParams.Add(cmd, u.SpaceName);
        DbParams.Add(cmd, u.Subpath);
        DbParams.Add(cmd, u.IsActive);
        DbParams.Add(cmd, (object?)u.Slug ?? DBNull.Value);
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Displayname));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Description));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(u.Tags));
        DbParams.Add(cmd, u.CreatedAt == default ? TimeUtils.Now() : u.CreatedAt);
        DbParams.Add(cmd, TimeUtils.Now());
        DbParams.Add(cmd, u.OwnerShortname);
        DbParams.Add(cmd, (object?)u.OwnerGroupShortname ?? DBNull.Value);
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Payload));
        DbParams.Add(cmd, (object?)u.LastChecksumHistory ?? DBNull.Value);
        DbParams.Add(cmd, JsonbHelpers.EnumMember(u.ResourceType));
        DbParams.Add(cmd, (object?)u.Password ?? DBNull.Value);
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(u.Roles));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(u.Groups));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Acl));
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.Relationships));
        DbParams.Add(cmd, JsonbHelpers.EnumNameLower(u.Type));
        DbParams.Add(cmd, JsonbHelpers.EnumNameLower(u.Language));
        DbParams.Add(cmd, NullIfEmptyIdentifier(u.Email));
        DbParams.Add(cmd, NullIfEmptyIdentifier(u.Msisdn));
        DbParams.Add(cmd, u.LockedToDevice);
        DbParams.Add(cmd, u.IsEmailVerified);
        DbParams.Add(cmd, u.IsMsisdnVerified);
        DbParams.Add(cmd, u.ForcePasswordChange);
        DbParams.Add(cmd, (object?)u.DeviceId ?? DBNull.Value);
        DbParams.Add(cmd, (object?)u.GoogleId ?? DBNull.Value);
        DbParams.Add(cmd, (object?)u.FacebookId ?? DBNull.Value);
        DbParams.Add(cmd, (object?)u.AppleId ?? DBNull.Value);
        DbParams.Add(cmd, (object?)u.SocialAvatarUrl ?? DBNull.Value);
#pragma warning disable CA1508 // Analyzer limitation: int? boxed via (object?) cast IS null when source is null; the ?? is load-bearing.
        DbParams.Add(cmd, (object?)u.AttemptCount ?? DBNull.Value);
#pragma warning restore CA1508
        AddJsonb(cmd, JsonbHelpers.ToJsonb(u.LastLogin));
        DbParams.Add(cmd, (object?)u.Notes ?? DBNull.Value);
        DbParams.Add(cmd, u.QueryPolicies.ToArray(), SqlValueKind.TextArray);

        bool inserted;
        if (ReturnsInsertedFlag(conn))
        {
            var raw = await cmd.ExecuteScalarAsync(ct);
            inserted = raw is bool flag && flag;
        }
        else
        {
            // No xmax to ask, but nothing needs asking: the SELECT above ran in
            // this same transaction, which holds the write lock, so "there was
            // no incumbent" is exactly "this statement inserted".
            await cmd.ExecuteNonQueryAsync(ct);
            inserted = prior is null;
        }
        await tx.CommitAsync(ct);
        // user.roles may have changed → clear the in-memory permission cache.
        await refresher.RefreshAsync(ct);
        return (prior, inserted);
    }

    public async Task DeleteAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        // Tombstone in the same transaction as the delete (§5.2). A consumer
        // that never learns a user was removed keeps an account that can still
        // be referenced by everything it owned.
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Tombstones.RecordAsync(conn, tx, "users", "shortname = $1",
            c => DbParams.Add(c, shortname), hasResourceType: false, ct);

        await using var cmd = conn.Command("DELETE FROM users WHERE shortname = $1", tx);
        DbParams.Add(cmd, shortname);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        await refresher.RefreshAsync(ct);
    }

    // True if the user owns any row that FK-references users(shortname): entries,
    // attachments, spaces, roles, groups, permissions, or other users. Used to give
    // a friendly "has created records" refusal before force is required. The users
    // clause excludes the user's own row (owner_shortname may be self) and must stay
    // in sync with ForceDeleteOnceAsync's ownsStructural check — otherwise a user who
    // owns only other users would take the plain-delete path, which does no ownership
    // reassignment or query_policies regeneration and leaves the owned users dangling
    // on a reusable shortname.
    public async Task<bool> OwnsAnyRecordsAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("""
            SELECT EXISTS (SELECT 1 FROM entries     WHERE owner_shortname = $1)
                OR EXISTS (SELECT 1 FROM attachments WHERE owner_shortname = $1)
                OR EXISTS (SELECT 1 FROM spaces      WHERE owner_shortname = $1)
                OR EXISTS (SELECT 1 FROM roles       WHERE owner_shortname = $1)
                OR EXISTS (SELECT 1 FROM groups      WHERE owner_shortname = $1)
                OR EXISTS (SELECT 1 FROM permissions WHERE owner_shortname = $1)
                OR EXISTS (SELECT 1 FROM users       WHERE owner_shortname = $1 AND shortname <> $1)
            """);
        DbParams.Add(cmd, shortname);
        return DbParams.ReadBool(await cmd.ExecuteScalarAsync(ct));
    }

    // True if the user owns the given space. Used to refuse a force-delete that
    // would otherwise wipe the management space (mirrors the Space-delete guard).
    public async Task<bool> OwnsSpaceAsync(string shortname, string spaceName, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT EXISTS (SELECT 1 FROM spaces WHERE owner_shortname = $1 AND shortname = $2)");
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, spaceName);
        return DbParams.ReadBool(await cmd.ExecuteScalarAsync(ct));
    }

    // The owner that inherits the user's STRUCTURAL objects (other users, roles,
    // groups, permissions, spaces) on a force-delete instead of having them deleted —
    // the "dmart" super_admin (AdminBootstrap.AdminShortname). owner_shortname on
    // roles/groups/permissions/spaces is a deferrable FK → users(shortname), checked
    // at COMMIT, so the target must exist by then. Bootstrap provisions "dmart" when
    // admin config is supplied; the cascade still upserts a minimal placeholder row
    // (ON CONFLICT DO NOTHING — never clobbers the real admin) so the FK resolves even
    // on a deployment that hasn't bootstrapped an admin yet (a later admin bootstrap
    // repairs the placeholder into the real super_admin).
    private const string FallbackOwner = "dmart";

    // Force-delete: reassign the user's STRUCTURAL objects, delete their DATA, then
    // delete the user — all atomically.
    //   * Reassigned to FallbackOwner (kept intact): other users, roles, groups,
    //     permissions, and whole spaces they own (the space row's owner only — its
    //     contents are untouched).
    //   * Deleted: their entries + attachments, the histories/locks for those
    //     entries (and any they authored), their sessions, and their resolved-
    //     permissions cache.
    // Returns the refs of the rows actually DELETED (entries + attachments);
    // reassigned objects survive and are not reported.
    // dryRun is a pure projection: it COUNTs the DATA rows a real force-delete would
    // remove (count(*) over a predicate equals what a DELETE over it removes) without
    // taking write locks, materialising the sentinel owner, or reassigning anything.
    public async Task<DeleteReport> ForceDeleteAsync(string shortname, bool dryRun = false, CancellationToken ct = default)
    {
        var report = await db.ExecuteWithRetryAsync(c => ForceDeleteOnceAsync(shortname, dryRun, c), ct);
        if (!dryRun) await refresher.RefreshAsync(ct);
        return report;
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: every sql is a const literal; user-supplied values bind only through positional $1.")]
    private async Task<DeleteReport> ForceDeleteOnceAsync(string shortname, bool dryRun, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        // A dryRun is a pure projection: COUNT the DATA rows a real force-delete would
        // remove (entries + attachments the user owns, plus the histories/locks for
        // those entries and any they authored) without taking write locks, opening a
        // transaction, materialising the sentinel owner, or reassigning anything. The
        // history/lock subquery still sees the entries because nothing is deleted, so
        // the projected counts match the real cascade exactly.
        if (dryRun)
        {
            async Task<long> CountAsync(string sql)
            {
                await using var cmd = conn.Command(sql);
                DbParams.Add(cmd, shortname);
                return DbParams.ReadCount(await cmd.ExecuteScalarAsync(ct));
            }

            var hProj = await CountAsync("""
                SELECT count(*) FROM histories
                WHERE owner_shortname = $1
                   OR (space_name, subpath, shortname) IN
                      (SELECT space_name, subpath, shortname FROM entries WHERE owner_shortname = $1)
                """);
            var lProj = await CountAsync("""
                SELECT count(*) FROM locks
                WHERE owner_shortname = $1
                   OR (space_name, subpath, shortname) IN
                      (SELECT space_name, subpath, shortname FROM entries WHERE owner_shortname = $1)
                """);
            var aProj = await CountAsync("SELECT count(*) FROM attachments WHERE owner_shortname = $1");
            var eProj = await CountAsync("SELECT count(*) FROM entries     WHERE owner_shortname = $1");
            return new DeleteReport(eProj, aProj, hProj, lProj);
        }

        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1. STRUCTURAL objects the user owns are REASSIGNED to FallbackOwner, not
        //    deleted: other users, roles, groups, permissions, and whole spaces
        //    (only the space row's owner changes — its contents stay put). Gated on
        //    actually owning one, so force-deleting a user who owns nothing
        //    structural never materialises the sentinel row.
        var ownsStructural = false;
        await using (var cmd = conn.Command("""
            SELECT EXISTS (SELECT 1 FROM spaces      WHERE owner_shortname = $1)
                OR EXISTS (SELECT 1 FROM roles       WHERE owner_shortname = $1)
                OR EXISTS (SELECT 1 FROM groups      WHERE owner_shortname = $1)
                OR EXISTS (SELECT 1 FROM permissions WHERE owner_shortname = $1)
                OR EXISTS (SELECT 1 FROM users       WHERE owner_shortname = $1 AND shortname <> $1)
            """, tx))
        {
            DbParams.Add(cmd, shortname);
            ownsStructural = DbParams.ReadBool(await cmd.ExecuteScalarAsync(ct));
        }

        if (ownsStructural)
        {
            // Materialise the sentinel owner so the deferrable owner_shortname FK on
            // roles/groups/permissions/spaces resolves at COMMIT. ON CONFLICT DO
            // NOTHING never touches an existing (operator-configured) anonymous row.
            // query_policies is NOT NULL and CHECK-constrained non-empty, so seed it
            // with the freshly generated patterns for the sentinel's own row.
            var anonPolicies = Utils.QueryPolicies.Generate(
                "management", "/users", "user", isActive: true, FallbackOwner, null, null).ToArray();
            // $1 is referenced twice (shortname and owner_shortname), so the
            // uuid binds last and the existing numbering is left alone.
            await using (var cmd = conn.Command("""
                INSERT INTO users (uuid, shortname, space_name, subpath, owner_shortname, is_active, query_policies)
                VALUES ($3, $1, 'management', '/users', $1, true, $2)
                ON CONFLICT (shortname) DO NOTHING
                """, tx))
            {
                DbParams.Add(cmd, FallbackOwner);
                DbParams.Add(cmd, anonPolicies, SqlValueKind.TextArray);
                DbParams.Add(cmd, Guid.NewGuid());
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Reassign every structural object the user owns to the sentinel. The
            // users pass excludes the user being deleted (their own row is removed
            // below). query_policies is regenerated for the new owner per row.
            await ReassignOwnerAsync(conn, tx, "spaces",      "space",      shortname, excludeSelf: false, ct);
            await ReassignOwnerAsync(conn, tx, "roles",       "role",       shortname, excludeSelf: false, ct);
            await ReassignOwnerAsync(conn, tx, "groups",      "group",      shortname, excludeSelf: false, ct);
            await ReassignOwnerAsync(conn, tx, "permissions", "permission", shortname, excludeSelf: false, ct);
            await ReassignOwnerAsync(conn, tx, "users",       "user",       shortname, excludeSelf: true,  ct);
        }

        async Task<long> DeleteCountAsync(string sql)
        {
            await using var cmd = conn.Command(sql, tx);
            DbParams.Add(cmd, shortname);
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        // 2. Clear the histories/locks for the user's own entries (about to be
        //    deleted) and every history/lock the user authored (owner_shortname).
        //    Runs BEFORE the entries delete below, while the matched entries still
        //    exist. histories/locks have no FK to users — nothing cascades them.
        var histories = await DeleteCountAsync("""
            DELETE FROM histories
            WHERE owner_shortname = $1
               OR (space_name, subpath, shortname) IN
                  (SELECT space_name, subpath, shortname FROM entries WHERE owner_shortname = $1)
            """);
        var locks = await DeleteCountAsync("""
            DELETE FROM locks
            WHERE owner_shortname = $1
               OR (space_name, subpath, shortname) IN
                  (SELECT space_name, subpath, shortname FROM entries WHERE owner_shortname = $1)
            """);

        // 3. DATA objects the user owns are DELETED: their attachments + entries.
        //
        // Tombstoned first, in this same transaction, over the same predicate
        // (§5.2). This path removes CONTENT — potentially a great deal of it —
        // and it is the least obvious place to look for it, because the caller
        // asked to delete a user rather than any content. A consumer that never
        // learns those rows went keeps them forever.
        void BindOwner(DbCommand c) => DbParams.Add(c, shortname);
        await Tombstones.RecordAsync(conn, tx, "attachments", "owner_shortname = $1",
            BindOwner, hasResourceType: true, ct);
        await Tombstones.RecordAsync(conn, tx, "entries", "owner_shortname = $1",
            BindOwner, hasResourceType: true, ct);
        await Tombstones.RecordAsync(conn, tx, "users", "shortname = $1",
            BindOwner, hasResourceType: false, ct);

        var attachments = await DeleteCountAsync("DELETE FROM attachments WHERE owner_shortname = $1");
        var entries = await DeleteCountAsync("DELETE FROM entries     WHERE owner_shortname = $1");

        // 4. Sessions and the resolved-permissions cache are keyed by the user, not
        //    by owner_shortname, and have no FK — nothing cascades them. Clear both
        //    so the deleted user leaves no live session or stale cached grant behind.
        foreach (var sql in new[]
        {
            "DELETE FROM sessions             WHERE shortname = $1",
            "DELETE FROM userpermissionscache WHERE user_shortname = $1",
        })
        {
            await using var cmd = conn.Command(sql, tx);
            DbParams.Add(cmd, shortname);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var del = conn.Command("DELETE FROM users WHERE shortname = $1", tx))
        {
            DbParams.Add(del, shortname);
            await del.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        // The report covers the user's cascaded DATA (entries + attachments) and the
        // history/lock rows cleared with them; the user's own row, sessions and cache
        // are bookkeeping, not entries, so they don't count toward `affected`.
        return new DeleteReport(entries, attachments, histories, locks);
    }

    // Reassign every row in `table` owned by `fromOwner` to FallbackOwner, within the
    // caller's transaction. query_policies embeds the owner shortname, so it is
    // regenerated for the new owner per row — otherwise a future user reusing
    // `fromOwner`'s shortname would inherit ACL access to the reassigned rows.
    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `table` and `resourceType` are hardcoded constants supplied only by ForceDeleteOnceAsync (never user input); all user-supplied values bind through positional parameters.")]
    private static async Task ReassignOwnerAsync(
        DbConnection conn, DbTransaction tx, string table, string resourceType,
        string fromOwner, bool excludeSelf, CancellationToken ct)
    {
        var rows = new List<(Guid Uuid, string Space, string Subpath, bool Active, string? OwnerGroup)>();
        var selectSql =
            $"SELECT uuid, space_name, subpath, is_active, owner_group_shortname FROM {table} WHERE owner_shortname = $1"
            + (excludeSelf ? " AND shortname <> $1" : "");
        await using (var sel = conn.Command(selectSql, tx))
        {
            DbParams.Add(sel, fromOwner);
            await using var reader = await sel.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                          reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        foreach (var row in rows)
        {
            var policies = Utils.QueryPolicies.Generate(
                row.Space, row.Subpath, resourceType, row.Active, FallbackOwner, row.OwnerGroup, null).ToArray();
            await using var upd = conn.Command($"UPDATE {table} SET owner_shortname = $2, query_policies = $3 WHERE uuid = $1", tx);
            DbParams.Add(upd, row.Uuid);
            DbParams.Add(upd, FallbackOwner);
            DbParams.Add(upd, policies, SqlValueKind.TextArray);
            await upd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task IncrementAttemptAsync(string shortname, DateTime failedAt, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("UPDATE users SET attempt_count = COALESCE(attempt_count, 0) + 1, last_failed_login = $2 WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, failedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Refresh the cool-down anchor on an attempt against an already-locked account
    // (reset-on-every-attempt), without touching the counter.
    public async Task TouchLastFailedLoginAsync(string shortname, DateTime failedAt, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "UPDATE users SET last_failed_login = $2 WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, failedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Clear an auto-lockout once the cool-down has elapsed: reset the counter, undo
    // the is_active flip, and drop the anchor so the next login starts clean.
    public async Task UnlockAfterCooldownAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "UPDATE users SET attempt_count = 0, is_active = true, last_failed_login = NULL WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ResetAttemptsAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "UPDATE users SET attempt_count = 0, last_failed_login = NULL WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Post-login bookkeeping: writes only `device_id` and/or `last_login`
    // (plus `updated_at`) so a concurrent plugin write that landed between
    // the auth check and here — e.g. an OAuth after-hook calling
    // update_user → UpsertWithPriorAsync to attach a Payload — isn't
    // clobbered by an UpsertAsync replaying the pre-login in-memory row.
    // Null arguments leave the corresponding column untouched (COALESCE
    // falls back to the existing value). AddJsonb already encodes the
    // jsonb wire type, so no `::jsonb` cast is needed in the SQL.
    public async Task TouchLoginAsync(
        string shortname, string? deviceId, Dictionary<string, object>? lastLogin,
        CancellationToken ct = default)
    {
        if (deviceId is null && lastLogin is null) return;
        await using var conn = await db.OpenAsync(ct);
        // A plain UPDATE, so there is no EXCLUDED row to read updated_at from —
        // it binds the same client wall-clock the upsert paths use.
        await using var cmd = conn.Command("""
            UPDATE users SET
                device_id  = COALESCE($2, device_id),
                last_login = COALESCE($3, last_login),
                updated_at = $4
            WHERE shortname = $1
            """);
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, (object?)deviceId ?? DBNull.Value);
        AddJsonb(cmd, JsonbHelpers.ToJsonb(lastLogin));
        DbParams.Add(cmd, TimeUtils.Now());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> GetAttemptCountAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT attempt_count FROM users WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is null or DBNull ? 0 : Convert.ToInt32(raw);
    }

    // ----- sessions -----
    // The bearer JWT is run through a keyed HMAC-SHA256 (see SessionTokenHasher)
    // before being persisted, so a DB dump never yields replayable tokens —
    // without the JWT secret an attacker can't recompute the column value.
    // The hash is deterministic, so every session lookup remains a single
    // indexed equality predicate (`WHERE shortname = $1 AND token = $2`),
    // unlike a password-grade KDF that would force a per-row Verify pass on
    // every authenticated request.
    //
    // `firebaseToken` is optional — Python persists it on the session row at
    // login time so downstream push-notification code can fan out to every
    // active session without a per-session update cycle. The C# port doesn't
    // ship a push sender (out of scope), but the row must still be written so
    // a future plugin has data to read via GetSessionFirebaseTokensAsync.
    public async Task CreateSessionAsync(
        string shortname, string token, string? firebaseToken = null, CancellationToken ct = default)
    {
        var tokenHash = tokenHasher.Hash(token);
        await using var conn = await db.OpenAsync(ct);
        // uuid and timestamp are bound rather than generated in SQL: pgcrypto's
        // gen_random_uuid() and NOW() have no SQLite equivalents, and the rest
        // of the codebase already mints UUIDs client-side.
        await using var cmd = conn.CreateCommand();
        var uuid = DbParams.Add(cmd, Guid.NewGuid());
        var sn = DbParams.Add(cmd, shortname);
        var tk = DbParams.Add(cmd, tokenHash);
        var fb = DbParams.Add(cmd, (object?)firebaseToken ?? DBNull.Value);
        // Same clock as the freshness comparisons that will read this row.
        var now = NowExpr(cmd);
        // CA3001 traces `shortname` from the HTTP boundary into this method and
        // flags the interpolation. It never reaches the SQL: every value here is
        // a $N placeholder returned by DbParams.Add, and `now` is either the
        // literal NOW() or another placeholder. Only placeholder text is
        // interpolated.
#pragma warning disable CA3001
        cmd.CommandText = $"""
            INSERT INTO sessions (uuid, shortname, token, firebase_token, timestamp)
            VALUES ({uuid}, {sn}, {tk}, {fb}, {now})
            """;
#pragma warning restore CA3001
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Update the firebase_token on exactly one session row — identified by
    // (shortname, token). Mirrors Python's db.update_session_firebase_token()
    // in backend/data_adapters/sql/adapter.py. Called from the profile update
    // flow when the caller PATCHes `firebase_token` on /user/profile.
    public async Task UpdateSessionFirebaseTokenAsync(
        string shortname, string token, string firebaseToken, CancellationToken ct = default)
    {
        var tokenHash = tokenHasher.Hash(token);
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("""
            UPDATE sessions SET firebase_token = $3
            WHERE shortname = $1 AND token = $2
            """);
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, tokenHash);
        DbParams.Add(cmd, firebaseToken);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Returns every non-null firebase_token across the user's active sessions.
    // Optionally filters out sessions whose timestamp is older than
    // `inactivityTtlSeconds` so callers don't push to stale devices. Mirrors
    // Python's db.get_user_session_firebase_tokens() — shipped now so a future
    // push plugin has a stable API to call.
    public async Task<List<string>> GetSessionFirebaseTokensAsync(
        string shortname, int? inactivityTtlSeconds = null, CancellationToken ct = default)
    {
        var result = new List<string>();
        await using var conn = await db.OpenAsync(ct);
        DbCommand cmd;
        if (inactivityTtlSeconds is int ttl && ttl > 0)
        {
            cmd = conn.CreateCommand();
            var sn = DbParams.Add(cmd, shortname);
            cmd.CommandText = $"""
                SELECT firebase_token FROM sessions
                WHERE shortname = {sn}
                  AND firebase_token IS NOT NULL
                  AND timestamp >= {SessionLiveSince(cmd, ttl)}
                """;
        }
        else
        {
            cmd = conn.Command(
                "SELECT firebase_token FROM sessions WHERE shortname = $1 AND firebase_token IS NOT NULL");
            DbParams.Add(cmd, shortname);
        }
        await using (cmd)
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0)) result.Add(reader.GetString(0));
            }
        }
        return result;
    }

    // `shortname` is folded into the WHERE clause as defense-in-depth — a
    // signature-valid token paired with the wrong actor returns false rather
    // than cross-matching another user's session row. With deterministic
    // hashing the second predicate is essentially free.
    public async Task<bool> IsSessionValidAsync(string shortname, string token, CancellationToken ct = default)
    {
        var tokenHash = tokenHasher.Hash(token);
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT 1 FROM sessions WHERE shortname = $1 AND token = $2");
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, tokenHash);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // Emits "the current instant" for the session columns.
    //
    // MUST agree with SessionLiveSince about which clock it reads. PostgreSQL
    // compares against server-side NOW(), so the write has to be server-side
    // NOW() too: writing a client wall-clock while comparing against the
    // server's silently breaks expiry whenever the two differ — a container
    // running UTC against a +03 host stamps every session three hours into the
    // future and no session ever expires. SQLite is in-process, so there is
    // only one clock and both sides use it.
    private static string NowExpr(DbCommand cmd)
        => cmd is Microsoft.Data.Sqlite.SqliteCommand
            ? DbParams.Add(cmd, TimeUtils.Now())
            : "NOW()";

    // Emits the session-freshness cutoff and binds whatever the engine needs.
    // PostgreSQL evaluates it server-side, which is the right authority when
    // several app hosts share one database clock. SQLite has no interval type
    // and, being in-process, no separate server clock, so the cutoff is
    // computed from the same wall-clock basis the timestamps were written with.
    private static string SessionLiveSince(DbCommand cmd, int inactivityTtlSeconds)
    {
        if (cmd is Microsoft.Data.Sqlite.SqliteCommand)
            return DbParams.Add(cmd, TimeUtils.Now().AddSeconds(-inactivityTtlSeconds));
        var p = DbParams.Add(cmd, inactivityTtlSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return $"NOW() - ({p} || ' seconds')::interval";
    }

    // Atomic session activity check + touch. When SessionInactivityTtl > 0:
    //   * UPDATE bumps the session's timestamp to NOW() iff it exists AND is
    //     not older than `inactivityTtlSeconds`. Returns 1 row on success.
    //   * If the UPDATE affected 0 rows, the session is either missing OR
    //     stale — we then DELETE any stale row so the caller can't continue
    //     under an expired token.
    // Returns true if the session is live (and was just touched), false if
    // it was missing or evicted. Called from the JwtBearer OnTokenValidated
    // hook so every authenticated request resets the inactivity clock.
    public async Task<bool> TouchSessionAsync(
        string shortname, string token, int inactivityTtlSeconds, CancellationToken ct = default)
    {
        var tokenHash = tokenHasher.Hash(token);
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var sn = DbParams.Add(cmd, shortname);
        var tk = DbParams.Add(cmd, tokenHash);
        var now = NowExpr(cmd);
        cmd.CommandText = $"""
            UPDATE sessions SET timestamp = {now}
            WHERE shortname = {sn} AND token = {tk}
              AND timestamp >= {SessionLiveSince(cmd, inactivityTtlSeconds)}
            """;
        var touched = await cmd.ExecuteNonQueryAsync(ct);
        if (touched > 0) return true;
        // Not touched — evict any stale row so SELECTs see the session gone.
        await using var purge = conn.Command(
            "DELETE FROM sessions WHERE shortname = $1 AND token = $2");
        DbParams.Add(purge, shortname);
        DbParams.Add(purge, tokenHash);
        await purge.ExecuteNonQueryAsync(ct);
        return false;
    }

    public async Task DeleteSessionAsync(string shortname, string token, CancellationToken ct = default)
    {
        var tokenHash = tokenHasher.Hash(token);
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "DELETE FROM sessions WHERE shortname = $1 AND token = $2");
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, tokenHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ----- query support (used by QueryService for management/users) -----

    public Task<List<User>> QueryAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunQueryAsync(db, SelectAllColumns, q, Hydrate, ct, tableName: "users");

    public Task<List<User>> QueryAsync(
        Models.Api.Query q, string actor, List<string>? queryPolicies, CancellationToken ct = default)
        => QueryHelper.RunQueryAsync(db, SelectAllColumns, q, Hydrate, ct,
            userShortname: actor, tableName: "users", queryPolicies: queryPolicies);

    public Task<int> CountQueryAsync(Models.Api.Query q, CancellationToken ct = default)
        => QueryHelper.RunCountAsync(db, "users", q, ct);

    public Task<int> CountQueryAsync(
        Models.Api.Query q, string actor, List<string>? queryPolicies, CancellationToken ct = default)
        => QueryHelper.RunCountAsync(db, "users", q, ct, actor, queryPolicies);

    public async Task DeleteAllSessionsAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("DELETE FROM sessions WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Count active session rows for a user. Useful in tests that verify
    // bot login bypasses session-row creation (Python parity).
    public async Task<int> CountSessionsAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT COUNT(*) FROM sessions WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    // Keep only the `keep` newest sessions for a user, evicting the rest.
    // Used to enforce max_sessions_per_user before creating a new session.
    public async Task EvictExcessSessionsAsync(string shortname, int keep, CancellationToken ct = default)
    {
        if (keep < 0) keep = 0;
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("""
            DELETE FROM sessions WHERE shortname = $1
            AND uuid NOT IN (
                SELECT uuid FROM sessions WHERE shortname = $1
                ORDER BY timestamp DESC LIMIT $2
            )
            """);
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, keep);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Return the placeholder so callers building a VALUES tuple can use it;
    // callers that rely on positional $n simply ignore it.
    private static string AddJsonb(DbCommand cmd, string? json)
        => DbParams.Add(cmd, (object?)json ?? DBNull.Value, SqlValueKind.Json);

    private static string AddJsonbNotNull(DbCommand cmd, string json)
        => DbParams.Add(cmd, json, SqlValueKind.Json);

    private static User Hydrate(DbDataReader r)
    {
        return new User
        {
            Uuid = r.GetGuid(0).ToString(),
            Shortname = r.GetString(1),
            SpaceName = r.GetString(2),
            Subpath = r.GetString(3),
            IsActive = r.GetBoolean(4),
            Slug = r.IsDBNull(5) ? null : r.GetString(5),
            Displayname = JsonbHelpers.FromTranslation(r.IsDBNull(6) ? null : r.GetString(6)),
            Description = JsonbHelpers.FromTranslation(r.IsDBNull(7) ? null : r.GetString(7)),
            Tags = JsonbHelpers.FromListString(r.IsDBNull(8) ? null : r.GetString(8)) ?? new(),
            CreatedAt = r.GetDateTime(9),
            UpdatedAt = r.GetDateTime(10),
            OwnerShortname = r.GetString(11),
            OwnerGroupShortname = r.IsDBNull(12) ? null : r.GetString(12),
            Payload = JsonbHelpers.FromPayload(r.IsDBNull(13) ? null : r.GetString(13)),
            LastChecksumHistory = r.IsDBNull(14) ? null : r.GetString(14),
            ResourceType = JsonbHelpers.ParseEnumMember<ResourceType>(r.GetString(15)),
            Password = r.IsDBNull(16) ? null : r.GetString(16),
            Roles = JsonbHelpers.FromListString(r.IsDBNull(17) ? null : r.GetString(17)) ?? new(),
            Groups = JsonbHelpers.FromListString(r.IsDBNull(18) ? null : r.GetString(18)) ?? new(),
            Acl = JsonbHelpers.FromAclList(r.IsDBNull(19) ? null : r.GetString(19)),
            Relationships = JsonbHelpers.FromRelationships(r.IsDBNull(20) ? null : r.GetString(20)),
            Type = JsonbHelpers.ParseEnumNameLower<UserType>(r.GetString(21)),
            Language = JsonbHelpers.ParseEnumNameLower<Language>(r.GetString(22)),
            Email = r.IsDBNull(23) ? null : r.GetString(23),
            Msisdn = r.IsDBNull(24) ? null : r.GetString(24),
            LockedToDevice = r.GetBoolean(25),
            IsEmailVerified = r.GetBoolean(26),
            IsMsisdnVerified = r.GetBoolean(27),
            ForcePasswordChange = r.GetBoolean(28),
            DeviceId = NullIfEmpty(r, 29),
            GoogleId = NullIfEmpty(r, 30),
            FacebookId = NullIfEmpty(r, 31),
            AppleId = NullIfEmpty(r, 32),
            SocialAvatarUrl = NullIfEmpty(r, 33),
            AttemptCount = r.IsDBNull(34) ? null : r.GetInt32(34),
            LastLogin = JsonbHelpers.FromDictStringObject(r.IsDBNull(35) ? null : r.GetString(35)),
            Notes = r.IsDBNull(36) ? null : r.GetString(36),
            QueryPolicies = DbParams.ReadTextArray(r.IsDBNull(37) ? null : r.GetValue(37)),
            LastFailedLogin = r.IsDBNull(38) ? null : r.GetDateTime(38),
            IsDeleted = !r.IsDBNull(39) && r.GetBoolean(39),
            DeletedAt = r.IsDBNull(40) ? null : r.GetDateTime(40),
        };
    }

    /// <summary>
    /// Marks a user deleted and clears the fields that identify them. The row
    /// stays so `owner_shortname` foreign keys keep resolving; nothing the user
    /// owns is touched.
    /// </summary>
    /// <remarks>
    /// IRREVERSIBLE. Nothing sets is_deleted back to false — the ON CONFLICT
    /// clauses on both upsert paths pin it to the existing value precisely so
    /// an unrelated write cannot.
    ///
    /// deleted_at is BOUND, not NOW(). The column default would be evaluated by
    /// the database server in ITS timezone, while everything dmart writes is
    /// host-local wall clock — the same trap that put tombstones three hours
    /// adrift (docs/parquet-export-design.md §5.1).
    ///
    /// Sessions go in the same transaction: a soft-deleted account with a live
    /// session would keep serving requests until the JWT expired, and the
    /// per-request IsUsable check is a second line of defence, not the first.
    /// </remarks>
    public async Task SoftDeleteAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var cmd = conn.Command("""
            UPDATE users SET
                is_deleted = true,
                deleted_at = $2,
                email = NULL,
                msisdn = NULL,
                password = NULL
            WHERE shortname = $1
            """, tx))
        {
            DbParams.Add(cmd, shortname);
            DbParams.Add(cmd, TimeUtils.Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.Command("DELETE FROM sessions WHERE shortname = $1", tx))
        {
            DbParams.Add(cmd, shortname);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        await refresher.RefreshAsync(ct);
    }

    // Reads a string column, returning null for both DB NULL and empty strings.
    private static string? NullIfEmpty(DbDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return null;
        var s = r.GetString(ordinal);
        return s.Length == 0 ? null : s;
    }

    // Write-side normalization for email/msisdn: '' and NULL both mean
    // "absent", but the partial unique indexes (idx_users_email_lower_unique,
    // idx_users_msisdn_unique — SqlSchema.cs) only exclude NULL rows. Callers
    // routinely send `"email": ""` to mean "no email" (admin UIs, msisdn-only
    // registration bodies); persisting that as '' would make the SECOND such
    // user collide on the index with a baffling 409. Normalizing here, at the
    // single write boundary, keeps '' out of the table entirely.
    private static object NullIfEmptyIdentifier(string? v)
        => string.IsNullOrEmpty(v) ? DBNull.Value : v;
}
