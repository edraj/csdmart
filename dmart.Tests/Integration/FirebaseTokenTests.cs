using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Confirms firebase_token flows through the login + /user/profile paths
// (Python parity). Each test provisions a throwaway user so cross-class
// parallelism can't step on the admin row.
public sealed class FirebaseTokenTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public FirebaseTokenTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Login_With_FirebaseToken_PersistsOnSession()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            var login = new UserLoginRequest(shortname, null, null, password,
                Otp: null, DeviceId: null, FirebaseToken: "fcm-login-token");
            var resp = await client.PostAsJsonAsync("/user/login", login, DmartJsonContext.Default.UserLoginRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            var token = await ExtractAccessTokenAsync(resp);
            token.ShouldNotBeNullOrEmpty();

            var stored = await ReadFirebaseTokenAsync(shortname, token!);
            stored.ShouldBe("fcm-login-token");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Profile_Update_FirebaseToken_WritesCurrentSessionRow()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            var loginResp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);
            var token = (await ExtractAccessTokenAsync(loginResp))!;

            (await ReadFirebaseTokenAsync(shortname, token)).ShouldBeNull();

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var patch = new Dictionary<string, object> { ["firebase_token"] = "fcm-from-profile" };
            var patchResp = await client.PostAsJsonAsync("/user/profile", patch,
                DmartJsonContext.Default.DictionaryStringObject);
            patchResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await ReadFirebaseTokenAsync(shortname, token)).ShouldBe("fcm-from-profile");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Profile_Update_FirebaseToken_DoesNotTouchOtherSessions()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            // Two independent login sessions for the same user.
            var client = _factory.CreateClient();
            var loginA = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: null, FirebaseToken: "fcm-A"),
                DmartJsonContext.Default.UserLoginRequest);
            var tokenA = (await ExtractAccessTokenAsync(loginA))!;

            var loginB = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: null, FirebaseToken: "fcm-B"),
                DmartJsonContext.Default.UserLoginRequest);
            var tokenB = (await ExtractAccessTokenAsync(loginB))!;

            // Update through session A.
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenA);
            var patch = new Dictionary<string, object> { ["firebase_token"] = "fcm-A-updated" };
            var patchResp = await client.PostAsJsonAsync("/user/profile", patch,
                DmartJsonContext.Default.DictionaryStringObject);
            patchResp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await ReadFirebaseTokenAsync(shortname, tokenA)).ShouldBe("fcm-A-updated");
            (await ReadFirebaseTokenAsync(shortname, tokenB)).ShouldBe("fcm-B");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task GetSessionFirebaseTokensAsync_Returns_ActiveTokens()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            // Two sessions: one with a token, one without.
            await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: null, FirebaseToken: "fcm-list-1"),
                DmartJsonContext.Default.UserLoginRequest);
            await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);

            var users = _factory.Services.GetRequiredService<UserRepository>();
            var tokens = await users.GetSessionFirebaseTokensAsync(shortname);
            tokens.ShouldContain("fcm-list-1");
            tokens.ShouldNotContain((string?)null!);
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // One FCM token identifies one device, and a push fan-out over
    // GetSessionFirebaseTokensAsync sends one notification per entry it
    // returns. Every login writes a NEW sessions row carrying the body's
    // firebase_token, so a phone that signs in twice used to leave two rows
    // holding the same token — and the user got every notification twice.
    [FactIfPg]
    public async Task Signing_In_Twice_From_One_Device_Yields_One_Token()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            const string fcm = "fcm-same-device";

            var first = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: null, FirebaseToken: fcm),
                DmartJsonContext.Default.UserLoginRequest);
            var tokenA = (await ExtractAccessTokenAsync(first))!;

            var second = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: null, FirebaseToken: fcm),
                DmartJsonContext.Default.UserLoginRequest);
            var tokenB = (await ExtractAccessTokenAsync(second))!;

            var users = _factory.Services.GetRequiredService<UserRepository>();
            var tokens = await users.GetSessionFirebaseTokensAsync(shortname);
            tokens.Count(t => t == fcm).ShouldBe(1, "one device, one push");

            // The newest session keeps the token; the older row is cleared, so
            // the duplicate never accumulates in the first place. (SELECT
            // DISTINCT would have hidden it from this read, but the row would
            // still be sitting there for any other reader of the column.)
            (await ReadFirebaseTokenAsync(shortname, tokenB)).ShouldBe(fcm);
            (await ReadFirebaseTokenAsync(shortname, tokenA)).ShouldBeNull();
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // Rotation — the case a token match cannot see. FCM reissues a device's
    // token (app reinstall, data restore, periodic refresh), so the rows that
    // phone left behind hold a DIFFERENT string. Nothing about the two strings
    // says they are the same handset; the device_id the client logged in with
    // does, which is why the session row carries one.
    [FactIfPg]
    public async Task A_Rotated_Token_Retires_The_Same_Devices_Older_Token()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            const string device = "handset-7";

            var before = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: device, FirebaseToken: "fcm-before-rotation"),
                DmartJsonContext.Default.UserLoginRequest);
            var tokenBefore = (await ExtractAccessTokenAsync(before))!;

            // Same handset, new FCM token.
            var after = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: device, FirebaseToken: "fcm-after-rotation"),
                DmartJsonContext.Default.UserLoginRequest);
            var tokenAfter = (await ExtractAccessTokenAsync(after))!;

            var users = _factory.Services.GetRequiredService<UserRepository>();
            var tokens = await users.GetSessionFirebaseTokensAsync(shortname);
            tokens.ShouldBe(new List<string> { "fcm-after-rotation" },
                "one handset must be one push target across a token rotation");

            (await ReadFirebaseTokenAsync(shortname, tokenAfter)).ShouldBe("fcm-after-rotation");
            (await ReadFirebaseTokenAsync(shortname, tokenBefore)).ShouldBeNull();
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task Two_Real_Devices_Both_Keep_Their_Tokens()
    {
        // The guard on the test above: clearing by device must not collapse a
        // user's genuinely separate handsets into one push target.
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: "phone", FirebaseToken: "fcm-phone"),
                DmartJsonContext.Default.UserLoginRequest);
            await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: "tablet", FirebaseToken: "fcm-tablet"),
                DmartJsonContext.Default.UserLoginRequest);

            var users = _factory.Services.GetRequiredService<UserRepository>();
            var tokens = await users.GetSessionFirebaseTokensAsync(shortname);
            tokens.Count.ShouldBe(2);
            tokens.ShouldContain("fcm-phone");
            tokens.ShouldContain("fcm-tablet");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    [FactIfPg]
    public async Task A_Profile_Patch_Retires_The_Same_Devices_Older_Token()
    {
        // Rotation reaching the server the other way: the app keeps its session
        // and PATCHes the new token onto /user/profile. The patch carries no
        // device_id, so the repository reads it off the session row being
        // updated.
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            var first = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: "handset-9", FirebaseToken: "fcm-old"),
                DmartJsonContext.Default.UserLoginRequest);
            var tokenOld = (await ExtractAccessTokenAsync(first))!;

            var second = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: "handset-9", FirebaseToken: null),
                DmartJsonContext.Default.UserLoginRequest);
            var tokenNew = (await ExtractAccessTokenAsync(second))!;

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenNew);
            var patch = new Dictionary<string, object> { ["firebase_token"] = "fcm-new" };
            (await client.PostAsJsonAsync("/user/profile", patch,
                DmartJsonContext.Default.DictionaryStringObject))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var users = _factory.Services.GetRequiredService<UserRepository>();
            (await users.GetSessionFirebaseTokensAsync(shortname))
                .ShouldBe(new List<string> { "fcm-new" });
            (await ReadFirebaseTokenAsync(shortname, tokenOld)).ShouldBeNull();
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // Prune-on-send. Everything above keeps duplicates from accumulating; this
    // is what removes a token FCM itself has retired, which no amount of
    // write-side care can predict.
    [FactIfPg]
    public async Task Invalidating_A_Token_Clears_It_Everywhere()
    {
        var (alice, alicePw) = await CreateUserAsync();
        var (bob, bobPw) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            // One shared handset, both accounts signed in on it, plus a token
            // that stays valid so the clear can be shown to be targeted.
            await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(alice, null, null, alicePw,
                    Otp: null, DeviceId: "shared", FirebaseToken: "fcm-dead"),
                DmartJsonContext.Default.UserLoginRequest);
            await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(bob, null, null, bobPw,
                    Otp: null, DeviceId: "shared", FirebaseToken: "fcm-dead"),
                DmartJsonContext.Default.UserLoginRequest);
            await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(bob, null, null, bobPw,
                    Otp: null, DeviceId: "bobs-own", FirebaseToken: "fcm-live"),
                DmartJsonContext.Default.UserLoginRequest);

            var users = _factory.Services.GetRequiredService<UserRepository>();
            (await users.GetSessionFirebaseTokensAsync(alice)).ShouldContain("fcm-dead");

            // FCM answered UNREGISTERED for it. A retired token is dead for
            // every account that shares the device, so this is not user-scoped.
            var cleared = await users.InvalidateFirebaseTokensAsync(new[] { "fcm-dead" });
            cleared.ShouldBe(2);

            (await users.GetSessionFirebaseTokensAsync(alice)).ShouldBeEmpty();
            (await users.GetSessionFirebaseTokensAsync(bob))
                .ShouldBe(new List<string> { "fcm-live" }, "only the rejected token goes");

            // Idempotent, and an empty request is a no-op rather than a wildcard.
            (await users.InvalidateFirebaseTokensAsync(new[] { "fcm-dead" })).ShouldBe(0);
            (await users.InvalidateFirebaseTokensAsync(Array.Empty<string>())).ShouldBe(0);
            (await users.GetSessionFirebaseTokensAsync(bob)).ShouldBe(new List<string> { "fcm-live" });
        }
        finally
        {
            await DeleteUserAsync(alice);
            await DeleteUserAsync(bob);
        }
    }

    [FactIfPg]
    public async Task Duplicate_Rows_That_Predate_The_Fix_Still_Collapse_On_Read()
    {
        // The read-side backstop, tested against rows the write path can no
        // longer produce: an upgraded database still holds them, and dropping
        // the DISTINCT would quietly bring the duplicate pushes back.
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            const string fcm = "fcm-legacy-dup";
            await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password,
                    Otp: null, DeviceId: null, FirebaseToken: fcm),
                DmartJsonContext.Default.UserLoginRequest);

            // A second row carrying the same token, written straight to the
            // table the way the pre-fix login path did.
            var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
            await using (var conn = await db.OpenAsync())
            await using (var cmd = conn.Command("""
                INSERT INTO sessions (uuid, shortname, token, firebase_token, timestamp)
                VALUES ($1, $2, $3, $4, $5)
                """))
            {
                DbParams.Add(cmd, Guid.NewGuid());
                DbParams.Add(cmd, shortname);
                DbParams.Add(cmd, $"legacy{Guid.NewGuid():N}");
                DbParams.Add(cmd, fcm);
                DbParams.Add(cmd, Dmart.Utils.TimeUtils.Now());
                await cmd.ExecuteNonQueryAsync();
            }

            var users = _factory.Services.GetRequiredService<UserRepository>();
            (await users.GetSessionFirebaseTokensAsync(shortname))
                .Count(t => t == fcm)
                .ShouldBe(1, "DISTINCT is what keeps legacy duplicates from double-pushing");
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // Regression: the bearer JWT is NEVER persisted in plaintext. The column
    // holds a keyed HMAC-SHA256 hex digest of the raw JWT; the raw JWT must
    // hash to the stored value but must not appear verbatim.
    [FactIfPg]
    public async Task Session_Token_Stored_Hashed_Not_Raw()
    {
        var (shortname, password) = await CreateUserAsync();
        try
        {
            var client = _factory.CreateClient();
            var loginResp = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(shortname, null, null, password, null),
                DmartJsonContext.Default.UserLoginRequest);
            loginResp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var rawJwt = (await ExtractAccessTokenAsync(loginResp))!;

            var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
            var tokenHasher = _factory.Services.GetRequiredService<SessionTokenHasher>();
            await using var conn = await db.OpenAsync();
            await using var cmd = conn.Command("SELECT token FROM sessions WHERE shortname = $1");
            DbParams.Add(cmd, shortname);
            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).ShouldBeTrue();
            var stored = reader.GetString(0);

            // 64 lowercase hex chars (SHA-256 / HMAC-SHA256 output).
            stored.Length.ShouldBe(64);
            stored.ShouldMatch("^[0-9a-f]{64}$");
            stored.ShouldNotBe(rawJwt);
            stored.ShouldBe(tokenHasher.Hash(rawJwt));
        }
        finally { await DeleteUserAsync(shortname); }
    }

    // ---- helpers ----

    private async Task<(string Shortname, string Password)> CreateUserAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"fcm_test_{suffix}";
        var password = "Test1234!fcm";
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var hasher = _factory.Services.GetRequiredService<PasswordHasher>();
        var user = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = hasher.Hash(password),
            Roles = new(),
            Groups = new(),
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user);
        return (shortname, password);
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

    private static async Task<string?> ExtractAccessTokenAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        return body?.Records?.FirstOrDefault()?.Attributes?["access_token"]?.ToString();
    }

    // Read sessions.firebase_token directly — the repository exposes only the
    // write paths and a list-by-shortname read, both of which this test
    // exercises elsewhere. Tokens are stored as keyed HMAC hex digests, so we
    // hash the raw token once and look it up directly.
    private async Task<string?> ReadFirebaseTokenAsync(string shortname, string token)
    {
        var db = _factory.Services.GetRequiredService<IDbConnectionFactory>();
        var tokenHasher = _factory.Services.GetRequiredService<SessionTokenHasher>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command("SELECT firebase_token FROM sessions WHERE shortname = $1 AND token = $2");
        DbParams.Add(cmd, shortname);
        DbParams.Add(cmd, tokenHasher.Hash(token));
        var raw = await cmd.ExecuteScalarAsync();
        return raw is string s ? s : null;
    }
}
