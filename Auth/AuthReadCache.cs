using System.Collections.Concurrent;
using Dmart.Config;
using Dmart.Models.Core;
using Microsoft.Extensions.Options;

namespace Dmart.Auth;

// Opt-in micro-cache for the two DB lookups every authenticated request pays
// in OnTokenValidated: the user row (GetByShortnameAsync) and the session
// validity probe (IsSessionValidAsync). At high request rates these two
// statements dominate the auth path's DB traffic — they run before any
// handler, on every call, for the same handful of actors.
//
// AUTH_CACHE_TTL=0 (the default) disables the cache entirely: behavior is
// byte-identical to before this class existed. A positive TTL (seconds)
// trades that for bounded staleness: a revoked session or deactivated user
// keeps working for at most TTL seconds on this node. UserRepository evicts
// on its own user/session mutations, so single-node deployments see changes
// immediately for the common flows (logout, password change, deactivation);
// the TTL is the backstop for multi-replica deployments and any mutation
// path that doesn't evict.
//
// Only the SESSION_INACTIVITY_TTL=0 path consults the session cache —
// TouchSessionAsync writes a timestamp per request and must keep hitting the
// database to preserve inactivity-expiry semantics.
public sealed class AuthReadCache(IOptions<DmartSettings> settings)
{
    private readonly record struct UserSlot(User? User, long ExpiresAtTicks);
    private readonly record struct SessionSlot(bool Valid, long ExpiresAtTicks);

    private readonly ConcurrentDictionary<string, UserSlot> _users = new(StringComparer.Ordinal);
    // Keyed by (shortname, raw token). The raw token string is already in
    // memory for the request; holding it as a key adds no exposure beyond
    // what the sessions table's hashed row already implies.
    private readonly ConcurrentDictionary<(string Shortname, string Token), SessionSlot> _sessions = new();

    // Prune threshold: opportunistic cleanup keeps the dictionaries bounded
    // even when clients rotate tokens rapidly (OAuth refresh timers).
    private const int PruneAt = 10_000;

    public bool Enabled => settings.Value.AuthCacheTtl > 0;

    private long TtlTicks => TimeSpan.FromSeconds(settings.Value.AuthCacheTtl).Ticks;

    public bool TryGetUser(string shortname, out User? user)
    {
        user = null;
        if (!Enabled) return false;
        if (!_users.TryGetValue(shortname, out var slot)) return false;
        if (slot.ExpiresAtTicks < DateTime.UtcNow.Ticks) return false;
        user = slot.User;
        return true;
    }

    public void SetUser(string shortname, User? user)
    {
        if (!Enabled) return;
        if (_users.Count > PruneAt) Prune(_users);
        _users[shortname] = new UserSlot(user, DateTime.UtcNow.Ticks + TtlTicks);
    }

    public bool TryGetSession(string shortname, string token, out bool valid)
    {
        valid = false;
        if (!Enabled) return false;
        if (!_sessions.TryGetValue((shortname, token), out var slot)) return false;
        if (slot.ExpiresAtTicks < DateTime.UtcNow.Ticks) return false;
        valid = slot.Valid;
        return true;
    }

    public void SetSession(string shortname, string token, bool valid)
    {
        if (!Enabled) return;
        if (_sessions.Count > PruneAt) PruneSessions();
        _sessions[(shortname, token)] = new SessionSlot(valid, DateTime.UtcNow.Ticks + TtlTicks);
    }

    // Full flush — used by bulk user writes where enumerating affected actors
    // isn't worth it.
    public void EvictAll()
    {
        _users.Clear();
        _sessions.Clear();
    }

    // Drops everything cached for one actor — called by UserRepository after
    // any user-row or session mutation for that shortname.
    public void Evict(string shortname)
    {
        _users.TryRemove(shortname, out _);
        foreach (var key in _sessions.Keys)
            if (key.Shortname == shortname)
                _sessions.TryRemove(key, out _);
    }

    private static void Prune(ConcurrentDictionary<string, UserSlot> map)
    {
        var now = DateTime.UtcNow.Ticks;
        foreach (var kv in map)
            if (kv.Value.ExpiresAtTicks < now)
                map.TryRemove(kv.Key, out _);
    }

    private void PruneSessions()
    {
        var now = DateTime.UtcNow.Ticks;
        foreach (var kv in _sessions)
            if (kv.Value.ExpiresAtTicks < now)
                _sessions.TryRemove(kv.Key, out _);
    }
}
