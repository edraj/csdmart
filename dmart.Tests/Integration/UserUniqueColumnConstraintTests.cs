using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the DB-level unique indexes added on `users`:
//   idx_users_google_id_unique
//   idx_users_facebook_id_unique
//   idx_users_apple_id_unique
//   idx_users_email_lower_unique   (expression index on lower(email))
//   idx_users_msisdn_unique
//
// Provider IDs (google/facebook/apple) are 1:1 by construction — one
// provider account → one provider-id-keyed dmart account — so their unique
// constraint is purely defense-in-depth. Email/msisdn uniqueness is
// primarily enforced by Services/UniquenessValidator.cs at the application
// layer; the DB index here is defense-in-depth on top of that.
//
// If a future refactor accidentally drops one of these indexes, the
// failure mode silently degrades from "409 Conflict at the wire" to
// "two rows coexist with the same identifier" — exactly the kind of
// regression that's invisible until production. These tests fail loud
// when an index is missing.
public sealed class UserUniqueColumnConstraintTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserUniqueColumnConstraintTests(DmartFactory factory) => _factory = factory;

    private async Task InsertAndAssertUniqueViolationAsync(
        UserRepository users, User first, User second, string expectedColumnHint)
    {
        await users.UpsertAsync(first);
        try
        {
            var ex = await Should.ThrowAsync<PostgresException>(() => users.UpsertAsync(second));
            ex.SqlState.ShouldBe("23505");
            // ConstraintName names the offending index; the helper at
            // PgErrorParsing.ExtractUniqueViolationKey unwraps it to the
            // column name. We assert on a hint substring rather than the
            // exact name to leave room for the constraint-name to evolve.
            (ex.ConstraintName ?? "").ShouldContain(expectedColumnHint,
                customMessage: $"23505 fired but the constraint name did not include '{expectedColumnHint}' — index may have been renamed or dropped");
        }
        finally
        {
            try { await users.DeleteAsync(first.Shortname); } catch { }
            try { await users.DeleteAsync(second.Shortname); } catch { }
        }
    }

    private static User NewUser(string shortname, string? email = null, string? msisdn = null,
        string? googleId = null, string? facebookId = null, string? appleId = null) => new()
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
        Msisdn = msisdn,
        GoogleId = googleId,
        FacebookId = facebookId,
        AppleId = appleId,
        Roles = new(), Groups = new(),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    [FactIfPg]
    public async Task Duplicate_Email_Rejected_By_DbIndex()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..10];
        var email = $"dupe_{stamp}@example.com";
        var a = NewUser($"uniq_a_{stamp}", email: email);
        var b = NewUser($"uniq_b_{stamp}", email: email);
        await InsertAndAssertUniqueViolationAsync(users, a, b, "email");
    }

    [FactIfPg]
    public async Task Duplicate_Email_DifferentCase_Rejected_By_DbIndex()
    {
        // idx_users_email_lower_unique is an expression index on
        // lower(email) — two rows differing only in case must still collide,
        // matching UserService/RequestHandler's write-time normalization.
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..10];
        var a = NewUser($"uniq_a_{stamp}", email: $"Dupe_{stamp}@Example.com");
        var b = NewUser($"uniq_b_{stamp}", email: $"dupe_{stamp}@example.com");
        await InsertAndAssertUniqueViolationAsync(users, a, b, "email");
    }

    [FactIfPg]
    public async Task Duplicate_Msisdn_Rejected_By_DbIndex()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var msisdn = "9647" + stamp.PadLeft(8, '1');
        var a = NewUser($"uniq_a_{stamp}", msisdn: msisdn);
        var b = NewUser($"uniq_b_{stamp}", msisdn: msisdn);
        await InsertAndAssertUniqueViolationAsync(users, a, b, "msisdn");
    }

    [FactIfPg]
    public async Task Duplicate_GoogleId_Rejected_By_DbIndex()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..10];
        var googleId = $"google_sub_{stamp}";
        var a = NewUser($"uniq_a_{stamp}", googleId: googleId);
        var b = NewUser($"uniq_b_{stamp}", googleId: googleId);
        await InsertAndAssertUniqueViolationAsync(users, a, b, "google_id");
    }

    [FactIfPg]
    public async Task Duplicate_AppleId_Rejected_By_DbIndex()
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..10];
        var appleId = $"apple_sub_{stamp}";
        var a = NewUser($"uniq_a_{stamp}", appleId: appleId);
        var b = NewUser($"uniq_b_{stamp}", appleId: appleId);
        await InsertAndAssertUniqueViolationAsync(users, a, b, "apple_id");
    }

    [FactIfPg]
    public async Task Both_Users_Without_Email_Coexist()
    {
        // Partial index is WHERE email IS NOT NULL — multiple users with
        // null email must coexist. Without that predicate, the unique
        // constraint would treat NULLs as distinct rows under default PG
        // semantics anyway, but the partial index makes the intent explicit
        // and the test guards against either flavour of regression.
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var stamp = Guid.NewGuid().ToString("N")[..10];
        var a = NewUser($"unnul_a_{stamp}");
        var b = NewUser($"unnul_b_{stamp}");
        try
        {
            await users.UpsertAsync(a);
            await users.UpsertAsync(b);
            (await users.GetByShortnameAsync(a.Shortname)).ShouldNotBeNull();
            (await users.GetByShortnameAsync(b.Shortname)).ShouldNotBeNull();
        }
        finally
        {
            try { await users.DeleteAsync(a.Shortname); } catch { }
            try { await users.DeleteAsync(b.Shortname); } catch { }
        }
    }
}
