using System.Collections.Concurrent;
using Dmart.DataAdapters.Sql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Two operability guarantees around schema initialization:
//
// 1. `users.payload` gets its containment (GIN) index. The generic query
//    machinery emits `payload::jsonb @> $n` against users exactly as it
//    does against entries, but only entries had a GIN index — on users
//    those filters seq-scanned (observed at 1.5-2.2s per query in
//    production).
//
// 2. RAISE NOTICE/WARNING emitted by CreateAll's DO blocks reaches the
//    application log. Postgres delivers RAISE messages as protocol
//    notices, not query results; without an Npgsql Notice handler they
//    vanish. That is how production ran for months with
//    "Skipping idx_users_email_lower_unique: N duplicate email group(s)
//    exist" being raised on every migrate while nobody could see it.
public sealed class SchemaInitializerObservabilityTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public SchemaInitializerObservabilityTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Users_Payload_Gin_Index_Is_Created_On_Startup()
    {
        _ = _factory.Services; // force host start → SchemaInitializer ran
        await using var conn = new NpgsqlConnection(DmartFactory.PgConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT 1 FROM pg_indexes
            WHERE schemaname = current_schema()
              AND tablename = 'users'
              AND indexname = 'idx_users_payload_gin'
            """, conn);
        (await cmd.ExecuteScalarAsync()).ShouldNotBeNull(
            "idx_users_payload_gin missing — payload containment filters on users will seq-scan");
    }

    [FactIfPg]
    public void Postgres_Notices_Are_Forwarded_To_The_Log()
    {
        // Boot the base factory first so every table exists; the derived
        // host's CreateAll then raises "relation ... already exists,
        // skipping" notices, which must surface through the logger.
        _ = _factory.Services;

        var capture = new CaptureLoggerProvider();
        using var derived = _factory.WithWebHostBuilder(b => b.ConfigureLogging(l =>
        {
            l.AddProvider(capture);
            l.SetMinimumLevel(LogLevel.Information);
        }));
        _ = derived.Services; // boot → SchemaInitializer runs against existing schema

        capture.Entries.ShouldContain(
            e => e.Category.Contains("SchemaInitializer") && e.Message.Contains("already exists, skipping"),
            customMessage: "no Postgres notice reached the log — RAISE WARNING from the " +
                           "index-skip guards in SqlSchema would be invisible to operators");
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public sealed record Entry(string Category, LogLevel Level, string Message);
        public ConcurrentQueue<Entry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class CaptureLogger(string category, ConcurrentQueue<Entry> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(new Entry(category, logLevel, formatter(state, exception)));
        }
    }
}
