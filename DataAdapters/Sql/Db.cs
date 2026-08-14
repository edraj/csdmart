using Dmart.Config;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dmart.DataAdapters.Sql;

// Thin connection factory. Singleton — opens a fresh NpgsqlConnection per call so we
// don't share state across handlers. Npgsql does its own pooling under the hood.
//
// Construction is non-throwing so the host can boot when PostgreSQL isn't configured
// (useful for tests, smoke checks, and graceful degradation). The connection-string
// requirement is enforced at OpenAsync time and surfaces as a clean exception in the
// failing handler rather than a DI resolution failure at startup.
//
// If Dmart:PostgresConnection isn't set explicitly, we assemble it from the
// individual DATABASE_HOST / DATABASE_PORT / DATABASE_USERNAME / DATABASE_PASSWORD /
// DATABASE_NAME / DATABASE_POOL_SIZE components — matching how dmart Python builds
// its SQLAlchemy URL from separate settings. This lets a plain config.env from a
// Python deployment drop in unchanged.
public sealed class Db(IOptions<DmartSettings> settings) : IDbConnectionFactory
{
    private readonly string? _conn = BuildConnectionString(settings.Value);
    private readonly string? _importConn = BuildImportConnectionString(settings.Value);

    public bool IsConfigured => !string.IsNullOrEmpty(_conn);

    // Explicit interface implementation so the PostgreSQL-typed OpenAsync below
    // stays the one repositories bind to. Widening its return type to
    // DbConnection would force a cast at every COPY / SQLSTATE call site that
    // legitimately needs Npgsql.
    async Task<System.Data.Common.DbConnection> IDbConnectionFactory.OpenAsync(CancellationToken ct)
        => await OpenAsync(ct);

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_conn))
            throw new InvalidOperationException("Dmart:PostgresConnection not configured");
        var c = new NpgsqlConnection(_conn);
        await c.OpenAsync(ct);
        return c;
    }

    // Connection used by the bulk-import sessions below. Identical to the app
    // connection except the command timeout is lifted — see
    // BuildImportConnectionString for why that matters.
    private async Task<NpgsqlConnection> OpenImportAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_importConn))
            throw new InvalidOperationException("Dmart:PostgresConnection not configured");
        var c = new NpgsqlConnection(_importConn);
        await c.OpenAsync(ct);
        return c;
    }

    // Opens a fast-import session: a dedicated connection in
    // session_replication_role='replica' (bypasses FK constraints AND
    // user-defined triggers for every statement on the session) with an open
    // transaction. Used by `dmart import --fast` to turn the source into a
    // trusted bulk load.
    //
    // Unlike a single import-long transaction, the session commits at BATCH
    // boundaries via RunBatchAsync, so a transport drop costs only the
    // in-flight batch — and the session transparently reconnects and replays
    // the batch (the bulk merge is idempotent under ON CONFLICT, and prior
    // batches are already committed). This is what lets a multi-hour import
    // survive a firewall/NAT idle reset or a briefly-restarted server instead
    // of discarding hours of work. The SET is session-level (not LOCAL), so it
    // persists across the per-batch commits and is re-applied after a reconnect.
    //
    // Hard-fails with a clear InvalidOperationException if the caller's DB
    // role can't set session_replication_role. That GUC is superuser-context,
    // so the role needs either superuser or — on PG 15+, which added
    // per-parameter grants — `GRANT SET ON PARAMETER session_replication_role`.
    // (There is no `pg_session_replication_role` predefined role; PostgreSQL
    // has never shipped one, so don't send operators looking for it.) No silent
    // fallback — the operator explicitly opted into `--fast`.
    public async Task<FastImportSession> BeginFastImportSessionAsync(CancellationToken ct = default)
    {
        var session = new FastImportSession(this, bypassConstraints: true);
        await session.OpenAsync(ct);
        return session;
    }

    // Same reconnectable, batch-committing session WITHOUT the replica role —
    // FK constraints and triggers stay enforced. This is the default import
    // path: it gets the bulk COPY + merge batching, while integrity errors
    // still surface (and are isolated per row by the importer's fallback).
    public async Task<FastImportSession> BeginBatchImportSessionAsync(CancellationToken ct = default)
    {
        var session = new FastImportSession(this, bypassConstraints: false);
        await session.OpenAsync(ct);
        return session;
    }

    // A reconnectable, batch-committing import session. The import calls
    // RunBatchAsync once per bulk batch (durable + retryable), then MarkSuccess()
    // before disposing so the trailing transaction (e.g. the non-idempotent
    // history slice, which deliberately runs outside the per-batch path) is
    // committed. Per-row failures the import collects into `results.Failed` are
    // NOT throws and don't prevent the commit.
    public sealed class FastImportSession : IAsyncDisposable
    {
        private readonly Db _db;
        private readonly bool _bypassConstraints;
        private NpgsqlConnection _conn = null!;
        private NpgsqlTransaction _tx = null!;
        private bool _success;

        // Per-connection setup the caller depends on, re-run verbatim whenever
        // this session gets a fresh connection or a fresh transaction. The
        // import uses it for its scratch TEMP tables, which die with their
        // session (reconnect) and are undone by a rollback (transaction reset).
        private Func<NpgsqlConnection, CancellationToken, Task>? _onConnect;

        internal FastImportSession(Db db, bool bypassConstraints)
        {
            _db = db;
            _bypassConstraints = bypassConstraints;
        }

        // True when the session runs in replica role (--fast): FK constraints
        // and triggers are bypassed, so integrity errors can't occur and the
        // importer's per-row fallback must not engage (its autonomous per-row
        // connections WOULD enforce FKs, silently changing --fast semantics).
        public bool BypassConstraints => _bypassConstraints;

        // The live connection. NOT stable across a reconnect — callers issuing
        // DB I/O must read this at the point of use, never cache it, or a
        // mid-run reconnect hands them a disposed connection.
        public NpgsqlConnection Connection => _conn;

        internal async Task OpenAsync(CancellationToken ct)
        {
            _conn = await _db.OpenImportAsync(ct);
            try
            {
                if (_bypassConstraints) await SetReplicaRoleAsync(_conn, ct);
                // Npgsql auto-associates the open transaction with any
                // NpgsqlCommand built on _conn, so the repos and bulk helpers
                // don't need to know about it.
                _tx = await _conn.BeginTransactionAsync(ct);
            }
            catch (PostgresException ex)
            {
                await _conn.DisposeAsync();
                // Actionable on purpose: the fix is one GRANT, but only if the
                // operator is told the right one. session_replication_role is a
                // superuser-context GUC, and PG 15+ can delegate exactly it via
                // a per-parameter grant.
                throw new InvalidOperationException(
                    "dmart import --fast could not SET session_replication_role: "
                    + $"{ex.MessageText}. Grant the privilege as a superuser with "
                    + "`GRANT SET ON PARAMETER session_replication_role TO <role>;` "
                    + "(PostgreSQL 15+), or run the import as a superuser. Note there is no "
                    + "`pg_session_replication_role` predefined role to grant.",
                    ex);
            }
            catch
            {
                await _conn.DisposeAsync();
                throw;
            }
        }

        public void MarkSuccess() => _success = true;

        // Register per-connection setup and run it once on the current
        // connection. `init` MUST be idempotent: it re-runs after every
        // reconnect (a fresh backend has none of the old session state) and
        // after every transaction reset (a rollback undoes anything the
        // initializer created). Call once, right after the session opens.
        public async Task InitConnectionAsync(
            Func<NpgsqlConnection, CancellationToken, Task> init, CancellationToken ct)
        {
            _onConnect = init;
            await init(_conn, ct);
        }

        // Run one batch as its own durable unit: execute `body` in the current
        // transaction, COMMIT it, then open a fresh transaction for the next
        // batch. On a TRANSPORT-level failure (connection reset, socket/IO
        // error — NOT a PostgresException, which is a deterministic server-side
        // rejection that replay won't fix) the dead connection is replaced and
        // `body` is replayed. Safe because prior batches are already committed
        // and the bulk merge is idempotent (ON CONFLICT). `body` returns its
        // stats deltas; the caller applies them only AFTER this returns, so a
        // replay can't double-count.
        public async Task<T> RunBatchAsync<T>(
            Func<NpgsqlConnection, CancellationToken, Task<T>> body,
            ILogger? log, CancellationToken ct)
        {
            const int maxAttempts = 5;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var result = await body(_conn, ct);
                    await _tx.CommitAsync(ct);
                    await _tx.DisposeAsync();
                    _tx = await _conn.BeginTransactionAsync(ct);
                    return result;
                }
                // A command timeout is NOT retried here. Replaying the same
                // rows takes the same too-long time, so the old behaviour was
                // to spend five timeouts' worth of wall-clock and then kill the
                // shard anyway. Hand it straight to the bisecting overload,
                // which halves the work instead — but leave the session usable
                // first: a cancelled statement leaves the transaction aborted
                // (every later command fails 25P02), and if Npgsql couldn't
                // resync the cancel the connection is gone too.
                catch (Exception ex) when (IsTimeout(ex) && !ct.IsCancellationRequested)
                {
                    try
                    {
                        if (_conn.State == System.Data.ConnectionState.Open) await ResetTransactionAsync(ct);
                        else                                                 await ReconnectAsync(ct);
                    }
                    catch
                    {
                        // Cleanup on a half-dead connection — a fresh one is
                        // the only remaining way to leave the session usable.
                        try { await ReconnectAsync(ct); } catch { /* rethrow below carries the real cause */ }
                    }
                    throw;
                }
                // Retry on either an explicitly transient exception OR a dead
                // connection (State != Open). The latter catches the case where
                // a prior reconnect's connection later broke and Npgsql surfaces
                // it as `InvalidOperationException("Connection is not open")` on
                // the next command — IsTransient doesn't recognize that flavor,
                // but the state check is authoritative: if the connection isn't
                // open, the only path forward is a fresh one + replay.
                catch (Exception ex) when (attempt < maxAttempts
                                           && (IsTransient(ex) || _conn.State != System.Data.ConnectionState.Open)
                                           && !ct.IsCancellationRequested)
                {
                    log?.LogWarning(ex,
                        "import: transport error on batch (attempt {Attempt}/{Max}, conn state {State}); reconnecting",
                        attempt, maxAttempts, _conn.State);
                    // Exponential backoff capped at 5s — a NAT reaper or a
                    // restarting server may need a beat before a fresh
                    // connection is accepted.
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(5000, 200 * (1 << (attempt - 1)))), ct);
                    await ReconnectAsync(ct);
                }
            }
        }

        // Row-oriented RunBatchAsync that BISECTS on timeout instead of giving
        // up. Plain RunBatchAsync replays the identical body on every attempt,
        // which is right for a dropped connection (replay succeeds on a healthy
        // one) but futile for a batch that is simply too slow to finish inside
        // the command timeout — there, five attempts fail identically and take
        // the whole shard down with them. Splitting the rows halves the server
        // work per attempt, and each half commits as its own durable batch.
        //
        // Only reachable when a command timeout is actually in force: the
        // import's own connection string sets none (BuildImportConnectionString),
        // so this is the safety net for an operator-supplied "Command Timeout"
        // or a server-side statement_timeout.
        //
        // `body` must tolerate replay — same contract as RunBatchAsync — and
        // `combine` folds two halves' stats into one. Deltas are returned, not
        // applied, so the caller still applies them exactly once.
        public async Task<T> RunBatchAsync<TRow, T>(
            List<TRow> rows,
            Func<NpgsqlConnection, List<TRow>, CancellationToken, Task<T>> body,
            Func<T, T, T> combine,
            ILogger? log, CancellationToken ct)
        {
            try
            {
                return await RunBatchAsync((c, t) => body(c, rows, t), log, ct);
            }
            catch (Exception ex) when (rows.Count > 1 && IsTimeout(ex) && !ct.IsCancellationRequested)
            {
                var half = rows.Count / 2;
                log?.LogWarning(ex,
                    "import: batch of {Count} exhausted its retries on a command timeout; "
                    + "splitting into {Left}+{Right} and retrying each half",
                    rows.Count, half, rows.Count - half);
                // Recursive, so a batch that is still too slow keeps halving
                // until it fits or reaches a single row (at which point the
                // guard above stops and the real error propagates).
                var left = await RunBatchAsync(rows.GetRange(0, half), body, combine, log, ct);
                var right = await RunBatchAsync(rows.GetRange(half, rows.Count - half), body, combine, log, ct);
                return combine(left, right);
            }
        }

        // Replace a dead connection with a fresh one in replica mode + open tx.
        // The old tx/conn are disposed best-effort — they're already broken.
        private async Task ReconnectAsync(CancellationToken ct)
        {
            try { await _tx.DisposeAsync(); }   catch { /* already broken */ }
            try { await _conn.DisposeAsync(); } catch { /* already broken */ }
            _conn = await _db.OpenImportAsync(ct);
            if (_bypassConstraints) await SetReplicaRoleAsync(_conn, ct);
            _tx = await _conn.BeginTransactionAsync(ct);
            // The old backend's session state (TEMP tables) died with it —
            // rebuild it before the caller replays its batch against it.
            if (_onConnect is not null) await _onConnect(_conn, ct);
        }

        // Recover from a deterministically-aborted transaction (e.g. an
        // integrity violation surfaced by the batch merge or its deferred-FK
        // commit): roll the dead transaction back and open a fresh one so the
        // session can keep batching. The connection itself is still healthy.
        public async Task ResetTransactionAsync(CancellationToken ct)
        {
            try { await _tx.RollbackAsync(ct); } catch { /* already aborted server-side */ }
            await _tx.DisposeAsync();
            _tx = await _conn.BeginTransactionAsync(ct);
            // The rollback also undid anything the initializer created inside
            // the dead transaction (TEMP tables created but not yet committed),
            // so re-run it on the fresh one.
            if (_onConnect is not null) await _onConnect(_conn, ct);
        }

        // Run one statement inside a savepoint so a per-row/per-line failure
        // (e.g. one bad history line) aborts only the savepoint, not the
        // session's open transaction. Without this, the first bad line poisons
        // the transaction and every subsequent statement fails with 25P02.
        public async Task RunInSavepointAsync(
            Func<NpgsqlConnection, CancellationToken, Task> body, CancellationToken ct)
        {
            await _tx.SaveAsync("sp_import_row", ct);
            try
            {
                await body(_conn, ct);
                await _tx.ReleaseAsync("sp_import_row", ct);
            }
            catch
            {
                await _tx.RollbackAsync("sp_import_row", ct);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_success) await _tx.CommitAsync();
                else          await _tx.RollbackAsync();
            }
            catch
            {
                // Best-effort: a broken connection means the transaction is
                // already aborted server-side. Continue to restore the role.
            }
            finally
            {
                await _tx.DisposeAsync();
            }
            if (_bypassConstraints)
            {
                try
                {
                    await using var cmd = new NpgsqlCommand("SET session_replication_role = DEFAULT", _conn);
                    await cmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Same reasoning as above.
                }
            }
            await _conn.DisposeAsync();
        }

        private static async Task SetReplicaRoleAsync(NpgsqlConnection conn, CancellationToken ct)
        {
            await using var cmd = new NpgsqlCommand("SET session_replication_role = 'replica'", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Is this a lost/unusable connection (reconnect + replay is right) or a
        // deterministic server rejection (replay won't help — let it abort)?
        // A dropped connection reaches us two ways: as a raw transport error
        // (NpgsqlException "Exception while reading from stream" / socket / IO),
        // OR as a PostgresException whose SQLSTATE is a connection-failure class
        // — e.g. 57P01 when a firewall/idle reaper or `pg_terminate_backend`
        // tears down the backend, or a class-08 connection exception. Everything
        // else (constraint violations, bad SQL) is deterministic and must NOT
        // retry, or we'd burn the retry budget masking a real error.
        private static bool IsTransient(Exception ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is PostgresException pg) return IsConnectionFailureState(pg.SqlState);
                if (e is NpgsqlException or System.Net.Sockets.SocketException or System.IO.IOException or TimeoutException)
                    return true;
            }
            return false;
        }

        // Narrower than IsTransient: is this specifically "the work didn't
        // finish in time" rather than "the connection went away"? Npgsql wraps
        // a client-side command timeout as NpgsqlException("Exception while
        // reading from stream") over an inner TimeoutException; a server-side
        // statement_timeout arrives as PostgresException 57014. Both mean
        // replaying the same rows is pointless — only less work per attempt
        // helps. A bare NpgsqlException (real socket loss) is deliberately NOT
        // matched: replay on a fresh connection is the correct fix there.
        internal static bool IsTimeout(Exception ex)
        {
            for (Exception? e = ex; e is not null; e = e.InnerException)
            {
                if (e is TimeoutException) return true;
                if (e is PostgresException pg) return pg.SqlState == "57014";
            }
            return false;
        }

        private static bool IsConnectionFailureState(string? sqlState) => sqlState switch
        {
            // Class 08 — connection exception.
            "08000" or "08003" or "08006" or "08001" or "08004" or "08007" or "08P01"
            // Class 57 — operator intervention that tears down the connection
            // (admin terminate, crash shutdown, cannot-connect-now).
            or "57P01" or "57P02" or "57P03" => true,
            _ => false,
        };
    }

    // Runs a transactional operation with bounded retry on PG 40P01 (deadlock
    // detected). Deadlocks are transient — the loser's transaction is fully
    // rolled back, so replay from scratch is correct. Use from repositories
    // that perform a multi-statement transaction where concurrent writers
    // can race on row locks (e.g. SpaceRepository.DeleteAsync vs a late
    // plugin writing into `entries`). Any PostgresException other than
    // 40P01 bubbles up unchanged so real errors aren't masked.
    public async Task<T> ExecuteWithRetryOnDeadlockAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct = default)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == "40P01" && attempt < maxAttempts)
            {
                // Linear backoff: 50ms, 100ms. Deadlocks resolve fast so
                // exponential backoff would just add latency without benefit.
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct);
            }
        }
    }

    // Assemble order: explicit PostgresConnection always wins (lets ops override
    // any individual tuning knob via a raw Npgsql connection string). Otherwise
    // we build from the components. If none of the DATABASE_* fields have been
    // set, we still emit a localhost-ish string with empty password so Npgsql
    // can surface a clearer error than "connection string is null".
    /// <summary>
    /// True when the operator actually configured PostgreSQL, as opposed to
    /// leaving every DATABASE_* field at its built-in default.
    /// </summary>
    /// <remarks>
    /// Mirrors Python's behaviour: if every component is still at its default,
    /// the database counts as "not configured" so IsConfigured stays false and
    /// smoke/test hosts can boot without PostgreSQL. Null values (from test
    /// overrides that explicitly clear a field) are treated as "not set".
    ///
    /// Named and made internal because DATABASE_DRIVER inference asks the very
    /// same question — "did anyone point this deployment at PostgreSQL?" — and
    /// two different answers to it would be a bug generator. In particular the
    /// bound settings object CANNOT be read directly for this: DatabaseHost
    /// defaults to "localhost" and DatabaseName to "dmart", so a config with no
    /// DATABASE_* keys at all still looks fully populated.
    /// </remarks>
    internal static bool HasExplicitPostgresConfig(DmartSettings s) =>
        !string.IsNullOrEmpty(s.PostgresConnection)
        || !string.IsNullOrEmpty(s.DatabasePassword)
        || (!string.IsNullOrEmpty(s.DatabaseUsername) && s.DatabaseUsername != "dmart")
        || (!string.IsNullOrEmpty(s.DatabaseName) && s.DatabaseName != "dmart")
        || (!string.IsNullOrEmpty(s.DatabaseHost) && s.DatabaseHost != "localhost")
        || s.DatabasePort != 5432;

    /// <summary>
    /// The host's IANA timezone id, or null if it cannot be resolved.
    /// </summary>
    /// <remarks>
    /// Linux reports IANA ids directly; Windows reports its own, which .NET can
    /// translate. If neither works the caller leaves the session timezone
    /// alone — an unresolvable zone must not stop the app connecting.
    /// </remarks>
    private static string? HostIanaTimeZone()
    {
        var id = TimeZoneInfo.Local.Id;
        if (id.Contains('/', StringComparison.Ordinal)) return id;   // already IANA
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : null;
    }

    /// <summary>
    /// Pins the PostgreSQL session timezone to the APP HOST's zone.
    /// </summary>
    /// <remarks>
    /// dmart stores naive timestamps — `timestamp without time zone`, a bare
    /// wall-clock reading with no offset attached. Two code paths write them:
    /// <c>TimeUtils.Now()</c> reads the APP HOST's clock, while SQL <c>NOW()</c>
    /// is rendered in the DATABASE SESSION's timezone. When those zones differ —
    /// a UTC database with a non-UTC host, which is the common production shape
    /// — the same instant is recorded as two different wall clocks in the same
    /// column, and nothing in a naive timestamp reveals which clock produced it.
    ///
    /// The observable damage: an incremental export selects
    /// `updated_at &gt;= watermark` against a host-clock watermark, so a row
    /// stamped by a `NOW()` path (folder move, rename) lands hours in the past
    /// and is silently skipped — permanently, because the next watermark is
    /// later still. Session-expiry comparisons of a stored timestamp against
    /// `NOW()` are off by the same offset.
    ///
    /// Setting the session timezone makes `NOW()` agree with the host clock by
    /// construction, which is what the naive convention always assumed and
    /// never enforced. No SQL changes anywhere.
    ///
    /// An explicit `Timezone=` in the operator's own connection string is left
    /// untouched — that is the escape hatch, and someone who set it meant it.
    ///
    /// NOTE: this corrects NEW writes. Rows already stamped through a `NOW()`
    /// path keep their offset, and no migration here can safely guess which
    /// ones those were.
    /// </remarks>
    private static string ApplyHostTimezone(string connectionString)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(csb.Timezone)) return connectionString;

        var zone = HostIanaTimeZone();
        if (zone is null) return connectionString;

        csb.Timezone = zone;
        return csb.ConnectionString;
    }

    private static string? BuildConnectionString(DmartSettings s)
    {
        if (!string.IsNullOrEmpty(s.PostgresConnection))
            return ApplyHostTimezone(s.PostgresConnection);

        if (!HasExplicitPostgresConfig(s)) return null;

        // Guard: Host is required by Npgsql — if it's null/empty we can't connect.
        if (string.IsNullOrEmpty(s.DatabaseHost)) return null;

        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = s.DatabaseHost,
            Port = s.DatabasePort,
            Username = s.DatabaseUsername,
            Password = s.DatabasePassword,
            Database = s.DatabaseName,
            MaxPoolSize = s.DatabasePoolSize + s.DatabaseMaxOverflow,
            Timeout = s.DatabasePoolTimeout,
            ConnectionIdleLifetime = s.DatabasePoolRecycle,
        };
        // Protocol keepalive (an idle-time SELECT 1) keeps NAT/firewall flow
        // state warm and surfaces a dead peer quickly; TcpKeepAlive adds the
        // OS-level SO_KEEPALIVE so a half-open socket during a long COPY is
        // detected too. Both off when DatabaseKeepalive is 0.
        if (s.DatabaseKeepalive > 0)
        {
            csb.KeepAlive = s.DatabaseKeepalive;
            csb.TcpKeepAlive = true;
        }
        // See ApplyHostTimezone: NOW() and TimeUtils.Now() must read the same
        // clock, or naive timestamps written by different paths are hours apart.
        var zone = HostIanaTimeZone();
        if (zone is not null) csb.Timezone = zone;
        return csb.ConnectionString;
    }

    // The app connection string with the per-command timeout lifted, used only
    // by the import sessions.
    //
    // Npgsql defaults CommandTimeout to 30s. A bulk batch is a COPY into a
    // scratch table plus an `INSERT ... ON CONFLICT` merge into a table that,
    // late in a multi-million-entry import, already holds millions of rows and
    // several indexes — routinely more than 30s of server work. Worse, Npgsql
    // surfaces a command timeout as NpgsqlException("Exception while reading
    // from stream") with an inner TimeoutException, which is indistinguishable
    // from a dropped connection: FastImportSession.RunBatchAsync classifies it
    // as transient and replays the identical batch, so all five attempts time
    // out the same way and the shard dies at 90%+ of a three-hour run.
    //
    // 0 = no client-side limit, matching the CommandTimeout = 0 the schema
    // initializer and the CLI's long-running DDL already use. This does not
    // make a hung import unkillable: Keepalive + TcpKeepAlive (set above) are
    // what detect a genuinely dead peer, and Ctrl-C still cancels via the
    // CancellationToken. An operator who set "Command Timeout" explicitly in
    // POSTGRES_CONNECTION keeps their value — explicit config always wins.
    internal static string? BuildImportConnectionString(DmartSettings s)
    {
        var baseConn = BuildConnectionString(s);
        if (string.IsNullOrEmpty(baseConn)) return baseConn;
        var csb = new NpgsqlConnectionStringBuilder(baseConn);
        // `Keys` holds only what the string actually set, normalized to Npgsql's
        // canonical spelling (so "commandtimeout" and "COMMAND TIMEOUT" both
        // land as "Command Timeout"). ContainsKey is NOT usable here: the typed
        // builder answers true for every keyword it knows about, set or not.
        var explicitTimeout = csb.Keys.Cast<string>()
            .Any(k => string.Equals(k, "Command Timeout", StringComparison.OrdinalIgnoreCase));
        if (!explicitTimeout) csb.CommandTimeout = 0;
        return csb.ConnectionString;
    }
}
