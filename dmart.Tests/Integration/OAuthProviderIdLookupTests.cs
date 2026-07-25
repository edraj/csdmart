using Dmart.Auth.OAuth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// The provider id (google_id / facebook_id / apple_id) is the identity an OAuth
// provider actually authenticates, so it has to be both PERSISTED when an
// account is first linked and READ BACK on subsequent logins.
//
// Neither happened. The columns were written into the in-memory record at the
// resolver's call site, but MaybeRefreshAsync's `dirty` flag only tracked email
// and picture — so an otherwise-unchanged row returned early and never reached
// UpsertAsync. And no query anywhere in the codebase filtered on those columns,
// so even a persisted link was never consulted: every login re-resolved through
// the email. An account whose provider stopped asserting a verified email
// therefore lost access to an account it had already linked.
//
// Both tests deliberately use a shortname that is NOT the synthetic
// `{provider}_{id}` form, so the exact-shortname path can't satisfy them.
public sealed class OAuthProviderIdLookupTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public OAuthProviderIdLookupTests(DmartFactory factory) => _factory = factory;

    private static User NewUser(string shortname, string? email, string? googleId = null) => new()
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

    // First link (matched by email) must write the provider id to the DB.
    // PictureUrl is deliberately null: it was the incidental picture change that
    // used to mark the row dirty and get the id persisted by accident. With no
    // picture there is nothing else to save, which is exactly the case that
    // silently dropped the link — and the case Apple always hits, since
    // AppleProvider never supplies a PictureUrl.
    [FactIfPg]
    public async Task Email_Link_Persists_The_Provider_Id()
    {
        var resolver = _factory.Services.GetRequiredService<OAuthUserResolver>();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..10];
        var providerId = $"sub{stamp}";
        var user = NewUser($"pid_{stamp}", email: $"pid_{stamp}@test.local");

        try
        {
            await users.UpsertAsync(user);

            var info = new OAuthUserInfo("google", providerId, user.Email, "A", "B", null);
            var resolved = await resolver.ResolveAsync(info);

            resolved.ShouldNotBeNull();
            resolved!.Shortname.ShouldBe(user.Shortname);

            // Re-read: the link must be on the row, not just on the returned object.
            var stored = await users.GetByShortnameAsync(user.Shortname);
            stored.ShouldNotBeNull();
            stored!.GoogleId.ShouldBe(providerId,
                "the provider id must be persisted on first link, not just attached in memory");
        }
        finally
        {
            try { await users.DeleteAsync(user.Shortname); } catch { }
        }
    }

    // A already-linked account must resolve from the provider id alone. The
    // OAuthUserInfo carries NO email, so the email path cannot answer this and
    // the shortname path cannot either — only a lookup against google_id can.
    [FactIfPg]
    public async Task Linked_Account_Resolves_By_Provider_Id_Without_An_Email()
    {
        var resolver = _factory.Services.GetRequiredService<OAuthUserResolver>();
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..10];
        var providerId = $"sub{stamp}";
        var user = NewUser($"pid_{stamp}", email: $"pid_{stamp}@test.local", googleId: providerId);

        try
        {
            await users.UpsertAsync(user);

            var info = new OAuthUserInfo("google", providerId, Email: null,
                FirstName: "A", LastName: "B", PictureUrl: null);
            var resolved = await resolver.ResolveAsync(info);

            resolved.ShouldNotBeNull(
                "a linked account must resolve from its provider id even when the provider sends no email");
            resolved!.Shortname.ShouldBe(user.Shortname);
        }
        finally
        {
            try { await users.DeleteAsync(user.Shortname); } catch { }
        }
    }

    // An unknown provider name must not reach SQL — ProviderIdColumn is a closed
    // whitelist precisely because its result is interpolated into the query.
    [FactIfPg]
    public async Task Unknown_Provider_Never_Reaches_The_Query()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();

        UserRepository.ProviderIdColumn("google").ShouldBe("google_id");
        UserRepository.ProviderIdColumn("facebook").ShouldBe("facebook_id");
        UserRepository.ProviderIdColumn("apple").ShouldBe("apple_id");
        UserRepository.ProviderIdColumn("twitter").ShouldBeNull();
        UserRepository.ProviderIdColumn("google_id\"; DROP TABLE users; --").ShouldBeNull();

        (await users.GetByProviderIdAsync("twitter", "whatever")).ShouldBeNull();
        (await users.GetByProviderIdAsync("google", "")).ShouldBeNull();
    }
}
