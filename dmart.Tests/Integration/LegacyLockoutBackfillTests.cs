using System.Net;
using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The upgrade hazard the counter-only lockout creates, and the startup step
// that clears it.
//
// The old auto-lockout wrote BOTH attempt_count >= max AND is_active = false.
// The new one writes only the counter, and treats is_active = false as "an
// admin deactivated this" — which the cool-down deliberately refuses to undo.
// So on an upgraded database every account the previous release locked is now
// permanently locked out: the cool-down clears its counter, then RejectIfNotActive
// rejects it on the flag instead, forever, with no login-side path that recovers it.
public sealed class LegacyLockoutBackfillTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public LegacyLockoutBackfillTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task A_PreUpgrade_AutoLocked_Account_Is_Locked_Out_Until_The_Backfill_Runs()
    {
        var max = MaxAttempts();
        var (shortname, password) = await SeedAsync();
        try
        {
            // Exactly what the previous release left behind: both columns set,
            // and a last_failed_login far enough back that the cool-down has
            // long since elapsed.
            await SetStateAsync(shortname, isActive: false, attemptCount: max,
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-(CooldownSeconds() + 600)));

            // The cool-down clears the counter, and the login is then refused on
            // is_active instead. Every subsequent attempt does the same, and no
            // login-side path ever restores the flag: the account is stuck.
            (await LoginAsync(shortname, password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var stuck = (await GetAsync(shortname))!;
            stuck.AttemptCount.ShouldBe(0, "the cool-down did its half");
            stuck.IsActive.ShouldBeFalse("…and left the account locked on the other half");

            (await LoginAsync(shortname, password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized,
                "and it stays that way — there is no self-recovery from here");

            // And note the second-order effect, which is why the repair runs at
            // startup rather than being left to the operator: the cool-down has
            // just cleared this row's counter, so it no longer carries the
            // legacy signature and the repair can no longer recognise it. One
            // retry is enough to make an account unrepairable. The startup pass
            // completes before the host begins listening, so no request can
            // land in front of it — but an operator who sets
            // RepairLegacyLockoutsOnStart=false, serves traffic, and only then
            // runs `dmart migrate` will have burned exactly these rows.
        }
        finally { await DeleteAsync(shortname); }
    }

    [FactIfPg]
    public async Task The_Backfill_Reactivates_A_PreUpgrade_AutoLocked_Account()
    {
        var max = MaxAttempts();
        var (shortname, password) = await SeedAsync();
        try
        {
            await SetStateAsync(shortname, isActive: false, attemptCount: max,
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-(CooldownSeconds() + 600)));

            (await LegacyLockoutBackfill.RunAsync(Db(), max)).ShouldBeGreaterThanOrEqualTo(1);

            var healed = (await GetAsync(shortname))!;
            healed.IsActive.ShouldBeTrue();
            healed.AttemptCount.ShouldBe(0);
            healed.LastFailedLogin.ShouldBeNull();

            // And the user can log in again, which is the whole point.
            (await LoginAsync(shortname, password)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally { await DeleteAsync(shortname); }
    }

    [FactIfPg]
    public async Task The_Backfill_Leaves_An_Admin_Deactivated_Account_Deactivated()
    {
        // The signature is `is_active = false AND attempt_count >= max`, and an
        // ordinary admin deactivation cannot produce it: RejectIfNotActive runs
        // BEFORE the credential check in LoginAsync, so a deactivated account
        // never reaches the counter to raise it. A repair that reactivated on
        // is_active alone would silently undo every admin deactivation in the
        // database on upgrade.
        var (shortname, password) = await SeedAsync();
        try
        {
            await SetStateAsync(shortname, isActive: false, attemptCount: 2, lastFailed: null);

            await LegacyLockoutBackfill.RunAsync(Db(), MaxAttempts());

            var after = (await GetAsync(shortname))!;
            after.IsActive.ShouldBeFalse("an admin deactivation is not a legacy lockout");
            after.AttemptCount.ShouldBe(2);
            (await LoginAsync(shortname, password)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally { await DeleteAsync(shortname); }
    }

    [FactIfPg]
    public async Task The_Backfill_Leaves_An_Active_AttemptLocked_Account_Alone()
    {
        // A lock written by the CURRENT release: counter at the threshold,
        // is_active untouched. Nothing to repair, and clearing the counter here
        // would cancel a live lockout.
        var (shortname, _) = await SeedAsync();
        try
        {
            await SetStateAsync(shortname, isActive: true, attemptCount: MaxAttempts(),
                lastFailed: Dmart.Utils.TimeUtils.Now());

            await LegacyLockoutBackfill.RunAsync(Db(), MaxAttempts());

            var untouched = (await GetAsync(shortname))!;
            untouched.AttemptCount.ShouldBe(MaxAttempts());
            untouched.IsActive.ShouldBeTrue();
        }
        finally { await DeleteAsync(shortname); }
    }

    [FactIfPg]
    public async Task The_Backfill_Is_A_NoOp_When_The_Lockout_Is_Disabled()
    {
        // With MAX_FAILED_LOGIN_ATTEMPTS at 0 there is no threshold to compare
        // against — and, more to the point, no legacy lock to undo. Without the
        // guard the predicate would degrade to "every deactivated account".
        var (shortname, _) = await SeedAsync();
        try
        {
            await SetStateAsync(shortname, isActive: false, attemptCount: 99, lastFailed: null);

            (await LegacyLockoutBackfill.RunAsync(Db(), 0)).ShouldBe(0);
            var disabled = (await GetAsync(shortname))!;
            disabled.IsActive.ShouldBeFalse();
            disabled.AttemptCount.ShouldBe(99);
        }
        finally { await DeleteAsync(shortname); }
    }

    [FactIfPg]
    public async Task Booting_The_Host_Runs_The_Repair()
    {
        // The repair's real trigger. `dmart migrate` can run it too, but an
        // operator who upgrades and restarts without running migrate must not
        // be left with a database full of permanently locked-out accounts —
        // and, per the test above, a single login retry destroys the evidence
        // the repair matches on. Hence a hosted service, which runs before the
        // host starts listening, rather than a manual step.
        // DmartFactory pins RepairLegacyLockoutsOnStart off for every other
        // test (see the comment there); this one turns it back on.
        var (shortname, password) = await SeedAsync();
        try
        {
            await SetStateAsync(shortname, isActive: false, attemptCount: MaxAttempts(),
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-(CooldownSeconds() + 600)));

            // A fresh host against the same database. Touching .Services builds
            // and starts it, which is what runs the hosted services.
            var repaired = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Dmart:RepairLegacyLockoutsOnStart"] = "true",
                })));
            _ = repaired.Services;

            var healed = (await GetAsync(shortname))!;
            healed.IsActive.ShouldBeTrue("startup must heal a pre-upgrade lockout");
            healed.AttemptCount.ShouldBe(0);
            (await LoginAsync(shortname, password)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally { await DeleteAsync(shortname); }
    }

    [FactIfPg]
    public async Task Deactivating_A_Locked_Account_Clears_The_Counter()
    {
        // The invariant that makes a boot-time repair safe. If deactivation left
        // the counter standing, an admin disabling an account that is currently
        // being brute-forced would write exactly the legacy signature — and the
        // next restart would silently reactivate it. Clearing the counter on the
        // way down (as we already do on the way up) keeps that shape exclusive
        // to the release that no longer exists.
        var admin = await _factory.CreateLoggedInUserAsync();
        var (shortname, _) = await SeedAsync();
        try
        {
            await SetStateAsync(shortname, isActive: true, attemptCount: MaxAttempts(),
                lastFailed: Dmart.Utils.TimeUtils.Now());

            var resp = await admin.Client.PostAsJsonAsync("/managed/request", new Request
            {
                RequestType = RequestType.Update,
                SpaceName = "management",
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.User,
                        Subpath = "/users",
                        Shortname = shortname,
                        Attributes = new() { ["is_active"] = false },
                    },
                },
            }, DmartJsonContext.Default.Request);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var after = (await GetAsync(shortname))!;
            after.IsActive.ShouldBeFalse();
            after.AttemptCount.ShouldBe(0,
                "a deactivated account must not also carry the legacy lock signature");
        }
        finally
        {
            await DeleteAsync(shortname);
            await admin.Cleanup();
        }
    }

    // ==================== helpers ====================

    private IDbConnectionFactory Db() =>
        _factory.Services.GetRequiredService<IDbConnectionFactory>();

    private int MaxAttempts() => Settings().MaxFailedLoginAttempts;
    private int CooldownSeconds() => Settings().LockoutCooldownSeconds;

    private Dmart.Config.DmartSettings Settings() =>
        _factory.Services.GetRequiredService<IOptions<Dmart.Config.DmartSettings>>().Value;

    private Task<HttpResponseMessage> LoginAsync(string shortname, string password) =>
        _factory.CreateClient().PostAsJsonAsync("/user/login",
            new UserLoginRequest(shortname, null, null, password, null),
            DmartJsonContext.Default.UserLoginRequest);

    private Task<User?> GetAsync(string shortname) =>
        _factory.Services.GetRequiredService<UserRepository>().GetByShortnameAsync(shortname);

    private async Task<(string Shortname, string Password)> SeedAsync()
    {
        var shortname = $"legacylock_{Guid.NewGuid():N}"[..20];
        const string password = "Test1234aaa";
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = hasher.Hash(password),
            Type = UserType.Web,
            Language = Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return (shortname, password);
    }

    private async Task SetStateAsync(
        string shortname, bool isActive, int attemptCount, DateTime? lastFailed)
    {
        await using var conn = await Db().OpenAsync();
        await using var cmd = conn.Command("""
            UPDATE users
               SET is_active = $1, attempt_count = $2, last_failed_login = $3
             WHERE shortname = $4
            """);
        DbParams.Add(cmd, isActive);
        DbParams.Add(cmd, attemptCount);
        DbParams.Add(cmd, (object?)lastFailed ?? DBNull.Value);
        DbParams.Add(cmd, shortname);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DeleteAsync(string shortname)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAllSessionsAsync(shortname);
            await users.DeleteAsync(shortname);
        }
        catch { /* best effort */ }
    }
}
