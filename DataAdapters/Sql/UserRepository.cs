using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Dmart.Auth;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.QueryGrammar;

namespace Dmart.DataAdapters.Sql;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
    Justification = "Audited: CommandText is assembled from compile-time SQL, dialect-produced fragments and $N placeholders only. Every caller-supplied value is bound through DbParams, never concatenated.")]
public sealed class UserRepository(
    IDbConnectionFactory db, AuthzCacheRefresher refresher, SessionTokenHasher tokenHasher,
    Dmart.Auth.AuthReadCache? authCache = null)
{
    // Best-effort local eviction of the opt-in auth micro-cache (see
    // AuthReadCache). Null in tests that construct the repository directly.
    private void EvictAuth(string shortname) => authCache?.Evict(shortname);
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
               is_deleted, deleted_at, mailbox, mail_aliases, services
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
                               is_deleted, deleted_at, mailbox, mail_aliases, services)
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
                -- Preserve the stored counter when the caller passes
                -- AttemptCount=null. Same protection the password column gets,
                -- and for a sharper reason: the counter IS the lockout now, so
                -- a read-modify-write writer replaying the value it read a few
                -- hundred milliseconds ago hands an in-flight brute-forcer its
                -- attempts back. Writers that mean to change it (an admin
                -- unlock, a successful login) pass an explicit number.
                attempt_count = COALESCE(EXCLUDED.attempt_count, users.attempt_count),
                last_login = EXCLUDED.last_login,
                notes = EXCLUDED.notes,
                query_policies = EXCLUDED.query_policies,
                -- NEVER from EXCLUDED. Soft-delete state changes only via
                -- SoftDeleteAsync or a hard delete; every other writer (profile
                -- update, admin update, OAuth provisioning) must leave a
                -- deleted row deleted rather than resurrecting it as a side
                -- effect of an unrelated field change.
                is_deleted = users.is_deleted,
                deleted_at = users.deleted_at,
                mailbox = EXCLUDED.mailbox,
                mail_aliases = EXCLUDED.mail_aliases,
                services = EXCLUDED.services
        """;

    public async Task UpsertAsync(User u, DbConnection conn, CancellationToken ct = default)
    {
        // Populate query_policies deterministically on every write so the
        // row-level ACL filter (QueryHelper.AppendAclFilter) can match
        // patterns against it. See EntryRepository.UpsertAsync for the
        // full rationale — same pattern, same invariant.
        u = u with { QueryPolicies = Utils.QueryPolicies.Generate(u) };

        // One transaction for the row and its directory index rows, so a
        // clashing address rolls the whole write back rather than leaving a user
        // whose mailbox is stored but not indexed (docs/user-directory-fields.md).
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        var tuple = BindUserRow(cmd, u);
        cmd.CommandText = $"{UserInsertColumns}\nVALUES {tuple}\n{UserConflictClause}";

        await cmd.ExecuteNonQueryAsync(ct);
        await SyncDirectoryIndexAsync(conn, tx, [u.Shortname], ct);
        await tx.CommitAsync(ct);
        // user.roles / groups may have changed — evict only THIS user's bundle:
        // a global clear sent every active actor back to the database at once.
        refresher.Evict(u.Shortname);
        EvictAuth(u.Shortname);
    }

    // A directory replica's write of its primary's row (Services/
    // DirectoryReplica): the row exactly as the primary has it, which the
    // ordinary upsert deliberately is not. It keeps the primary's updated_at
    // (LDAP's modifyTimestamp, and how the replica knows a row is unchanged);
    // writes the password hash as given, so a password the primary removed is
    // removed here too instead of surviving the COALESCE; takes the primary's
    // uuid; and revives a local copy that was soft-deleted, since the primary
    // says the account is live.
    public async Task UpsertReplicatedAsync(User u, CancellationToken ct = default)
    {
        u = u with { QueryPolicies = Utils.QueryPolicies.Generate(u) };
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            var tuple = BindUserRow(cmd, u, keepUpdatedAt: true);
            cmd.CommandText = $"{UserInsertColumns}\nVALUES {tuple}\n{UserConflictClause}";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // The uuid too: the conflict clause resolves on shortname and keeps the
        // local one, and a replica's own bootstrap admin was minted locally.
        await using (var exact = conn.Command(
            "UPDATE users SET password = $2, uuid = $3, is_deleted = false, deleted_at = NULL WHERE shortname = $1", tx))
        {
            DbParams.Add(exact, u.Shortname);
            DbParams.Add(exact, (object?)u.Password ?? DBNull.Value);
            DbParams.Add(exact, u.Uuid);
            await exact.ExecuteNonQueryAsync(ct);
        }
        await SyncDirectoryIndexAsync(conn, tx, [u.Shortname], ct);
        await tx.CommitAsync(ct);
        refresher.Evict(u.Shortname);
        EvictAuth(u.Shortname);
    }

    /// <summary>
    /// Binds one user's 43 columns and returns the VALUES tuple that reads them.
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
    private static string BindUserRow(DbCommand cmd, User u, bool keepUpdatedAt = false)
    {
        var p = new string[43];
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
        // behavioural change smuggled in alongside a performance one. The one
        // exception is a directory replica mirroring its primary's row
        // (UpsertReplicatedAsync), whose timestamp IS the data.
        p[i++] = DbParams.Add(cmd, keepUpdatedAt && u.UpdatedAt != default ? u.UpdatedAt : TimeUtils.Now());
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
        p[i++] = DbParams.Add(cmd, (object?)DirectoryFields.NormalizeAddress(u.Mailbox) ?? DBNull.Value);
        p[i++] = AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(DirectoryFields.NormalizeAddresses(u.MailAliases)));
        p[i++] = AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(DirectoryFields.NormalizeServices(u.Services)));

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
    /// Batched at <see cref="RestoreBatchRows"/> because users are 43 columns
    /// wide and both drivers cap bound parameters: PostgreSQL at 65535, SQLite
    /// lower. 200 rows is 8,600 parameters, comfortably inside both — and the
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

            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
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
            var batch = new string[take];
            for (var i = 0; i < take; i++) batch[i] = users[offset + i].Shortname;
            await SyncDirectoryIndexAsync(conn, tx, batch, ct);
            await tx.CommitAsync(ct);
        }

        await refresher.RefreshAsync(ct);
        authCache?.EvictAll();
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
                               is_deleted, deleted_at, mailbox, mail_aliases, services)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21,
                    {ENUM_CASTS},$24,$25,$26,$27,$28,$29,$30,$31,$32,$33,$34,$35,$36,$37,$38,
                    $39,$40,$41,$42,$43)
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
                -- Preserve the stored counter when the caller passes
                -- AttemptCount=null. Same protection the password column gets,
                -- and for a sharper reason: the counter IS the lockout now, so
                -- a read-modify-write writer replaying the value it read a few
                -- hundred milliseconds ago hands an in-flight brute-forcer its
                -- attempts back. Writers that mean to change it (an admin
                -- unlock, a successful login) pass an explicit number.
                attempt_count = COALESCE(EXCLUDED.attempt_count, users.attempt_count),
                last_login = EXCLUDED.last_login,
                notes = EXCLUDED.notes,
                query_policies = EXCLUDED.query_policies,
                -- NEVER from EXCLUDED. Soft-delete state changes only via
                -- SoftDeleteAsync or a hard delete; every other writer (profile
                -- update, admin update, OAuth provisioning) must leave a
                -- deleted row deleted rather than resurrecting it as a side
                -- effect of an unrelated field change.
                is_deleted = users.is_deleted,
                deleted_at = users.deleted_at,
                mailbox = EXCLUDED.mailbox,
                mail_aliases = EXCLUDED.mail_aliases,
                services = EXCLUDED.services
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
        // $39/$40 — bound for the INSERT; the ON CONFLICT clause pins both to
        // the existing row, so this path cannot resurrect either.
        DbParams.Add(cmd, u.IsDeleted);
        DbParams.Add(cmd, (object?)u.DeletedAt ?? DBNull.Value);
        // $41-$43 — the directory fields, normalized exactly as BindUserRow does.
        DbParams.Add(cmd, (object?)DirectoryFields.NormalizeAddress(u.Mailbox) ?? DBNull.Value);
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(DirectoryFields.NormalizeAddresses(u.MailAliases)));
        AddJsonbNotNull(cmd, JsonbHelpers.ToJsonbList(DirectoryFields.NormalizeServices(u.Services)));

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
        await SyncDirectoryIndexAsync(conn, tx, [u.Shortname], ct);
        await tx.CommitAsync(ct);
        // user.roles / groups may have changed — evict only THIS user's bundle:
        // a global clear sent every active actor back to the database at once.
        refresher.Evict(u.Shortname);
        EvictAuth(u.Shortname);
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
        await DeleteDirectoryIndexAsync(conn, tx, shortname, ct);

        await using var cmd = conn.Command("DELETE FROM users WHERE shortname = $1", tx);
        DbParams.Add(cmd, shortname);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        refresher.Evict(shortname);
        EvictAuth(shortname);
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
    internal const string FallbackOwner = "dmart";

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
        if (!dryRun)
        {
            await refresher.RefreshAsync(ct);
            EvictAuth(shortname);
        }
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

            // Reassign every structural object the user owns to the sentinel —
            // the same rewrite a rename does, pointed at FallbackOwner. The
            // user's own (self-owned) row is rewritten too, harmlessly: it is
            // deleted below in this same transaction.
            await RenameOwnerAsync(conn, tx, StructuralTables, shortname, FallbackOwner, ct);
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

        await DeleteDirectoryIndexAsync(conn, tx, shortname, ct);
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

    // Rename a user in place — the user branch of a `move` request. Python's
    // adapter.move only rewrites the users row, but here owner_shortname on
    // entries/attachments/spaces/roles/groups/permissions is a deferrable FK →
    // users(shortname), so every row the user owns must follow the rename in
    // the same transaction or the COMMIT fails. Also carried over: the rows
    // keyed on the user's own path — its attachments (subpath "<user
    // subpath>/<shortname>"), the lock on the user record and the history of
    // both — and the user's live locks, whose owner must keep matching the
    // actor that holds them. Sessions and the resolved-permissions cache are
    // cleared instead — they are keyed by the old name, and the client's JWT
    // names it too, so the user signs in again under the new shortname.
    //
    // NOT rewritten: history AUTHORSHIP (histories.owner_shortname). It is an
    // append-only audit record of who did what, under the name they had at
    // the time; rewriting years of it in one statement would both falsify
    // that and hold a long lock on the table. The rename itself is recorded
    // as a history row at the new coordinates by the caller. Also left alone
    // (Python doesn't either): user shortnames embedded in JSON — entry acl /
    // collaborators, relationships.
    //
    // Returns false when `from` doesn't exist. A taken `to` surfaces as the
    // UNIQUE violation on users.shortname; the transaction rolls back intact.
    public async Task<bool> RenameAsync(string from, string to, CancellationToken ct = default)
    {
        var renamed = await db.ExecuteWithRetryAsync(c => RenameOnceAsync(from, to, c), ct);
        if (renamed)
        {
            // A global clear, not Evict: the rename also rewrote owner_shortname
            // on users/roles/groups/permissions the user owns, which other
            // actors' cached bundles hold (same reasoning as ForceDeleteAsync).
            await refresher.RefreshAsync(ct);
            EvictAuth(from);
            EvictAuth(to);
        }
        return renamed;
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: every sql is a const literal or interpolates only NowExpr/dialect placeholders; user-supplied values bind through positional parameters.")]
    private async Task<bool> RenameOnceAsync(string from, string to, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string space, subpath, owner;
        string? ownerGroup;
        bool isActive;
        await using (var sel = conn.Command(
            "SELECT space_name, subpath, is_active, owner_shortname, owner_group_shortname FROM users WHERE shortname = $1", tx))
        {
            DbParams.Add(sel, from);
            await using var reader = await sel.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return false;
            space = reader.GetString(0);
            subpath = reader.GetString(1);
            isActive = reader.GetBoolean(2);
            owner = reader.GetString(3);
            ownerGroup = reader.IsDBNull(4) ? null : reader.GetString(4);
        }

        // 1. The user row. A self-owned user keeps owning itself under the new
        //    name. The old key is tombstoned so an incremental export consumer
        //    keyed on (space, subpath, shortname) drops it rather than keeping
        //    a phantom account next to the renamed one.
        await Tombstones.RecordAsync(conn, tx, "users", "shortname = $1",
            c => DbParams.Add(c, from), hasResourceType: false, ct);
        var newOwner = owner == from ? to : owner;
        await using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            DbParams.Add(upd, from);
            DbParams.Add(upd, to);
            DbParams.Add(upd, newOwner);
            DbParams.Add(upd, Utils.QueryPolicies.Generate(space, subpath, "user", isActive, newOwner, ownerGroup, null).ToArray(),
                SqlValueKind.TextArray);
            // The host's clock, bound, never the database's NOW(): every other
            // write stamps updated_at with it, and PostgreSQL's NOW() is in the
            // server's timezone, so on a UTC server with a +03 host a renamed
            // user would land hours behind an incremental reader's watermark
            // (see Tombstones.RecordAsync).
            var renamedAt = DbParams.Add(upd, TimeUtils.Now());
            upd.CommandText = $"""
                UPDATE users
                   SET shortname = $2, owner_shortname = $3, query_policies = $4, updated_at = {renamedAt}
                 WHERE shortname = $1
                """;
            await upd.ExecuteNonQueryAsync(ct);
        }
        // The directory index rows follow the user. Their foreign keys are
        // deferred, so they may point at the old name until this runs.
        foreach (var table in new[] { "user_addresses", "user_services" })
        {
            await using var idx = conn.Command($"UPDATE {table} SET shortname = $2 WHERE shortname = $1", tx);
            DbParams.Add(idx, from);
            DbParams.Add(idx, to);
            await idx.ExecuteNonQueryAsync(ct);
        }

        // 2. Everything the user owns. query_policies embeds the owner, so it is
        //    rewritten per row (see RenameOwnerAsync).
        await RenameOwnerAsync(conn, tx, OwnedTables, from, to, ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            DbParams.Add(cmd, from);
            DbParams.Add(cmd, to);
            cmd.CommandText = $"UPDATE attachments SET owner_shortname = $2, updated_at = {NowExpr(cmd)} WHERE owner_shortname = $1";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 3. Rows keyed on the user's own path: its attachments (and their
        //    history, whose coords are the attachment's — same as an entry
        //    move), the lock on the user record, and its history.
        //
        //    The users UPDATE above proved `to` vacant as a USER, but a lock
        //    or attachment can still sit at the destination key: force-delete
        //    removes only what the deleted user OWNED, so another actor's lock
        //    on, or comment under, a long-deleted user survives at coordinates
        //    nothing lives at — and nothing can reach, the parent-ACL filter
        //    being fail-closed. The unique indexes would turn such an orphan
        //    into "destination already occupied" for a user that does not
        //    exist, with no way to clear it, so they are purged first (the
        //    same purge EntryRepository's move does for locks; attachments
        //    are tombstoned like every other delete). History at the
        //    destination is left alone: nothing is unique there, and a reused
        //    shortname inheriting its predecessor's history is what a plain
        //    re-create does too.
        var oldPrefix = subpath.TrimEnd('/') + "/" + from;
        var newPrefix = subpath.TrimEnd('/') + "/" + to;
        await using (var cmd = conn.Command("""
            DELETE FROM locks
             WHERE space_name = $1 AND ((subpath = $2 AND shortname = $3) OR subpath = $4)
            """, tx))
        {
            DbParams.Add(cmd, space);
            DbParams.Add(cmd, subpath);
            DbParams.Add(cmd, to);
            DbParams.Add(cmd, newPrefix);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        void BindUnderPrefix(DbCommand c, string prefix) { DbParams.Add(c, space); DbParams.Add(c, prefix); }
        await Tombstones.RecordAsync(conn, tx, "attachments", "space_name = $1 AND subpath = $2",
            c => BindUnderPrefix(c, newPrefix), hasResourceType: true, ct);
        await using (var cmd = conn.Command("DELETE FROM attachments WHERE space_name = $1 AND subpath = $2", tx))
        {
            BindUnderPrefix(cmd, newPrefix);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        //    The attachments' old coordinates are tombstoned like the user
        //    row's (step 1), and updated_at is bumped like the entry move
        //    does: an incremental export consumer drops the old key and picks
        //    the relocated row up on its next scan instead of keeping a
        //    phantom at the old path and never seeing the new one.
        await Tombstones.RecordAsync(conn, tx, "attachments", "space_name = $1 AND subpath = $2",
            c => BindUnderPrefix(c, oldPrefix), hasResourceType: true, ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            BindUnderPrefix(cmd, oldPrefix);
            DbParams.Add(cmd, newPrefix);
            cmd.CommandText = $"UPDATE attachments SET subpath = $3, updated_at = {NowExpr(cmd)} WHERE space_name = $1 AND subpath = $2";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var sql in new[]
        {
            "UPDATE locks     SET subpath = $3 WHERE space_name = $1 AND subpath = $2",
            "UPDATE histories SET subpath = $3 WHERE space_name = $1 AND subpath = $2",
        })
        {
            await using var cmd = conn.Command(sql, tx);
            BindUnderPrefix(cmd, oldPrefix);
            DbParams.Add(cmd, newPrefix);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var sql in new[]
        {
            "UPDATE locks     SET shortname = $4 WHERE space_name = $1 AND subpath = $2 AND shortname = $3",
            "UPDATE histories SET shortname = $4 WHERE space_name = $1 AND subpath = $2 AND shortname = $3",
        })
        {
            await using var cmd = conn.Command(sql, tx);
            DbParams.Add(cmd, space);
            DbParams.Add(cmd, subpath);
            DbParams.Add(cmd, from);
            DbParams.Add(cmd, to);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 4. Live locks the user holds: the lease check compares the holder to
        //    the actor, who will act under the new name. (History authorship
        //    deliberately stays — see the header.)
        await using (var cmd = conn.Command("UPDATE locks SET owner_shortname = $2 WHERE owner_shortname = $1", tx))
        {
            DbParams.Add(cmd, from);
            DbParams.Add(cmd, to);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 5. Keyed by the old name — drop rather than carry (see header).
        foreach (var sql in new[]
        {
            "DELETE FROM sessions             WHERE shortname = $1",
            "DELETE FROM userpermissionscache WHERE user_shortname = $1",
        })
        {
            await using var cmd = conn.Command(sql, tx);
            DbParams.Add(cmd, from);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    // The tables whose owner_shortname is a FK → users, with the resource type
    // their query_policies are generated for (null: read it off the row —
    // entries hold several types). Rename carries all of them; force-delete
    // reassigns the STRUCTURAL ones (entries and attachments are deleted).
    private static readonly (string Table, string? Type)[] OwnedTables =
    {
        ("entries", null), ("spaces", "space"), ("roles", "role"), ("groups", "group"),
        ("permissions", "permission"), ("users", "user"),
    };
    private static readonly (string Table, string? Type)[] StructuralTables =
        OwnedTables.Where(t => t.Table is not "entries").ToArray();

    // Point every row in `tables` owned by `from` at `to`, within the caller's
    // transaction. query_policies embeds the owner, so each row's patterns are
    // rewritten with it — otherwise a future user reusing `from`'s shortname
    // would inherit ACL access to the rows.
    //
    // Fast path: one set-based UPDATE per table that rewrites only the
    // owner-scoped query_policies patterns in place. Of the patterns
    // QueryPolicies.Generate emits, the owner-scoped literal is the only one
    // ending in ":<owner>" — the unscoped and __all_subpaths__ forms end in
    // ":true"/":false", the group-scoped one in ":<owner_group>". So swapping
    // that suffix is exactly what regenerating with the new owner would
    // produce, without pulling a single row into the app. That stops holding
    // when the old name could be mistaken for another suffix: a group named
    // like the user, or a user literally named "true"/"false". Those rows are
    // left for the slow path.
    //
    // Slow path: whatever the fast path skipped, regenerated per row. A folder
    // entry also contributes its own shortname to the patterns — mirrors
    // QueryPolicies.Generate(Entry). Paged by uuid: an updated row no longer
    // matches `owner_shortname = from`, so each page is simply the next batch
    // still owned by the old name. One EXISTS across all the tables decides
    // whether it is needed at all, so the common case (nothing ambiguous)
    // costs a single extra round trip rather than an empty page SELECT per
    // table.
    private static async Task RenameOwnerAsync(
        DbConnection conn, DbTransaction tx, IReadOnlyList<(string Table, string? Type)> tables,
        string from, string to, CancellationToken ct)
    {
        var bulkRan = from is not ("true" or "false");
        if (bulkRan)
        {
            foreach (var (table, _) in tables)
                await RenameOwnerBulkAsync(conn, tx, table, from, to, ct);

            await using var any = conn.Command("SELECT " + string.Join(" OR ",
                tables.Select(t => $"EXISTS (SELECT 1 FROM {t.Table} WHERE owner_shortname = $1)")), tx);
            DbParams.Add(any, from);
            if (!DbParams.ReadBool(await any.ExecuteScalarAsync(ct))) return;
        }
        foreach (var (table, type) in tables)
            await RenameOwnerPagedAsync(conn, tx, table, type, from, to, ct);
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `table` comes only from the OwnedTables constant (never user input); all user-supplied values bind through positional parameters.")]
    private static async Task RenameOwnerBulkAsync(
        DbConnection conn, DbTransaction tx, string table, string from, string to, CancellationToken ct)
    {
        await using var bulk = conn.CreateCommand();
        bulk.Transaction = tx;
        DbParams.Add(bulk, from);
        DbParams.Add(bulk, to);
        // Suffix compared with right()/substr(), not LIKE: '_' is legal in a
        // shortname and is a LIKE wildcard.
        var rewritten = bulk is Microsoft.Data.Sqlite.SqliteCommand
            ? $"""
              (SELECT json_group_array(CASE WHEN substr(p.value, -(length($1) + 1)) = ':' || $1
                                            THEN substr(p.value, 1, length(p.value) - length($1)) || $2
                                            ELSE p.value END)
                 FROM (SELECT value FROM json_each({table}.query_policies) ORDER BY key) AS p)
              """
            : """
              ARRAY(SELECT CASE WHEN right(p, length($1) + 1) = ':' || $1
                                THEN left(p, length(p) - length($1)) || $2
                                ELSE p END
                      FROM unnest(query_policies) WITH ORDINALITY AS t(p, i) ORDER BY i)
              """;
        bulk.CommandText = $"""
            UPDATE {table}
               SET owner_shortname = $2, query_policies = {rewritten}, updated_at = {NowExpr(bulk)}
             WHERE owner_shortname = $1
               AND (owner_group_shortname IS NULL OR owner_group_shortname <> $1)
            """;
        await bulk.ExecuteNonQueryAsync(ct);
    }

    [SuppressMessage("Security", "CA2100",
        Justification = "Audited: `table` and `resourceType` come only from the OwnedTables constant (never user input); all user-supplied values bind through positional parameters.")]
    private static async Task RenameOwnerPagedAsync(
        DbConnection conn, DbTransaction tx, string table, string? resourceType,
        string from, string to, CancellationToken ct)
    {
        const int PageSize = 1000;
        var typeCol = resourceType is null ? "resource_type" : "''";
        while (true)
        {
            var rows = new List<(Guid Uuid, string Space, string Subpath, bool Active, string? OwnerGroup, string Type, string Shortname)>();
            await using (var sel = conn.Command(
                $"SELECT uuid, space_name, subpath, is_active, owner_group_shortname, {typeCol}, shortname FROM {table} WHERE owner_shortname = $1 ORDER BY uuid LIMIT $2", tx))
            {
                DbParams.Add(sel, from);
                DbParams.Add(sel, PageSize);
                await using var reader = await sel.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                              reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6)));
            }
            if (rows.Count == 0) return;

            foreach (var row in rows)
            {
                var type = resourceType ?? row.Type;
                var policies = Utils.QueryPolicies.Generate(
                    row.Space, row.Subpath, type, row.Active, to, row.OwnerGroup,
                    resourceType is null && type == "folder" ? row.Shortname : null).ToArray();
                await using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                DbParams.Add(upd, row.Uuid);
                DbParams.Add(upd, to);
                DbParams.Add(upd, policies, SqlValueKind.TextArray);
                upd.CommandText = $"UPDATE {table} SET owner_shortname = $2, query_policies = $3, updated_at = {NowExpr(upd)} WHERE uuid = $1";
                await upd.ExecuteNonQueryAsync(ct);
            }
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
    /// <summary>
    /// Writes ONLY the password hash. Used by the rehash-on-login upgrade path.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. A rehash is not a password change: the secret is
    /// unchanged and the user did not ask for anything, so it must not touch
    /// updated_at, must not trip LogoutOnPwdChange, and must not write history
    /// or events. Round-tripping a whole User through UpsertAsync would do
    /// several of those, and would also race any concurrent profile update by
    /// writing back fields read before it.
    ///
    /// Returns the rows affected so the caller can tell "upgraded" from "the
    /// row vanished underneath us" without a second query.
    /// </remarks>
    public async Task<int> UpdatePasswordHashOnlyAsync(
        string shortname, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "UPDATE users SET password = $2 WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, passwordHash);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task TouchLastFailedLoginAsync(string shortname, DateTime failedAt, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "UPDATE users SET last_failed_login = $2 WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, failedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Clear an auto-lockout once the cool-down has elapsed: reset the counter and
    // drop the anchor so the next login starts clean. Deliberately does NOT touch
    // is_active — the attempt lock never sets it, so clearing it here would hand
    // back the flag an admin deliberately cleared on an account that is both
    // deactivated and at the threshold. See UserService.RejectIfAttemptLockedAsync.
    //
    // A named alias for ResetAttemptsAsync rather than a second copy of the same
    // UPDATE: since the lock became counter-only, "unlock" and "reset the
    // counter" ARE the same statement, and two copies of it would drift the
    // first time one of them grows a clause. The name survives so the call site
    // reads as intent.
    public Task UnlockAfterCooldownAsync(string shortname, CancellationToken ct = default)
        => ResetAttemptsAsync(shortname, ct);

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
        string shortname, string token, string? firebaseToken = null,
        string? deviceId = null, CancellationToken ct = default)
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
        var dev = DbParams.Add(cmd, (object?)deviceId ?? DBNull.Value);
        // Same clock as the freshness comparisons that will read this row.
        var now = NowExpr(cmd);
        // CA3001 traces `shortname` from the HTTP boundary into this method and
        // flags the interpolation. It never reaches the SQL: every value here is
        // a $N placeholder returned by DbParams.Add, and `now` is either the
        // literal NOW() or another placeholder. Only placeholder text is
        // interpolated.
#pragma warning disable CA3001
        cmd.CommandText = $"""
            INSERT INTO sessions (uuid, shortname, token, firebase_token, timestamp, device_id)
            VALUES ({uuid}, {sn}, {tk}, {fb}, {now}, {dev})
            """;
#pragma warning restore CA3001
        await cmd.ExecuteNonQueryAsync(ct);
        if (!string.IsNullOrEmpty(firebaseToken))
            await ClearDuplicateFirebaseTokenAsync(conn, shortname, firebaseToken!, tokenHash, deviceId, ct);
    }

    // One device gets one push, so at most one of a user's session rows may
    // carry a given device's FCM token. Sessions are per-login and devices are
    // not, so without this every login leaves another row holding a push target
    // and a fan-out over GetSessionFirebaseTokensAsync delivers the same
    // notification once per row. The row that just claimed the device keeps the
    // token; the user's other rows for it are cleared.
    //
    // "The same device" is recognised two ways, and it needs both:
    //
    //   * the same firebase_token string — the simple case, a phone signing in
    //     again with the token it already had;
    //   * the same device_id — the case the token match cannot see. FCM rotates
    //     a device's token (app reinstall, data restore, periodic refresh), so
    //     after a rotation the rows that phone left behind hold a DIFFERENT
    //     string. Nothing about those strings says they are the same handset;
    //     the device_id the client sent with its login does.
    //
    // device_id is null for clients that do not send one (web) and for the OAuth
    // grants, which have no device to name — those fall back to token matching,
    // which is exactly the pre-device_id behaviour.
    //
    // The SELECT DISTINCT in GetSessionFirebaseTokensAsync stays as the
    // read-side backstop for rows written before any of this.
    private static async Task ClearDuplicateFirebaseTokenAsync(
        DbConnection conn, string shortname, string firebaseToken, string keepTokenHash,
        string? deviceId, CancellationToken ct)
    {
        // Two shapes rather than one with an `OR ($4 IS NOT NULL AND …)`: a bare
        // parameter in a null test needs a cast on PostgreSQL, and the two-branch
        // SQL says what it does without one.
        await using var cmd = string.IsNullOrEmpty(deviceId)
            ? conn.Command("""
                UPDATE sessions SET firebase_token = NULL
                WHERE shortname = $1 AND token <> $2 AND firebase_token = $3
                """)
            : conn.Command("""
                UPDATE sessions SET firebase_token = NULL
                WHERE shortname = $1 AND token <> $2
                  AND (firebase_token = $3 OR device_id = $4)
                """);
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, keepTokenHash);
        DbParams.Add(cmd, firebaseToken);
        if (!string.IsNullOrEmpty(deviceId)) DbParams.Add(cmd, deviceId!);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Clear a set of FCM tokens wherever they appear, across every user.
    //
    // The other half of keeping the table honest, and the reactive one: FCM's
    // send response names the tokens it rejected (UNREGISTERED, or
    // INVALID_ARGUMENT for a malformed one), and a token FCM has retired is dead
    // for everyone — hence no shortname scope. A push sender feeds the rejects
    // back here so the next fan-out does not retry them. Without it, dead tokens
    // sit on their rows until SESSION_INACTIVITY_TTL ages the session out, which
    // for a long-lived session is never.
    //
    // Returns the number of session rows cleared. Chunked because FCM sends in
    // batches of up to 500 and PostgreSQL caps a statement at 65535 parameters.
    public async Task<int> InvalidateFirebaseTokensAsync(
        IReadOnlyCollection<string> tokens, CancellationToken ct = default)
    {
        var wanted = tokens.Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) return 0;

        const int ChunkSize = 500;
        var cleared = 0;
        await using var conn = await db.OpenAsync(ct);
        for (var offset = 0; offset < wanted.Count; offset += ChunkSize)
        {
            await using var cmd = conn.CreateCommand();
            var placeholders = new List<string>();
            foreach (var token in wanted.Skip(offset).Take(ChunkSize))
                placeholders.Add(DbParams.Add(cmd, token));
            cmd.CommandText =
                $"UPDATE sessions SET firebase_token = NULL WHERE firebase_token IN ({string.Join(", ", placeholders)})";
            cleared += await cmd.ExecuteNonQueryAsync(ct);
        }
        return cleared;
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

        // The device is whatever this session was opened from — read it back
        // rather than asking the caller, because /user/profile's firebase_token
        // patch carries no device_id of its own.
        string? deviceId;
        await using (var dev = conn.Command(
            "SELECT device_id FROM sessions WHERE shortname = $1 AND token = $2"))
        {
            DbParams.Add(dev, shortname);
            DbParams.Add(dev, tokenHash);
            deviceId = await dev.ExecuteScalarAsync(ct) as string;
        }
        await ClearDuplicateFirebaseTokenAsync(conn, shortname, firebaseToken, tokenHash, deviceId, ct);
    }

    // Returns the DISTINCT set of non-null firebase_tokens across the user's
    // active sessions — one entry per token, however many session rows carry
    // it, because a push fan-out over the raw column delivers the same
    // notification once per duplicated row. Optionally filters out sessions
    // whose timestamp is older than `inactivityTtlSeconds` so callers don't
    // push to stale devices. Mirrors Python's
    // db.get_user_session_firebase_tokens() — shipped now so a future push
    // plugin has a stable API to call.
    //
    // DISTINCT is the read-side backstop; ClearDuplicateFirebaseTokenAsync
    // keeps the duplicates from accumulating in the first place. See its
    // comment for the case neither of them can catch (token rotation).
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
                SELECT DISTINCT firebase_token FROM sessions
                WHERE shortname = {sn}
                  AND firebase_token IS NOT NULL
                  AND timestamp >= {SessionLiveSince(cmd, ttl)}
                """;
        }
        else
        {
            cmd = conn.Command(
                "SELECT DISTINCT firebase_token FROM sessions WHERE shortname = $1 AND firebase_token IS NOT NULL");
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
        EvictAuth(shortname);
    }

    // ----- directory index (docs/user-directory-fields.md) -----

    // Re-derives the user_addresses / user_services rows of the given users
    // FROM THEIR STORED ROWS, inside the caller's transaction. Reading back
    // what was written rather than trusting the model matters: the upsert's
    // conflict clause pins is_deleted to the existing value, so the model can
    // disagree with the row about whether the user is live.
    //
    // An address another user already holds fails the INSERT on the
    // user_addresses primary key, and the caller's whole transaction — user
    // row included — rolls back. UserService pre-checks for a readable
    // message; this is what makes the race between two writers safe.
    private static async Task SyncDirectoryIndexAsync(
        DbConnection conn, DbTransaction tx, string[] shortnames, CancellationToken ct)
    {
        if (shortnames.Length == 0) return;
        var sqlite = conn is Microsoft.Data.Sqlite.SqliteConnection;
        foreach (var sql in DirectoryIndexStatements(sqlite, filtered: true))
        {
            await using var cmd = conn.Command(sql, tx);
            DbParams.Add(cmd, shortnames, SqlValueKind.TextArray);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task DeleteDirectoryIndexAsync(
        DbConnection conn, DbTransaction tx, string shortname, CancellationToken ct)
    {
        foreach (var table in new[] { "user_addresses", "user_services" })
        {
            await using var cmd = conn.Command($"DELETE FROM {table} WHERE shortname = $1", tx);
            DbParams.Add(cmd, shortname);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // The four statements that (re)build the index: clear, then derive the
    // addresses and the services from users. `filtered` restricts all four to
    // the shortnames bound as $1; unfiltered they rebuild the whole index.
    // Values are copied verbatim — BindUserRow already normalized them — so
    // the two engines' differing lower() on non-ASCII text never comes into it.
    private static string[] DirectoryIndexStatements(bool sqlite, bool filtered)
    {
        string In(string column) => !filtered ? "TRUE"
            : sqlite ? $"{column} IN (SELECT value FROM json_each($1))" : $"{column} = ANY($1)";
        var live = sqlite ? "u.is_deleted = 0" : "NOT u.is_deleted";
        string Each(string column, string alias) => sqlite
            ? $"json_each(u.{column}) AS {alias}"
            : $"LATERAL jsonb_array_elements_text(u.{column}) AS {alias}(value)";
        string IsArray(string column) => sqlite
            ? $"json_type(u.{column}) = 'array'"
            : $"jsonb_typeof(u.{column}) = 'array'";
        return
        [
            $"DELETE FROM user_addresses WHERE {In("shortname")}",
            $"DELETE FROM user_services WHERE {In("shortname")}",
            $"""
            INSERT INTO user_addresses (address, shortname, kind)
            SELECT u.mailbox, u.shortname, 'mailbox' FROM users u
             WHERE {In("u.shortname")} AND {live} AND u.mailbox IS NOT NULL AND u.mailbox <> ''
            UNION ALL
            SELECT a.value, u.shortname, 'alias' FROM users u CROSS JOIN {Each("mail_aliases", "a")}
             WHERE {In("u.shortname")} AND {live} AND {IsArray("mail_aliases")}
            """,
            // ON CONFLICT rather than DISTINCT: services are de-duplicated on
            // write already, and DISTINCT over a whole-table rebuild is a sort —
            // at 3M users, 7M rows spilling to disk under a 1 GB cap, for minutes.
            // (SQLite needs the SELECT's WHERE to parse an upsert after a join.)
            $"""
            INSERT INTO user_services (service, shortname)
            SELECT s.value, u.shortname FROM users u CROSS JOIN {Each("services", "s")}
             WHERE {In("u.shortname")} AND {live} AND {IsArray("services")}
            ON CONFLICT DO NOTHING
            """,
        ];
    }

    // Rebuilds both index tables from users when they are empty but some live
    // user has directory fields: the first start after the upgrade, or a
    // restore that loaded users through a path that predates the index.
    // Returns the number of address + service rows written, 0 when nothing
    // needed doing.
    public async Task<int> RebuildDirectoryIndexIfEmptyAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        var sqlite = conn is Microsoft.Data.Sqlite.SqliteConnection;
        var hasFields = sqlite
            ? "EXISTS (SELECT 1 FROM users WHERE is_deleted = 0 AND (mailbox IS NOT NULL OR json_array_length(mail_aliases) > 0 OR json_array_length(services) > 0))"
            : "EXISTS (SELECT 1 FROM users WHERE NOT is_deleted AND (mailbox IS NOT NULL OR jsonb_array_length(mail_aliases) > 0 OR jsonb_array_length(services) > 0))";
        await using (var probe = conn.Command(
            $"SELECT CASE WHEN {hasFields} AND NOT EXISTS (SELECT 1 FROM user_addresses) AND NOT EXISTS (SELECT 1 FROM user_services) THEN 1 ELSE 0 END"))
        {
            probe.CommandTimeout = 0;
            if (DbParams.ReadCount(await probe.ExecuteScalarAsync(ct)) == 0) return 0;
        }

        // One all-or-nothing transaction, and NO command timeout: at 3M users
        // the insert is 13M rows, which on a memory-capped PostgreSQL ran past
        // Npgsql's default 30 s, rolled back, and left the index empty. A
        // half-built index would be worse than an empty one — the probe above
        // would see rows and never finish the job.
        // On PostgreSQL the foreign keys are dropped for the bulk insert and
        // re-added after it, in the same transaction. Kept in place, each of
        // the 13M rows fires a row-level check that takes FOR KEY SHARE on its
        // users row — a random heap read plus a dirtied page per row. At 3M users
        // under a 1 GB cap that was 31 GB read and 10 GB written in 18 minutes,
        // unfinished. Re-adding validates them all with one set-based join.
        // (Deferred instead, the same 13M checks run at COMMIT, which is bound
        // by the connection's timeout rather than these commands' zero.)
        // SQLite's checks are plain index probes and need none of this.
        string[] pre = sqlite ? [] :
        [
            "ALTER TABLE user_addresses DROP CONSTRAINT IF EXISTS user_addresses_shortname_fkey",
            "ALTER TABLE user_services DROP CONSTRAINT IF EXISTS user_services_shortname_fkey",
        ];
        string[] post = sqlite ? [] :
        [
            "ALTER TABLE user_addresses ADD CONSTRAINT user_addresses_shortname_fkey FOREIGN KEY (shortname) REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED",
            "ALTER TABLE user_services ADD CONSTRAINT user_services_shortname_fkey FOREIGN KEY (shortname) REFERENCES users(shortname) DEFERRABLE INITIALLY DEFERRED",
        ];

        await using var tx = await conn.BeginTransactionAsync(ct);
        var written = 0;
        foreach (var sql in pre.Concat(DirectoryIndexStatements(sqlite, filtered: false)).Concat(post))
        {
            await using var cmd = conn.Command(sql, tx);
            cmd.CommandTimeout = 0;
            var n = await cmd.ExecuteNonQueryAsync(ct);
            if (sql.StartsWith("INSERT", StringComparison.Ordinal)) written += n;
        }
        await tx.CommitAsync(ct);
        return written;
    }

    // The live user holding `address` as their mailbox or as an alias, or
    // null. `address` must already be normalized (DirectoryFields).
    public async Task<(string Shortname, string Kind)?> FindAddressOwnerAsync(string address, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT shortname, kind FROM user_addresses WHERE address = $1");
        DbParams.Add(cmd, address);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    // The user whose mailbox (kind "mailbox") or alias (kind "alias") is
    // `address`, through the user_addresses primary key.
    public async Task<User?> GetByAddressAsync(string address, string kind, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"{SelectAllColumns} WHERE shortname = (SELECT shortname FROM user_addresses WHERE address = $1 AND kind = $2)");
        DbParams.Add(cmd, address);
        DbParams.Add(cmd, kind);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Hydrate(reader) : null;
    }

    // One keyset page of the live users granted `service`, in shortname order,
    // read off the user_services primary key — the LDAP face's listing of
    // `(authorizedService=x)` without touching any other user's row.
    public async Task<List<User>> ListByServiceAsync(string service, string? after, int limit, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(after is null
            ? $"{SelectAllColumns} WHERE shortname IN (SELECT shortname FROM user_services WHERE service = $1 ORDER BY shortname LIMIT $2) ORDER BY shortname"
            : $"{SelectAllColumns} WHERE shortname IN (SELECT shortname FROM user_services WHERE service = $1 AND shortname > $3 ORDER BY shortname LIMIT $2) ORDER BY shortname");
        DbParams.Add(cmd, service);
        DbParams.Add(cmd, limit);
        if (after is not null) DbParams.Add(cmd, after);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<User>();
        while (await reader.ReadAsync(ct)) list.Add(Hydrate(reader));
        return list;
    }

    // ----- directory support (used by Ldap/LdapDirectory) -----

    // One page of live users for the LDAP face's unanchored searches, in
    // shortname order. Keyset rather than OFFSET: a scan resumes after the last
    // shortname it saw, so page N costs the same as page 1 instead of re-reading
    // every row before it — the difference between linear and quadratic over a
    // multi-million-row table.
    public async Task<List<User>> ListForDirectoryAsync(string? after, int limit, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(after is null
            ? $"{SelectAllColumns} WHERE is_deleted = false ORDER BY shortname LIMIT $1"
            : $"{SelectAllColumns} WHERE is_deleted = false AND shortname > $2 ORDER BY shortname LIMIT $1");
        DbParams.Add(cmd, limit);
        if (after is not null) DbParams.Add(cmd, after);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<User>();
        while (await reader.ReadAsync(ct)) list.Add(Hydrate(reader));
        return list;
    }

    // Shortnames of the live users whose `groups` array holds `group` — the
    // `member` values of a group entry in the LDAP face.
    public async Task<List<string>> ListShortnamesInGroupAsync(string group, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var contains = QueryHelper.DialectFor(db)
            .JsonArrayContainsAny("groups", [group], (v, k) => DbParams.Add(cmd, v, k));
        cmd.CommandText = $"SELECT shortname FROM users WHERE is_deleted = false AND {contains} ORDER BY shortname";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    // The directory feed's changes walk (Services/DirectoryFeed): users whose
    // row changed at or after `since`, soft-deleted ones included, in
    // (updated_at, shortname) order after the (since, after) keyset position.
    // idx_users_updated_at serves it.
    public async Task<List<User>> ListChangedSinceAsync(DateTime since, string after, int limit, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            $"{SelectAllColumns} WHERE updated_at > $1 OR (updated_at = $1 AND shortname > $2) "
            + "ORDER BY updated_at, shortname LIMIT $3");
        DbParams.Add(cmd, since);
        DbParams.Add(cmd, after);
        DbParams.Add(cmd, limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<User>();
        while (await reader.ReadAsync(ct)) list.Add(Hydrate(reader));
        return list;
    }

    // Shortnames of users hard-deleted (or renamed away) at or after `since`,
    // from the tombstones every delete and rename records.
    public async Task<List<string>> ListDeletedSinceAsync(DateTime since, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "SELECT DISTINCT shortname FROM deletions WHERE table_name = 'users' AND deleted_at >= $1 ORDER BY shortname");
        DbParams.Add(cmd, since);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    public async Task<string?> GetShortnameByUuidAsync(string uuid, CancellationToken ct = default)
    {
        // A Guid, not the string: PostgreSQL's column is uuid and has no
        // uuid = text operator; DbParams writes SQLite's canonical text form.
        if (!Guid.TryParse(uuid, out var id)) return null;
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT shortname FROM users WHERE uuid = $1");
        DbParams.Add(cmd, id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    // Live shortnames in (after, upTo] (upTo null: to the end), in order: the
    // replica's full walk reconciles each page of the primary against them.
    public async Task<List<string>> ListShortnamesBetweenAsync(string after, string? upTo, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(upTo is null
            ? "SELECT shortname FROM users WHERE shortname > $1 ORDER BY shortname"
            : "SELECT shortname FROM users WHERE shortname > $1 AND shortname <= $2 ORDER BY shortname");
        DbParams.Add(cmd, after);
        if (upTo is not null) DbParams.Add(cmd, upTo);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await reader.ReadAsync(ct)) list.Add(reader.GetString(0));
        return list;
    }

    // A replica's position in its primary's directory feed: the primary's clock
    // at the start of the last complete walk, or null before the first one.
    public async Task<DateTime?> GetReplicaWatermarkAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command("SELECT watermark FROM directory_replica_state WHERE id = 1");
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : Convert.ToDateTime(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task SetReplicaWatermarkAsync(DateTime? watermark, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "INSERT INTO directory_replica_state (id, watermark) VALUES (1, $1) "
            + "ON CONFLICT (id) DO UPDATE SET watermark = $1");
        DbParams.Add(cmd, (object?)watermark ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Members of several groups in one query: the LDAP face's group listing,
    // which on SQLite would otherwise scan the users table once per group.
    // Every requested group is a key, members in shortname order.
    public async Task<Dictionary<string, List<string>>> ListGroupMembersAsync(
        IReadOnlyCollection<string> groups, CancellationToken ct = default)
    {
        var result = groups.Distinct(StringComparer.Ordinal).ToDictionary(g => g, _ => new List<string>(), StringComparer.Ordinal);
        if (result.Count == 0) return result;
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var contains = QueryHelper.DialectFor(db)
            .JsonArrayContainsAny("groups", result.Keys.ToList(), (v, k) => DbParams.Add(cmd, v, k));
        cmd.CommandText = $"SELECT shortname, groups FROM users WHERE is_deleted = false AND {contains} ORDER BY shortname";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var shortname = reader.GetString(0);
            foreach (var g in JsonbHelpers.FromListString(reader.IsDBNull(1) ? null : reader.GetString(1)) ?? [])
                if (result.TryGetValue(g, out var members)) members.Add(shortname);
        }
        return result;
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
        EvictAuth(shortname);
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
        EvictAuth(shortname);
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
            Mailbox = r.IsDBNull(41) ? null : r.GetString(41),
            MailAliases = JsonbHelpers.FromListString(r.IsDBNull(42) ? null : r.GetString(42)) ?? new(),
            Services = JsonbHelpers.FromListString(r.IsDBNull(43) ? null : r.GetString(43)) ?? new(),
        };
    }

    /// <summary>
    /// Clears the soft-delete flags so the shortname can be used again.
    /// </summary>
    /// <remarks>
    /// THE ONLY WAY BACK, and deliberately narrow. Both upsert paths pin
    /// is_deleted/deleted_at to the existing row precisely so an ordinary write
    /// cannot resurrect an account by accident; this is the one explicit door,
    /// called only from the CREATE path, where the caller is asking for a new
    /// account under that name rather than editing the deleted one.
    ///
    /// It clears the flags only. Every other column is then written by the
    /// create that follows, so nothing survives from the deleted account except
    /// the shortname itself — which is the point.
    /// </remarks>
    public async Task ClearSoftDeleteAsync(string shortname, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var cmd = conn.Command(
            "UPDATE users SET is_deleted = false, deleted_at = NULL WHERE shortname = $1");
        DbParams.Add(cmd, shortname);
        await cmd.ExecuteNonQueryAsync(ct);
        EvictAuth(shortname);
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
                -- A change like any other: an incremental reader keyed on
                -- updated_at (the directory feed) must see the account go.
                updated_at = $2,
                email = NULL,
                msisdn = NULL,
                password = NULL,
                mailbox = NULL,
                mail_aliases = '[]',
                services = '[]'
            WHERE shortname = $1
            """, tx))
        {
            DbParams.Add(cmd, shortname);
            DbParams.Add(cmd, TimeUtils.Now());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        // Released like the email and msisdn above: a deleted account's mailbox
        // and aliases become available to the next account that asks for them.
        await DeleteDirectoryIndexAsync(conn, tx, shortname, ct);

        await using (var cmd = conn.Command("DELETE FROM sessions WHERE shortname = $1", tx))
        {
            DbParams.Add(cmd, shortname);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        refresher.Evict(shortname);
        EvictAuth(shortname);
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
