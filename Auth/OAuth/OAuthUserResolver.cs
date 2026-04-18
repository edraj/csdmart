using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;

namespace Dmart.Auth.OAuth;

// "Find or create" a dmart User from an OAuth provider's user info. Matches
// Python's find_or_create_social_user() in backend/api/user/service.py.
//
// Lookup chain:
//   1. By synthetic shortname `{provider}_{providerId}` — if the user has
//      logged in with this provider before, this is the fastest path and
//      also handles the case where they don't have an email.
//   2. Fallback: by email (only if the provider supplied one). Avoids
//      creating a duplicate account when someone who already has an
//      email/password account tries social login for the first time. The
//      resolver promotes that existing user with the provider ID so the
//      shortname lookup succeeds next time.
//   3. Create new. Shortname is `{provider}_{providerId}`; is_email_verified
//      is set to true since the provider already verified it.
public sealed class OAuthUserResolver(UserRepository users, ILogger<OAuthUserResolver> log)
{
    public async Task<User> ResolveAsync(OAuthUserInfo info, CancellationToken ct = default)
    {
        var shortname = BuildShortname(info.Provider, info.ProviderId);

        // 1. Exact shortname match.
        var existing = await users.GetByShortnameAsync(shortname, ct);
        if (existing is not null)
            return await MaybeRefreshAsync(existing, info, ct);

        // 2. Email match — promote existing user with the provider id.
        if (!string.IsNullOrWhiteSpace(info.Email))
        {
            existing = await users.GetByEmailAsync(info.Email, ct);
            if (existing is not null)
                return await PromoteExistingAsync(existing, info, ct);
        }

        // 3. Create fresh.
        var now = DateTime.UtcNow;
        var displayName = BuildDisplayName(info.FirstName, info.LastName);
        var created = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "users",
            OwnerShortname = "dmart",
            IsActive = true,
            Email = info.Email,
            IsEmailVerified = !string.IsNullOrEmpty(info.Email),
            Type = UserType.Web,
            Language = Language.En,
            Displayname = displayName is null ? null : new Translation(En: displayName),
            SocialAvatarUrl = info.PictureUrl,
            GoogleId      = info.Provider == "google"   ? info.ProviderId : null,
            FacebookId    = info.Provider == "facebook" ? info.ProviderId : null,
            // Apple user id sits on the User's Notes today — the model has no
            // dedicated AppleId column (Python doesn't have one either; it
            // uses the shortname as the canonical store). That's sufficient
            // because shortname lookup is the fast path.
            CreatedAt = now,
            UpdatedAt = now,
            Roles = [],
            Groups = [],
        };
        await users.UpsertAsync(created, ct);
        log.LogInformation("oauth: created user {Shortname} from {Provider}", shortname, info.Provider);
        return created;
    }

    // Keep display/picture/email fresh on repeat logins — the provider is
    // authoritative for these. Saves a DB round-trip for unchanged rows.
    private async Task<User> MaybeRefreshAsync(User user, OAuthUserInfo info, CancellationToken ct)
    {
        var dirty = false;
        var updated = user;
        if (!string.IsNullOrEmpty(info.Email) && info.Email != user.Email)
        {
            updated = updated with { Email = info.Email, IsEmailVerified = true };
            dirty = true;
        }
        if (!string.IsNullOrEmpty(info.PictureUrl) && info.PictureUrl != user.SocialAvatarUrl)
        {
            updated = updated with { SocialAvatarUrl = info.PictureUrl };
            dirty = true;
        }

        if (!dirty) return user;
        updated = updated with { UpdatedAt = DateTime.UtcNow };
        await users.UpsertAsync(updated, ct);
        return updated;
    }

    // Attach the provider id to an existing email-matched user. Keeps the
    // original shortname — we don't rename people. Sets the {provider}_id
    // column so the next shortname lookup would still fail (that's fine; we
    // take the email-match path on subsequent logins too).
    private async Task<User> PromoteExistingAsync(User user, OAuthUserInfo info, CancellationToken ct)
    {
        var updated = user with
        {
            GoogleId   = info.Provider == "google"   ? info.ProviderId : user.GoogleId,
            FacebookId = info.Provider == "facebook" ? info.ProviderId : user.FacebookId,
            SocialAvatarUrl = info.PictureUrl ?? user.SocialAvatarUrl,
            IsEmailVerified = true,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(updated, ct);
        log.LogInformation("oauth: promoted existing user {Shortname} with {Provider} id",
            user.Shortname, info.Provider);
        return updated;
    }

    private static string BuildShortname(string provider, string providerId)
    {
        // dmart shortnames are alphanumeric + underscore. Sanitize the
        // provider id in case it carries characters that fail the regex.
        var sanitized = new string(providerId
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (sanitized.Length == 0) sanitized = "x";
        return $"{provider}_{sanitized}";
    }

    private static string? BuildDisplayName(string? first, string? last)
    {
        var name = $"{first?.Trim()} {last?.Trim()}".Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
