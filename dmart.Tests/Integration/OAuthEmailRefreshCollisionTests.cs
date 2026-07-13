using Dmart.Auth.OAuth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// OAuth repeat-login refreshes the account's email from the provider
// (MaybeRefreshAsync). With the DB-level unique index on lower(email), that
// refresh can collide: the provider-side email changed to an address some
// OTHER dmart account already holds. Login availability wins over email
// freshness — the refresh is skipped (stale email kept) and the login
// succeeds, rather than surfacing the unique violation as a failed login.
public sealed class OAuthEmailRefreshCollisionTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OAuthEmailRefreshCollisionTests(DmartFactory factory) => _factory = factory;

    private static User NewUser(string shortname, string email, string? googleId = null) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = "management",
        Subpath = "/users",
        OwnerShortname = shortname,
        IsActive = true,
        Type = UserType.Web,
        Language = Language.En,
        Email = email,
        GoogleId = googleId,
        Roles = new(), Groups = new(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [FactIfPg]
    public async Task Provider_Email_Colliding_With_Other_Account_Does_Not_Block_Login()
    {
        var resolver = _factory.Services.GetRequiredService<OAuthUserResolver>();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..10];
        var providerId = $"collide{stamp}";
        // A is provider-keyed (shortname google_<id>) so ResolveAsync takes
        // the exact-shortname path straight into MaybeRefreshAsync.
        var a = NewUser($"google_{providerId}", email: $"a_{stamp}@test.local", googleId: providerId);
        var b = NewUser($"other_{stamp}", email: $"b_{stamp}@test.local");
        try
        {
            await users.UpsertAsync(a);
            await users.UpsertAsync(b);

            // The provider now reports B's email for A's provider account.
            var info = new OAuthUserInfo("google", providerId, b.Email, "A", "B", null);
            var resolved = await resolver.ResolveAsync(info);

            resolved.ShouldNotBeNull("login must survive an email-refresh unique collision");
            resolved!.Shortname.ShouldBe(a.Shortname);

            // The refresh was skipped, not half-applied: A keeps its stored
            // email, B is untouched.
            (await users.GetByShortnameAsync(a.Shortname))!.Email.ShouldBe(a.Email);
            (await users.GetByShortnameAsync(b.Shortname))!.Email.ShouldBe(b.Email);
        }
        finally
        {
            try { await users.DeleteAsync(a.Shortname); } catch { }
            try { await users.DeleteAsync(b.Shortname); } catch { }
        }
    }
}
