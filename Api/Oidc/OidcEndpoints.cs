using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dmart.Auth;
using Dmart.Auth.Oidc;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Dmart.Utils;
using Microsoft.Extensions.Options;
using DmartUser = Dmart.Models.Core.User;

namespace Dmart.Api.Oidc;

// dmart as an OpenID Connect provider (docs/oidc-provider.md): the
// authorization code flow, for the relying parties listed in OidcClientsFile.
// It is what lets the suite drop Dex: Matrix's MAS, Gitea and the rest sign
// users in against dmart directly.
//
//   GET  /.well-known/openid-configuration   discovery
//   GET  /oidc/jwks                          the signing key
//   GET  /oidc/authorize                     single sign-on, or the sign-in form
//   POST /oidc/authorize                     the sign-in form's submission
//   POST /oidc/token                         code -> ID token + access token
//   GET|POST /oidc/userinfo                  claims, for the access token
//
// Separate from /oauth/* (the MCP authorization server): different clients
// (configured, not self-registered), different tokens (RS256 JWTs a relying
// party can verify but not mint, never a dmart session token), and it is on
// whenever OidcIssuer is set, whatever EnableMcp says.
public static class OidcEndpoints
{
    private const string FormCookieName = "dmart_oidc_form";
    private static readonly string[] SupportedScopes = ["openid", "profile", "email", "phone", "groups"];
    private static readonly string[] SupportedClaims =
    [
        "sub", "iss", "aud", "exp", "iat", "auth_time", "nonce", "azp",
        "name", "preferred_username", "locale", "updated_at",
        "email", "email_verified", "phone_number", "phone_number_verified", "groups",
    ];

    public static IEndpointRouteBuilder MapOidc(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/openid-configuration", Discovery).WithTags("OIDC");
        var g = app.MapGroup("/oidc").WithTags("OIDC");
        g.MapGet("/jwks", (OidcSigningKey key) => Json(w => key.WriteJwks(w)));
        // Public, but every request may mint a code: metered per address.
        g.MapGet("/authorize", AuthorizeGetAsync).RequireRateLimiting("public-by-ip");
        // A sign-in attempt, rate-limited like /user/login.
        g.MapPost("/authorize", AuthorizePostAsync).RequireRateLimiting("auth-by-ip");
        g.MapPost("/token", TokenAsync);
        g.MapGet("/userinfo", UserInfoAsync);
        g.MapPost("/userinfo", UserInfoAsync);
        return app;
    }

    // The issuer exactly as configured: what `iss` must equal, character for
    // character, in discovery, tokens and responses (OpenID Connect Discovery
    // §4.3). Endpoint URLs are built from it without its trailing slash.
    private static string Issuer(DmartSettings s) => s.OidcIssuer;
    private static string BaseUrl(DmartSettings s) => s.OidcIssuer.TrimEnd('/');

    // ---- discovery ----------------------------------------------------------

    private static JsonBody Discovery(IOptions<DmartSettings> settings)
    {
        var iss = Issuer(settings.Value);
        return Json(w =>
        {
            w.WriteStartObject();
            var url = BaseUrl(settings.Value);
            w.WriteString("issuer", iss);
            w.WriteString("authorization_endpoint", url + "/oidc/authorize");
            w.WriteString("token_endpoint", url + "/oidc/token");
            w.WriteString("userinfo_endpoint", url + "/oidc/userinfo");
            w.WriteString("jwks_uri", url + "/oidc/jwks");
            Array(w, "response_types_supported", "code");
            Array(w, "response_modes_supported", "query");
            Array(w, "grant_types_supported", "authorization_code");
            Array(w, "subject_types_supported", "public");
            Array(w, "id_token_signing_alg_values_supported", "RS256");
            Array(w, "scopes_supported", SupportedScopes);
            Array(w, "token_endpoint_auth_methods_supported", "client_secret_basic", "client_secret_post", "none");
            Array(w, "code_challenge_methods_supported", "S256");
            Array(w, "claims_supported", SupportedClaims);
            w.WriteBoolean("authorization_response_iss_parameter_supported", true);
            // Both default to true when absent, which would promise request
            // objects this provider does not read.
            w.WriteBoolean("request_parameter_supported", false);
            w.WriteBoolean("request_uri_parameter_supported", false);
            w.WriteEndObject();
        });
    }

    // ---- authorize ----------------------------------------------------------

    private sealed record AuthRequest(
        string? ResponseType, string? ClientId, string? RedirectUri, string? Scope, string? State,
        string? Nonce, string? CodeChallenge, string? CodeChallengeMethod, string? Prompt, string? MaxAge,
        string? LoginHint)
    {
        public static AuthRequest From(Func<string, string?> get) => new(
            get("response_type"), get("client_id"), get("redirect_uri"), get("scope"), get("state"),
            get("nonce"), get("code_challenge"), get("code_challenge_method"), get("prompt"), get("max_age"),
            get("login_hint"));

        public HashSet<string> Prompts => (Prompt ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }

    // A request that passed validation: the client, and the scopes it may have.
    private sealed record Checked(OidcClientConfig Client, AuthRequest Request, string Scope);

    // Either a checked request or the response that ends it. Errors that make
    // the redirect URI untrustworthy (an unknown client, an unregistered URI)
    // are shown here and never redirected (RFC 6749 §4.1.2.1); every other one
    // goes back to the client.
    private static (Checked? Ok, IResult? Fail) Check(AuthRequest r, OidcClients clients, DmartSettings s)
    {
        var client = clients.Find(r.ClientId);
        if (client is null)
            return (null, Page(400, "Unknown application",
                "This sign-in link names an application this server does not know."));
        if (string.IsNullOrEmpty(r.RedirectUri) || !client.RedirectUris.Contains(r.RedirectUri, StringComparer.Ordinal))
            return (null, Page(400, "Unknown return address",
                "This sign-in link would send you somewhere the application has not registered."));

        IResult Back(string error, string description) => Redirect(r.RedirectUri, s, r.State, error: error, description: description);

        if (r.ResponseType != "code")
            return (null, Back("unsupported_response_type", "only the authorization code flow (response_type=code) is supported"));
        var requested = (r.Scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!requested.Contains("openid", StringComparer.Ordinal))
            return (null, Back("invalid_scope", "the openid scope is required"));
        var scope = string.Join(' ', SupportedScopes.Where(requested.Contains));
        if (r.CodeChallenge is not null)
        {
            // Absent means `plain` (RFC 7636 §4.3), which proves nothing to
            // anyone who saw the authorization request.
            if (r.CodeChallengeMethod != "S256")
                return (null, Back("invalid_request", "code_challenge_method must be S256"));
            if (r.CodeChallenge.Length is < 43 or > 128)
                return (null, Back("invalid_request", "code_challenge must be 43-128 characters"));
        }
        else if (OidcClients.IsPublic(client))
        {
            return (null, Back("invalid_request", "a public client must use PKCE (code_challenge)"));
        }
        if (r.Nonce is { Length: > 512 } || r.State is { Length: > 2048 })
            return (null, Back("invalid_request", "nonce or state is too long"));
        if (r.Prompts.Contains("none") && r.Prompts.Count > 1)
            return (null, Back("invalid_request", "prompt=none cannot be combined with other values"));
        if (r.MaxAge is not null && !long.TryParse(r.MaxAge, out _))
            return (null, Back("invalid_request", "max_age must be a number of seconds"));
        return (new Checked(client, r, scope), null);
    }

    private static async Task<IResult> AuthorizeGetAsync(HttpContext http, OidcClients clients, OidcCodeStore codes,
        JwtIssuer jwt, UserRepository users, IOptions<DmartSettings> settings, CancellationToken ct)
    {
        var s = settings.Value;
        var (ok, fail) = Check(AuthRequest.From(k => http.Request.Query[k].FirstOrDefault()), clients, s);
        if (ok is null) return fail!;
        var r = ok.Request;

        // Single sign-on: a browser already signed in to dmart is not asked
        // again, unless the client insists (prompt=login) or the sign-in is
        // older than it accepts (max_age).
        var session = r.Prompts.Contains("login") ? null : await SessionAsync(http, jwt, users, s, ct);
        // A session whose sign-in time is unknown is as old as any max_age.
        if (session is { } live && r.MaxAge is not null
            && (live.AuthTime is not { } at || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at > long.Parse(r.MaxAge)))
            session = null;

        if (session is { } signedIn)
        {
            if (signedIn.User.ForcePasswordChange) return MustChangePassword();
            return Grant(ok, signedIn.User, signedIn.AuthTime ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), codes, s);
        }
        if (r.Prompts.Contains("none"))
            return Redirect(r.RedirectUri!, s, r.State, error: "login_required", description: "no signed-in session");
        return SignInForm(http, ok, s, error: null);
    }

    private static async Task<IResult> AuthorizePostAsync(HttpContext http, OidcClients clients, OidcCodeStore codes,
        UserService userService, IOptions<DmartSettings> settings, CancellationToken ct)
    {
        var s = settings.Value;
        var form = await http.Request.ReadFormAsync(ct);
        var (ok, fail) = Check(AuthRequest.From(k => form[k].FirstOrDefault()), clients, s);
        if (ok is null) return fail!;

        // The form carries a token that must match a cookie set with it, so a
        // page elsewhere cannot post someone else's credentials through this
        // browser and sign it in as them (login CSRF).
        var formToken = form["form_token"].FirstOrDefault();
        var (cookieName, cookieOptions) = FormCookie(s);
        var cookieToken = http.Request.Cookies[cookieName];
        if (string.IsNullOrEmpty(formToken) || cookieToken is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(formToken), Encoding.UTF8.GetBytes(cookieToken)))
            return SignInForm(http, ok, s, error: "The form expired. Please sign in again.");

        var username = form["username"].FirstOrDefault()?.Trim();
        var password = form["password"].FirstOrDefault();
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return SignInForm(http, ok, s, error: "Enter your username and password.");

        // An address signs in by contact email or hosted mailbox, anything
        // else by shortname, as /user/login does.
        var request = username.Contains('@')
            ? new UserLoginRequest(Shortname: null, Email: username, Msisdn: null, Password: password)
            : new UserLoginRequest(Shortname: username, Email: null, Msisdn: null, Password: password);
        // The headers /user/login records with last_login, less credentials.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in http.Request.Headers)
            if (!h.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                && !h.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase))
                headers[h.Key] = h.Value.ToString();
        var result = await userService.LoginAsync(request, headers, ct);
        // A bot's credential is a machine's; it signs in to no application.
        // Same message as a wrong password, and no browser session.
        if (!result.IsOk || result.Value.User.Type == UserType.Bot)
            // One message for every failure, as /user/login gives one code.
            return SignInForm(http, ok, s, error: "Sign-in failed. Check your username and password.");

        var (access, _, user, _) = result.Value;
        // The same browser session /user/login opens, so the next application
        // that sends this browser here signs in without the form.
        http.Response.Cookies.Append("auth_token", access, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromSeconds(s.JwtAccessExpires),
            Path = "/",
        });
        http.Response.Cookies.Delete(cookieName, cookieOptions);

        if (user.ForcePasswordChange) return MustChangePassword();
        return Grant(ok, user, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), codes, s);
    }

    // A temporary password set by an administrator is for changing, not for
    // signing in to every other application with: not through the form, and
    // not through a session opened with it either.
    private static WithHeader MustChangePassword() => Page(403, "Change your password first",
        "Your password was set by an administrator. Sign in to dmart and choose your own, then try again.");

    // The sign-in form's anti-CSRF cookie. Over HTTPS (the issuer's scheme,
    // which is what the browser sees behind a proxy) a __Host- cookie: Secure,
    // Path=/, no Domain, so nothing on a sibling host can set or shadow it.
    // Over plain HTTP, scoped to the provider's path as the browser sees it,
    // which is the issuer's: a proxy may mount dmart under a prefix.
    private static (string Name, CookieOptions Options) FormCookie(DmartSettings s)
    {
        var https = s.OidcIssuer.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var path = https ? "/"
            : (Uri.TryCreate(s.OidcIssuer, UriKind.Absolute, out var u) ? u.AbsolutePath.TrimEnd('/') : "") + "/oidc";
        return (https ? "__Host-" + FormCookieName : FormCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = https,
            SameSite = SameSiteMode.Strict,
            Path = path,
        });
    }

    // The user is signed in: hand the client a code, or tell it no.
    private static IResult Grant(Checked ok, DmartUser user, long authTime, OidcCodeStore codes, DmartSettings s)
    {
        var r = ok.Request;
        if (!OidcClients.Admits(ok.Client, user.Services))
            return Redirect(r.RedirectUri!, s, r.State, error: "access_denied",
                description: "this account is not granted this service");
        var code = codes.Issue(user.Shortname, ok.Client.ClientId, r.RedirectUri!, ok.Scope, r.Nonce, r.CodeChallenge, authTime);
        if (code is null)
            return Redirect(r.RedirectUri!, s, r.State, error: "temporarily_unavailable",
                description: "too many sign-ins in progress; try again shortly");
        return Redirect(r.RedirectUri!, s, r.State, code: code);
    }

    // The dmart browser session, if the request carries a live one: the
    // auth_token cookie /user/login sets, valid, of a usable non-bot account,
    // with its sessions row still there (a logout or password change deletes
    // it). Read directly rather than through the bearer middleware, whose
    // cookie CSRF rule refuses cross-site requests, and every request here
    // arrives from another site by design.
    // AuthTime is the session token's iat, or null if it has none.
    private static async Task<(DmartUser User, long? AuthTime)?> SessionAsync(
        HttpContext http, JwtIssuer jwt, UserRepository users, DmartSettings s, CancellationToken ct)
    {
        var token = http.Request.Cookies["auth_token"];
        if (string.IsNullOrEmpty(token) || jwt.Validate(token, TokenUse.Access) is not { Identity.Name: { } shortname })
            return null;
        var user = await users.GetByShortnameAsync(shortname, ct);
        if (user is not { IsUsable: true } || user.Type == UserType.Bot) return null;
        var live = s.SessionInactivityTtl > 0
            ? await users.TouchSessionAsync(shortname, token, s.SessionInactivityTtl, ct)
            : await users.IsSessionValidAsync(shortname, token, ct);
        if (!live) return null;
        using var payload = JsonDocument.Parse(OidcSigningKey.FromBase64Url(token.Split('.')[1]));
        long? iat = payload.RootElement.TryGetProperty("iat", out var v) && v.TryGetInt64(out var t) ? t : null;
        return (user, iat);
    }

    // ---- token --------------------------------------------------------------

    private static async Task<IResult> TokenAsync(HttpContext http, OidcClients clients, OidcCodeStore codes,
        OidcSigningKey key, UserRepository users, IOptions<DmartSettings> settings, CancellationToken ct)
    {
        var s = settings.Value;
        if (!http.Request.HasFormContentType)
            return TokenError(400, "invalid_request", "the token request must be form-encoded");
        var form = await http.Request.ReadFormAsync(ct);

        // Client authentication: HTTP Basic (RFC 6749 §2.3.1, each half
        // form-encoded) or client_secret in the body, never both; a public
        // client sends only its client_id.
        string? clientId = form["client_id"].FirstOrDefault(), secret = form["client_secret"].FirstOrDefault();
        var basic = false;
        if (http.Request.Headers.Authorization.FirstOrDefault() is { } auth
            && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            if (secret is not null) return TokenError(400, "invalid_request", "use one client authentication method");
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
                var colon = pair.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0) return ClientError(basic: true);
                var basicId = WebUtility.UrlDecode(pair[..colon]);
                if (clientId is not null && clientId != basicId) return ClientError(basic: true);
                clientId = basicId;
                secret = WebUtility.UrlDecode(pair[(colon + 1)..]);
                basic = true;
            }
            catch (FormatException) { return ClientError(basic: true); }
        }
        var client = clients.Find(clientId);
        if (client is null) return ClientError(basic);
        if (OidcClients.IsPublic(client) ? secret is not null : !OidcClients.SecretMatches(client, secret))
            return ClientError(basic);

        if (form["grant_type"].FirstOrDefault() != "authorization_code")
            return TokenError(400, "unsupported_grant_type", "only grant_type=authorization_code is supported");
        var code = form["code"].FirstOrDefault();
        var redirectUri = form["redirect_uri"].FirstOrDefault();
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri))
            return TokenError(400, "invalid_request", "code and redirect_uri are required");

        var grant = codes.Consume(code, client.ClientId, redirectUri, form["code_verifier"].FirstOrDefault());
        if (grant is null)
            return TokenError(400, "invalid_grant", "the code is unknown, expired, already used, or was issued elsewhere");
        // Everything that admitted the user at /oidc/authorize may have changed
        // in the code's minute: deactivation, deletion, the service grant.
        var user = await users.GetByShortnameAsync(grant.UserShortname, ct);
        if (user is not { IsUsable: true } || user.Type == UserType.Bot || user.ForcePasswordChange
            || !OidcClients.Admits(client, user.Services))
            return TokenError(400, "invalid_grant", "the account can no longer sign in here");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = now + s.OidcTokenSeconds;
        var scopes = grant.Scope.Split(' ').ToHashSet(StringComparer.Ordinal);
        var idToken = key.Sign(Payload(w =>
        {
            w.WriteStartObject();
            w.WriteString("iss", Issuer(s));
            w.WriteString("sub", user.Uuid);
            w.WriteString("aud", client.ClientId);
            w.WriteString("azp", client.ClientId);
            w.WriteNumber("iat", now);
            w.WriteNumber("exp", exp);
            w.WriteNumber("auth_time", grant.AuthTime);
            if (grant.Nonce is not null) w.WriteString("nonce", grant.Nonce);
            WriteClaims(w, user, scopes);
            w.WriteEndObject();
        }), "JWT");
        // RFC 9068 shape. Its audience is the userinfo endpoint: it is not a
        // dmart session token and opens nothing else (dmart's API verifies
        // HS256 with its own secret and rejects it).
        var accessToken = key.Sign(Payload(w =>
        {
            w.WriteStartObject();
            w.WriteString("iss", Issuer(s));
            w.WriteString("sub", user.Uuid);
            w.WriteString("aud", BaseUrl(s) + "/oidc/userinfo");
            w.WriteString("client_id", client.ClientId);
            w.WriteString("scope", grant.Scope);
            w.WriteNumber("iat", now);
            w.WriteNumber("exp", exp);
            w.WriteString("jti", Guid.NewGuid().ToString("N"));
            w.WriteEndObject();
        }), "at+jwt");

        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("access_token", accessToken);
            w.WriteString("token_type", "Bearer");
            w.WriteNumber("expires_in", s.OidcTokenSeconds);
            w.WriteString("id_token", idToken);
            w.WriteString("scope", grant.Scope);
            w.WriteEndObject();
        });
    }

    private static IResult ClientError(bool basic)
    {
        var result = TokenError(401, "invalid_client", "client authentication failed");
        return basic ? new WithHeader(result, "WWW-Authenticate", "Basic realm=\"dmart\"") : result;
    }

    // ---- userinfo -----------------------------------------------------------

    private static async Task<IResult> UserInfoAsync(HttpContext http, OidcSigningKey key, UserRepository users,
        IOptions<DmartSettings> settings, CancellationToken ct)
    {
        var s = settings.Value;
        string? token = null;
        if (http.Request.Headers.Authorization.FirstOrDefault() is { } auth
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = auth[7..].Trim();
        else if (HttpMethods.IsPost(http.Request.Method) && http.Request.HasFormContentType)
            token = (await http.Request.ReadFormAsync(ct))["access_token"].FirstOrDefault();

        using var doc = token is null ? null : key.Verify(token, "at+jwt");
        var claims = doc?.RootElement;
        if (claims is not { } c
            || !c.TryGetProperty("iss", out var iss) || iss.ValueKind != JsonValueKind.String || iss.GetString() != Issuer(s)
            || !c.TryGetProperty("aud", out var aud) || aud.ValueKind != JsonValueKind.String || aud.GetString() != BaseUrl(s) + "/oidc/userinfo"
            || !c.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var e) || e < DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            || !c.TryGetProperty("sub", out var sub) || sub.ValueKind != JsonValueKind.String || sub.GetString() is not { } uuid)
            return BearerError();

        var user = await users.GetShortnameByUuidAsync(uuid, ct) is { } shortname
            ? await users.GetByShortnameAsync(shortname, ct) : null;
        if (user is not { IsUsable: true }) return BearerError();

        var scopes = (c.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() ?? "" : "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        http.Response.Headers.CacheControl = "no-store";
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("sub", user.Uuid);
            WriteClaims(w, user, scopes);
            w.WriteEndObject();
        });
    }

    private static WithHeader BearerError()
        => new WithHeader(TokenError(401, "invalid_token", "the access token is invalid or expired"),
            "WWW-Authenticate", "Bearer error=\"invalid_token\"");

    // ---- claims -------------------------------------------------------------

    // The claims a scope releases, the same in the ID token and from userinfo.
    // `email` is the address the LDAP face serves as `mail`: the hosted
    // mailbox, else the contact email.
    internal static void WriteClaims(Utf8JsonWriter w, DmartUser u, IReadOnlySet<string> scopes)
    {
        if (scopes.Contains("profile"))
        {
            w.WriteString("name", FirstNonEmpty(u.Displayname?.En, u.Displayname?.Ar, u.Displayname?.Ku) ?? u.Shortname);
            w.WriteString("preferred_username", u.Shortname);
            w.WriteString("locale", u.Language switch
            {
                Language.Ar => "ar", Language.Ku => "ku", Language.Fr => "fr", Language.Tr => "tr", _ => "en",
            });
            w.WriteNumber("updated_at", new DateTimeOffset(u.UpdatedAt).ToUnixTimeSeconds());
        }
        if (scopes.Contains("email") && (u.Mailbox ?? u.Email) is { Length: > 0 } email)
        {
            w.WriteString("email", email);
            w.WriteBoolean("email_verified", u.Mailbox is not null || u.IsEmailVerified);
        }
        if (scopes.Contains("phone") && u.Msisdn is { Length: > 0 } phone)
        {
            w.WriteString("phone_number", phone);
            w.WriteBoolean("phone_number_verified", u.IsMsisdnVerified);
        }
        if (scopes.Contains("groups"))
            Array(w, "groups", u.Groups.ToArray());
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    // ---- responses ----------------------------------------------------------

    // Back to the client: code (or error) and state, plus the issuer
    // (RFC 9207), which tells a client talking to several providers which one
    // this answer came from.
    private static IResult Redirect(string redirectUri, DmartSettings s, string? state,
        string? code = null, string? error = null, string? description = null)
    {
        var q = new StringBuilder(redirectUri);
        var sep = redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        void Add(string name, string? value)
        {
            if (value is null) return;
            q.Append(sep).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            sep = '&';
        }
        Add("code", code);
        Add("error", error);
        Add("error_description", description);
        Add("state", state);
        Add("iss", Issuer(s));
        return Results.Redirect(q.ToString());
    }

    private static JsonBody TokenError(int status, string error, string description)
        => Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("error", error);
            w.WriteString("error_description", description);
            w.WriteEndObject();
        }, status);

    private static JsonBody Json(Action<Utf8JsonWriter> write, int status = 200) => new JsonBody(Payload(write), status);

    internal sealed class JsonBody(byte[] body, int status) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = status;
            http.Response.ContentType = "application/json";
            return http.Response.Body.WriteAsync(body).AsTask();
        }
    }

    private static byte[] Payload(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) write(w);
        return ms.ToArray();
    }

    private static void Array(Utf8JsonWriter w, string name, params string[] values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    internal sealed class WithHeader(IResult inner, string name, string value) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers[name] = value;
            return inner.ExecuteAsync(http);
        }
    }

    // ---- pages --------------------------------------------------------------

    private static WithHeader SignInForm(HttpContext http, Checked ok, DmartSettings s, string? error)
    {
        var r = ok.Request;
        var formToken = OidcSigningKey.Base64Url(RandomNumberGenerator.GetBytes(24));
        var (cookieName, cookieOptions) = FormCookie(s);
        cookieOptions.MaxAge = TimeSpan.FromMinutes(15);
        http.Response.Cookies.Append(cookieName, formToken, cookieOptions);

        var app = ok.Client.Name is { Length: > 0 } n ? n : ok.Client.ClientId;
        var host = Uri.TryCreate(r.RedirectUri, UriKind.Absolute, out var u) ? u.Host : "";
        var body = new StringBuilder();
        body.Append("<h1>Sign in</h1><p class=\"sub\">to continue to <b>").Append(Html(app)).Append("</b>");
        if (host.Length > 0) body.Append(" (").Append(Html(host)).Append(')');
        body.Append("</p>");
        if (error is not null) body.Append("<div class=\"err\" role=\"alert\">").Append(Html(error)).Append("</div>");
        // Empty action: post back to wherever the proxy exposed this page.
        body.Append("<form method=\"post\" action=\"\">");
        foreach (var (name, value) in new[]
                 {
                     ("response_type", r.ResponseType), ("client_id", r.ClientId), ("redirect_uri", r.RedirectUri),
                     ("scope", r.Scope), ("state", r.State), ("nonce", r.Nonce), ("code_challenge", r.CodeChallenge),
                     ("code_challenge_method", r.CodeChallengeMethod), ("max_age", r.MaxAge), ("form_token", formToken),
                 })
            if (value is not null)
                body.Append("<input type=\"hidden\" name=\"").Append(name).Append("\" value=\"").Append(Html(value)).Append("\">");
        body.Append("<label for=\"username\">Username or email</label>")
            .Append("<input id=\"username\" name=\"username\" autocomplete=\"username\" required autofocus value=\"")
            .Append(Html(r.LoginHint ?? "")).Append("\">")
            .Append("<label for=\"password\">Password</label>")
            .Append("<input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>")
            .Append("<button type=\"submit\">Sign in</button></form>");
        return HtmlPage(200, "Sign in", body.ToString(), r.RedirectUri);
    }

    private static WithHeader Page(int status, string title, string message)
        => HtmlPage(status, title, $"<h1>{Html(title)}</h1><p class=\"sub\">{Html(message)}</p>");

    // `redirectUri`: where the page's form ends up on success, which its
    // form-action must admit (Utils/FormActionCsp).
    private static WithHeader HtmlPage(int status, string title, string body, string? redirectUri = null)
    {
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="{{Html(FormActionCsp.For(redirectUri))}}">
            <meta name="referrer" content="no-referrer">
            <title>{{Html(title)}} · dmart</title>
            <style>
              :root { color-scheme: light dark; --bg:#f6f7f9; --card:#fff; --text:#111827; --muted:#6b7280;
                      --line:#d1d5db; --accent:#2563eb; --err-bg:#fef2f2; --err:#991b1b; }
              @media (prefers-color-scheme: dark) {
                :root { --bg:#0f172a; --card:#1e293b; --text:#e5e7eb; --muted:#94a3b8; --line:#334155;
                        --accent:#3b82f6; --err-bg:#7f1d1d; --err:#fecaca; } }
              body { margin:0; min-height:100vh; display:flex; align-items:center; justify-content:center;
                     background:var(--bg); color:var(--text); font-family:system-ui,-apple-system,sans-serif; }
              main { background:var(--card); width:min(360px, calc(100vw - 32px)); padding:2rem; border-radius:12px;
                     box-shadow:0 8px 30px rgba(0,0,0,.12); box-sizing:border-box; }
              h1 { margin:0 0 .25rem; font-size:1.3rem; }
              .sub { margin:0 0 1.25rem; color:var(--muted); font-size:.9rem; overflow-wrap:anywhere; }
              label { display:block; font-size:.85rem; margin:0 0 .3rem; }
              input { width:100%; box-sizing:border-box; padding:.6rem .7rem; margin:0 0 1rem; font:inherit;
                      border:1px solid var(--line); border-radius:6px; background:var(--bg); color:var(--text); }
              button { width:100%; padding:.7rem; border:0; border-radius:6px; background:var(--accent); color:#fff;
                       font:inherit; font-weight:600; cursor:pointer; }
              .err { background:var(--err-bg); color:var(--err); padding:.55rem .75rem; border-radius:6px;
                     margin:0 0 1rem; font-size:.88rem; }
            </style>
            </head>
            <body><main>{{body}}</main></body>
            </html>
            """;
        // no-store: the sign-in form carries a one-time form token.
        return new WithHeader(Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status),
            "Cache-Control", "no-store");
    }

    private static string Html(string s) => WebUtility.HtmlEncode(s);
}
