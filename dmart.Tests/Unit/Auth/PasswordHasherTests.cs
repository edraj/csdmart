using System.Diagnostics;
using Dmart.Auth;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

public class PasswordHasherTests
{
    // The default-parameter hasher. At m=19456 this costs ~40 ms on a laptop,
    // which is fine for the handful of round-trip cases below; everything that
    // only exercises LOGIC uses m=8192 so the suite does not pay for hardness
    // it is not asserting on.
    private readonly PasswordHasher _h = new();

    // A real hash written by dmart 1.5.x with the old hard-coded parameters,
    // generated once and pinned here. The only test allowed to spend 100 MiB —
    // it is the compatibility promise, and a synthesized string would prove
    // nothing about whether the hasher still derives the same bytes — which
    // is the entire question when the implementation underneath changes.
    private const string LegacyHash =
        "$argon2id$v=19$m=102400,t=3,p=8$XyxnlHQVrhuJqCKcmetlCw$fqibrf9io3jWbMIDWyuakQ0mJiCEmAXbI3Wc5CSFSaY";
    private const string LegacyPassword = "hunter22hunter";

    // Cheap parameters for the limiter and parsing tests. 8192 KiB is below
    // OWASP's floor on purpose — these assert queueing and bookkeeping, not
    // cryptographic hardness, and the validator is what enforces the floor on
    // real configuration.
    private const int CheapKb = 8192;

    private static PasswordHasher Cheap(int budgetMib, int timeoutSeconds = 30)
        => new(CheapKb, 1, 1, budgetMib, TimeSpan.FromSeconds(timeoutSeconds));

    // ---------------- format and round-trip ----------------

    [Fact]
    public void Hash_Then_Verify_Round_Trips()
    {
        var hash = _h.Hash("hunter22hunter");
        _h.Verify("hunter22hunter", hash).ShouldBeTrue();
    }

    [Fact]
    public void Verify_Wrong_Password_Returns_False()
    {
        var hash = _h.Hash("hunter22hunter");
        _h.Verify("wrong", hash).ShouldBeFalse();
    }

    [Fact]
    public void Two_Hashes_Of_Same_Password_Have_Different_Salts()
    {
        var a = _h.Hash("password");
        var b = _h.Hash("password");
        a.ShouldNotBe(b);
        _h.Verify("password", a).ShouldBeTrue();
        _h.Verify("password", b).ShouldBeTrue();
    }

    [Fact]
    public void Verify_Garbage_Hash_Returns_False()
    {
        _h.Verify("password", "this-is-not-a-valid-hash").ShouldBeFalse();
        _h.Verify("password", "").ShouldBeFalse();
    }

    [Fact]
    public void Defaults_Are_The_OWASP_Recommendation()
    {
        // Pinned as the PHC prefix rather than as the constants, because this is
        // what ends up in the database and what another implementation reads.
        _h.Hash("whatever").ShouldStartWith("$argon2id$v=19$m=19456,t=2,p=1$");
    }

    // ---------------- backward compatibility ----------------

    [Fact]
    public void Legacy_1_5_x_Hash_Still_Verifies()
    {
        // Verification reads m/t/p from the stored string, so the configured
        // parameters are irrelevant here — that is the whole promise.
        _h.Verify(LegacyPassword, LegacyHash).ShouldBeTrue();
        _h.Verify("wrong", LegacyHash).ShouldBeFalse();
    }

    [Fact]
    public void Legacy_Hash_Needs_Rehash_And_A_Current_One_Does_Not()
    {
        _h.NeedsRehash(LegacyHash).ShouldBeTrue();
        _h.NeedsRehash(_h.Hash("whatever")).ShouldBeFalse();
    }

    [Fact]
    public void NeedsRehash_Is_False_For_An_Unparseable_Hash()
    {
        // Rehashing on a parse failure would overwrite a hash that Verify could
        // not have matched either — losing the only evidence of what went wrong.
        _h.NeedsRehash("not-a-hash").ShouldBeFalse();
        _h.NeedsRehash("").ShouldBeFalse();
    }

    [Fact]
    public void NeedsRehash_Notices_A_Lowered_Cost_Too()
    {
        // An operator who REDUCES the cost wants that to take effect as well;
        // the upgrade path is not one-directional.
        var expensive = new PasswordHasher(65_536, 3, 1).Hash("x");
        _h.NeedsRehash(expensive).ShouldBeTrue();
    }

    // ---------------- memory budget ----------------

    [Fact]
    public async Task Limiter_Never_Admits_More_Than_The_Budget()
    {
        // 16 MiB of budget, 8 MiB per hash => at most two concurrent, ever.
        var h = Cheap(budgetMib: 16);
        var stored = h.Hash(LegacyPassword);
        // Task.Run, not a bare Select: the Argon2 computation runs
        // synchronously once budget is acquired, so without a real thread per
        // caller these serialize and the assertion below proves nothing.
        await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => h.VerifyAsync(LegacyPassword, stored))));

        h.PeakInFlightMib.ShouldBeLessThanOrEqualTo(16,
            "the sum of in-flight Argon2 memory must never exceed the budget");
    }

    [Fact]
    public async Task A_Hash_Larger_Than_The_Whole_Budget_Runs_Alone()
    {
        // Budget smaller than one hash. The permit count is clamped to the
        // budget so the work still runs — refusing it would make a legacy
        // 100 MiB hash unverifiable on any small box, which is worse than
        // running it by itself.
        var h = Cheap(budgetMib: 4);            // one hash wants 8 MiB
        (await h.VerifyAsync(LegacyPassword, h.Hash(LegacyPassword))).ShouldBeTrue();
        h.PeakInFlightMib.ShouldBe(4, "the oversized hash takes the entire budget");
    }

    [Fact]
    public async Task Concurrent_Hashes_Queue_Rather_Than_Fail()
    {
        var h = Cheap(budgetMib: 8);            // exactly one hash at a time
        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => h.HashAsync("password"))));

        results.Length.ShouldBe(4);
        results.ShouldAllBe(r => r.StartsWith("$argon2id$"));
        h.PeakInFlightMib.ShouldBe(8, "serialized by the budget, one at a time");
    }

    [Fact]
    public async Task Queue_Timeout_Surfaces_As_A_Capacity_Exception()
    {
        // Budget for one hash, a timeout far shorter than a hash takes, and
        // enough contenders that somebody has to wait.
        var h = new PasswordHasher(CheapKb, 3, 1, budgetMib: 8, queueTimeout: TimeSpan.FromMilliseconds(1));

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(
            async () =>
            {
                try { await h.HashAsync("password"); return (PasswordHashingCapacityException?)null; }
                catch (PasswordHashingCapacityException ex) { return ex; }
            })));

        var rejected = attempts.Where(a => a is not null).ToList();
        rejected.ShouldNotBeEmpty("a 1 ms wait for a busy budget must give up rather than queue forever");
        rejected[0]!.RetryAfterSeconds.ShouldBeGreaterThanOrEqualTo(1,
            "Retry-After must be a usable number of seconds, never 0");
    }

    [Fact]
    public async Task An_Unbudgeted_Hasher_Never_Rejects()
    {
        // The internal test/CLI constructor. Used by ~30 integration fixtures
        // that seed users directly; it must not acquire permits or time out.
        var h = new PasswordHasher(CheapKb, 1, 1);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => h.HashAsync("password"))));
        h.PeakInFlightMib.ShouldBe(0, "no budget means no bookkeeping");
    }

    [Fact]
    public async Task Verify_Budgets_The_Stored_Cost_Not_The_Configured_One()
    {
        // Configured cheap, stored expensive: the limiter must charge what the
        // hash will actually allocate, or several legacy verifies would be
        // admitted at the cheap price and blow the budget they are meant to
        // respect. Budget is generous so this asserts the CHARGE, not queueing.
        var h = new PasswordHasher(CheapKb, 1, 1, budgetMib: 128, queueTimeout: TimeSpan.FromSeconds(30));
        (await h.VerifyAsync(LegacyPassword, LegacyHash)).ShouldBeTrue();

        h.PeakInFlightMib.ShouldBe(100, "a m=102400 hash costs 100 MiB of budget regardless of configuration");
    }

    [Fact]
    public void Empty_Password_Returns_False_Instead_Of_Throwing()
    {
        // Regression from 1.5.7 and earlier, where the managed implementation
        // threw ArgumentException on a zero-length password and LoginAsync feeds
        // it `req.Password ?? string.Empty` — so an unauthenticated
        // POST /user/login carrying only a shortname produced HTTP 500.
        //
        // libargon2 would accept "" and hash it, so the crash is gone either
        // way; what this pins is the stronger property, that a blank password
        // never authenticates regardless of what is stored.
        var stored = _h.Hash("realpassword");
        _h.Verify("", stored).ShouldBeFalse();
        _h.Verify("", _h.DecoyHash).ShouldBeFalse();
        _h.Verify("", LegacyHash).ShouldBeFalse();
    }

    [Fact]
    public async Task Empty_Password_Still_Costs_A_Full_Hash()
    {
        // The crash fix must not become a timing oracle: a blank password has
        // to cost what a wrong one costs, or the decoy stops hiding anything.
        var h = new PasswordHasher(CheapKb, 1, 1, budgetMib: 64, queueTimeout: TimeSpan.FromSeconds(30));
        var stored = h.Hash("realpassword");

        var blank = Stopwatch.StartNew();
        (await h.VerifyAsync("", stored)).ShouldBeFalse();
        blank.Stop();

        var wrong = Stopwatch.StartNew();
        (await h.VerifyAsync("alsowrongbutpresent", stored)).ShouldBeFalse();
        wrong.Stop();

        // Generous bound — this asserts "same order of magnitude", not a
        // constant-time guarantee, which a wall clock in a test cannot show.
        blank.ElapsedMilliseconds.ShouldBeGreaterThan(wrong.ElapsedMilliseconds / 4,
            "a blank password must not short-circuit past the Argon2 computation");
    }

    // ---------------- decoy ----------------

    [Fact]
    public void Decoy_Uses_The_Configured_Parameters()
    {
        // A decoy pinned to old constants would reintroduce the timing oracle
        // the moment the parameters changed.
        var h = new PasswordHasher(CheapKb, 1, 1);
        h.DecoyHash.ShouldStartWith($"$argon2id$v=19$m={CheapKb},t=1,p=1$");
    }

    [Fact]
    public void Decoy_Is_Stable_And_Never_Matches()
    {
        var h = new PasswordHasher(CheapKb, 1, 1);
        h.DecoyHash.ShouldBe(h.DecoyHash, "built once and reused, not re-derived per call");
        h.Verify("", h.DecoyHash).ShouldBeFalse();
        h.Verify("password", h.DecoyHash).ShouldBeFalse();
    }

    [Fact]
    public void Decoy_Is_Not_Built_Until_It_Is_Asked_For()
    {
        // The 1.5.x version of this was a `static readonly` computed during type
        // initialization: 100 MiB allocated at startup on every boot, whether or
        // not a failed login ever arrived.
        var sw = Stopwatch.StartNew();
        var h = new PasswordHasher(65_536, 3, 1);
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeLessThan(50,
            "construction must not hash; the decoy is lazy");
    }
}
