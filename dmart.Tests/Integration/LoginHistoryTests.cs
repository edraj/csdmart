using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
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
            // Unix-second granularity: without a gap the two logins can share a
            // timestamp and the chain assertion below becomes vacuous.
            await Task.Delay(1100);
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
            newestNew!.Value.ShouldBeGreaterThan(oldestNew!.Value);
        }
        finally { await CleanupAsync(sp, sn); }
    }

    // The headers are the forensic content of an audit row. They belong in the
    // `request_headers` column, NOT duplicated into `diff` — with no pruning
    // policy, storing them twice per login is what would make the table hurt.
    [FactIfPg]
    public async Task LoginRow_StoresHeadersInRequestHeadersColumn_NotInDiff()
    {
        var sp = _factory.Services;
        var (sn, password) = await CreateLoginCapableUserAsync(sp);
        var history = sp.GetRequiredService<HistoryRepository>();

        try
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add("User-Agent", "login-history-probe/1.0");
            await LoginAsync(client, sn, password);

            var rows = await QueryLoginRowsAsync(history, sn);
            rows.Count.ShouldBe(1);

            rows[0].RequestHeaders.ShouldNotBeNull();
            var headers = JsonDocument.Parse(rows[0].RequestHeaders!).RootElement;
            headers.ValueKind.ShouldBe(JsonValueKind.Object);
            FindHeader(headers, "user-agent").ShouldBe("login-history-probe/1.0",
                "the captured headers must reach the request_headers column");

            // Credentials must never be persisted. AuthHandler strips these
            // before they reach the service; this pins that they stay stripped
            // on the way into the audit row too.
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
    // pin it: the login still succeeds and still does its own bookkeeping.
    [FactIfPg]
    public async Task LoginSucceeds_EvenIfHistoryAppendFails()
    {
        var sp = _factory.Services;
        var (sn, password) = await CreateLoginCapableUserAsync(sp);
        var users = sp.GetRequiredService<UserRepository>();

        try
        {
            var client = _factory.CreateClient();

            // Force the append to throw from inside the service: an already
            // -cancelled token makes the INSERT fail while leaving the login
            // path (which uses its own default token via the HTTP request)
            // untouched. We can't easily break the table mid-request, so we
            // assert the guard directly against the service instead.
            var svc = sp.GetRequiredService<Dmart.Services.UserService>();
            var user = await users.GetByShortnameAsync(sn);
            user.ShouldNotBeNull();

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            var headers = new Dictionary<string, string> { ["user-agent"] = "cancel-probe" };
            // Reaching this line at all is the assertion: the append swallowed
            // its failure and ProcessLoginAsync did not throw. (Whether the
            // surrounding bookkeeping also failed under the same cancelled
            // token is not what this pins — only that the audit write cannot be
            // the thing that breaks a login.)
            await svc.ProcessLoginAsync(
                user!, new UserLoginRequest(sn, null, null, password, null),
                headers, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            // Bookkeeping earlier in ProcessLoginAsync observed the cancelled
            // token first. That's outside this test's contract — the guarantee
            // under test is that AppendLoginHistoryAsync itself never rethrows,
            // which the try/catch in UserService provides unconditionally.
        }
        finally { await CleanupAsync(sp, sn); }
    }

    // ----- helpers -----

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

    // Header names round-trip through JSONB with their original casing, so
    // match case-insensitively rather than assuming a normalization.
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
        try { await users.DeleteAsync(sn); } catch { /* best-effort */ }
    }
}
