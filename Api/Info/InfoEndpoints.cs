namespace Dmart.Api.Info;

// Deliberate divergence from Python dmart, in both directions — worth stating
// because everything else in this file tree tracks upstream closely.
//
//   * Upstream gates every /info route on plain JWTBearer()
//     (backend/api/info/router.py) — authentication, no role floor. We add
//     GlobalAdminFilter on the group in Program.cs, because these routes
//     answer "how is this server configured, what is loaded, what version" —
//     reconnaissance material that a self-registered user has no business
//     reading. /info/settings in particular returns the whole settings
//     snapshot; redaction there is a denylist, and a denylist is the wrong
//     thing to have standing between an ordinary user and every secret.
//   * Upstream HAS GET /info/me (router.py:51, JWT-gated). Ours was
//     AllowAnonymous — MORE permissive than upstream — and is now removed
//     entirely in favour of /user/profile, which returns the caller's actual
//     user record rather than {shortname, authenticated}. Callers that used it
//     as an anonymous "am I signed in" probe should check for a stored token
//     locally and call /user/profile only when they have one; both SPAs in
//     this repo now do exactly that.
public static class InfoEndpoints
{
    public static RouteGroupBuilder MapInfo(this RouteGroupBuilder g)
    {
        SettingsHandler.Map(g);
        ManifestHandler.Map(g);
        PluginsHandler.Map(g);
        return g;
    }
}
