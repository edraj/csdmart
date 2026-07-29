namespace Dmart.Api.Info;

// One deliberate divergence from Python dmart, worth stating because
// everything else in this file tree tracks upstream closely.
//
// Upstream gates every /info route on plain JWTBearer()
// (backend/api/info/router.py) — authentication, no role floor. We add
// GlobalAdminFilter on the group in Program.cs, because these routes answer
// "how is this server configured, what is loaded, what version" —
// reconnaissance material that a self-registered user has no business reading.
// /info/settings in particular returns the whole settings snapshot; redaction
// there is a denylist, and a denylist is the wrong thing to have standing
// between an ordinary user and every secret.
//
// /info/me is the exception, and matches upstream exactly: JWT-gated, returning
// {shortname}. Asking who you are is not reconnaissance, so it carries
// AllowAuthenticated and skips the role floor — see MeHandler for why it is no
// longer anonymous. Every route added here is admin-only by default; the filter
// fails closed, so opting out is a visible edit at the route's own map call.
public static class InfoEndpoints
{
    public static RouteGroupBuilder MapInfo(this RouteGroupBuilder g)
    {
        MeHandler.Map(g);
        SettingsHandler.Map(g);
        ManifestHandler.Map(g);
        PluginsHandler.Map(g);
        return g;
    }
}
