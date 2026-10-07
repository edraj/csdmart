using System.Collections.Concurrent;
using Dmart.Config;
using Dmart.Models.Core;
using Microsoft.Extensions.Options;

namespace Dmart.DataAdapters.Sql;

// Owns the process-local in-memory cache of resolved (User, Permissions) tuples
// that PermissionService consults on every request. Centralizing the cache here
// means:
//
//   * UserRepository / AccessRepository call RefreshAsync (or Evict for a single
//     user) on every write that could affect access control, so the in-memory
//     cache is invalidated automatically without PermissionService having to
//     subscribe to anything.
//
//   * PermissionService can stay a thin singleton — it just calls
//     refresher.GetCachedUserAccess / SetCachedUserAccess.
//
// Entries also carry a TTL (DmartSettings.AuthzCacheTtl). Local writes evict
// immediately, so a single node is always fresh; the TTL is what bounds
// staleness on OTHER replicas, which never see this process's evictions —
// without it a role removed on one replica kept granting on the others until
// they restarted.
//
// The parameterless construction (`new AuthzCacheRefresher()`) is kept for the
// CLI paths that build repositories by hand; they get the default TTL.
//
// Tests that need a clean slate call InvalidateAllInMemory() (or write any
// user/role/permission, which triggers the same path).
public sealed class AuthzCacheRefresher(IOptions<DmartSettings>? settings = null)
{
    // The resolved access bundle for one user. Holds the User row (so callers can
    // check IsActive, Groups, etc.) plus the flattened list of Permission rows
    // reachable via user.Roles → role.Permissions → permission rows.
    public sealed record CachedUserAccess(User? User, List<Permission> Permissions);

    private readonly record struct Slot(CachedUserAccess Value, long ExpiresAtTicks);

    private const int DefaultTtlSeconds = 60;

    // Process-local cache. Singleton lifetime ensures every request sees the same
    // dictionary. ConcurrentDictionary is lock-free for reads on the hot path.
    private readonly ConcurrentDictionary<string, Slot> _userAccess = new();

    private long TtlTicks
    {
        get
        {
            var seconds = settings?.Value.AuthzCacheTtl ?? DefaultTtlSeconds;
            return seconds > 0 ? TimeSpan.FromSeconds(seconds).Ticks : 0;
        }
    }

    public CachedUserAccess? GetCachedUserAccess(string shortname)
    {
        if (!_userAccess.TryGetValue(shortname, out var slot)) return null;
        if (slot.ExpiresAtTicks != 0 && DateTime.UtcNow.Ticks >= slot.ExpiresAtTicks)
        {
            // Expired: drop it so the next resolver call re-reads the database.
            _userAccess.TryRemove(shortname, out _);
            return null;
        }
        return slot.Value;
    }

    public void SetCachedUserAccess(string shortname, CachedUserAccess value)
    {
        var ttl = TtlTicks;
        _userAccess[shortname] = new Slot(value, ttl > 0 ? DateTime.UtcNow.Ticks + ttl : 0);
    }

    // Drops one user's bundle. Use this for writes that can only have changed
    // THAT user's access (its own row: roles, groups, activation) — a global
    // clear there sent every active actor back to the database at once.
    public void Evict(string shortname) => _userAccess.TryRemove(shortname, out _);

    // Clears the in-memory user-access cache. Called from RefreshAsync and from
    // AccessRepository.InvalidateAllCachesAsync. Still the right call for role,
    // group and permission writes, which can touch any number of users.
    public void InvalidateAllInMemory() => _userAccess.Clear();

    public Task RefreshAsync(CancellationToken ct = default)
    {
        InvalidateAllInMemory();
        return Task.CompletedTask;
    }
}
