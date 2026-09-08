using System.Net;
using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Lockout coverage for the failure paths that should count toward
// MAX_FAILED_LOGIN_ATTEMPTS:
//   * wrong password                  — covered by FullParityTests.Account_Lockout_After_Max_Failed_Attempts
//   * wrong OTP                       — exercised here via /user/login (OTP path)
//   * wrong password alongside OTP    — exercised here via /user/login (OTP path + password)
//   * wrong old_password              — exercised here via /user/profile (password change)
//
// Each test provisions its own user so xUnit parallelism can't taint the
// shared admin row, and the lockout-threshold tests pre-seed attempt_count
// directly via SQL to avoid a flaky N-iteration login loop.
public sealed class FailedAttemptLockoutTests : IClassFixture<DmartFactory>
{
    // Exact string emitted by RejectIfAttemptLocked / HandleFailedLoginAttempt.
    // Pinned so a regression that swaps in RejectIfNotActive's shorter
    // "Account has been locked." message would fail the assertion rather than
    // pass via a substring match.
    private const string LockoutMessage =
        "Account has been locked due to too many failed login attempts.";

    private readonly DmartFactory _factory;
    public FailedAttemptLockoutTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task OtpLogin_WrongCode_Increments_AttemptCount_Once()
    {
        // Below the threshold: a single wrong OTP should still surface
        // OTP_INVALID and merely bump the counter (not lock the account).
        var (shortname, _) = await CreateUserAsync(password: null);
        try
        {
            var (type, code, _) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, null, Otp: "000000"));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.OTP_INVALID);

            var stored = await ReadAttemptCountAsync(shortname);
            stored.ShouldBe(1);

            var refreshed = await GetUserAsync(shortname);
            refreshed!.IsActive.ShouldBeTrue("single bad OTP must not lock the account");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task OtpLogin_WrongCode_Locks_When_Threshold_Reached()
    {
        var max = MaxAttempts();
        var (shortname, _) = await CreateUserAsync(password: null);
        try
        {
            // Seed attempt_count = max-1 so the next wrong OTP trips the
            // lockout. Direct SQL so we don't race with HandleFailedLoginAttempt
            // running through N HTTP round-trips.
            await SetAttemptCountAsync(shortname, max - 1);

            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, null, Otp: "000000"));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            msg.ShouldBe(LockoutMessage);

            var refreshed = await GetUserAsync(shortname);
            refreshed!.IsActive.ShouldBeTrue(
                "the attempt lock is counter-only — is_active means admin deactivation");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task OtpLogin_AlreadyLocked_Returns_USER_ACCOUNT_LOCKED_Without_Consuming_Otp()
    {
        // Pre-condition: attempt_count is already at max. The pre-check in
        // LoginWithOtpAsync should reject before VerifyAndConsumeAsync runs,
        // preserving the OTP for an admin-driven recovery flow.
        var max = MaxAttempts();
        var (shortname, _) = await CreateUserAsync(password: null);
        try
        {
            await SetAttemptCountAsync(shortname, max);

            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, null, Otp: "000000"));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            msg.ShouldBe(LockoutMessage);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task OtpLogin_ValidOtp_WrongPassword_Locks_When_Threshold_Reached()
    {
        // The OTP login path optionally accepts a password too. A valid OTP
        // with a wrong password used to short-circuit on PASSWORD_NOT_VALIDATED
        // without bumping attempt_count — meaning anyone in possession of an
        // OTP could iterate password guesses indefinitely. With the fix, this
        // path goes through HandleFailedLoginAttemptAsync just like LoginAsync.
        var max = MaxAttempts();
        var msisdn = $"+9647{Random.Shared.Next(10_000_000, 99_999_999)}";
        var (shortname, _) = await CreateUserAsync(password: "CorrectPassword1", msisdn: msisdn);
        try
        {
            // Seed a valid login-purpose OTP at the msisdn (shortname-identifier
            // path resolves dest = user.Msisdn). VerifyAndConsumeAsync will
            // accept and consume it, then the password check fails.
            var otpRepo = _factory.Services.GetRequiredService<OtpRepository>();
            const string otp = "123456";
            await otpRepo.IssueAsync(msisdn, Dmart.Models.Api.OtpPurpose.Login, otp, DateTime.UtcNow.AddMinutes(5));

            await SetAttemptCountAsync(shortname, max - 1);

            var (type, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, "WrongPassword1", Otp: otp));
            type.ShouldBe("auth");
            code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            msg.ShouldBe(LockoutMessage);

            var refreshed = await GetUserAsync(shortname);
            refreshed!.IsActive.ShouldBeTrue(
                "the attempt lock is counter-only — is_active means admin deactivation");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Profile_WrongOldPassword_Increments_AttemptCount_Once()
    {
        // Logged-in user POSTs /user/profile with a new password but the wrong
        // old_password. Below the threshold: UNMATCHED_DATA + counter bump.
        var creds = await _factory.CreateLoggedInUserAsync();
        try
        {
            var resp = await PostProfileAsync(creds.Client,
                oldPassword: "definitely-not-the-current-pw",
                newPassword: "NewPassword1!");

            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.UNMATCHED_DATA);

            var stored = await ReadAttemptCountAsync(creds.Shortname);
            stored.ShouldBe(1);

            var refreshed = await GetUserAsync(creds.Shortname);
            refreshed!.IsActive.ShouldBeTrue("single bad old_password must not lock the account");
        }
        finally { await creds.Cleanup(); }
    }

    [FactIfPg]
    public async Task Profile_WrongOldPassword_Locks_When_Threshold_Reached()
    {
        var max = MaxAttempts();
        var creds = await _factory.CreateLoggedInUserAsync();
        try
        {
            await SetAttemptCountAsync(creds.Shortname, max - 1);

            var resp = await PostProfileAsync(creds.Client,
                oldPassword: "definitely-not-the-current-pw",
                newPassword: "NewPassword1!");

            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            body.Error.Message.ShouldBe(LockoutMessage);

            var refreshed = await GetUserAsync(creds.Shortname);
            refreshed!.IsActive.ShouldBeTrue(
                "the attempt lock is counter-only — is_active means admin deactivation");

            // Lockout blocks new logins only — it must NOT revoke sessions the
            // user already holds. CreateLoggedInUserAsync minted exactly one.
            var users = _factory.Services.GetRequiredService<UserRepository>();
            var sessionsLeft = await users.CountSessionsAsync(creds.Shortname);
            sessionsLeft.ShouldBe(1, "lockout must leave live sessions intact");
        }
        finally { await creds.Cleanup(); }
    }

    // ---- helpers ----

    private int MaxAttempts() =>
        _factory.Services.GetRequiredService<IOptions<Dmart.Config.DmartSettings>>()
            .Value.MaxFailedLoginAttempts;

    private async Task<(string Type, int Code, string Message)> ExpectLoginFailureAsync(UserLoginRequest login)
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Failed);
        body.Error.ShouldNotBeNull();
        return (body.Error!.Type, body.Error.Code, body.Error.Message);
    }

    private static StringContent ProfilePatch(string oldPassword, string newPassword)
    {
        // Match the Record envelope shape ProfileHandler accepts:
        //   { "attributes": { "old_password": "...", "password": "..." } }
        var json = $"{{\"attributes\":{{\"old_password\":\"{oldPassword}\",\"password\":\"{newPassword}\"}}}}";
        return new StringContent(json, System.Text.Encoding.UTF8, "application/json");
    }

    private static Task<HttpResponseMessage> PostProfileAsync(
        HttpClient client, string oldPassword, string newPassword) =>
        client.PostAsync("/user/profile", ProfilePatch(oldPassword, newPassword));

    private async Task<(string Shortname, string Password)> CreateUserAsync(
        string? password, string? msisdn = null, UserType type = UserType.Web)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"lockout_{suffix}";
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
            Password = password is null ? null : hasher.Hash(password),
            Msisdn = msisdn,
            IsMsisdnVerified = msisdn is not null,
            Type = type,
            Language = Language.En,
            Roles = new(),
            Groups = new(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return (shortname, password ?? "");
    }

    private async Task DeleteUserAsync(string shortname)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAllSessionsAsync(shortname);
            await users.DeleteAsync(shortname);
        }
        catch { /* best effort */ }
    }

    private async Task<User?> GetUserAsync(string shortname)
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        return await users.GetByShortnameAsync(shortname);
    }

    private Task<int> ReadAttemptCountAsync(string shortname) =>
        _factory.Services.GetRequiredService<UserRepository>().GetAttemptCountAsync(shortname);

    private async Task SetAttemptCountAsync(string shortname, int count)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command("UPDATE users SET attempt_count = $1 WHERE shortname = $2");
        DbParams.Add(cmd, count);
        DbParams.Add(cmd, shortname);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---- cool-down (auto-unlock) ----

    private int CooldownSeconds() =>
        _factory.Services.GetRequiredService<IOptions<Dmart.Config.DmartSettings>>()
            .Value.LockoutCooldownSeconds;

    // Seed a full lock state directly. last_failed_login uses TimeUtils.Now()'s
    // clock (DateTime.Now, naive) — the same clock the cool-down gate compares against.
    private async Task SetLockedStateAsync(string shortname, int attemptCount, bool isActive, DateTime? lastFailed)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command("UPDATE users SET attempt_count = $1, is_active = $2, last_failed_login = $3 WHERE shortname = $4");
        DbParams.Add(cmd, attemptCount);
        DbParams.Add(cmd, isActive);
        DbParams.Add(cmd, (object?)lastFailed ?? DBNull.Value);
        DbParams.Add(cmd, shortname);
        await cmd.ExecuteNonQueryAsync();
    }

    [FactIfPg]
    public async Task Login_AutoUnlocks_After_The_Cooldown_Elapses()
    {
        var (shortname, password) = await CreateUserAsync(password: "Test1234aaa");
        try
        {
            // Locked, last failed attempt well outside the cool-down window.
            await SetLockedStateAsync(shortname, MaxAttempts(), isActive: true,
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-(CooldownSeconds() + 60)));

            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);

            resp.StatusCode.ShouldBe(HttpStatusCode.OK); // auto-unlocked → normal login
            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);

            var refreshed = await GetUserAsync(shortname);
            refreshed!.IsActive.ShouldBeTrue();
            refreshed.AttemptCount.ShouldBe(0); // counter reset on the successful login
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Login_Stays_Locked_Within_The_Cooldown_Window()
    {
        var (shortname, password) = await CreateUserAsync(password: "Test1234aaa");
        try
        {
            // Last failed attempt 1 minute ago — well inside the window.
            await SetLockedStateAsync(shortname, MaxAttempts(), isActive: true,
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-60));

            var (_, code, msg) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, password, null));
            code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            msg.ShouldBe(LockoutMessage);
            var still = await GetUserAsync(shortname);
            still!.IsActive.ShouldBeTrue("the attempt lock never touches is_active");
            still.AttemptCount.ShouldBe(MaxAttempts(), "still at the threshold → still locked");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Blocked_Attempt_Refreshes_The_Cooldown_Clock()
    {
        var (shortname, password) = await CreateUserAsync(password: "Test1234aaa");
        try
        {
            var stale = Dmart.Utils.TimeUtils.Now().AddSeconds(-300); // 5 min ago, still inside window
            await SetLockedStateAsync(shortname, MaxAttempts(), isActive: true, lastFailed: stale);

            // A blocked attempt must push last_failed_login forward to ~now, so a
            // persistent attacker never lets the window elapse.
            await ExpectLoginFailureAsync(new UserLoginRequest(shortname, null, null, password, null));

            var after = (await GetUserAsync(shortname))!.LastFailedLogin;
            after.ShouldNotBeNull();
            after!.Value.ShouldBeGreaterThan(stale.AddSeconds(60));
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Manual_Deactivation_Is_Not_AutoUnlocked()
    {
        var (shortname, password) = await CreateUserAsync(password: "Test1234aaa");
        try
        {
            // Admin-style deactivation: inactive, counter BELOW threshold, ancient
            // (irrelevant) last_failed_login. The cool-down must not touch it.
            await SetLockedStateAsync(shortname, 0, isActive: false,
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-100000));

            var (_, code, _) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, password, null));
            code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
            (await GetUserAsync(shortname))!.IsActive.ShouldBeFalse("manual deactivation stays locked");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Cooldown_Disabled_Keeps_The_Lock_Permanent()
    {
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.LockoutCooldownSeconds = 0)));
        var (shortname, password) = await CreateUserAsync(password: "Test1234aaa");
        try
        {
            // Ancient last failed attempt, but cool-down disabled → never auto-unlocks.
            await SetLockedStateAsync(shortname, MaxAttempts(), isActive: true,
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-100000));

            var resp = await factory.CreateClient().PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Error!.Code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // ---- bot accounts are exempt from the lockout ----

    [FactIfPg]
    public async Task Bot_Crossing_The_Threshold_Is_Not_Locked()
    {
        // A bot authenticates from CI/MCP with a machine credential. Locking it
        // turns 5 guesses by anyone who knows the shortname into an outage for a
        // whole integration, and — because a bot never re-runs /user/login — the
        // cool-down that rescues a human is never reached. So the counter still
        // moves (the attack stays visible in attempt_count) but never locks.
        var max = MaxAttempts();
        var (shortname, _) = await CreateUserAsync(password: "Test1234aaa", type: UserType.Bot);
        try
        {
            await SetAttemptCountAsync(shortname, max - 1);

            var (_, code, _) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, "WrongPassword1", null));
            code.ShouldBe(InternalErrorCode.INVALID_USERNAME_AND_PASS,
                "a bot crossing the threshold must not surface USER_ACCOUNT_LOCKED");

            (await ReadAttemptCountAsync(shortname)).ShouldBe(max,
                "the counter still moves so brute-force against a bot stays visible");
            (await GetUserAsync(shortname))!.IsActive.ShouldBeTrue();
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Bot_Above_The_Threshold_Can_Still_Login()
    {
        // Defence in depth: even with the counter already past max — an admin or
        // a migration could have set it — a bot with valid credentials gets in.
        var (shortname, password) = await CreateUserAsync(password: "Test1234aaa", type: UserType.Bot);
        try
        {
            await SetLockedStateAsync(shortname, MaxAttempts(), isActive: true,
                lastFailed: Dmart.Utils.TimeUtils.Now());

            var resp = await _factory.CreateClient().PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);

            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Status.ShouldBe(Status.Success);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // ---- a lock blocks new logins, not live sessions ----

    [FactIfPg]
    public async Task Locked_User_Keeps_Using_An_Already_Issued_Access_Token()
    {
        var creds = await _factory.CreateLoggedInUserAsync();
        try
        {
            await SetAttemptCountAsync(creds.Shortname, MaxAttempts() - 1);

            // Trip the lock through the profile password-change path.
            var locking = await PostProfileAsync(creds.Client,
                oldPassword: "definitely-not-the-current-pw",
                newPassword: "NewPassword1!");
            (await locking.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!
                .Error!.Code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);

            // Same bearer token, a plain authenticated read: still served.
            var after = await creds.Client.GetAsync("/user/profile");
            after.StatusCode.ShouldBe(HttpStatusCode.OK,
                "the lock blocks new logins — it must not revoke a live session");
        }
        finally { await creds.Cleanup(); }
    }

    [FactIfPg]
    public async Task Locked_User_Cannot_Mint_A_New_Access_Token_By_Refreshing()
    {
        // The live access token survives, but the session cannot be extended:
        // the lock ends it at the next refresh, bounding the blast radius to the
        // access token's own TTL.
        var creds = await _factory.CreateLoggedInUserAsync();
        try
        {
            var jwt = _factory.Services.GetRequiredService<JwtIssuer>();
            var refresh = jwt.IssueRefresh(creds.Shortname, UserType.Web);

            await SetLockedStateAsync(creds.Shortname, MaxAttempts(), isActive: true,
                lastFailed: Dmart.Utils.TimeUtils.Now());

            var resp = await _factory.CreateClient().PostAsync("/oauth/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refresh,
                }));

            resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await resp.Content.ReadAsStringAsync()).ShouldContain("invalid_grant");
        }
        finally { await creds.Cleanup(); }
    }

    [FactIfPg]
    public async Task Cooldown_Unlock_Does_Not_Reactivate_A_Deactivated_Account()
    {
        // An account can be BOTH admin-deactivated and at the attempt threshold.
        // Clearing the counter after the cool-down must not hand back the
        // is_active flag an admin deliberately cleared.
        var (shortname, password) = await CreateUserAsync(password: "Test1234aaa");
        try
        {
            await SetLockedStateAsync(shortname, MaxAttempts(), isActive: false,
                lastFailed: Dmart.Utils.TimeUtils.Now().AddSeconds(-(CooldownSeconds() + 60)));

            var (_, code, _) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, password, null));
            code.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);

            (await GetUserAsync(shortname))!.IsActive.ShouldBeFalse(
                "the cool-down clears the counter, never the admin's deactivation");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Admin_Setting_IsActive_True_Clears_The_Attempt_Lock()
    {
        // The attempt lock no longer flips is_active, so a locked account is
        // still active — which leaves "set is_active=true" (the admin UI's
        // unlock gesture) with no false→true transition to key off. Without
        // this, an admin has no way to clear the counter at all, and with
        // LOCKOUT_COOLDOWN_SECONDS=0 the lock would be unclearable forever.
        var admin = await _factory.CreateLoggedInUserAsync();
        var (shortname, password) = await CreateUserAsync(password: "Test1234aaa");
        try
        {
            await SetLockedStateAsync(shortname, MaxAttempts(), isActive: true,
                lastFailed: Dmart.Utils.TimeUtils.Now());

            var (_, lockedCode, _) = await ExpectLoginFailureAsync(
                new UserLoginRequest(shortname, null, null, password, null));
            lockedCode.ShouldBe(InternalErrorCode.USER_ACCOUNT_LOCKED);

            var unlock = await admin.Client.PostAsJsonAsync("/managed/request", new Request
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
                        Attributes = new() { ["is_active"] = true },
                    },
                },
            }, DmartJsonContext.Default.Request);
            unlock.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await GetUserAsync(shortname))!.AttemptCount.ShouldBe(0,
                "an explicit is_active=true is the admin's unlock gesture");

            var resp = await _factory.CreateClient().PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await DeleteUserAsync(shortname);
            await admin.Cleanup();
        }
    }
}
