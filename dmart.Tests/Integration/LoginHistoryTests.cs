using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Plugins;
using Dmart.Services;
using Dmart.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the append-only login audit trail added to UserService.
//
// `users.last_login` is overwritten on every successful auth, so it can only
// ever answer "when was the most recent login". These tests pin the companion
// guarantee: each successful login ALSO appends a row to `histories` at
// management//users/<shortname>, so the full sign-in sequence survives.
//
// Note this is a deliberate divergence from Python dmart, which writes
// last_login via internal_sys_update_model and records no history row — so
// there is no upstream behaviour these tests are keeping us honest against.
// They are the specification.
public sealed class LoginHistoryTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public LoginHistoryTests(DmartFactory factory) => _factory = factory;

    // The load-bearing test: N logins must leave N rows, and each row's
    // {old, new} must chain to the one before it. A single overwritten
    // column cannot satisfy this — that's the whole point of the change.
    [FactIfPg]
    public async Task EachLogin_AppendsHistoryRow_ChainingPreviousTimestamp()
    {
        var sp = _factory.Services;
        var (sn, password) = await CreateLoginCapableUserAsync(sp);
        var history = sp.GetRequiredService<HistoryRepository>();

        try
        {
            var client = _factory.CreateClient();

            await LoginAsync(client, sn, password);
            await LoginAsync(client, sn, password);

            var rows = await QueryLoginRowsAsync(history, sn);
            rows.Count.ShouldBe(2, "each successful login must append exactly one history row");

            // QueryHistoryAsync orders newest-first.
            var (newestOld, newestNew) = ReadLastLoginDiff(rows[0]);
            var (oldestOld, oldestNew) = ReadLastLoginDiff(rows[1]);

            oldestOld.ValueKind.ShouldBe(JsonValueKind.Null,
                "first-ever login has no previous timestamp");
            oldestNew.ValueKind.ShouldBe(JsonValueKind.String);

            newestOld.GetString().ShouldBe(oldestNew.GetString(),
                "the second row's `old` must be the first row's `new` — that chain is what "
                + "lets a reader walk the login sequence backwards");
            ParseStamp(newestNew).ShouldBeGreaterThanOrEqualTo(ParseStamp(oldestNew),
                "back-to-back logins may share a tick; the chain assertion above is what "
                + "proves the two rows are distinct logins");
        }
        finally { await CleanupAsync(sp, sn); }
    }

    // The headers are the forensic content of an audit row. They belong in the
    // `request_headers` column, NOT duplicated into `diff` — with no pruning
    // policy, storing them twice per login is what would make the table hurt.
    // And only the allowlisted ones: these rows are never pruned, so a header
    // carrying a credential would be persisted forever, in every export.
    [FactIfPg]
    public async Task LoginRow_StoresAllowlistedHeadersOnly_InRequestHeadersColumn()
    {
        var sp = _factory.Services;
        var (sn, password) = await CreateLoginCapableUserAsync(sp);
        var history = sp.GetRequiredService<HistoryRepository>();

        try
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "login-history-probe/1.0");
            client.DefaultRequestHeaders.Add("Accept-Language", "ar-IQ");
            // Off the allowlist, and the reason the allowlist exists: a
            // deployment that carries a credential in a custom header must not
            // have it landed in an unprunable audit row.
            client.DefaultRequestHeaders.Add("X-Api-Key", "super-secret-key");
            await LoginAsync(client, sn, password);

            var rows = await QueryLoginRowsAsync(history, sn);
            rows.Count.ShouldBe(1);

            rows[0].RequestHeaders.ShouldNotBeNull();
            var headers = JsonDocument.Parse(rows[0].RequestHeaders!).RootElement;
            headers.ValueKind.ShouldBe(JsonValueKind.Object);
            FindHeader(headers, "user-agent").ShouldBe("login-history-probe/1.0",
                "the captured headers must reach the request_headers column");
            FindHeader(headers, "accept-language").ShouldBe("ar-IQ");
            // `x-forwarded-for` is on the allowlist but deliberately not
            // asserted here: UseForwardedHeaders (Program.cs:1963) consumes it
            // before any endpoint sees it, so it only ever lands on a row in
            // deployments where that middleware is disabled.

            FindHeader(headers, "x-api-key").ShouldBeNull(
                "headers off the allowlist must never be persisted");
            // Credentials must never be persisted. AuthHandler strips these
            // before they reach the service; the allowlist is the second line.
            FindHeader(headers, "authorization").ShouldBeNull();
            FindHeader(headers, "cookie").ShouldBeNull();

            // diff carries only the timestamp transition.
            rows[0].Diff.ShouldNotBeNull();
            var diff = JsonDocument.Parse(rows[0].Diff!).RootElement;
            diff.EnumerateObject().Select(p => p.Name).ShouldBe(new[] { "last_login" });
            diff.GetProperty("last_login").TryGetProperty("headers", out _)
                .ShouldBeFalse("headers must not be duplicated into diff");
            rows[0].Diff!.Contains("login-history-probe", StringComparison.Ordinal)
                .ShouldBeFalse("headers must appear exactly once per row, in request_headers");
        }
        finally { await CleanupAsync(sp, sn); }
    }

    // A history-write failure must not be able to lock users out. This is the
    // one place the codebase deliberately swallows an AppendAsync failure, so
    // pin it with an append that genuinely fails: a HistoryRepository pointed at
    // an unreachable database. Everything else in the service is the real thing,
    // so a regression that lets the exception escape fails here.
    [FactIfPg]
    public async Task LoginSucceeds_EvenIfHistoryAppendFails()
    {
        var sp = _factory.Services;
        var (sn, password) = await CreateLoginCapableUserAsync(sp);
        var users = sp.GetRequiredService<UserRepository>();
        var history = sp.GetRequiredService<HistoryRepository>();

        try
        {
            var user = await users.GetByShortnameAsync(sn);
            user.ShouldNotBeNull();

            var svc = BuildServiceWithBrokenHistory(sp);
            var result = await svc.ProcessLoginAsync(
                user!, new UserLoginRequest(sn, null, null, password, null),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["user-agent"] = "broken-history-probe",
                },
                CancellationToken.None);

            result.IsOk.ShouldBeTrue(
                "a failed audit write must not fail the login: " + result.ErrorMessage);
            result.Value.Access.ShouldNotBeNullOrEmpty();

            // The rest of the login bookkeeping still happened...
            var after = await users.GetByShortnameAsync(sn);
            after!.LastLogin.ShouldNotBeNull("last_login must still be written");
            // ...and the audit row really was lost, i.e. the append did fail
            // rather than quietly succeeding and making this test vacuous.
            (await QueryLoginRowsAsync(history, sn)).ShouldBeEmpty();
        }
        finally { await CleanupAsync(sp, sn); }
    }

    // The trail must not be gated on the caller passing headers: /oauth/authorize
    // completes a password login with requestHeaders: null (OAuthEndpoints.cs),
    // and a credential-verified sign-in that leaves no trace defeats the point.
    [FactIfPg]
    public async Task LoginWithoutRequestHeaders_StillAppendsHistoryRow()
    {
        var sp = _factory.Services;
        var (sn, password) = await CreateLoginCapableUserAsync(sp);
        var history = sp.GetRequiredService<HistoryRepository>();
        var users = sp.GetRequiredService<UserRepository>();

        try
        {
            var svc = sp.GetRequiredService<UserService>();
            var user = await users.GetByShortnameAsync(sn);
            user.ShouldNotBeNull();

            var result = await svc.ProcessLoginAsync(
                user!, new UserLoginRequest(sn, null, null, password, null),
                requestHeaders: null, CancellationToken.None);
            result.IsOk.ShouldBeTrue(result.ErrorMessage);

            var rows = await QueryLoginRowsAsync(history, sn);
            rows.Count.ShouldBe(1, "a headerless login is still a login");

            var (old, @new) = ReadLastLoginDiff(rows[0]);
            old.ValueKind.ShouldBe(JsonValueKind.Null);
            @new.ValueKind.ShouldBe(JsonValueKind.String);

            // No headers to record — the column is NOT NULL, so it holds {}.
            JsonDocument.Parse(rows[0].RequestHeaders!).RootElement
                .EnumerateObject().Any().ShouldBeFalse();
        }
        finally { await CleanupAsync(sp, sn); }
    }

    // The registration auto-login is the second call site: it issues tokens
    // inline instead of delegating to ProcessLoginAsync, so without its own
    // append the very first login of every self-registered account would be
    // missing from the trail.
    [FactIfPg]
    public async Task Registration_AutoLogin_AppendsHistoryRow()
    {
        // OTP disabled so /user/create succeeds without a prior otp-request.
        var factory = _factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<DmartSettings>(s => s.IsOtpForCreateRequired = false)));
        var sp = factory.Services;
        var history = sp.GetRequiredService<HistoryRepository>();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", "registration-probe/1.0");
        var email = "lhreg_" + Guid.NewGuid().ToString("N")[..10] + "@test.local";
        var body = new StringContent(
            "{\"attributes\":{\"email\":\"" + email + "\",\"password\":\"Test1234!lh\"}}",
            System.Text.Encoding.UTF8, "application/json");

        var resp = await client.PostAsync("/user/create", body);
        resp.IsSuccessStatusCode.ShouldBeTrue(await resp.Content.ReadAsStringAsync());
        var created = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        var sn = created!.Records![0].Shortname;

        try
        {
            var rows = await QueryLoginRowsAsync(history, sn);
            rows.Count.ShouldBe(1, "the auto-login at registration must be audited too");

            var (old, @new) = ReadLastLoginDiff(rows[0]);
            old.ValueKind.ShouldBe(JsonValueKind.Null, "a brand-new account has no previous login");
            @new.ValueKind.ShouldBe(JsonValueKind.String);
            FindHeader(JsonDocument.Parse(rows[0].RequestHeaders!).RootElement, "user-agent")
                .ShouldBe("registration-probe/1.0");
        }
        finally
        {
            await DeleteLoginRowsAsync(sp, sn);
            await TestUserCleanup.DeleteUserAndOwnedAsync(sp, sn);
        }
    }

    // The stored shape itself. `last_login.timestamp` reads like created_at /
    // updated_at rather than as epoch seconds, so the audit trail is legible
    // without conversion — and the column and the history row must agree, since
    // the row is supposed to be a faithful record of what the column was set to.
    [FactIfPg]
    public async Task LastLogin_Timestamp_Is_A_Naive_Local_Stamp_Matching_CreatedAt()
    {
        var sp = _factory.Services;
        var (sn, password) = await CreateLoginCapableUserAsync(sp);
        var users = sp.GetRequiredService<UserRepository>();
        var history = sp.GetRequiredService<HistoryRepository>();

        try
        {
            await LoginAsync(_factory.CreateClient(), sn, password);

            var user = await users.GetByShortnameAsync(sn);
            user!.LastLogin.ShouldNotBeNull();
            var stamp = (JsonElement)user.LastLogin!["timestamp"]!;

            stamp.ValueKind.ShouldBe(JsonValueKind.String,
                "epoch seconds are unreadable in an audit trail — see UserService.LoginTimestamp");
            // ParseStamp's format carries no offset specifier, so this both
            // parses the value and asserts its shape: a Kind=Local or Kind=Utc
            // stamp would render "+03:00" or "Z" and throw here. That is what
            // makes it render exactly like a created_at read back from a
            // TIMESTAMP (without time zone) column. The two explicit checks
            // below say the same thing in the failure message, since a
            // FormatException alone would not name the cause.
            var parsed = ParseStamp(stamp);
            stamp.GetString()!.ShouldNotContain("+");
            stamp.GetString()!.EndsWith("Z", StringComparison.Ordinal).ShouldBeFalse();

            // Same wall clock the row was created on, give or take the test.
            parsed.ShouldBeInRange(DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(5));

            // And the audit row records exactly what the column holds.
            var (_, @new) = ReadLastLoginDiff((await QueryLoginRowsAsync(history, sn))[0]);
            @new.GetString().ShouldBe(stamp.GetString());
        }
        finally { await CleanupAsync(sp, sn); }
    }

    // The migration case, and the reason PreviousLoginTimestamp stays untyped:
    // rows written before this format change (or by Python dmart, which still
    // writes int(datetime.now().timestamp())) hold an epoch NUMBER. The first
    // login after an upgrade therefore produces one row whose `old` is a number
    // and whose `new` is a string. That must be recorded verbatim, not crash and
    // not be silently reinterpreted as a date it never was.
    [FactIfPg]
    public async Task Legacy_Epoch_Timestamp_Is_Carried_Into_The_Audit_Row_Unchanged()
    {
        var sp = _factory.Services;
        var (sn, password) = await CreateLoginCapableUserAsync(sp);
        var users = sp.GetRequiredService<UserRepository>();
        var history = sp.GetRequiredService<HistoryRepository>();

        try
        {
            // Exactly what a pre-upgrade row looks like.
            const long legacyEpoch = 1_785_000_000;
            var user = await users.GetByShortnameAsync(sn);
            await users.UpsertAsync(user! with
            {
                LastLogin = new Dictionary<string, object>
                {
                    ["timestamp"] = legacyEpoch,
                    ["headers"] = new Dictionary<string, object> { ["user-agent"] = "legacy" },
                },
            });

            await LoginAsync(_factory.CreateClient(), sn, password);

            var (old, @new) = ReadLastLoginDiff((await QueryLoginRowsAsync(history, sn))[0]);
            old.ValueKind.ShouldBe(JsonValueKind.Number,
                "a legacy epoch must be recorded as the number it was");
            old.GetInt64().ShouldBe(legacyEpoch);
            @new.ValueKind.ShouldBe(JsonValueKind.String,
                "the new side is always the readable form");
        }
        finally { await CleanupAsync(sp, sn); }
    }

    // Pins StampFormat itself, deterministically — the tests above can only
    // exercise whatever width the clock happens to produce, and the width is
    // exactly what went wrong: the first version of this helper used "O",
    // which demands 7 fractional digits, while System.Text.Json trims trailing
    // zeros. It passed until a login landed on a tick ending in zero.
    [Theory]
    [InlineData("2026-07-29T10:15:31.1365341")]  // full 7 digits
    [InlineData("2026-07-29T10:15:31.136534")]   // one trailing zero trimmed — the failing case
    [InlineData("2026-07-29T10:15:31.136")]
    [InlineData("2026-07-29T10:15:31")]          // whole second: fraction dropped entirely
    public void StampFormat_Accepts_Every_Width_Stj_Emits(string stamp)
        => Should.NotThrow(() => DateTime.ParseExact(
            stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None));

    // And rejects the shapes that would mean the value stopped matching
    // created_at / updated_at.
    [Theory]
    [InlineData("2026-07-29T10:15:31.136534+03:00")]  // Kind=Local leaked through
    [InlineData("2026-07-29T10:15:31.136534Z")]       // Kind=Utc leaked through
    [InlineData("1785293645")]                        // back to epoch seconds
    public void StampFormat_Rejects_Offsets_And_Epochs(string stamp)
        => Should.Throw<FormatException>(() => DateTime.ParseExact(
            stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None));

    // ----- helpers -----

    // A UserService whose HistoryRepository points at an unreachable database:
    // every AppendAsync throws, every other dependency is the real one from DI.
    private static UserService BuildServiceWithBrokenHistory(IServiceProvider sp)
    {
        var unreachable = new Db(Options.Create(new DmartSettings
        {
            // Port 1 refuses immediately; the short timeout keeps the test fast
            // even where it doesn't.
            PostgresConnection = "Host=127.0.0.1;Port=1;Username=nobody;"
                + "Password=nobody;Database=nowhere;Timeout=1;",
        }));
        return new UserService(
            sp.GetRequiredService<UserRepository>(),
            sp.GetRequiredService<OtpRepository>(),
            sp.GetRequiredService<PasswordHasher>(),
            sp.GetRequiredService<JwtIssuer>(),
            new HistoryRepository(unreachable),
            sp.GetRequiredService<PluginManager>(),
            sp.GetRequiredService<SchemaValidator>(),
            sp.GetRequiredService<RegexPatternsConfig>(),
            sp.GetRequiredService<IOptions<DmartSettings>>(),
            sp.GetRequiredService<ILogger<UserService>>());
    }

    private static async Task LoginAsync(HttpClient client, string sn, string password)
    {
        var res = await client.PostAsJsonAsync("/user/login",
            new UserLoginRequest(sn, null, null, password, null),
            DmartJsonContext.Default.UserLoginRequest);
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static Task<List<HistoryRecord>> QueryLoginRowsAsync(
        HistoryRepository history, string sn)
        => history.QueryHistoryAsync(new Query
        {
            Type = QueryType.History,
            SpaceName = "management",
            Subpath = "/users",
            FilterShortnames = new List<string> { sn },
            Limit = 50,
        });

    // Returns the {old, new} values of the row's last_login diff as raw
    // elements rather than a parsed type, because the two sides are NOT
    // guaranteed to share one: `new` is always a naive-local timestamp string,
    // while `old` is whatever the previous login stored — an epoch number for
    // any row written before the format change, or by Python dmart.
    private static (JsonElement Old, JsonElement New) ReadLastLoginDiff(HistoryRecord row)
    {
        row.Diff.ShouldNotBeNull();
        var last = JsonDocument.Parse(row.Diff!).RootElement.GetProperty("last_login");
        return (last.GetProperty("old").Clone(), last.GetProperty("new").Clone());
    }

    // The shape UserService.LoginTimestamp() serializes to, and the format
    // these tests hold it to.
    //
    // NOT "O". System.Text.Json writes ISO 8601 with trailing zeros TRIMMED, so
    // the fraction is 0-7 digits wide, while "O" demands exactly 7 — a stamp
    // whose tick count happens to end in a zero (about one run in ten) fails to
    // parse. `FFFFFFF` accepts any width. Carrying no offset specifier is what
    // keeps this strict about the part that matters: a Kind=Local or Kind=Utc
    // value would render "+03:00" or "Z" and fail here, which is exactly the
    // drift away from created_at/updated_at we want caught.
    private const string StampFormat = "yyyy-MM-ddTHH:mm:ss.FFFFFFF";

    private static DateTime ParseStamp(JsonElement stamp) =>
        DateTime.ParseExact(stamp.GetString()!, StampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None);

    // Header names are lowercased on the way into the row, but match
    // case-insensitively so this helper keeps working if that changes.
    private static string? FindHeader(JsonElement headers, string name)
    {
        foreach (var p in headers.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.GetString();
        return null;
    }

    private static async Task<(string Shortname, string Password)> CreateLoginCapableUserAsync(
        IServiceProvider sp)
    {
        var sn = "lh_" + Guid.NewGuid().ToString("N")[..10];
        var password = "Test1234!lh";
        var users = sp.GetRequiredService<UserRepository>();
        var hasher = sp.GetRequiredService<PasswordHasher>();
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = sn,
            SpaceName = "management", Subpath = "/users",
            OwnerShortname = sn, IsActive = true,
            Password = hasher.Hash(password),
            Email = $"{sn}@test.local", IsEmailVerified = true,
            Roles = new(), Groups = new(),
            Type = UserType.Web, Language = Language.En,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        return (sn, password);
    }

    private static async Task CleanupAsync(IServiceProvider sp, string sn)
    {
        var users = sp.GetRequiredService<UserRepository>();
        await DeleteLoginRowsAsync(sp, sn);
        try { await users.DeleteAsync(sn); } catch { /* best-effort */ }
    }

    // `histories` has no FK to `users`, so deleting the user leaves its audit
    // rows behind — and this suite writes one per login. Purge them explicitly
    // rather than accumulating dead rows in every dev/CI database.
    private static async Task DeleteLoginRowsAsync(IServiceProvider sp, string sn)
    {
        try
        {
            var db = sp.GetRequiredService<Db>();
            await using var conn = await db.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "DELETE FROM histories WHERE space_name = 'management' "
                + "AND subpath = '/users' AND shortname = $1", conn);
            cmd.Parameters.Add(new() { Value = sn });
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort */ }
    }
}
