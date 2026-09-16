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
public sealed class PasswordHashingCapacityException : Exception
{
    /// <summary>Seconds to advertise in the Retry-After response header.</summary>
    /// <remarks>
    /// Defaults to 1 on the standard constructors: a Retry-After of 0 invites an
    /// immediate retry into the same saturated budget, so the floor is a second
    /// even when nothing told us how long the wait was.
    /// </remarks>
    public int RetryAfterSeconds { get; } = 1;

    /// <summary>The constructor dmart actually uses.</summary>
    public PasswordHashingCapacityException(int retryAfterSeconds)
        : base($"password hashing is at capacity; retry after {retryAfterSeconds}s")
        => RetryAfterSeconds = retryAfterSeconds;

    // The three standard exception constructors (CA1032). Nothing in dmart calls
    // them — the budget limiter always knows its own timeout — but an exception
    // type that cannot be constructed the ordinary way is a nuisance to anyone
    // deserializing, wrapping or rethrowing it.
    public PasswordHashingCapacityException()
        : base("password hashing is at capacity") { }

    public PasswordHashingCapacityException(string message)
        : base(message) { }

    public PasswordHashingCapacityException(string message, Exception innerException)
        : base(message, innerException) { }
}
