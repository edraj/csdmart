using System.Net;
using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// /oauth/token is the second door into an account, and both its grants have to
// answer the same question /user/login answers: is this account usable right
// now? The refresh grant asks; the authorization_code grant did not ask at all;
// and the shared predicate had two holes of its own — it skipped the
// deactivated/deleted check on one branch, and it WROTE to the database, which
// turned "may I refresh?" into a way to clear a brute-force lockout.
public sealed class LockoutTokenGateTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public LockoutTokenGateTests(DmartFactory factory) => _factory = factory;

    // ---- the predicate must check usability FIRST ----

    [FactIfPg]
    public async Task Refresh_Is_Refused_To_A_Deactivated_Account_Past_The_Cooldown()
    {
        // The exact hole: attempt-locked AND deactivated AND the cool-down has
        // elapsed. The counter branch fired first, saw an expired window, and
        // returned "not locked" — never reaching the IsUsable check below it.
        // Deactivating a compromised account therefore did not stop its refresh
        // tokens from minting fresh access tokens.
        var (shortname, _) = await SeedAsync();
        try
        {
            await SetStateAsync(shortname, isActive: false, isDeleted: false,
                attemptCount: MaxAttempts(),
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-(CooldownSeconds() + 60)));

            var resp = await RefreshAsync(shortname);
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
                "a deactivated account is locked whatever the cool-down says");
            (await resp.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
        }
        finally { await DeleteAsync(shortname); }
    }

    [FactIfPg]
    public async Task Refresh_Is_Refused_To_A_SoftDeleted_Account_Past_The_Cooldown()
    {
        var (shortname, _) = await SeedAsync();
        try
        {
            await SetStateAsync(shortname, isActive: true, isDeleted: true,
                attemptCount: MaxAttempts(),
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-(CooldownSeconds() + 60)));

            var resp = await RefreshAsync(shortname);
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest,
                "a soft-deleted account can never mint a new token");
        }
        finally { await DeleteAsync(shortname); }
    }

    // ---- the predicate must not write ----

    [FactIfPg]
    public async Task Refreshing_Past_The_Cooldown_Does_Not_Clear_The_Attempt_Counter()
    {
        // The gate used to call UnlockAfterCooldownAsync from inside the
        // predicate. So an attacker holding a refresh token could brute-force
        // the password to the threshold, wait out LOCKOUT_COOLDOWN_SECONDS, and
        // spend one refresh to wipe attempt_count — the lock itself, and the
        // only surviving evidence of the run — without ever presenting a
        // credential. The cool-down still releases the lock (the refresh
        // succeeds); it just isn't this call's job to persist that.
        var (shortname, _) = await SeedAsync();
        try
        {
            await SetStateAsync(shortname, isActive: true, isDeleted: false,
                attemptCount: MaxAttempts(),
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-(CooldownSeconds() + 60)));

            var resp = await RefreshAsync(shortname);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK, "the cool-down has elapsed");

            var after = await GetAsync(shortname);
            after!.AttemptCount.ShouldBe(MaxAttempts(),
                "presenting a refresh token is not a login and must not clear the counter");
            after.LastFailedLogin.ShouldNotBeNull(
                "nor drop the cool-down anchor");
        }
        finally { await DeleteAsync(shortname); }
    }

    [FactIfPg]
    public async Task An_Otp_Request_Does_Not_Clear_The_Attempt_Counter()
    {
        // Same predicate, same rule, through the other caller. /user/otp-request
        // answers a silent 200 either way, so the observable is the counter.
        var msisdn = $"9647{Random.Shared.Next(100_000_000, 999_999_999)}";
        var (shortname, _) = await SeedAsync(msisdn);
        try
        {
            await SetStateAsync(shortname, isActive: true, isDeleted: false,
                attemptCount: MaxAttempts(),
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-(CooldownSeconds() + 60)));

            var resp = await _factory.CreateClient().PostAsJsonAsync("/user/otp-request",
                new Dmart.Models.Api.SendOTPRequest(
                    Msisdn: msisdn, Email: null, Purpose: Dmart.Models.Api.OtpPurpose.Login),
                Dmart.Models.Json.DmartJsonContext.Default.SendOTPRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await GetAsync(shortname))!.AttemptCount.ShouldBe(MaxAttempts(),
                "asking for a code is not a login and must not clear the counter");
        }
        finally { await DeleteAsync(shortname); }
    }

    // ---- the authorization_code grant needs the same gate ----

    [FactIfPg]
    public async Task Authorization_Code_Is_Refused_To_An_AttemptLocked_Account()
    {
        // A code is minted at /oauth/authorize and stays redeemable for its
        // whole TTL. This grant had no lock, IsUsable, or IsDeleted check of any
        // kind — only a null check on the user row — so an account locked in
        // that window still exchanged its code for an access token AND a live
        // sessions row, while the refresh grant fifty lines below refused.
        var (shortname, code) = await IssueCodeAsync();
        try
        {
            await SetStateAsync(shortname, isActive: true, isDeleted: false,
                attemptCount: MaxAttempts(), lastFailed: Dmart.Utils.TimeUtils.Now());

            var resp = await ExchangeAsync(code);
            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await resp.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");

            var users = _factory.Services.GetRequiredService<UserRepository>();
            (await users.CountSessionsAsync(shortname)).ShouldBe(0,
                "a refused exchange must not leave a session row behind");
        }
        finally { await DeleteAsync(shortname); }
    }

    [FactIfPg]
    public async Task Authorization_Code_Is_Refused_To_A_Deactivated_Account()
    {
        var (shortname, code) = await IssueCodeAsync();
        try
        {
            await SetStateAsync(shortname, isActive: false, isDeleted: false,
                attemptCount: 0, lastFailed: null);

            (await ExchangeAsync(code)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally { await DeleteAsync(shortname); }
    }

    [FactIfPg]
    public async Task Authorization_Code_Still_Works_For_A_Healthy_Account()
    {
        // The gate must not break the flow it guards.
        var (shortname, code) = await IssueCodeAsync();
        try
        {
            (await ExchangeAsync(code)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally { await DeleteAsync(shortname); }
    }

    // ==================== helpers ====================

    private const string ClientId = "lockgate-test-client";
    private const string RedirectUri = "http://localhost/lockgate/callback";

    private int MaxAttempts() => Settings().MaxFailedLoginAttempts;
    private int CooldownSeconds() => Settings().LockoutCooldownSeconds;

    private Dmart.Config.DmartSettings Settings() =>
        _factory.Services.GetRequiredService<IOptions<Dmart.Config.DmartSettings>>().Value;

    private async Task<(string Shortname, string Password)> SeedAsync(string? msisdn = null)
    {
        var shortname = $"lockgate_{Guid.NewGuid():N}"[..18];
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
            Msisdn = msisdn,
            IsMsisdnVerified = msisdn is not null,
            Type = UserType.Web,
            Language = Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return (shortname, password);
    }

    // Mints an authorization code straight from the store the token endpoint
    // consumes. The /oauth/authorize round-trip (client registration, PKCE) is
    // covered by McpOAuthAndSseTests; what these tests need is a code issued
    // while the account was healthy, so the state can change underneath it.
    // No code_challenge → Consume skips PKCE, which keeps the setup to one call.
    private async Task<(string Shortname, string Code)> IssueCodeAsync()
    {
        var (shortname, _) = await SeedAsync();
        var codes = _factory.Services.GetRequiredService<OAuthCodeStore>();
        return (shortname, codes.Issue(shortname, ClientId, RedirectUri, null, null, "mcp"));
    }

    private Task<HttpResponseMessage> ExchangeAsync(string code) =>
        _factory.CreateClient().PostAsync("/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = ClientId,
                ["redirect_uri"] = RedirectUri,
            }));

    private Task<HttpResponseMessage> RefreshAsync(string shortname)
    {
        var jwt = _factory.Services.GetRequiredService<JwtIssuer>();
        return _factory.CreateClient().PostAsync("/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = jwt.IssueRefresh(shortname, UserType.Web),
            }));
    }

    private Task<User?> GetAsync(string shortname) =>
        _factory.Services.GetRequiredService<UserRepository>().GetByShortnameAsync(shortname);

    // Seeds the four columns the gate reads, in one statement. is_deleted is
    // written directly rather than through SoftDeleteAsync because the point is
    // the column the predicate reads, not the deletion workflow.
    private async Task SetStateAsync(
        string shortname, bool isActive, bool isDeleted, int attemptCount, DateTime? lastFailed)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command("""
            UPDATE users
               SET is_active = $1, is_deleted = $2, attempt_count = $3, last_failed_login = $4
             WHERE shortname = $5
            """);
        DbParams.Add(cmd, isActive);
        DbParams.Add(cmd, isDeleted);
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
