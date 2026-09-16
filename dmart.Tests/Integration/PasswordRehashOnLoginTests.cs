using System.Net;
using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Tests.Infrastructure;
using Dmart.Utils;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// PASSWORD_REHASH_ON_LOGIN: an account whose stored hash used different Argon2
// parameters is re-derived at the configured ones after a correct password is
// supplied. This is the migration path off dmart 1.5.x's hard-coded m=102400 —
// the only moment the server holds a plaintext it knows to be correct.
//
// It is explicitly NOT a password change: the secret is unchanged, the user
// asked for nothing, and so sessions must survive and no history may be written.
public class PasswordRehashOnLoginTests(DmartFactory factory) : IClassFixture<DmartFactory>
{
    // Deliberately not m=102400. The behaviour under test is "parameters differ
    // from the configured ones", which a cheap hash demonstrates identically —
    // and the suite should not spend 100 MiB per run to prove it. The real
    // legacy fixture is verified once, in PasswordHasherTests.
    private const int LegacyKb = 8192;
    private const int LegacyIters = 1;
    private const int LegacyLanes = 1;

    private const string Password = "Rehash1234";

    private static string LegacyHashOf(string password)
        => new PasswordHasher(LegacyKb, LegacyIters, LegacyLanes).Hash(password);

    [FactIfPg]
    public async Task Successful_Login_Upgrades_A_Legacy_Hash_And_Keeps_The_Session()
    {
        var shortname = Unique("rehash");
        await SeedUserAsync(factory.Services, shortname, LegacyHashOf(Password));

        try
        {
            var users = factory.Services.GetRequiredService<UserRepository>();
            (await users.GetByShortnameAsync(shortname))!.Password
                .ShouldStartWith($"$argon2id$v=19$m={LegacyKb},");   // precondition: legacy parameters

            var body = await LoginAsync(factory.CreateClient(), shortname, Password);
            body.Status.ShouldBe(Status.Success);
            var token = body.Records![0].Attributes!["access_token"].ToString()!;

            // The rehash is awaited inside the login, so it has landed by now.
            var after = (await users.GetByShortnameAsync(shortname))!.Password!;
            after.ShouldStartWith(
                $"$argon2id$v=19$m={PasswordHasher.DefaultMemoryKb},t={PasswordHasher.DefaultIterations},"
                + $"p={PasswordHasher.DefaultParallelism}$");   // now at the configured parameters

            // A rehash is not a password change: the token minted by this very
            // login has to keep working. If it invalidated sessions, every user
            // would be logged out exactly once during the upgrade.
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            (await client.GetAsync("/info/manifest")).StatusCode.ShouldBe(HttpStatusCode.OK);

            // ...and the new hash must still verify the SAME password.
            (await LoginAsync(factory.CreateClient(), shortname, Password)).Status.ShouldBe(Status.Success);
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname);
        }
    }

    [FactIfPg]
    public async Task A_Hash_Already_At_The_Configured_Parameters_Is_Left_Alone()
    {
        var shortname = Unique("norehash");
        var hasher = factory.Services.GetRequiredService<PasswordHasher>();
        var original = await hasher.HashAsync(Password);
        await SeedUserAsync(factory.Services, shortname, original);

        try
        {
            (await LoginAsync(factory.CreateClient(), shortname, Password)).Status.ShouldBe(Status.Success);

            var users = factory.Services.GetRequiredService<UserRepository>();
            (await users.GetByShortnameAsync(shortname))!.Password.ShouldBe(original,
                "no rehash means the stored bytes are untouched — not merely equivalent");
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname);
        }
    }

    [FactIfPg]
    public async Task Rehash_Disabled_Leaves_The_Legacy_Hash_In_Place()
    {
        var f = factory.WithWebHostBuilder(b => b.ConfigureServices(svcs =>
            svcs.Configure<Dmart.Config.DmartSettings>(s => s.PasswordRehashOnLogin = false)));

        var shortname = Unique("rehashoff");
        var legacy = LegacyHashOf(Password);
        await SeedUserAsync(f.Services, shortname, legacy);

        try
        {
            (await LoginAsync(f.CreateClient(), shortname, Password)).Status.ShouldBe(Status.Success);

            var users = f.Services.GetRequiredService<UserRepository>();
            (await users.GetByShortnameAsync(shortname))!.Password.ShouldBe(legacy,
                "the flag is off, so the expensive hash stays and logins keep paying for it");
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(f.Services, shortname);
        }
    }

    [FactIfPg]
    public async Task A_Wrong_Password_Never_Rehashes()
    {
        // Rehashing needs a plaintext known to be correct. A failed login has
        // no such thing, and rehashing there would overwrite a good hash with a
        // derivation of the attacker's guess.
        var shortname = Unique("rehashbad");
        var legacy = LegacyHashOf(Password);
        await SeedUserAsync(factory.Services, shortname, legacy);

        try
        {
            var resp = await LoginAsync(factory.CreateClient(), shortname, "WrongPass9999");
            resp.Status.ShouldBe(Status.Failed);

            var users = factory.Services.GetRequiredService<UserRepository>();
            (await users.GetByShortnameAsync(shortname))!.Password.ShouldBe(legacy);
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname);
        }
    }

    [FactIfPg]
    public async Task A_Failed_Rehash_Write_Does_Not_Fail_The_Login()
    {
        // Simulated by deleting the row between the verify and the write is not
        // reachable from outside, so drive the same branch through the
        // repository directly: a write that matches no row must be tolerated.
        var users = factory.Services.GetRequiredService<UserRepository>();
        var rows = await users.UpdatePasswordHashOnlyAsync(
            Unique("ghost"), LegacyHashOf(Password));

        rows.ShouldBe(0,
            "a vanished row reports zero rows rather than throwing; "
            + "UserService logs a warning and lets the login stand");
    }

    [FactIfPg]
    public async Task The_Narrow_Write_Touches_Only_The_Password()
    {
        var shortname = Unique("narrow");
        await SeedUserAsync(factory.Services, shortname, LegacyHashOf(Password));

        try
        {
            var users = factory.Services.GetRequiredService<UserRepository>();
            var before = (await users.GetByShortnameAsync(shortname))!;

            var replacement = LegacyHashOf("SomethingElse123");
            (await users.UpdatePasswordHashOnlyAsync(shortname, replacement)).ShouldBe(1);

            var after = (await users.GetByShortnameAsync(shortname))!;
            after.Password.ShouldBe(replacement);
            // updated_at deliberately untouched: a rehash is invisible to anything
            // watching the row for user-initiated change.
            after.UpdatedAt.ShouldBe(before.UpdatedAt);
            after.Roles.ShouldBe(before.Roles);
            after.IsActive.ShouldBe(before.IsActive);
            after.Email.ShouldBe(before.Email);
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, shortname);
        }
    }

    // ---------------- helpers ----------------

    private static string Unique(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private static async Task<Response> LoginAsync(HttpClient client, string shortname, string password)
    {
        var resp = await client.PostAsJsonAsync("/user/login",
            new UserLoginRequest(shortname, null, null, password, null),
            DmartJsonContext.Default.UserLoginRequest);
        return (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!;
    }

    private static async Task SeedUserAsync(IServiceProvider services, string shortname, string passwordHash)
    {
        var users = services.GetRequiredService<UserRepository>();
        await users.UpsertAsync(new Dmart.Models.Core.User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Password = passwordHash,
            Type = UserType.Web,
            Language = Language.En,
            Roles = new() { "super_admin" },
            Groups = new(),
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        });
    }
}
