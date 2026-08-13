using Dmart.Auth;
using Dmart.Utils;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Sql;

// The OTP row's value column is the one hstore in the schema. SQLite has no
// hstore, so the same key->string map is stored as a JSON object and updated
// with json_set instead of hstore concatenation.
//
// These assert the behaviours that divergence could break: TTL enforcement
// (which reads a key out of the map), and the attempts counter (which does an
// in-place partial update and must not clobber the other keys).
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

    [Fact]
    public async Task StoreThenVerify_ConsumesTheCode()
    {
        await _repo.StoreAsync("k1", "123456", Soon);

        (await _repo.VerifyAndConsumeAsync("k1", "999999")).ShouldBeFalse();
        (await _repo.VerifyAndConsumeAsync("k1", "123456")).ShouldBeTrue();
        // Consumed — a replay must fail.
        (await _repo.VerifyAndConsumeAsync("k1", "123456")).ShouldBeFalse();
    }

    [Fact]
    public async Task StoredValueIsHashed_NotThePlaintextCode()
    {
        await _repo.StoreAsync("k2", "123456", Soon);
        var stored = await _repo.PeekStoredHashAsync("k2");
        stored.ShouldNotBeNull();
        stored.ShouldNotBe("123456", "a DB read must not surface a replayable code");
        stored!.Length.ShouldBe(64, "keyed HMAC rendered as fixed-width hex");
    }

    [Fact]
    public async Task ExpiredCode_IsRejectedAndInvisible()
    {
        await _repo.StoreAsync("k3", "123456", TimeUtils.Now().AddSeconds(-1));
        // expires_at lives inside the map, so this exercises the JSON read path.
        (await _repo.PeekStoredHashAsync("k3")).ShouldBeNull();
        (await _repo.VerifyAndConsumeAsync("k3", "123456")).ShouldBeFalse();
    }

    [Fact]
    public async Task AttemptsCap_SpendsTheCode_AndPreservesOtherKeys()
    {
        await _repo.StoreAsync("k4", "123456", Soon);

        // Two wrong guesses under a cap of three: the counter is bumped in
        // place. If json_set clobbered the object instead of setting one key,
        // the code would be lost and the correct guess below would fail.
        (await _repo.VerifyAndConsumeAsync("k4", "000000", maxAttempts: 3)).ShouldBeFalse();
        (await _repo.VerifyAndConsumeAsync("k4", "000000", maxAttempts: 3)).ShouldBeFalse();
        (await _repo.PeekStoredHashAsync("k4")).ShouldNotBeNull("code must survive sub-cap failures");

        // The third failure reaches the cap and spends the code permanently.
        (await _repo.VerifyAndConsumeAsync("k4", "000000", maxAttempts: 3)).ShouldBeFalse();
        (await _repo.PeekStoredHashAsync("k4")).ShouldBeNull();
        (await _repo.VerifyAndConsumeAsync("k4", "123456", maxAttempts: 3))
            .ShouldBeFalse("an exhausted code must not be redeemable even with the right value");
    }

    [Fact]
    public async Task UncappedAttempts_NeverSpendTheCode()
    {
        await _repo.StoreAsync("k5", "123456", Soon);
        for (var i = 0; i < 5; i++)
            (await _repo.VerifyAndConsumeAsync("k5", "000000")).ShouldBeFalse();
        // maxAttempts = 0 preserves the original uncapped behaviour.
        (await _repo.VerifyAndConsumeAsync("k5", "123456")).ShouldBeTrue();
    }

    [Fact]
    public async Task GetCreatedSince_IsZeroImmediatelyAfterWrite()
    {
        (await _repo.GetCreatedSinceAsync("missing")).ShouldBeNull();

        await _repo.StoreAsync("k6", "123456", Soon);
        var since = await _repo.GetCreatedSinceAsync("k6");
        since.ShouldNotBeNull();
        // Computed in C# rather than via julianday(), whose float day-count
        // round trip loses a second — a 60s gap measures as 59, which would let
        // a resend through early.
        since!.Value.ShouldBeInRange(0, 5);
    }

    [Fact]
    public async Task Delete_RemovesTheRow()
    {
        await _repo.StoreAsync("k7", "123456", Soon);
        await _repo.DeleteAsync("k7");
        (await _repo.PeekStoredHashAsync("k7")).ShouldBeNull();
    }
}
