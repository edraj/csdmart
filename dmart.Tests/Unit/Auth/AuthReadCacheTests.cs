using Dmart.Auth;
using Dmart.Config;
using Dmart.Models.Core;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Auth;

// Covers the ENABLED path of the opt-in auth micro-cache. The disabled default
// (AUTH_CACHE_TTL=0) is exercised implicitly by the rest of the auth suite —
// those tests pass unchanged precisely because a disabled cache is a no-op.
public class AuthReadCacheTests
{
    private static AuthReadCache NewCache(int ttlSeconds) =>
        new(Options.Create(new DmartSettings { AuthCacheTtl = ttlSeconds }));

    private static User NewUser(string shortname) => new()
    {
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "users",
        Uuid = "00000000-0000-0000-0000-000000000001",
        OwnerShortname = "dmart",
    };

    // ----- disabled (default) is a pure no-op -----

    [Fact]
    public void Disabled_TtlZero_NeverStores_And_NotEnabled()
    {
        var cache = NewCache(0);
        cache.Enabled.ShouldBeFalse();

        cache.SetUser("alice", NewUser("alice"));
        cache.TryGetUser("alice", out var user).ShouldBeFalse();
        user.ShouldBeNull();

        cache.SetSession("alice", "tok", valid: true);
        cache.TryGetSession("alice", "tok", out var valid).ShouldBeFalse();
        valid.ShouldBeFalse();
    }

    // ----- enabled: user slot round-trips, including a cached negative -----

    [Fact]
    public void Enabled_SetUser_Then_TryGetUser_ReturnsSameInstance()
    {
        var cache = NewCache(30);
        cache.Enabled.ShouldBeTrue();
        var stored = NewUser("alice");

        cache.SetUser("alice", stored);

        cache.TryGetUser("alice", out var got).ShouldBeTrue();
        got.ShouldBeSameAs(stored);
    }

    [Fact]
    public void Enabled_CachedNegativeLookup_IsAHit_WithNullUser()
    {
        // A "user not found" result is worth caching too — a hit with a null
        // payload, distinct from a miss (which returns false).
        var cache = NewCache(30);
        cache.SetUser("ghost", null);

        cache.TryGetUser("ghost", out var got).ShouldBeTrue();
        got.ShouldBeNull();
    }

    [Fact]
    public void Enabled_UnknownActor_IsAMiss()
    {
        var cache = NewCache(30);
        cache.TryGetUser("nobody", out _).ShouldBeFalse();
    }

    // ----- enabled: sessions are keyed by (shortname, token) -----

    [Fact]
    public void Enabled_Session_RoundTrips()
    {
        var cache = NewCache(30);
        cache.SetSession("alice", "raw-token", valid: true);

        cache.TryGetSession("alice", "raw-token", out var valid).ShouldBeTrue();
        valid.ShouldBeTrue();
    }

    [Fact]
    public void Enabled_Session_DifferentToken_IsAMiss()
    {
        // Same actor, different raw token → different key → miss (a rotated or
        // forged token must not ride another token's cached validity).
        var cache = NewCache(30);
        cache.SetSession("alice", "token-a", valid: true);

        cache.TryGetSession("alice", "token-b", out _).ShouldBeFalse();
    }

    // ----- eviction (the correctness backstop UserRepository relies on) -----

    [Fact]
    public void Evict_DropsUser_And_AllOfThatActorsSessions_Only()
    {
        var cache = NewCache(30);
        cache.SetUser("alice", NewUser("alice"));
        cache.SetSession("alice", "t1", valid: true);
        cache.SetSession("alice", "t2", valid: true);
        cache.SetUser("bob", NewUser("bob"));
        cache.SetSession("bob", "t3", valid: true);

        cache.Evict("alice");

        cache.TryGetUser("alice", out _).ShouldBeFalse();
        cache.TryGetSession("alice", "t1", out _).ShouldBeFalse();
        cache.TryGetSession("alice", "t2", out _).ShouldBeFalse();
        // bob is untouched.
        cache.TryGetUser("bob", out _).ShouldBeTrue();
        cache.TryGetSession("bob", "t3", out _).ShouldBeTrue();
    }

    [Fact]
    public void EvictAll_ClearsEverything()
    {
        var cache = NewCache(30);
        cache.SetUser("alice", NewUser("alice"));
        cache.SetSession("bob", "t1", valid: true);

        cache.EvictAll();

        cache.TryGetUser("alice", out _).ShouldBeFalse();
        cache.TryGetSession("bob", "t1", out _).ShouldBeFalse();
    }

    // ----- TTL expiry (the multi-replica staleness backstop) -----

    [Fact]
    public async Task Enabled_Entries_Expire_AfterTtl()
    {
        // Smallest positive TTL, so the wait stays short but real.
        var cache = NewCache(1);
        cache.SetUser("alice", NewUser("alice"));
        cache.SetSession("alice", "tok", valid: true);

        // Still fresh immediately after storing.
        cache.TryGetUser("alice", out _).ShouldBeTrue();
        cache.TryGetSession("alice", "tok", out _).ShouldBeTrue();

        await Task.Delay(1_200);

        // Past the TTL both lookups miss (the DB is re-consulted upstream).
        cache.TryGetUser("alice", out _).ShouldBeFalse();
        cache.TryGetSession("alice", "tok", out _).ShouldBeFalse();
    }
}
