using System.Net;
using System.Net.Http.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Dmart.Utils;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The far end of the Argon2 memory budget, through the real HTTP surface.
//
// Argon2id allocates its `m` outright, so peak RSS here scales with request
// CONCURRENCY — the one place in dmart where it does. Three simultaneous logins
// at the old m=102400 were enough for the kernel to OOM-kill the process on a
// 512 MB board. Budgeted, they queue; past the queue timeout the server says
// 503 and means it, rather than dying or hanging.
public class PasswordHashingBackpressureTests(DmartFactory factory) : IClassFixture<DmartFactory>
{
    private const string Password = "Backpressure1234";

    [FactIfPg]
    public async Task Queue_Timeout_Answers_503_With_Retry_After()
    {
        // Budget for one hash at a time and a timeout too short to wait out a
        // hash. Everything past the first contender must be refused rather than
        // queued indefinitely.
        var f = factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s =>
            {
                // Cost is bought with ITERATIONS, not memory: the queue timeout
                // floor is 1s, so each hash has to take long enough that a
                // serialized burst overruns it. t=12 at m=19456 is ~0.2s here,
                // so 20 contenders need ~4s of budget time against a 1s wait.
                s.PasswordHashMemoryKb = 19_456;
                s.PasswordHashIterations = 12;
                s.PasswordHashMemoryBudgetMb = 19;          // exactly one hash at a time
                s.PasswordHashQueueTimeoutSeconds = 1;
                s.AuthRateLimitPerMinute = 1000;            // not the limiter under test
            })));

        var shortname = Unique("bp");
        await SeedUserAsync(f.Services, shortname, Password);

        try
        {
            // Wrong password on purpose: a correct one would also rehash, and
            // this is about the budget, not about the upgrade path.
            var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            {
                var client = f.CreateClient();
                return await client.PostAsJsonAsync("/user/login",
                    new UserLoginRequest(shortname, null, null, "WrongPass0000", null),
                    DmartJsonContext.Default.UserLoginRequest);
            })));

            var busy = responses.Where(r => r.StatusCode == HttpStatusCode.ServiceUnavailable).ToList();
            busy.ShouldNotBeEmpty(
                "20 concurrent logins against a one-hash budget with a 1s queue timeout "
                + "must shed load rather than queue without bound");

            // Retry-After is the contract that makes a 503 actionable.
            var retryAfter = busy[0].Headers.RetryAfter;
            retryAfter.ShouldNotBeNull("a 503 from the hashing budget must carry Retry-After");
            retryAfter!.Delta!.Value.TotalSeconds.ShouldBeGreaterThanOrEqualTo(1);

            // ...and the body is the same failure envelope every other error uses.
            var body = await busy[0].Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
            body!.Status.ShouldBe(Status.Failed);
            body.Error!.Code.ShouldBe(InternalErrorCode.PASSWORD_HASHING_BUSY);

            // Whatever was NOT shed still got a real answer — 401 for the wrong
            // password. Backpressure must not corrupt the requests it admits.
            var admitted = responses.Where(r => r.StatusCode != HttpStatusCode.ServiceUnavailable).ToList();
            admitted.ShouldNotBeEmpty("shedding everything would be a broken endpoint, not backpressure");
            admitted.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Unauthorized);

            foreach (var r in responses) r.Dispose();
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(f.Services, shortname);
        }
    }

    [FactIfPg]
    public async Task A_Generous_Budget_Serves_Every_Concurrent_Login()
    {
        // The complement, and the actual goal of the change: with room to queue,
        // a burst that used to OOM now simply takes longer. Nothing is shed.
        var f = factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s =>
            {
                s.PasswordHashMemoryKb = 19_456;
                s.PasswordHashMemoryBudgetMb = 19;      // still one at a time...
                s.PasswordHashQueueTimeoutSeconds = 60; // ...but with time to wait
                s.AuthRateLimitPerMinute = 1000;
            })));

        var shortname = Unique("bpok");
        await SeedUserAsync(f.Services, shortname, Password);

        try
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                var client = f.CreateClient();
                return await client.PostAsJsonAsync("/user/login",
                    new UserLoginRequest(shortname, null, null, Password, null),
                    DmartJsonContext.Default.UserLoginRequest);
            })));

            responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK,
                "all three logins must succeed — queued, not refused, not fatal");
            foreach (var r in responses) r.Dispose();
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(f.Services, shortname);
        }
    }

    // ---------------- helpers ----------------

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private static async Task SeedUserAsync(IServiceProvider services, string shortname, string password)
    {
        var users = services.GetRequiredService<UserRepository>();
        var hasher = services.GetRequiredService<Dmart.Auth.PasswordHasher>();
        await users.UpsertAsync(new Dmart.Models.Core.User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = await hasher.HashAsync(password),
            Type = UserType.Web,
            Language = Language.En,
            Roles = new() { "super_admin" },
            Groups = new(),
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        });
    }
}
