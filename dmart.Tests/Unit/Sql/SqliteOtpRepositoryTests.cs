using Dmart.Auth;
using Dmart.Utils;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// The `otps` table is append-only: one row per issued code, doubling as
// request history. These assert the lifecycle invariants on the SQLite
// provider: at most one redeemable code per (identifier, purpose) — issuing
// supersedes the predecessor; consumption is a guarded update (replays fail);
// purpose scopes redemption; the attempts cap dead-ends a row in place; and
// the per-day counter sees every issued row regardless of state.
public sealed class SqliteOtpRepositoryTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"dmart-otp-{Guid.NewGuid():N}.db");
    private OtpRepository _repo = null!;
    private SqliteConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new SqliteConnectionFactory(
            Options.Create(new DmartSettings { SqlitePath = _dbPath }));
        await new SqliteSchemaInitializer(_factory, Options.Create(new DmartSettings { DatabaseDriver = "sqlite" }), NullLogger<SqliteSchemaInitializer>.Instance)
            .StartAsync(CancellationToken.None);
        _repo = new OtpRepository(_factory,
            new OtpHasher(new DmartSettings { JwtSecret = new string('k', 48) }));
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    private static DateTime Soon => TimeUtils.Now().AddMinutes(5);
    private const string Login = OtpPurpose.Login;
    private const string Verify = OtpPurpose.VerifyContact;

    [Fact]
    public async Task IssueThenVerify_ConsumesTheCode()
    {
        await _repo.IssueAsync("k1", Login, "123456", Soon);

        (await _repo.VerifyAndConsumeAsync("k1", Login, "999999", 0)).ShouldBeFalse();
        (await _repo.VerifyAndConsumeAsync("k1", Login, "123456", 0)).ShouldBeTrue();
        // Consumed — a replay must fail.
        (await _repo.VerifyAndConsumeAsync("k1", Login, "123456", 0)).ShouldBeFalse();
    }

    [Fact]
    public async Task Purpose_ScopesRedemption()
    {
        // A code issued for verify-contact must not redeem as a login code —
        // purpose is part of the OTP's identity, not a tag.
        await _repo.IssueAsync("k2", Verify, "123456", Soon);
        (await _repo.VerifyAndConsumeAsync("k2", Login, "123456", 0)).ShouldBeFalse();
        (await _repo.VerifyAndConsumeAsync("k2", Verify, "123456", 0)).ShouldBeTrue();
    }

    [Fact]
    public async Task Reissue_SupersedesThePredecessor()
    {
        // Only the code in the latest SMS/email is redeemable: a resend kills
        // the previous code rather than widening the guessing window to two.
        await _repo.IssueAsync("k3", Login, "111111", Soon);
        await _repo.IssueAsync("k3", Login, "222222", Soon);

        (await _repo.VerifyAndConsumeAsync("k3", Login, "111111", 0)).ShouldBeFalse();
        (await _repo.VerifyAndConsumeAsync("k3", Login, "222222", 0)).ShouldBeTrue();
    }

    [Fact]
    public async Task ExpiredCode_IsRejected()
    {
        await _repo.IssueAsync("k4", Login, "123456", TimeUtils.Now().AddSeconds(-1));
        (await _repo.VerifyAndConsumeAsync("k4", Login, "123456", 0)).ShouldBeFalse();
    }

    [Fact]
    public async Task AttemptsCap_DeadEndsTheCodeInPlace()
    {
        await _repo.IssueAsync("k5", Login, "123456", Soon);

        // Two wrong guesses under a cap of three: the counter is bumped in
        // place and the code survives.
        (await _repo.VerifyAndConsumeAsync("k5", Login, "000000", 3)).ShouldBeFalse();
        (await _repo.VerifyAndConsumeAsync("k5", Login, "000000", 3)).ShouldBeFalse();
        (await _repo.VerifyAndConsumeAsync("k5", Login, "123456", 3))
            .ShouldBeTrue("code must survive sub-cap failures");

        // Fresh code, cap exhausted: even the correct value is refused.
        await _repo.IssueAsync("k5b", Login, "123456", Soon);
        for (var i = 0; i < 3; i++)
            (await _repo.VerifyAndConsumeAsync("k5b", Login, "000000", 3)).ShouldBeFalse();
        (await _repo.VerifyAndConsumeAsync("k5b", Login, "123456", 3))
            .ShouldBeFalse("an exhausted code must not be redeemable even with the right value");
    }

    [Fact]
    public async Task UncappedAttempts_NeverSpendTheCode()
    {
        await _repo.IssueAsync("k6", Login, "123456", Soon);
        for (var i = 0; i < 5; i++)
            (await _repo.VerifyAndConsumeAsync("k6", Login, "000000", 0)).ShouldBeFalse();
        // maxAttempts = 0 disables the cap.
        (await _repo.VerifyAndConsumeAsync("k6", Login, "123456", 0)).ShouldBeTrue();
    }

    [Fact]
    public async Task GetCreatedSince_IsZeroImmediatelyAfterIssue_AndPurposeScoped()
    {
        (await _repo.GetCreatedSinceAsync("missing", Login)).ShouldBeNull();

        await _repo.IssueAsync("k7", Login, "123456", Soon);
        var since = await _repo.GetCreatedSinceAsync("k7", Login);
        since.ShouldNotBeNull();
        // Computed in C# rather than via julianday(), whose float day-count
        // round trip loses a second — a 60s gap measures as 59, which would let
        // a resend through early.
        since!.Value.ShouldBeInRange(0, 5);

        // The purpose-scoped variant answers "was a code minted for THIS
        // flow" — a login issue is invisible at the reset purpose.
        (await _repo.GetCreatedSinceAsync("k7", OtpPurpose.Reset)).ShouldBeNull();
    }

    [Fact]
    public async Task GetCreatedSinceBucket_SpansPurposesWithinABucket()
    {
        // Within a bucket the anchor is shared, so switching purpose is not a
        // cooldown bypass: a login issue must anchor the cooldown that a
        // register request then checks.
        (await _repo.GetCreatedSinceBucketAsync("k7b", Login)).ShouldBeNull();

        await _repo.IssueAsync("k7b", Login, "123456", Soon);

        var sinceLogin = await _repo.GetCreatedSinceBucketAsync("k7b", Login);
        sinceLogin.ShouldNotBeNull();
        sinceLogin!.Value.ShouldBeInRange(0, 5);

        var sinceRegister = await _repo.GetCreatedSinceBucketAsync("k7b", OtpPurpose.Register);
        sinceRegister.ShouldNotBeNull("register shares the sign-in bucket with login");
    }

    // The bucket boundary, which is the whole point. `register` needs no JWT
    // and no existing user, so a destination-wide cooldown let an anonymous
    // caller hold it open with one request a minute and silently swallow every
    // password reset the victim asked for. Reset anchors on reset alone.
    [Fact]
    public async Task GetCreatedSinceBucket_Isolates_Reset_From_SignIn()
    {
        await _repo.IssueAsync("k7c", Login, "123456", Soon);

        (await _repo.GetCreatedSinceBucketAsync("k7c", OtpPurpose.Reset))
            .ShouldBeNull("a login issue must not start the reset cooldown");

        await _repo.IssueAsync("k7c", OtpPurpose.Reset, "654321", Soon);

        (await _repo.GetCreatedSinceBucketAsync("k7c", OtpPurpose.Reset))
            .ShouldNotBeNull("a reset issue anchors the reset cooldown");
    }

    [Fact]
    public async Task CountIssuedSince_SpansPurposesAndStates()
    {
        var cutoff = TimeUtils.Now().AddHours(-24);
        (await _repo.CountIssuedSinceAsync("k8", cutoff)).ShouldBe(0);

        await _repo.IssueAsync("k8", Login, "111111", Soon);
        await _repo.IssueAsync("k8", Verify, "222222", Soon);
        // Consume one — history rows still count toward the daily cap.
        (await _repo.VerifyAndConsumeAsync("k8", Verify, "222222", 0)).ShouldBeTrue();

        (await _repo.CountIssuedSinceAsync("k8", cutoff)).ShouldBe(2);
        // Other identifiers are unaffected.
        (await _repo.CountIssuedSinceAsync("k8-other", cutoff)).ShouldBe(0);
    }

    [Fact]
    public async Task PurgeOlderThan_RemovesOnlyAgedRows()
    {
        await _repo.IssueAsync("k9", Login, "123456", Soon);
        // A cutoff in the past purges nothing…
        (await _repo.PurgeOlderThanAsync(TimeUtils.Now().AddDays(-7))).ShouldBe(0);
        // …a cutoff in the future takes the fresh row (superseded rows from
        // IssueAsync would go the same way).
        (await _repo.PurgeOlderThanAsync(TimeUtils.Now().AddMinutes(1))).ShouldBe(1);
        (await _repo.GetCreatedSinceAsync("k9", Login)).ShouldBeNull();
    }
}
