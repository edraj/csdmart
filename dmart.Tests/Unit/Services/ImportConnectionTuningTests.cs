using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// The import sessions run on their own connection string, tuned differently
// from the app's. Two properties matter, and both are load-bearing for a
// multi-hour bulk import:
//
//   1. No command timeout. Npgsql defaults to 30s; a 10k-row COPY + merge into
//      a table already holding millions of rows exceeds that late in a run, and
//      Npgsql reports the timeout as NpgsqlException("Exception while reading
//      from stream") — indistinguishable from a dropped connection, so the
//      session's retry loop replays the same too-slow batch until the shard
//      dies. Lifting the timeout removes the failure mode at the source.
//   2. An operator who set "Command Timeout" explicitly keeps their value —
//      POSTGRES_CONNECTION is documented as overriding every tuning knob.
public sealed class ImportConnectionTuningTests
{
    private static DmartSettings Configured(string? explicitConn = null) => new()
    {
        DatabaseHost = "localhost",
        DatabasePort = 5432,
        DatabaseUsername = "dmart",
        DatabasePassword = "secret",
        DatabaseName = "dmart",
        PostgresConnection = explicitConn,
    };

    [Fact]
    public void ImportConnection_LiftsTheCommandTimeout()
    {
        var conn = Db.BuildImportConnectionString(Configured());

        conn.ShouldNotBeNull();
        new NpgsqlConnectionStringBuilder(conn).CommandTimeout.ShouldBe(0);
    }

    [Fact]
    public void AppConnection_KeepsNpgsqlDefaultTimeout()
    {
        // Only the import path gets the unlimited timeout — request handling
        // must keep a bounded one, or a pathological query pins a connection.
        var settings = Configured();
        var app = new NpgsqlConnectionStringBuilder(
            new NpgsqlConnectionStringBuilder { Host = settings.DatabaseHost }.ConnectionString);

        app.CommandTimeout.ShouldBe(30, "sanity: Npgsql's default is what the app path inherits");
    }

    [Fact]
    public void ExplicitOperatorTimeout_IsPreserved()
    {
        var conn = Db.BuildImportConnectionString(
            Configured("Host=db;Username=u;Password=p;Database=d;Command Timeout=45"));

        conn.ShouldNotBeNull();
        new NpgsqlConnectionStringBuilder(conn).CommandTimeout.ShouldBe(45,
            "an explicit POSTGRES_CONNECTION knob must win over our default");
    }

    [Fact]
    public void ExplicitConnectionWithoutTimeout_StillGetsTheLift()
    {
        var conn = Db.BuildImportConnectionString(
            Configured("Host=db;Username=u;Password=p;Database=d"));

        conn.ShouldNotBeNull();
        new NpgsqlConnectionStringBuilder(conn).CommandTimeout.ShouldBe(0);
    }

    [Fact]
    public void UnconfiguredDatabase_StaysUnconfigured()
    {
        // Hosts that boot without Postgres must not gain a connection string
        // just because the import builder ran.
        Db.BuildImportConnectionString(new DmartSettings()).ShouldBeNullOrEmpty();
    }

    // ---- timeout classification --------------------------------------
    //
    // The session retries a lost connection by replaying the batch, but a
    // timeout needs the opposite response (less work per attempt), so the two
    // must not be confused. This is exactly the distinction the original
    // failure blurred: a timeout arrived looking like a transport drop.

    [Fact]
    public void CommandTimeout_IsClassifiedAsTimeout()
    {
        var wrapped = new NpgsqlException(
            "Exception while reading from stream",
            new TimeoutException("Timeout during reading attempt"));

        Db.FastImportSession.IsTimeout(wrapped).ShouldBeTrue();
    }

    [Fact]
    public void ServerSideStatementTimeout_IsClassifiedAsTimeout()
    {
        Db.FastImportSession.IsTimeout(new PostgresException(
            "canceling statement due to statement timeout",
            "ERROR", "ERROR", "57014")).ShouldBeTrue();
    }

    [Fact]
    public void DroppedConnection_IsNotClassifiedAsTimeout()
    {
        // A real socket loss must keep the replay-on-a-fresh-connection path;
        // bisecting it would be pointless work.
        Db.FastImportSession.IsTimeout(new NpgsqlException(
            "Exception while reading from stream",
            new IOException("connection reset by peer"))).ShouldBeFalse();

        Db.FastImportSession.IsTimeout(new PostgresException(
            "terminating connection due to administrator command",
            "FATAL", "FATAL", "57P01")).ShouldBeFalse();
    }

    [Fact]
    public void IntegrityViolation_IsNotClassifiedAsTimeout()
    {
        Db.FastImportSession.IsTimeout(new PostgresException(
            "duplicate key value violates unique constraint",
            "ERROR", "ERROR", "23505")).ShouldBeFalse();
    }
}
