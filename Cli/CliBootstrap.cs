using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Plugins;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Cli;

/// <summary>
/// The service graph a CLI command needs to write through the application
/// layer instead of around it.
/// </summary>
/// <param name="Entries2">
/// EntryService — every create/update/delete goes here, so the write pays for
/// permissions, schema validation, folder policy, uniqueness, hooks, history
/// and query_policies exactly as an HTTP caller's would.
/// </param>
/// <param name="Entries">
/// EntryRepository — READS only. Reading has no application logic to respect,
/// and routing list queries through QueryService would need a second graph
/// (and an ACL pass) for no benefit to a super-admin CLI run.
/// </param>
/// <param name="Permissions">
/// Used to verify up front that the actor is an effective super admin, so a
/// missing prerequisite is one clear error rather than a run of failed writes.
/// </param>
internal sealed record CliServices(
    EntryService Entries2,
    EntryRepository Entries,
    PermissionService Permissions);

// Shared bootstrap for CLI subcommands that need a configured Db (passwd,
// check, export, import, migrate, fix_query_policies). Each of those used to
// repeat the same 8-line block:
//   - build IConfiguration from dotenv values + env vars
//   - bind into DmartSettings
//   - construct a Db
//   - refuse to proceed when Db isn't configured (exit 1)
//
// BuildOrExit consolidates that into one call. The error message is
// per-caller because different subcommands historically surfaced slightly
// different wording (some "Database not configured", others point at the
// specific DATABASE_* keys). Preserved verbatim to avoid behavior drift for
// anyone grepping output in a script.
//
// On the "not configured" path the helper calls Environment.Exit(1) rather
// than throwing, mirroring the pre-existing `Environment.ExitCode = 1; return;`
// semantics of every caller — the process terminates immediately with the
// same exit code, and the caller never sees the tuple.
internal static class CliBootstrap
{
    public static (DmartSettings Settings, Db Db) BuildOrExit(
        string? dotenvPath,
        IDictionary<string, string?> dotenvValues,
        string? dbRequiredErrorMessage = null)
    {
        var cfgBuilder = new ConfigurationBuilder();
        if (dotenvPath is not null) cfgBuilder.AddInMemoryCollection(dotenvValues);
        cfgBuilder.AddEnvironmentVariables();
        var cfg = cfgBuilder.Build();
        var s = new DmartSettings();
        cfg.GetSection("Dmart").Bind(s);
        var db = new Db(Options.Create(s));
        if (!db.IsConfigured)
        {
            Console.Error.WriteLine(dbRequiredErrorMessage ?? "Database not configured");
            Environment.Exit(1);
        }
        return (s, db);
    }

    // Driver-aware sibling of BuildOrExit, for the subcommands that can run on
    // either backend. Returns the connection factory the configured driver
    // selects — the same choice Program.cs makes for the server — instead of
    // hard-wiring PostgreSQL.
    //
    // Deliberately additive rather than a change to BuildOrExit. The other
    // subcommands (migrate, passwd, fix_query_policies, check) are still
    // PostgreSQL-only in their bodies, not merely in their bootstrap, so
    // widening the shared helper would hand them a factory they cannot use and
    // turn a clear "not configured" exit into a failure deep inside a command.
    public static (DmartSettings Settings, IDbConnectionFactory Db) BuildFactoryOrExit(
        string? dotenvPath,
        IDictionary<string, string?> dotenvValues,
        string? dbRequiredErrorMessage = null)
    {
        var cfgBuilder = new ConfigurationBuilder();
        if (dotenvPath is not null) cfgBuilder.AddInMemoryCollection(dotenvValues);
        cfgBuilder.AddEnvironmentVariables();
        var cfg = cfgBuilder.Build();
        var s = new DmartSettings();
        cfg.GetSection("Dmart").Bind(s);

        if (!DatabaseDriverParser.TryResolve(s, out var driver, out var inferred))
        {
            Console.Error.WriteLine(
                $"Unknown DATABASE_DRIVER '{s.DatabaseDriver}' (supported: {DatabaseDriverParser.Supported})");
            Environment.Exit(1);
        }

        Console.Error.WriteLine($"database driver: {DatabaseDriverParser.Describe(driver, inferred)}");

        if (driver == DatabaseDriver.Sqlite)
        {
            var sqlite = new SqliteConnectionFactory(Options.Create(s));
            // A reindex is allowed to run against a database file that does
            // not exist yet — the flat files are the source of truth, and
            // requiring a separate migrate step first would make "rebuild the
            // index" a two-command operation for no reason.
            SqliteSchemaInitializer.EnsureSchemaAsync(sqlite, CliLogger()).GetAwaiter().GetResult();
            return (s, sqlite);
        }

        var db = new Db(Options.Create(s));
        if (!db.IsConfigured)
        {
            Console.Error.WriteLine(dbRequiredErrorMessage ?? "Database not configured");
            Environment.Exit(1);
        }
        return (s, db);
    }

    /// <summary>
    /// Human-readable name of the store a CLI command is about to touch, for
    /// the "Migrating X ..." style banners.
    /// </summary>
    /// <remarks>
    /// Printing "$name@$host:$port" unconditionally was actively misleading
    /// once these commands became driver-aware: a SQLite run reported the
    /// PostgreSQL host and database it was NOT using, both of which come from
    /// DmartSettings defaults and are populated even when nothing configured
    /// them. An operator reading that has every reason to believe the command
    /// hit PostgreSQL.
    /// </remarks>
    public static string DescribeStore(DmartSettings s, IDbConnectionFactory db) =>
        db is SqliteConnectionFactory
            ? s.SqlitePath
            : $"{s.DatabaseName}@{s.DatabaseHost}:{s.DatabasePort}";

    // Build the ImportExportService with the same null-logger / null-plugin
    // wiring the CLI subcommands need. Used by `export`, `import`, and `seed`
    // — three sites that historically each constructed an identical 9-line
    // graph (entry/user/access repos + refresher + permission svc + service).
    // Centralizing here means a new ImportExportService dependency lands in
    // one place instead of three. The helper does not need the intermediate
    // repositories because none of the callers reuse them outside the
    // service construction.
    //
    // LIFECYCLE: builds an ephemeral object graph — a fresh
    // AuthzCacheRefresher, fresh repositories, fresh PermissionService — every
    // call. Intended for short-lived CLI invocations where the process exits
    // before any cache invalidation matters. DO NOT call from long-running
    // code paths (HTTP handlers, hosted services); cache invalidations on
    // the dedicated DI graph would not propagate to instances built here.
    public static ImportExportService BuildImportExportService(DmartSettings s, IDbConnectionFactory db)
    {
        // Real (not null) logger so a long `dmart import` shows progress —
        // shard fan-out, per-batch commit counts, reconnect warnings — instead
        // of running silently for hours. A minimal stderr provider keeps this
        // AOT-safe (the generic AddConsoleFormatter<,> the serve path avoids is
        // never touched) and gives clean human-readable lines.
        var nlog = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new StderrLoggerProvider(LogLevel.Information)));
        var refresher = new AuthzCacheRefresher();
        var entryRepo = new EntryRepository(db);
        var userRepo = new UserRepository(db, refresher, new SessionTokenHasher(s));
        // The dialect follows the factory, not the config key: whatever the
        // repositories were handed is what they must emit for.
        var dialect = db is SqliteConnectionFactory
            ? (Dmart.QueryGrammar.ISqlDialect)Dmart.QueryGrammar.SqliteSqlDialect.Instance
            : Dmart.QueryGrammar.PostgresSqlDialect.Instance;
        var accessRepo = new AccessRepository(db, dialect, refresher, userRepo);
        return new ImportExportService(
            entryRepo,
            new AttachmentRepository(db, dialect),
            userRepo,
            accessRepo,
            new SpaceRepository(db),
            new HistoryRepository(db, dialect),
            new PermissionService(userRepo, accessRepo, refresher),
            db,
            Options.Create(s),
            nlog.CreateLogger<ImportExportService>());
    }

    // The write path a CLI command needs when it must behave like an API
    // caller rather than like an importer: EntryService applies permissions,
    // schema validation, folder content policy, uniqueness, history and
    // query_policies to every create/update/delete. `client-schema-migrate`
    // uses it for exactly that reason.
    //
    // Entries (the repository) rides along for READS, which have no
    // application logic to respect and would otherwise need a second graph.
    //
    // PluginManager is built with EMPTY plugin lists: LoadAsync is never
    // called, so DispatchBefore/After find no hooks and return immediately.
    // A CLI process has no plugin directory contract to honour and no HTTP
    // context for the hooks that assume one — same null-plugin wiring
    // BuildImportExportService documents.
    //
    // LIFECYCLE: ephemeral, single-invocation object graph. See the note on
    // BuildImportExportService — do not call this from long-running code.
    public static CliServices BuildEntryService(DmartSettings s, IDbConnectionFactory db)
    {
        var nlog = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Warning)
            .AddProvider(new StderrLoggerProvider(LogLevel.Warning)));
        var opts = Options.Create(s);
        var dialect = db is SqliteConnectionFactory
            ? (Dmart.QueryGrammar.ISqlDialect)Dmart.QueryGrammar.SqliteSqlDialect.Instance
            : Dmart.QueryGrammar.PostgresSqlDialect.Instance;

        var refresher = new AuthzCacheRefresher();
        var entryRepo = new EntryRepository(db);
        var userRepo = new UserRepository(db, refresher, new SessionTokenHasher(s));
        var accessRepo = new AccessRepository(db, dialect, refresher, userRepo);
        var attachmentRepo = new AttachmentRepository(db, dialect);
        var historyRepo = new HistoryRepository(db, dialect);
        var lockRepo = new LockRepository(db, dialect);
        var perms = new PermissionService(userRepo, accessRepo, refresher);

        var plugins = new PluginManager(
            [], [],
            new SpaceEventLogger(opts, nlog.CreateLogger<SpaceEventLogger>()),
            nlog.CreateLogger<PluginManager>());

        var entryService = new EntryService(
            entryRepo,
            attachmentRepo,
            historyRepo,
            perms,
            plugins,
            new SchemaValidator(entryRepo, nlog.CreateLogger<SchemaValidator>()),
            new WorkflowEngine(entryRepo, nlog.CreateLogger<WorkflowEngine>()),
            lockRepo,
            opts,
            nlog.CreateLogger<EntryService>(),
            new UniquenessValidator(entryRepo, userRepo, accessRepo, attachmentRepo,
                nlog.CreateLogger<UniquenessValidator>()),
            new FolderContentValidator(entryRepo, nlog.CreateLogger<FolderContentValidator>(), opts));

        return new CliServices(entryService, entryRepo, perms);
    }

    // Just the history table — `prune-empty-histories` needs nothing else.
    //
    // The dialect follows the FACTORY, not the config key, for the same reason
    // the graph above does: whatever the repository was handed is what it must
    // emit for.
    public static HistoryRepository BuildHistoryRepository(IDbConnectionFactory db)
        => new(db, db is SqliteConnectionFactory
            ? (Dmart.QueryGrammar.ISqlDialect)Dmart.QueryGrammar.SqliteSqlDialect.Instance
            : Dmart.QueryGrammar.PostgresSqlDialect.Instance);

    // The Parquet archiver needs most of the graph the zip one does, now that
    // it writes spaces, users, roles and permissions alongside entries.
    public static ParquetArchiveService BuildParquetArchiveService(
        DmartSettings s, IDbConnectionFactory db)
    {
        var nlog = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new StderrLoggerProvider(LogLevel.Information)));
        var refresher = new AuthzCacheRefresher();
        var userRepo = new UserRepository(db, refresher, new SessionTokenHasher(s));
        var dialect = db is SqliteConnectionFactory
            ? (Dmart.QueryGrammar.ISqlDialect)Dmart.QueryGrammar.SqliteSqlDialect.Instance
            : Dmart.QueryGrammar.PostgresSqlDialect.Instance;
        var accessRepo = new AccessRepository(db, dialect, refresher, userRepo);
        return new ParquetArchiveService(
            db,
            BuildImportExportService(s, db),
            new EntryRepository(db),
            new AttachmentRepository(db, dialect),
            new HistoryRepository(db, dialect),
            new SpaceRepository(db),
            userRepo,
            accessRepo,
            new PermissionService(userRepo, accessRepo, refresher),
            Options.Create(s),
            nlog.CreateLogger<ParquetArchiveService>());
    }

    // Verifies that the DATABASE matches an archive after a restore — the other
    // half of the archive-readability check the export already does.
    public static ParquetRestoreVerifier BuildParquetRestoreVerifier(
        DmartSettings s, IDbConnectionFactory db)
    {
        var nlog = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new StderrLoggerProvider(LogLevel.Information)));
        var dialect = db is SqliteConnectionFactory
            ? (Dmart.QueryGrammar.ISqlDialect)Dmart.QueryGrammar.SqliteSqlDialect.Instance
            : Dmart.QueryGrammar.PostgresSqlDialect.Instance;
        var refresher = new AuthzCacheRefresher();
        var userRepo = new UserRepository(db, refresher, new SessionTokenHasher(s));
        return new ParquetRestoreVerifier(
            new EntryRepository(db),
            new AttachmentRepository(db, dialect),
            new HistoryRepository(db, dialect),
            new SpaceRepository(db),
            userRepo,
            new AccessRepository(db, dialect, refresher, userRepo),
            nlog.CreateLogger<ParquetRestoreVerifier>());
    }

    // Logger for bootstrap-time work that happens before the service graph
    // exists (schema creation). Same stderr provider the import uses, so the
    // two are visually consistent in one CLI run.
    private static ILogger CliLogger() =>
        LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new StderrLoggerProvider(LogLevel.Information)))
            .CreateLogger("dmart");

    // Minimal AOT-safe console logger: writes Information+ messages to stderr
    // as plain "info: <message>" / "warn: <message>" lines. No reflection, no
    // generic formatter — safe under NativeAOT. stderr (not stdout) so import
    // progress doesn't intermingle with any machine-readable stdout a caller
    // might capture.
    private sealed class StderrLoggerProvider(LogLevel min) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new StderrLogger(min);
        public void Dispose() { }

        private sealed class StderrLogger(LogLevel min) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= min && level != LogLevel.None;

            public void Log<TState>(LogLevel level, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(level)) return;
                var tag = level >= LogLevel.Error ? "error" : level >= LogLevel.Warning ? "warn" : "info";
                Console.Error.WriteLine($"{tag}: {formatter(state, exception)}");
                if (exception is not null) Console.Error.WriteLine($"  {exception.Message}");
            }
        }
    }
}
