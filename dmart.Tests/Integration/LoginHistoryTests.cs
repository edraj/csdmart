using System.Net;
using System.Net.Http.Json;
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

            oldestOld.ShouldBeNull("first-ever login has no previous timestamp");
            oldestNew.ShouldNotBeNull();

            newestOld.ShouldBe(oldestNew,
                "the second row's `old` must be the first row's `new` — that chain is what "
                + "lets a reader walk the login sequence backwards");
            // Unix-second granularity: back-to-back logins legitimately land in
            // the same second, so this is >= rather than >. The chain assertion
            // above is what actually proves the two rows are distinct logins.
            newestNew!.Value.ShouldBeGreaterThanOrEqualTo(oldestNew!.Value);
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
            old.ShouldBeNull();
            @new.ShouldNotBeNull();

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
            old.ShouldBeNull("a brand-new account has no previous login");
            @new.ShouldNotBeNull();
            FindHeader(JsonDocument.Parse(rows[0].RequestHeaders!).RootElement, "user-agent")
                .ShouldBe("registration-probe/1.0");
        }
        finally
        {
            await DeleteLoginRowsAsync(sp, sn);
            await TestUserCleanup.DeleteUserAndOwnedAsync(sp, sn);
        }
    }

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

    // Returns the {old, new} timestamps of the row's last_login diff.
    private static (long? Old, long? New) ReadLastLoginDiff(HistoryRecord row)
    {
        row.Diff.ShouldNotBeNull();
        var last = JsonDocument.Parse(row.Diff!).RootElement.GetProperty("last_login");
        long? Read(string name)
        {
            var v = last.GetProperty(name);
            return v.ValueKind == JsonValueKind.Null ? null : v.GetInt64();
        }
        return (Read("old"), Read("new"));
    }

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
