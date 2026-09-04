using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The OTP code itself must become unusable after too many wrong guesses,
// independent of the per-IP rate limit — a distributed attacker could
// otherwise grind the 6-digit space within the code's TTL.
// VerifyAndConsumeAsync enforces a per-code attempt cap for every consumer
// (login, reset and profile verification all pass MaxOtpVerifyAttempts).
//
// The wire response is unchanged — an exhausted code returns the same
// OTP_INVALID as an expired one, preserving the anti-enumeration posture.
// This exercises the PostgreSQL provider (in-place `attempts + 1` UPDATE);
// the SQLite twin lives in SqliteOtpRepositoryTests.
public sealed class OtpVerifyAttemptCapTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OtpVerifyAttemptCapTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Code_Is_Invalidated_After_Cap_Wrong_Attempts()
    {
        var repo = _factory.Services.GetRequiredService<OtpRepository>();
        var ident = $"otptest-{Guid.NewGuid():N}";
        const string realCode = "123456";
        const int cap = 5;

        await repo.IssueAsync(ident, OtpPurpose.Login, realCode, DateTime.UtcNow.AddMinutes(5));

        // Exhaust the cap with wrong codes.
        for (var i = 0; i < cap; i++)
            (await repo.VerifyAndConsumeAsync(ident, OtpPurpose.Login, "000000", cap)).ShouldBeFalse();

        // The correct code must now be rejected — the row is dead in place.
        (await repo.VerifyAndConsumeAsync(ident, OtpPurpose.Login, realCode, cap)).ShouldBeFalse();
    }

    [FactIfPg]
    public async Task Correct_Code_Still_Works_Under_The_Cap()
    {
        var repo = _factory.Services.GetRequiredService<OtpRepository>();
        var ident = $"otptest-{Guid.NewGuid():N}";
        const string realCode = "654321";
        const int cap = 5;

        await repo.IssueAsync(ident, OtpPurpose.Login, realCode, DateTime.UtcNow.AddMinutes(5));

        // A few wrong attempts (below cap) must not lock out a legitimate user.
        for (var i = 0; i < cap - 1; i++)
            (await repo.VerifyAndConsumeAsync(ident, OtpPurpose.Login, "000000", cap)).ShouldBeFalse();

        (await repo.VerifyAndConsumeAsync(ident, OtpPurpose.Login, realCode, cap)).ShouldBeTrue();
    }
}
