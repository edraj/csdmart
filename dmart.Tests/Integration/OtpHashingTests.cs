using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Utils;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// OTP codes are stored hashed (keyed HMAC via OtpHasher), never as the raw
// 6-digit code. A DB read must not surface a live, replayable credential. These
// tests pin the at-rest representation AND that verification still round-trips
// over the append-only `otps` table (consumed rows stay as history — the
// tombstone is consumed_at, not a delete).
public sealed class OtpHashingTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OtpHashingTests(DmartFactory factory) => _factory = factory;

    private OtpRepository Repo() => _factory.Services.GetRequiredService<OtpRepository>();
    private OtpHasher Hasher() => _factory.Services.GetRequiredService<OtpHasher>();

    private sealed record RawRow(string CodeHash, bool Consumed, string? Status);

    // Reads the latest row straight from the table, bypassing the repository —
    // the code_hash is what an attacker with DB read access would see.
    private async Task<RawRow?> RawLatestRowAsync(string identifier, string purpose)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command(
            "SELECT code_hash, consumed_at, status FROM otps " +
            "WHERE identifier = $1 AND purpose = $2 ORDER BY created_at DESC LIMIT 1");
        DbParams.Add(cmd, identifier);
        DbParams.Add(cmd, purpose);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new RawRow(
            reader.GetString(0),
            reader.GetValue(1) is not (null or DBNull),
            reader.GetValue(2) is string s ? s : null);
    }

    // Expiries are seeded with TimeUtils.Now(), never DateTime.UtcNow, and that
    // is load-bearing rather than stylistic. dmart stores timestamps LOCAL-NAIVE
    // in `timestamp without time zone` columns ("no UTC anywhere" — see the
    // TIMESTAMPTZ→TIMESTAMP migration in SqlSchema.cs), and
    // VerifyAndConsumeAsync compares expires_at against TimeUtils.Now(). A UTC
    // expiry is therefore off by the host's offset: on a host ahead of UTC the
    // code is born already expired and every verification returns false.
    //
    // PostgreSQL used to hide that. Npgsql infers `timestamptz` from a
    // Kind=Utc DateTime, and the server then coerces it into the TIMESTAMP
    // column through the session TimeZone — silently converting UTC back to
    // local and repairing the mistake. SQLite writes the wall-clock verbatim,
    // so it does not. CI runners are UTC, where the two are equal, which is why
    // this only ever appeared for a developer in a non-UTC zone.
    [FactIfPg]
    public async Task IssueAsync_Persists_Hashed_Code_Not_Plaintext()
    {
        var ident = $"otphash_{Guid.NewGuid():N}@x.yz";
        const string code = "123456";
        await Repo().IssueAsync(ident, OtpPurpose.VerifyContact, code, TimeUtils.Now().AddMinutes(5));

        var row = await RawLatestRowAsync(ident, OtpPurpose.VerifyContact);
        row.ShouldNotBeNull();
        row!.CodeHash.ShouldNotBe(code, "the raw 6-digit code must never be persisted");
        row.CodeHash.ShouldBe(Hasher().Hash(code), "the stored value is the keyed HMAC of the code");
    }

    [FactIfPg]
    public async Task VerifyAndConsume_Succeeds_With_Correct_Code_And_Tombstones_Row()
    {
        var ident = $"otphash_{Guid.NewGuid():N}@x.yz";
        const string code = "654321";
        await Repo().IssueAsync(ident, OtpPurpose.VerifyContact, code, TimeUtils.Now().AddMinutes(5));

        (await Repo().VerifyAndConsumeAsync(ident, OtpPurpose.VerifyContact, code, 0)).ShouldBeTrue();

        // The row survives as history but is dead: consumed_at set, status
        // recording WHY it died, and a replay refused.
        var row = await RawLatestRowAsync(ident, OtpPurpose.VerifyContact);
        row.ShouldNotBeNull("a consumed OTP row stays as history");
        row!.Consumed.ShouldBeTrue();
        row.Status.ShouldBe("consumed");
        (await Repo().VerifyAndConsumeAsync(ident, OtpPurpose.VerifyContact, code, 0)).ShouldBeFalse();
    }

    [FactIfPg]
    public async Task VerifyAndConsume_Fails_With_Wrong_Code()
    {
        var ident = $"otphash_{Guid.NewGuid():N}@x.yz";
        await Repo().IssueAsync(ident, OtpPurpose.VerifyContact, "111111", TimeUtils.Now().AddMinutes(5));
        (await Repo().VerifyAndConsumeAsync(ident, OtpPurpose.VerifyContact, "222222", 0)).ShouldBeFalse();
    }

    [FactIfPg]
    public async Task Reissue_Supersedes_The_Predecessor_Row()
    {
        var ident = $"otphash_{Guid.NewGuid():N}@x.yz";
        await Repo().IssueAsync(ident, OtpPurpose.VerifyContact, "111111", TimeUtils.Now().AddMinutes(5));
        await Repo().IssueAsync(ident, OtpPurpose.VerifyContact, "333333", TimeUtils.Now().AddMinutes(5));

        // Only the newest code redeems; the older row is tombstoned as
        // superseded rather than deleted.
        (await Repo().VerifyAndConsumeAsync(ident, OtpPurpose.VerifyContact, "111111", 0)).ShouldBeFalse();
        (await Repo().VerifyAndConsumeAsync(ident, OtpPurpose.VerifyContact, "333333", 0)).ShouldBeTrue();
    }

    [FactIfPg]
    public async Task VerifyAndConsume_Returns_False_For_Missing_Identifier()
    {
        (await Repo().VerifyAndConsumeAsync(
            $"otphash_missing_{Guid.NewGuid():N}", OtpPurpose.VerifyContact, "123456", 0))
            .ShouldBeFalse();
    }
}
