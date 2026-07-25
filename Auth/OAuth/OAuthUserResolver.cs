using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dmart.Auth.OAuth;

// Resolve a dmart User from an OAuth provider's user info. OAuth login does
// NOT create accounts — the user must already exist in dmart.
//
// Lookup chain:
//   1. By synthetic shortname `{provider}_{providerId}` — accounts that were
//      auto-created by the legacy social-signup path carry this shortname.
//   2. By stored provider id (google_id / facebook_id / apple_id) — the
//      account was linked on an earlier login. This is the identity the
//      provider actually authenticated, so it outranks the email match below
//      and works for users with no email at all.
//   3. By email — first link only. A dmart account carrying the same address
//      adopts the provider id, so step 2 answers every subsequent login.
//   4. No match: return null. The HTTP layer turns this into a 401.
//
// Step 2 is what makes the linkage in step 3 worth writing. Before it existed
// the provider-id columns were written but never read back, so every login
// re-resolved through the email — which meant a user whose provider stopped
// asserting a verified email (see the caveat below) lost access to an account
// they had already linked.
//
// Account takeover caveat: step 2 trusts the provider to have verified the
// email it hands us. Only enable providers that enforce email verification —
// a provider that lets a user assert an arbitrary unverified email would let
// an attacker take over the matching dmart account on first OAuth login.
public sealed class OAuthUserResolver(
    UserRepository users,
    PluginManager plugins,
    IOptions<DmartSettings> settings,
    ILogger<OAuthUserResolver> log)
{
    // Python parity: api/user/router.py:61 defines USERS_SUBPATH="users" (no
    // leading slash) and passes that verbatim to every Event it dispatches in
    // this file. Plugin filter matching is case-sensitive and compares this
    // against EventFilter.subpaths exactly, so the bare "users" form is
    // load-bearing for plugin filters configured against the Python convention.
    private const string UsersSubpath = "users";

    public async Task<User?> ResolveAsync(OAuthUserInfo info, CancellationToken ct = default)
    {
        var shortname = BuildShortname(info.Provider, info.ProviderId);

        // Python parity (api/user/router.py:1337-1346): fire the before-hook
        // unconditionally at the top of find_or_create_social_user, before the
        // existence check. Plugins that care whether a create is actually about
        // to happen must check themselves. Exceptions are logged and swallowed
        // — unlike EntryService.CreateAsync, which translates a thrown before-
        // hook into a Result.Fail, Python's OAuth path doesn't let a plugin
        // block login. Don't surprise existing integrations.
        var preEvent = new Event
        {
            SpaceName = settings.Value.ManagementSpace,
            Subpath = UsersSubpath,
            Shortname = shortname,
            ActionType = ActionType.Create,
            ResourceType = ResourceType.User,
            UserShortname = shortname,
        };
        try { await plugins.BeforeActionAsync(preEvent, ct); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "oauth: before-create plugin hook threw for {Shortname}; continuing", shortname);
        }

        // 1. Exact shortname match.
        var existing = await users.GetByShortnameAsync(shortname, ct);
        if (existing is not null)
            return await MaybeRefreshAsync(existing, info, ct);

        // 2. Already linked to this provider identity.
        var byProviderId = await users.GetByProviderIdAsync(info.Provider, info.ProviderId, ct);
        if (byProviderId is not null)
            return await MaybeRefreshAsync(byProviderId, info, ct);

        // 3. First link: adopt the account carrying this (provider-verified)
        //    email. MaybeRefreshAsync attaches and PERSISTS the provider id, so
        //    step 2 resolves every login after this one.
        var byEmail = !string.IsNullOrEmpty(info.Email)
            ? await users.GetByEmailAsync(info.Email, ct)
            : null;
        if (byEmail is not null)
            return await MaybeRefreshAsync(byEmail, info, ct);
        // No dmart account matches this provider id or email. OAuth login no
        // longer auto-creates accounts — the caller turns null into a 401.
        return null;
    }

    // Keep the provider link, display picture and email fresh on repeat logins —
    // the provider is authoritative for these. Saves a DB round-trip for
    // unchanged rows.
    private async Task<User> MaybeRefreshAsync(User user, OAuthUserInfo info, CancellationToken ct)
    {
        var dirty = false;
        var updated = user;

        // Attach the provider id here rather than at the call site. Done by the
        // caller it was silently discarded: the record was modified in memory,
        // but `dirty` only tracked email and picture, so an otherwise-unchanged
        // row returned early and never hit UpsertAsync. Accounts linked by email
        // whose picture also happened to change got persisted; everyone else
        // re-resolved through the email on every single login, and Apple — which
        // never supplies a PictureUrl — never persisted a link at all.
        var storedProviderId = info.Provider switch
        {
            "google" => user.GoogleId,
            "facebook" => user.FacebookId,
            "apple" => user.AppleId,
            _ => null,
        };
        if (!string.IsNullOrEmpty(info.ProviderId) && info.ProviderId != storedProviderId)
        {
            updated = info.Provider switch
            {
                "google" => updated with { GoogleId = info.ProviderId },
                "facebook" => updated with { FacebookId = info.ProviderId },
                "apple" => updated with { AppleId = info.ProviderId },
                _ => updated,
            };
            // Unknown provider leaves the record untouched — don't mark a
            // no-op write dirty.
            if (!ReferenceEquals(updated, user)) dirty = true;
        }

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
        updated = updated with { UpdatedAt = TimeUtils.Now() };
        try
        {
            await users.UpsertAsync(updated, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            // The refreshed email (or attached provider id) collides with a
            // unique index on `users` — e.g. the provider-side email changed
            // to an address another dmart account already holds. Login
            // availability wins over profile freshness: keep the stale stored
            // values and let the login proceed instead of surfacing the
            // conflict as a failed login.
            log.LogWarning(ex,
                "oauth: skipped profile refresh for {Shortname} — refreshed value collides with another account (constraint {Constraint})",
                user.Shortname, ex.ConstraintName);
            return user;
        }
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
}
