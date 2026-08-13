using System.Data.Common;
using Dmart.Config;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Dmart.DataAdapters.Sql;

// Opens SQLite connections with the PRAGMAs csdmart depends on.
//
// PRAGMAs are CONNECTION-scoped, not database-scoped, with two exceptions
// (journal_mode and, on disk, page_size) that persist in the file header. The
// app opens a fresh connection per call, and Microsoft.Data.Sqlite pools by
// connection string — a pooled connection keeps its PRAGMA state, so a hit
// re-applies them redundantly but never incorrectly. Issuing them here, in the
// one place connections are created, is what makes that guarantee hold; doing
// it "once at startup" would silently apply to a single connection and leave
// every other one at defaults.
//
// The settings are deliberate (see docs/sqlite-backend-audit.md §10):
//
//   journal_mode=WAL   Concurrent readers alongside one writer. Without it,
//                      every read blocks every write and the tier is unusable
//                      under even light concurrency. Persists in the file.
//   busy_timeout       Absorbs ordinary lock contention by sleeping+retrying
//                      inside the engine instead of surfacing SQLITE_BUSY.
//                      Does NOT cover deferred-transaction lock upgrades — see
//                      SqliteRetry for the layer that does.
//   synchronous=NORMAL Correct and standard under WAL: safe across process
//                      crashes, with a small durability window on OS/power
//                      loss. FULL costs an fsync per commit for a rebuildable
//                      index, which is not a trade worth making here.
//   foreign_keys=ON    OFF is SQLite's default. The schema declares deferred
//                      foreign keys, and leaving this off makes every one of
//                      them silently decorative.
//   mmap_size          Reads served from the page cache without a syscall.
//                      Ignored when not compiled in, so it is safe to set
//                      unconditionally.
//   cache_size         Negative = KiB rather than pages, so the footprint does
//                      not change meaning with page_size.
//   case_sensitive_like=ON
//                      SQLite's LIKE is ASCII-case-INSENSITIVE by default;
//                      PostgreSQL's is case-SENSITIVE. Two emission sites rely
//                      on the PostgreSQL semantics, and both are correctness-
//                      critical: the row-level ACL policy match
//                      (QueryHelper.AppendAclFilter) and the hierarchical
//                      subpath prefix filter. Left at the default, the ACL
//                      would match MORE rows on SQLite than on PostgreSQL —
//                      'management:/USERS:*' would satisfy a
//                      'management:/users:%' policy — which is an
//                      access-control widening, not a cosmetic difference.
//
//                      This works only because the case-INSENSITIVE sites
//                      (PostgreSQL ILIKE) are emitted as explicit
//                      lower(x) LIKE lower(y) by SqliteSqlDialect rather than
//                      relying on LIKE's default folding. The two halves are a
//                      pair: turning this pragma on without the lower() emission
//                      would silently make every wildcard search case-sensitive.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100",
    Justification = "Audited: CommandText is assembled from compile-time SQL, dialect-produced fragments and $N placeholders only. Every caller-supplied value is bound through DbParams, never concatenated.")]
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly int _busyTimeoutMs;
    private readonly long _mmapBytes;
    private readonly int _cacheKib;

    public SqliteConnectionFactory(IOptions<DmartSettings> settings)
    {
        var s = settings.Value;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = s.SqlitePath,
            // The schema initializer creates the file when it is absent;
            // ReadWriteCreate keeps a fresh deployment from failing on a
            // missing path before it gets there.
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Shared cache is a legacy mode that interacts badly with WAL and
            // introduces table-level locking. Explicitly private.
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ConnectionString;

        _busyTimeoutMs = 5000;
        _mmapBytes = 256L * 1024 * 1024;
        _cacheKib = 64 * 1024;
    }

    // SQLite always has somewhere to write, so unlike the PostgreSQL factory
    // there is no "not configured" state to degrade into.
    public bool IsConfigured => true;

    public async Task<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(ct);
            await ApplyPragmasAsync(conn, ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    // Exposed so tests can assert the exact PRAGMA state a request-path
    // connection runs under, rather than trusting that it was applied.
    internal async Task ApplyPragmasAsync(SqliteConnection conn, CancellationToken ct)
    {
        // journal_mode returns a row ("wal"); the rest are silent. Executing
        // them all through ExecuteNonQuery is fine — the row is simply
        // discarded.
        var pragmas = new[]
        {
            "PRAGMA journal_mode = WAL",
            $"PRAGMA busy_timeout = {_busyTimeoutMs}",
            "PRAGMA synchronous = NORMAL",
            "PRAGMA foreign_keys = ON",
            $"PRAGMA mmap_size = {_mmapBytes}",
            $"PRAGMA cache_size = -{_cacheKib}",
            "PRAGMA case_sensitive_like = ON",
        };
        foreach (var pragma in pragmas)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = pragma;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
