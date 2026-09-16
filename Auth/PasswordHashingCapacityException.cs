namespace Dmart.Auth;

/// <summary>
/// Thrown when a password hash could not start because the Argon2 memory
/// budget was saturated for longer than the configured queue timeout, or the
/// wait queue itself was full.
/// </summary>
/// <remarks>
/// This is backpressure, not an error in the request. Argon2id is deliberately
/// memory-hard, so concurrent hashes are the one part of dmart whose peak RSS
/// scales with request concurrency rather than with data size — on a 512 MB
/// board three simultaneous logins were enough for the kernel to OOM-kill the
/// process. The budget limiter turns that into a queue; this exception is what
/// the far end of the queue looks like.
///
/// Callers on an HTTP path do not catch it: PasswordHashingCapacityMiddleware
/// turns it into a 503 with Retry-After. Callers with no request to fail
/// (AdminBootstrap, the `dmart passwd` CLI) let it propagate, which is correct
/// — those run alone and a saturated budget there means the budget is
/// misconfigured.
/// </remarks>
public sealed class PasswordHashingCapacityException(int retryAfterSeconds)
    : Exception($"password hashing is at capacity; retry after {retryAfterSeconds}s")
{
    /// <summary>Seconds to advertise in the Retry-After response header.</summary>
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
