using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Dmart.Config;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Dmart.Auth;

// Argon2id password hashing, in the standard PHC string format:
//   $argon2id$v=19$m=<kib>,t=<iters>,p=<lanes>$<base64 salt>$<base64 hash>
//
// Salt and hash are base64 with padding stripped. `Verify` reads m/t/p from the
// stored string, so every hash dmart has ever written keeps verifying — including
// the m=102400,t=3,p=8 hashes written by 1.5.x and by dmart Python's argon2-cffi.
// Only hash CREATION uses the configured parameters.
//
// Two things make this class more than a wrapper around Konscious:
//
//   1. **The parameters are configuration, not constants.** 1.5.x hard-coded
//      m=102400 (100 MiB), which is a per-hash allocation. On a 512 MB board
//      that is most of the machine.
//
//   2. **Argon2 memory is budgeted.** Peak RSS here scales with request
//      CONCURRENCY, not with data size, and nothing else in dmart does that. A
//      per-IP rate limit does not help: it caps arrivals per minute, not how
//      many hashes are resident at once. The limiter below bounds the sum of
//      in-flight `m` instead, so hashes queue rather than the kernel choosing
//      which process dies.
//
// A hash larger than the whole budget is clamped to the budget and runs alone,
// which is what makes a legacy 100 MiB hash still verifiable on a budget of 64.
public sealed class PasswordHasher
{
    // OWASP's recommended Argon2id configuration. Small enough for a Pi Zero 2 W,
    // still well past the point where a GPU attack is cheap.
    public const int DefaultMemoryKb = 19_456;
    public const int DefaultIterations = 2;
    public const int DefaultParallelism = 1;

    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Argon2Version = 19;   // 0x13

    // Waiters allowed to queue for budget before new arrivals are rejected
    // outright. A queued waiter costs an awaited task, not a thread, so this is
    // about bounding how stale a served request can be rather than about
    // memory: past this depth the queue timeout would expire before the turn
    // arrives anyway, and failing fast is the more honest answer.
    private const int QueueLimit = 256;

    private readonly int _memoryKb;
    private readonly int _iterations;
    private readonly int _parallelism;

    // null when unbudgeted — the internal test/CLI constructor. Production
    // always goes through the DI constructor, which always builds one.
    private readonly ConcurrencyLimiter? _limiter;
    private readonly int _budgetMib;
    private readonly TimeSpan _queueTimeout;

    // Built on first use, not at construction: a 19 MiB hash at startup is
    // wasted work for a process that may never see a failed login, and the
    // 1.5.x version of this (a `static readonly` in UserService) paid 100 MiB
    // during type initialization on every boot.
    private readonly Lazy<string> _decoyHash;

    /// <summary>Test/CLI constructor: configured parameters, no memory budget.</summary>
    /// <remarks>
    /// `internal` on purpose. An unbudgeted hasher is exactly the thing this
    /// class exists to prevent, so production code must not be able to
    /// construct one — it resolves the DI singleton instead. The test project
    /// reaches this through InternalsVisibleTo (see GlobalUsings.cs).
    /// </remarks>
    internal PasswordHasher(
        int memoryKb = DefaultMemoryKb,
        int iterations = DefaultIterations,
        int parallelism = DefaultParallelism)
    {
        _memoryKb = memoryKb;
        _iterations = iterations;
        _parallelism = parallelism;
        _budgetMib = int.MaxValue;
        _queueTimeout = Timeout.InfiniteTimeSpan;
        _decoyHash = new Lazy<string>(BuildDecoyHash);
    }

    /// <summary>Test constructor for the limiter itself: explicit budget and timeout.</summary>
    internal PasswordHasher(int memoryKb, int iterations, int parallelism, int budgetMib, TimeSpan queueTimeout)
        : this(memoryKb, iterations, parallelism)
    {
        _budgetMib = Math.Max(1, budgetMib);
        _queueTimeout = queueTimeout;
        _limiter = BuildLimiter(_budgetMib);
    }

    public PasswordHasher(IOptions<DmartSettings> settings, ILogger<PasswordHasher> log)
    {
        var s = settings.Value;
        _memoryKb = s.PasswordHashMemoryKb;
        _iterations = s.PasswordHashIterations;
        _parallelism = s.PasswordHashParallelism;
        _budgetMib = ResolveBudgetMib(s.PasswordHashMemoryBudgetMb, _memoryKb);
        _queueTimeout = TimeSpan.FromSeconds(Math.Max(1, s.PasswordHashQueueTimeoutSeconds));
        _limiter = BuildLimiter(_budgetMib);
        _decoyHash = new Lazy<string>(BuildDecoyHash);

        log.LogInformation(
            "Dmart.Startup: password hashing m={MemoryKb}KiB t={Iterations} p={Parallelism}, "
            + "memory budget {BudgetMib}MiB ({BudgetSource}), queue timeout {TimeoutSeconds}s",
            _memoryKb, _iterations, _parallelism, _budgetMib,
            s.PasswordHashMemoryBudgetMb > 0 ? "configured" : "auto", (int)_queueTimeout.TotalSeconds);

        // p greater than the core count does not buy parallelism, it just
        // fragments the same work across lanes the scheduler has to interleave.
        // Not fatal — a config shared between a laptop and a Pi is a normal
        // thing to have — so warn rather than refuse to start.
        if (_parallelism > Environment.ProcessorCount)
        {
            log.LogWarning(
                "Dmart.Startup: PASSWORD_HASH_PARALLELISM={Parallelism} exceeds ProcessorCount={Cores}; "
                + "lanes beyond the core count add latency without adding cost to an attacker",
                _parallelism, Environment.ProcessorCount);
        }
    }

    /// <summary>The configured hash parameters, as they would appear in a PHC string.</summary>
    internal (int MemoryKb, int Iterations, int Parallelism) Parameters
        => (_memoryKb, _iterations, _parallelism);

    /// <summary>Budget in MiB the limiter is enforcing. Exposed for tests and startup logging.</summary>
    internal int BudgetMib => _budgetMib;

    /// <summary>
    /// A hash of a random password, used to equalize timing on login paths that
    /// have no real hash to check (no such user, account with no password).
    /// </summary>
    /// <remarks>
    /// Built with the CONFIGURED parameters, so the decoy always costs what a
    /// real verify costs — a decoy pinned to old constants would reintroduce
    /// the timing oracle the moment the parameters changed.
    ///
    /// One residual difference: until an account created by 1.5.x logs in once
    /// and gets rehashed, verifying it costs m=102400 while the decoy costs the
    /// configured m. That is measurable, and it closes as accounts migrate. It
    /// distinguishes "legacy account" from "no account", not "valid password"
    /// from "invalid", so it leaks the weaker of the two facts.
    /// </remarks>
    public string DecoyHash => _decoyHash.Value;

    // Built outside the budget: this runs at most once per process, on a path
    // that is already waiting, and taking permits here would let the first
    // failed login of a cold process contend with a real one.
    private string BuildDecoyHash()
        => HashWith(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    _memoryKb, _iterations, _parallelism);

    // ========================================================================
    // HASH / VERIFY
    // ========================================================================

    /// <summary>Hashes a password with the configured parameters, waiting for memory budget.</summary>
    /// <exception cref="PasswordHashingCapacityException">The budget stayed saturated past the queue timeout.</exception>
    public async Task<string> HashAsync(string password, CancellationToken ct = default)
    {
        using var lease = await AcquireAsync(_memoryKb, ct);
        return HashWith(password, _memoryKb, _iterations, _parallelism);
    }

    /// <summary>Verifies a password against a stored PHC hash, waiting for memory budget.</summary>
    /// <remarks>
    /// The budget requested is the STORED hash's m, not the configured one — a
    /// legacy hash really does allocate 100 MiB no matter what the current
    /// settings say, and charging it the configured cost would let several of
    /// them through at once.
    /// </remarks>
    /// <exception cref="PasswordHashingCapacityException">The budget stayed saturated past the queue timeout.</exception>
    public async Task<bool> VerifyAsync(string password, string encoded, CancellationToken ct = default)
    {
        if (!TryParse(encoded, out var m, out var t, out var p, out var salt, out var expected))
            return false;   // malformed hash: no work to budget for

        using var lease = await AcquireAsync(m, ct);
        if (password.Length == 0) { BurnEmptyPassword(salt, m, t, p, expected.Length); return false; }
        var actual = ComputeArgon2id(password, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    // Konscious throws ArgumentException("Argon2 needs a password set") on a
    // zero-length password. `LoginAsync` reaches this on every request that
    // omits the password field — `req.Password ?? string.Empty` — so before
    // this guard, an unauthenticated POST /user/login carrying only a shortname
    // for an unknown user produced an unhandled exception and HTTP 500.
    //
    // Returning early would fix the crash and open a timing oracle instead: a
    // blank password would answer in ~0 ms where a wrong one costs a full
    // Argon2, which is precisely the signal the decoy exists to suppress. So do
    // the whole computation against fixed filler and throw the answer away. The
    // result is unconditionally false, never a comparison — an account whose
    // password somehow equalled the filler must not be reachable with "".
    private const string EmptyPasswordFiller = "\0dmart\0empty\0password\0filler";

    private static void BurnEmptyPassword(byte[] salt, int m, int t, int p, int outputLength)
        => _ = ComputeArgon2id(EmptyPasswordFiller, salt, m, t, p, outputLength);

    /// <summary>Synchronous hash. Test and CLI paths only — see HashAsync.</summary>
    internal string Hash(string password) => HashWith(password, _memoryKb, _iterations, _parallelism);

    /// <summary>Synchronous verify. Test and CLI paths only — see VerifyAsync.</summary>
    // Reads m/t/p from the stored hash, so it genuinely touches no instance
    // state — but it is the instance-shaped counterpart of VerifyAsync and the
    // partner of the instance `Hash` above. Making it static would split the
    // pair across two call styles and rewrite every fixture that verifies a
    // seeded password, for no behavioural gain.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822",
        Justification = "Instance method for API symmetry with VerifyAsync/Hash; see comment above.")]
    internal bool Verify(string password, string encoded)
    {
        if (!TryParse(encoded, out var m, out var t, out var p, out var salt, out var expected))
            return false;
        if (password.Length == 0) { BurnEmptyPassword(salt, m, t, p, expected.Length); return false; }
        var actual = ComputeArgon2id(password, salt, m, t, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// True when <paramref name="encoded"/> was created with parameters other
    /// than the configured ones, and should be rehashed on next successful login.
    /// </summary>
    /// <remarks>
    /// Any difference counts, in either direction. An operator who LOWERS the
    /// cost wants the cheaper hash to take effect too, not only upgrades.
    /// A hash this cannot parse is left alone — rehashing on a parse failure
    /// would overwrite a hash that `Verify` also could not have matched.
    /// </remarks>
    public bool NeedsRehash(string encoded)
        => TryParse(encoded, out var m, out var t, out var p, out _, out _)
           && (m != _memoryKb || t != _iterations || p != _parallelism);

    // ========================================================================
    // BUDGET
    // ========================================================================

    private static ConcurrencyLimiter BuildLimiter(int budgetMib)
        => new(new ConcurrencyLimiterOptions
        {
            // One permit is one MiB of Argon2 working memory.
            PermitLimit = budgetMib,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = QueueLimit,
        });

    // Auto budget: half of what the runtime believes it may use. That figure
    // already accounts for DOTNET_GCHeapHardLimit and for a container memory
    // limit, which is the whole reason for reading it instead of total RAM.
    // Half leaves room for everything dmart does that is not hashing — the
    // request pipeline, Npgsql buffers, the GC's own headroom.
    //
    // Floor is one configured hash: a budget that cannot admit a single hash
    // would deadlock every login, and a misconfigured limit must not be able to
    // brick authentication.
    private static int ResolveBudgetMib(int configuredMb, int memoryKb)
    {
        var oneHashMib = (int)Math.Ceiling(memoryKb / 1024.0);
        if (configuredMb > 0) return Math.Max(oneHashMib, configuredMb);

        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var half = (int)Math.Min(int.MaxValue, available / 2 / (1024L * 1024L));
        return Math.Max(oneHashMib, half);
    }

    // A disposable no-op lease for the unbudgeted path, so callers can `using`
    // unconditionally instead of branching.
    private sealed class NoLease : IDisposable
    {
        internal static readonly NoLease Instance = new();
        public void Dispose() { }
    }

    // Permits held right now, and the high-water mark. Instrumentation only —
    // the limiter enforces the ceiling on its own. A test cannot observe
    // "did the budget hold" from the outside without this, because the whole
    // point is that over-budget work never starts.
    private int _inFlightMib;
    private int _peakInFlightMib;

    /// <summary>High-water mark, in MiB, of Argon2 memory admitted at once.</summary>
    internal int PeakInFlightMib => Volatile.Read(ref _peakInFlightMib);

    private sealed class TrackedLease(PasswordHasher owner, RateLimitLease lease, int permits) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.Add(ref owner._inFlightMib, -permits);
            lease.Dispose();
        }
    }

    private void RecordAdmitted(int permits)
    {
        var now = Interlocked.Add(ref _inFlightMib, permits);
        // Lock-free max: retry only while we still hold the larger value.
        int seen;
        while (now > (seen = Volatile.Read(ref _peakInFlightMib)))
        {
            if (Interlocked.CompareExchange(ref _peakInFlightMib, now, seen) == seen) break;
        }
    }

    private async Task<IDisposable> AcquireAsync(int memoryKb, CancellationToken ct)
    {
        if (_limiter is null) return NoLease.Instance;

        // Clamp: a hash whose m exceeds the entire budget still has to run, and
        // AcquireAsync would throw if asked for more permits than exist. It
        // takes the whole budget instead, so it runs alone — which is exactly
        // the containment we want for a 100 MiB legacy hash on a small box.
        var permits = Math.Clamp((int)Math.Ceiling(memoryKb / 1024.0), 1, _budgetMib);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_queueTimeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(_queueTimeout);

        RateLimitLease lease;
        try
        {
            lease = await _limiter.AcquireAsync(permits, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our timeout fired, not the caller's cancellation.
            throw new PasswordHashingCapacityException(RetryAfterSeconds());
        }

        if (!lease.IsAcquired)
        {
            // Queue was full — rejected without waiting.
            lease.Dispose();
            throw new PasswordHashingCapacityException(RetryAfterSeconds());
        }
        RecordAdmitted(permits);
        return new TrackedLease(this, lease, permits);
    }

    private int RetryAfterSeconds()
        => _queueTimeout == Timeout.InfiniteTimeSpan ? 1 : Math.Max(1, (int)_queueTimeout.TotalSeconds);

    // ========================================================================
    // PHC FORMAT
    // ========================================================================

    private static string HashWith(string password, int memoryKb, int iterations, int parallelism)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = ComputeArgon2id(password, salt, memoryKb, iterations, parallelism, HashSize);
        return $"$argon2id$v={Argon2Version}$m={memoryKb},t={iterations},p={parallelism}$" +
               $"{Base64NoPad(salt)}${Base64NoPad(hash)}";
    }

    // PHC string format: $argon2id$v=19$m=...,t=...,p=...$<salt>$<hash>
    // Split by '$' yields 6 parts (the leading '$' produces an empty first element).
    private static bool TryParse(
        string encoded, out int m, out int t, out int p, out byte[] salt, out byte[] expected)
    {
        m = t = p = 0;
        salt = expected = [];

        var parts = encoded.Split('$');
        if (parts.Length != 6) return false;
        if (parts[1] != "argon2id") return false;
        if (!parts[2].StartsWith("v=", StringComparison.Ordinal)) return false;

        foreach (var seg in parts[3].Split(','))
        {
            var kv = seg.Split('=');
            if (kv.Length != 2) continue;
            switch (kv[0])
            {
                case "m": int.TryParse(kv[1], out m); break;
                case "t": int.TryParse(kv[1], out t); break;
                case "p": int.TryParse(kv[1], out p); break;
            }
        }
        if (m <= 0 || t <= 0 || p <= 0) return false;

        try
        {
            salt = Base64FromNoPad(parts[4]);
            expected = Base64FromNoPad(parts[5]);
        }
        catch
        {
            return false;
        }
        return expected.Length > 0;
    }

    private static byte[] ComputeArgon2id(
        string password, byte[] salt, int memoryKb, int iterations, int parallelism, int outputLength)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            Iterations = iterations,
            MemorySize = memoryKb,
        };
        return argon2.GetBytes(outputLength);
    }

    private static string Base64NoPad(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] Base64FromNoPad(string s)
    {
        var pad = (4 - (s.Length % 4)) % 4;
        return Convert.FromBase64String(s + new string('=', pad));
    }
}
