using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Dmart.Tests.Infrastructure;
using Dmart.Utils;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// An account imported from LDAP with an {SSHA} hash (`dmart import-ldif`)
// signs in with the password it always had, by /user/login or by an LDAP bind,
// and the first success replaces the hash with Argon2id, whatever
// PasswordRehashOnLogin says: a fast salted SHA-1 is not kept a login longer
// than it has to be.
public sealed class LegacyHashUpgradeTests(DmartFactory factory) : IClassFixture<DmartFactory>
{
    private const string Password = "Imported12345";

    private static string Ssha(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(8);
        var digest = SHA1.HashData([.. Encoding.UTF8.GetBytes(password), .. salt]);
        return "{SSHA}" + Convert.ToBase64String([.. digest, .. salt]);
    }

    private async Task<string> CreateAsync()
    {
        var sn = $"lgh{Guid.NewGuid():N}"[..14];
        await factory.Services.GetRequiredService<UserRepository>().UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = sn, SpaceName = "management", Subpath = "/users",
            OwnerShortname = sn, IsActive = true, Password = Ssha(Password), Type = UserType.Web,
            Language = Language.En, Roles = new(), Groups = new(), CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });
        return sn;
    }

    private async Task<string?> StoredHashAsync(string sn)
    {
        var db = factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command("SELECT password FROM users WHERE shortname = $1");
        DbParams.Add(cmd, sn);
        return await cmd.ExecuteScalarAsync() as string;
    }

    [FactIfPg]
    public async Task A_Login_Accepts_The_Imported_Hash_And_Replaces_It()
    {
        var sn = await CreateAsync();
        try
        {
            var client = factory.CreateClient();
            var wrong = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(sn, null, null, "Wrong12345", null), DmartJsonContext.Default.UserLoginRequest);
            wrong.StatusCode.ShouldNotBe(HttpStatusCode.OK);
            (await StoredHashAsync(sn))!.ShouldStartWith("{SSHA}");

            var users = factory.Services.GetRequiredService<UserRepository>();
            var before = (await users.GetByShortnameAsync(sn))!.UpdatedAt;
            var ok = await client.PostAsJsonAsync("/user/login",
                new UserLoginRequest(sn, null, null, Password, null), DmartJsonContext.Default.UserLoginRequest);
            ok.StatusCode.ShouldBe(HttpStatusCode.OK);
            // A change a directory replica must see, unlike an ordinary rehash.
            (await users.GetByShortnameAsync(sn))!.UpdatedAt.ShouldBeGreaterThan(before);
            var stored = await StoredHashAsync(sn);
            stored!.ShouldStartWith("$argon2id$");
            factory.Services.GetRequiredService<PasswordHasher>().Verify(Password, stored).ShouldBeTrue();
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, sn);
        }
    }

    [FactIfPg]
    public async Task A_Directory_Bind_Does_The_Same()
    {
        var sn = await CreateAsync();
        try
        {
            var svc = factory.Services.GetRequiredService<UserService>();
            (await svc.VerifyDirectoryBindAsync(sn, "Wrong12345")).WrongPassword.ShouldBeTrue();
            (await svc.VerifyDirectoryBindAsync(sn, Password)).User.ShouldNotBeNull();
            (await StoredHashAsync(sn))!.ShouldStartWith("$argon2id$");
            (await svc.VerifyDirectoryBindAsync(sn, Password)).User.ShouldNotBeNull("and the new hash verifies");
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, sn);
        }
    }
}
