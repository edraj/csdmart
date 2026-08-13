using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Cli;

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
