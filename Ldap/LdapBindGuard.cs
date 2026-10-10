using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Ldap;

// What stands between an LDAP client and password guessing, beyond the
// per-account lockout that binds share with /user/login.
//
// Per address: the HTTP login endpoints allow AuthRateLimitPerMinute requests a
// minute per client address (the "auth-by-ip" policy). Binds get the same
// number, counted differently: only FAILED binds use it up. An LDAP client
// binds on every connection, and pooled clients reconnect constantly, so
// counting successes would throttle ordinary use. Counting failures caps
// guessing just as well. Once an address has used its failures for the minute,
// further binds from it are answered `busy` before any password is checked.
// LdapTrustedPeers are exempt: Dovecot or Postfix bind for every user from one
// address, so a limit on that address would be a limit on everyone.
//
// Per account: a client that keeps presenting the SAME wrong password is a
// stale saved password (a phone after a password change), not a guesser. On
// the lockout counter it would count every retry, lock the account within
// MaxFailedLoginAttempts retries, and keep it locked for as long as the device
// keeps trying, because every attempt refreshes the cool-down. So a failed bind
// whose password matches one of the account's last failed passwords does not
// count again. A guesser gains nothing from this: a repeated wrong guess is
// still wrong. Only a keyed fingerprint is kept, in memory, under a key that
// dies with the process; never the password, and nothing that outlives a restart.
internal sealed class LdapBindGuard : IDisposable
{
    private const int RememberedPerAccount = 2;
    private const int MaxAccounts = 50_000;
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);

    private readonly IPNetwork[] _trusted;
    private readonly PartitionedRateLimiter<IPAddress> _failures;
    private readonly byte[] _fingerprintKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, Remembered> _recent = new(StringComparer.Ordinal);

    private sealed record Remembered(byte[][] Fingerprints, DateTime At);

    public LdapBindGuard(IOptions<DmartSettings> settings)
    {
        var s = settings.Value;
        _trusted = s.ParseLdapTrustedPeers(out _);
        var permit = Math.Max(1, s.AuthRateLimitPerMinute);
        _failures = PartitionedRateLimiter.Create<IPAddress, IPAddress>(ip =>
            RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    }

    public bool IsTrusted(IPAddress? peer)
    {
        if (peer is null) return false;
        peer = Canonical(peer);
        foreach (var net in _trusted)
            if (net.Contains(peer)) return true;
        return false;
    }

    // True when an untrusted address has no failed binds left this minute.
    public bool IsThrottled(IPAddress? peer)
        => peer is not null && !IsTrusted(peer)
           && _failures.GetStatistics(Canonical(peer)) is { CurrentAvailablePermits: <= 0 };

    public void RecordFailure(IPAddress? peer)
    {
        if (peer is null || IsTrusted(peer)) return;
        using var _ = _failures.AttemptAcquire(Canonical(peer));
    }

    // Whether `password` is one this account recently failed with.
    public bool IsRepeatedFailure(string shortname, string password)
    {
        if (!_recent.TryGetValue(shortname, out var r) || DateTime.UtcNow - r.At > ForgetAfter) return false;
        var fingerprint = Fingerprint(shortname, password);
        foreach (var f in r.Fingerprints)
            if (CryptographicOperations.FixedTimeEquals(f, fingerprint)) return true;
        return false;
    }

    public void RememberFailure(string shortname, string password)
    {
        if (_recent.Count >= MaxAccounts) Prune();
        var fingerprint = Fingerprint(shortname, password);
        _recent.AddOrUpdate(shortname,
            _ => new Remembered([fingerprint], DateTime.UtcNow),
            (_, r) => new Remembered(
                [fingerprint, .. r.Fingerprints.Where(f => !CryptographicOperations.FixedTimeEquals(f, fingerprint))
                    .Take(RememberedPerAccount - 1)],
                DateTime.UtcNow));
    }

    // A successful bind: whatever was failing before is no longer stale.
    public void Forget(string shortname) => _recent.TryRemove(shortname, out _);

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - ForgetAfter;
        foreach (var (name, r) in _recent)
            if (r.At < cutoff) _recent.TryRemove(name, out _);
        // Still full of fresh entries: something is cycling through accounts
        // faster than they expire. Dropping them costs only a few extra counts.
        if (_recent.Count >= MaxAccounts) _recent.Clear();
    }

    private byte[] Fingerprint(string shortname, string password)
        => HMACSHA256.HashData(_fingerprintKey, Encoding.UTF8.GetBytes(shortname + "\0" + password));

    // An IPv4 client on a dual-stack listener arrives as ::ffff:a.b.c.d.
    private static IPAddress Canonical(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    public void Dispose() => _failures.Dispose();
}
