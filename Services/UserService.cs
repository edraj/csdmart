using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Plugins;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

public sealed class UserService(
    UserRepository users,
    OtpRepository otp,
    PasswordHasher hasher,
    JwtIssuer jwt,
    HistoryRepository history,
    PluginManager plugins,
    SchemaValidator schemas,
    RegexPatternsConfig regexConfig,
    IOptions<DmartSettings> settings,
    ILogger<UserService> log)
{
    // Management space name — comes from DmartSettings.ManagementSpace so the
    // caller can rename it uniformly via config. Default is "management".
    private string MgmtSpace => settings.Value.ManagementSpace;

    public Task<User?> GetByShortnameAsync(string shortname, CancellationToken ct = default)
        => users.GetByShortnameAsync(shortname, ct);

    // Python-parity password regex (utils/regex.py::PASSWORD). Requires at
    // least one digit (Latin or Arabic-Indic), one uppercase letter (Latin
    // A-Z or Arabic ا-ي), length 8-64 from a specific character class.
    private const string PasswordPattern =
        "^(?=.*[0-9\u0660-\u0669])(?=.*[A-Z\u0621-\u064a])" +
        "[a-zA-Z\u0621-\u064a0-9\u0660-\u0669 _#@%*!?$^&()+={}\\[\\]~|;:,.<>/-]{8,64}$";

    // Decoy hash for LoginAsync's miss paths. An Argon2id verify at
    // m=102400,t=3,p=8 costs 100-300ms, so a login that rejects in ~1ms
    // because the identifier resolved to nothing (or to a row with no
    // password) tells an unauthenticated caller which identifiers exist \u2014
    // purely from the clock, no matter how uniform the error body is.
    // Verifying against this throwaway makes the miss path pay what the hit
    // path pays. Hashed once at class init over a random password nobody
    // holds, so the verify can never succeed; its result is always discarded.
    //
    // SCOPE, so nobody reads this as "enumeration is solved": it closes the
    // clock for accounts that are neither locked nor deactivated. A locked
    // account still answers USER_ACCOUNT_LOCKED and a deactivated one its own
    // code, both BEFORE any hashing — so for those two states existence is
    // disclosed by the response body outright, and the timing follows. Closing
    // that means collapsing those codes into the generic
    // INVALID_USERNAME_AND_PASS, which diverges from Python and from what the
    // cxb login UI shows the user ("your account is locked" is the message a
    // legitimate locked-out user needs), so it is deliberately left open.
    private static readonly string DecoyHash =
        new PasswordHasher().Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    // Python's /user/create takes a core.Record body and returns a Record with
    // {access_token, type} — i.e. it auto-logs-in the new user. This mirrors
    // that flow:
    //   1. Validate is_registrable + email/msisdn + OTPs + password regex
    //   2. Verify OTPs via peek (Python's verify_user doesn't consume)
    //   3. Build User from rec.Attributes (email, msisdn, password, roles,
    //      displayname, description, payload, language)
    //   4. Persist + auto-login (issue access/refresh + create session row)
    public async Task<Result<(User User, string Access, string Refresh)>> CreateAsync(
        Record rec, Dictionary<string, string>? requestHeaders = null,
        CancellationToken ct = default)
    {
        var s = settings.Value;
        if (!s.IsRegistrable)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.SESSION, "Register API is disabled", ErrorTypes.Create);
        // An empty REGISTRATION_ENABLED_CHANNELS means "no self-registration"
        // — same outcome as IsRegistrable=false. Without this gate, disabling
        // both channels would silently lift the "email or msisdn required"
        // check below and open contact-less, OTP-less signup instead of
        // closing registration.
        var emailChannelEnabled = s.IsRegistrationChannelEnabled("email");
        var msisdnChannelEnabled = s.IsRegistrationChannelEnabled("msisdn");
        if (!emailChannelEnabled && !msisdnChannelEnabled)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.SESSION, "Register API is disabled", ErrorTypes.Create);

        var attrs = rec.Attributes ?? new();
        var email = ConvertToString(attrs.GetValueOrDefault("email"))?.ToLowerInvariant();
        var msisdn = ConvertToString(attrs.GetValueOrDefault("msisdn"));
        var password = ConvertToString(attrs.GetValueOrDefault("password"));
        var emailOtp = ConvertToString(attrs.GetValueOrDefault("email_otp"));
        var msisdnOtp = ConvertToString(attrs.GetValueOrDefault("msisdn_otp"));

        // Python-parity validation chain (last-match wins — mirrors the
        // sequential `validation_message = …` assignments in router.py).
        // At least one channel is enabled here (both-disabled returned
        // above), so the "required" check is unconditional; a supplied value
        // on a DISABLED channel is rejected rather than counted.
        string? validationMessage = null;
        if (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(msisdn))
            validationMessage = "Email or MSISDN is required";
        if (!string.IsNullOrEmpty(email) && !emailChannelEnabled)
            validationMessage = "Email registration is disabled";
        if (!string.IsNullOrEmpty(msisdn) && !msisdnChannelEnabled)
            validationMessage = "MSISDN registration is disabled";
        if (regexConfig.ValidateEmailFormat(email) is { } emailFormatError)
            validationMessage = emailFormatError;
        if (regexConfig.ValidateMsisdnFormat(msisdn) is { } msisdnFormatError)
            validationMessage = msisdnFormatError;
        if (!string.IsNullOrEmpty(email) && s.IsOtpForCreateRequired && string.IsNullOrEmpty(emailOtp))
            validationMessage = "Email OTP is required";
        if (!string.IsNullOrEmpty(msisdn) && s.IsOtpForCreateRequired && string.IsNullOrEmpty(msisdnOtp))
            validationMessage = "MSISDN OTP is required";
        if (!string.IsNullOrEmpty(password) && !Regex.IsMatch(password, PasswordPattern))
            validationMessage = "password dose not match required rules";
        if (validationMessage is not null)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.SESSION, validationMessage, ErrorTypes.Create);

        // Shortname conflict → SHORTNAME_ALREADY_EXIST (400). Self-registration
        // server-allocates the shortname (RegistrationHandler sends "auto") so
        // this never trips here; kept for any caller that supplies one. The
        // email/msisdn uniqueness check is deliberately deferred until AFTER OTP
        // verification below — see the anti-enumeration note there.
        if (!string.IsNullOrWhiteSpace(rec.Shortname)
            && await users.GetByShortnameAsync(rec.Shortname, ct) is not null)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.SHORTNAME_ALREADY_EXIST, "already exists", ErrorTypes.Create);

        // Capped verify-and-consume at the register purpose — the code is
        // spent on first use, so a failure after this point requires a fresh
        // OTP. Everything that CAN be checked first now is (see below), so
        // what remains after this line is the uniqueness check, which is
        // deliberately late for anti-enumeration reasons. Skipped entirely
        // when is_otp_for_create_required=false, in which case both channels
        // are treated as verified.
        var emailVerified = false;
        var payload = ExtractPayload(attrs);
        // Schema validation runs BEFORE the codes are redeemed. A payload that
        // fails its schema is a plain client-side data error and entirely
        // recoverable — but redeeming first meant the caller's still-valid
        // code was already spent when the error came back, and re-requesting
        // inside AllowOtpResendAfter returns a silent 200 Ok, so they would
        // sit on the OTP screen watching a success response and no message
        // arrive. Validation has no side effects, so it costs nothing to ask
        // first. Same reasoning as TryImplicitRegisterAsync.
        //
        // Python parity (serve_request_create): validate payload.body against
        // payload.schema_shortname before persisting. /user/create runs through
        // this service, not EntryService, so without this gate the declared
        // schema was never enforced on self-registration.
        var payloadSchemaError = await schemas.ValidatePayloadAsync(MgmtSpace, ResourceType.User, payload, ct);
        if (payloadSchemaError is not null)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.INVALID_DATA, payloadSchemaError, ErrorTypes.Request);

        var msisdnVerified = false;
        if (!string.IsNullOrEmpty(msisdn))
        {
            if (s.IsOtpForCreateRequired)
            {
                if (!await otp.VerifyAndConsumeAsync(msisdn, OtpPurpose.Register,
                        msisdnOtp ?? "", s.MaxOtpVerifyAttempts, ct))
                    return Result<(User, string, string)>.Fail(
                        InternalErrorCode.SESSION, "Invalid MSISDN OTP", ErrorTypes.Create);
            }
            msisdnVerified = true;
        }
        if (!string.IsNullOrEmpty(email))
        {
            if (s.IsOtpForCreateRequired)
            {
                if (!await otp.VerifyAndConsumeAsync(email, OtpPurpose.Register,
                        emailOtp ?? "", s.MaxOtpVerifyAttempts, ct))
                    return Result<(User, string, string)>.Fail(
                        InternalErrorCode.SESSION, "Invalid Email OTP", ErrorTypes.Create);
            }
            emailVerified = true;
        }

        // Anti-enumeration: the email/msisdn uniqueness check runs ONLY after
        // OTP verification. A caller who doesn't control the address can't
        // produce its OTP, so they get the generic "Invalid OTP" error instead
        // of a "@email:<value> already exists" existence oracle; a caller who
        // does control it only learns about their own address. (Python ran this
        // before OTP — we intentionally diverge to close the enumeration.) When
        // is_otp_for_create_required=false the OTP gate is a no-op, so the
        // surfaced DATA_SHOULD_BE_UNIQUE error is unchanged for that config.
        if (!string.IsNullOrEmpty(email) && await users.GetByEmailAsync(email, ct) is not null)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                $"Entry properties should be unique: @email:{email} ", ErrorTypes.Request);
        if (!string.IsNullOrEmpty(msisdn) && await users.GetByMsisdnAsync(msisdn, ct) is not null)
            return Result<(User, string, string)>.Fail(
                InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                $"Entry properties should be unique: @msisdn:{msisdn} ", ErrorTypes.Request);

        // Self-registration must not let a user grant themselves access: the
        // `roles`/`groups` attributes in the request body are deliberately
        // ignored here. A user cannot set nor change their own role/group.
        // Instead, when configured, every self-created user gets exactly the
        // single default role/group from settings; otherwise the list starts
        // empty. (The managed/admin create path is unaffected — an authorized
        // admin may still assign roles/groups there.)
        var rolesList = DefaultAccessOrEmpty(s.UserCreateDefaultRole);
        var groupsList = DefaultAccessOrEmpty(s.UserCreateDefaultGroup);
        var language = ParseLanguage(ConvertToString(attrs.GetValueOrDefault("language")));
        var displayname = attrs.TryGetValue("displayname", out var dn) ? ParseTranslation(dn) : null;
        var description = attrs.TryGetValue("description", out var desc) ? ParseTranslation(desc) : null;
        var tags = AttrHelper.ExtractTags(attrs);

        var user = new User
        {
            Uuid = string.IsNullOrEmpty(rec.Uuid) ? Guid.NewGuid().ToString() : rec.Uuid,
            Shortname = rec.Shortname,
            SpaceName = MgmtSpace,
            // Canonical persisted form is the leading-slash variant so a
            // query like `Subpath = "/users"` matches both bootstrap admin
            // (AdminBootstrap.cs:74) and self-registered users. Without the
            // slash, /managed/query for /users returned only the admin.
            Subpath = "/users",
            OwnerShortname = "dmart",
            Email = email,
            Msisdn = msisdn,
            Password = string.IsNullOrEmpty(password) ? null : hasher.Hash(password),
            ForcePasswordChange = string.IsNullOrEmpty(password),
            Language = language,
            Displayname = displayname,
            Description = description,
            Tags = tags,
            Payload = payload,
            Roles = rolesList,
            Groups = groupsList,
            Type = UserType.Web,
            IsActive = true,
            IsEmailVerified = emailVerified,
            IsMsisdnVerified = msisdnVerified,
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        };
        await users.UpsertAsync(user, ct);

        // Auto-login (Python: process_user_login at the end of create_user).
        var access = jwt.IssueAccess(user.Shortname, user.Roles, user.Type);
        var refresh = jwt.IssueRefresh(user.Shortname, user.Type);
        await users.CreateSessionAsync(user.Shortname, access, null, ct);

        var timestamp = LoginTimestamp();
        // Read the previous login off the pre-update row — null here, since the
        // account was created moments ago — before the upsert below overwrites it.
        var previousLogin = PreviousLoginTimestamp(user);

        if (requestHeaders is not null)
        {
            var loginInfo = new Dictionary<string, object>
            {
                ["timestamp"] = timestamp,
                ["headers"] = requestHeaders,
            };
            user = user with { LastLogin = loginInfo, UpdatedAt = TimeUtils.Now() };
            await users.UpsertAsync(user, ct);
        }

        // This path issues tokens inline rather than delegating to
        // ProcessLoginAsync, so it needs its own audit row — otherwise the very
        // first login of every account would be missing from the trail. Appended
        // regardless of whether headers were threaded through: the audit trail's
        // completeness must not depend on the caller (see AppendLoginHistoryAsync).
        await AppendLoginHistoryAsync(user.Shortname, previousLogin, timestamp, requestHeaders, ct);

        return Result<(User, string, string)>.Ok((user, access, refresh));
    }

    // Delegates to the canonical implementation (AttrHelper); kept as a
    // private alias so the call sites in this file stay unqualified.
    private static string? ConvertToString(object? v) => AttrHelper.ConvertToString(v);

    // Translate a configured default role/group into the new user's access
    // list: a configured value becomes the sole entry, a blank/whitespace
    // setting means "no default" → empty list. Trimmed so a stray-space config
    // value (e.g. "viewer ") doesn't mint a role name that never resolves.
    private static List<string> DefaultAccessOrEmpty(string? configured)
        => string.IsNullOrWhiteSpace(configured)
            ? new List<string>()
            : new List<string> { configured.Trim() };

    private static Translation? ParseTranslation(object? value)
    {
        if (value is null) return null;
        if (value is JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                // Empty object → null, not an empty Translation (matches what
                // Python's `{}` passes through — no localized strings set).
                if (!el.EnumerateObject().Any()) return null;
                return new Translation(
                    En: el.TryGetProperty("en", out var en) ? en.GetString() : null,
                    Ar: el.TryGetProperty("ar", out var ar) ? ar.GetString() : null,
                    Ku: el.TryGetProperty("ku", out var ku) ? ku.GetString() : null);
            }
            if (el.ValueKind == JsonValueKind.String) return new Translation(En: el.GetString());
            return null;
        }
        return new Translation(En: value.ToString());
    }
    private static Payload? ExtractPayload(Dictionary<string, object> attrs)
    {
        if (!attrs.TryGetValue("payload", out var raw) || raw is null) return null;
        if (raw is not JsonElement el || el.ValueKind != JsonValueKind.Object) return null;
        return new Payload
        {
            ContentType = el.TryGetProperty("content_type", out var ct)
                && ct.ValueKind == JsonValueKind.String
                && Enum.TryParse<ContentType>(ct.GetString(), true, out var cte)
                    ? cte : ContentType.Json,
            SchemaShortname = el.TryGetProperty("schema_shortname", out var ss)
                && ss.ValueKind == JsonValueKind.String ? ss.GetString() : null,
            Body = el.TryGetProperty("body", out var b) ? b.Clone() : null,
        };
    }

    // Standard password-based login. Mirrors Python's login() PATH C (password).
    public async Task<Result<(string Access, string Refresh, User User, bool Created)>> LoginAsync(
        UserLoginRequest req, Dictionary<string, string>? requestHeaders = null, CancellationToken ct = default)
    {
        var user = await ResolveUserAsync(req, ct);
        if (user is null)
        {
            // Deliberate constant-work path — see DecoyHash. The body already
            // matches the wrong-password response; this makes the timing match
            // too, so "no such user" isn't distinguishable from "bad password".
            _ = hasher.Verify(req.Password ?? string.Empty, DecoyHash);
            return Result<(string, string, User, bool)>.Fail(
                InternalErrorCode.INVALID_USERNAME_AND_PASS, "Invalid username or password", ErrorTypes.Auth);
        }

        var (attemptLocked, unlockedUser) = await RejectIfAttemptLockedAsync(user, ct);
        if (attemptLocked is { } al) return al;
        user = unlockedUser; // possibly auto-unlocked after the cool-down
        if (RejectIfNotActive(user) is { } inactiveReject) return inactiveReject;

        // A row with no password (OTP-only / OAuth-provisioned account), or a
        // request that omitted one, short-circuits past hasher.Verify and so
        // would reject in ~1ms — the same clock leak as the not-found branch,
        // just over account state instead of existence. Burn the decoy on that
        // leg too; see DecoyHash.
        bool passwordOk;
        if (string.IsNullOrEmpty(user.Password) || req.Password is null)
        {
            _ = hasher.Verify(req.Password ?? string.Empty, DecoyHash);
            passwordOk = false;
        }
        else
        {
            passwordOk = hasher.Verify(req.Password, user.Password);
        }

        if (!passwordOk)
        {
            var locked = await HandleFailedLoginAttemptAsync(user, ct);
            return locked
                ? Result<(string, string, User, bool)>.Fail(
                    InternalErrorCode.USER_ACCOUNT_LOCKED,
                    "Account has been locked due to too many failed login attempts.", ErrorTypes.Auth)
                // Python returns INVALID_USERNAME_AND_PASS(10) for BOTH "no
                // user" and "wrong password" to avoid username enumeration.
                // Previously C# surfaced PASSWORD_NOT_VALIDATED(13) here which
                // lets callers tell the two apart — parity gap.
                : Result<(string, string, User, bool)>.Fail(
                    InternalErrorCode.INVALID_USERNAME_AND_PASS, "Invalid username or password", ErrorTypes.Auth);
        }

        // Device lock check — applies regardless of user type.
        if (user.LockedToDevice && !string.IsNullOrEmpty(user.DeviceId)
            && (string.IsNullOrEmpty(req.DeviceId) || req.DeviceId != user.DeviceId))
        {
            return Result<(string, string, User, bool)>.Fail(
                InternalErrorCode.USER_ACCOUNT_LOCKED,
                "This account is locked to a unique device !", ErrorTypes.Auth);
        }
        // New device detection for mobile users (OTP required). Python uses
        // OTP_NEEDED (115) — clients inspect the numeric code to route the
        // user to the OTP screen on first-device login.
        if (user.Type == UserType.Mobile && !string.IsNullOrEmpty(user.DeviceId)
            && !string.IsNullOrEmpty(req.DeviceId) && req.DeviceId != user.DeviceId)
        {
            return Result<(string, string, User, bool)>.Fail(
                InternalErrorCode.OTP_NEEDED, "New device detected, login with otp", "auth");
        }

        if (RejectIfContactUnverified(user, req) is { } unverifiedReject) return unverifiedReject;
        return await ProcessLoginAsync(user, req, requestHeaders, ct);
    }

    // OTP-based login. Mirrors Python's login() PATH B (OTP).
    public async Task<Result<(string Access, string Refresh, User User, bool Created)>> LoginWithOtpAsync(
        UserLoginRequest req, Dictionary<string, string>? requestHeaders = null, CancellationToken ct = default)
    {
        // Python parity: OTP login must carry exactly one identifier.
        var identifierCount = (req.Shortname is not null ? 1 : 0)
                            + (req.Email is not null ? 1 : 0)
                            + (req.Msisdn is not null ? 1 : 0);
        if (identifierCount > 1)
            return Result<(string, string, User, bool)>.Fail(
                InternalErrorCode.OTP_ISSUE,
                "Provide either msisdn, email or shortname, not both.", "auth");
        if (identifierCount == 0)
            return Result<(string, string, User, bool)>.Fail(
                InternalErrorCode.OTP_ISSUE,
                "Either msisdn, email or shortname must be provided.", "auth");

        var user = await ResolveUserAsync(req, ct);
        if (user is null)
        {
            if (settings.Value.EnableOtpImplicitRegistration
                && string.IsNullOrEmpty(req.Shortname) && !string.IsNullOrEmpty(req.Otp)
                && await TryImplicitRegisterAsync(req, requestHeaders, ct) is { } created)
                return created;
            return Result<(string, string, User, bool)>.Fail(
                InternalErrorCode.INVALID_USERNAME_AND_PASS, "Invalid username or password", ErrorTypes.Auth);
        }

        var (attemptLocked, unlockedUser) = await RejectIfAttemptLockedAsync(user, ct);
        if (attemptLocked is { } al) return al;
        user = unlockedUser; // possibly auto-unlocked after the cool-down
        if (RejectIfNotActive(user) is { } inactiveReject) return inactiveReject;

        // Validate OTP code. The destination is derived from the REQUEST
        // identifier, not the user record — a shortname identifier falls
        // back to `user.msisdn` since /otp-request writes login codes there
        // for the shortname path. Verified at the login purpose, capped by
        // MaxOtpVerifyAttempts.
        var dest = !string.IsNullOrEmpty(req.Shortname)
            ? user.Msisdn
            : (req.Msisdn ?? req.Email?.ToLowerInvariant());
        if (string.IsNullOrEmpty(dest) || string.IsNullOrEmpty(req.Otp)
            || !await otp.VerifyAndConsumeAsync(dest, OtpPurpose.Login, req.Otp,
                    settings.Value.MaxOtpVerifyAttempts, ct))
        {
            // Wrong OTP counts as a failed login attempt. Keeps the lock-out
            // promise intact — without this, an attacker who guessed a valid
            // identifier could brute-force the 6-digit code without ever
            // tripping the threshold.
            var locked = await HandleFailedLoginAttemptAsync(user, ct);
            return locked
                ? Result<(string, string, User, bool)>.Fail(
                    InternalErrorCode.USER_ACCOUNT_LOCKED,
                    "Account has been locked due to too many failed login attempts.", ErrorTypes.Auth)
                : Result<(string, string, User, bool)>.Fail(
                    InternalErrorCode.OTP_INVALID, "Wrong OTP", ErrorTypes.Auth);
        }

        // Python also optionally verifies password if provided alongside OTP.
        if (!string.IsNullOrEmpty(req.Password)
            && !string.IsNullOrEmpty(user.Password)
            && !hasher.Verify(req.Password, user.Password))
        {
            var locked = await HandleFailedLoginAttemptAsync(user, ct);
            return locked
                ? Result<(string, string, User, bool)>.Fail(
                    InternalErrorCode.USER_ACCOUNT_LOCKED,
                    "Account has been locked due to too many failed login attempts.", ErrorTypes.Auth)
                : Result<(string, string, User, bool)>.Fail(
                    InternalErrorCode.PASSWORD_NOT_VALIDATED, "Invalid username or password", ErrorTypes.Auth);
        }

        if (RejectIfContactUnverified(user, req) is { } unverifiedReject) return unverifiedReject;
        return await ProcessLoginAsync(user, req, requestHeaders, ct);
    }

    // Implicit registration: a direct msisdn/email login-purpose OTP for an
    // identifier with no matching user creates the account instead of
    // failing, gated the same way /user/create is gated. Returns null (not a
    // failure Result) when the OTP is invalid, an existing user raced the
    // caller to the same contact, or a shortname couldn't be allocated — the
    // caller falls through to the ordinary "no such user" failure either way.
    // The account never gets a password from this path: Password stays
    // null and ForcePasswordChange is always true, matching a contact-only
    // self-registration; req.Password (if supplied) is ignored.
    private async Task<Result<(string Access, string Refresh, User User, bool Created)>?> TryImplicitRegisterAsync(
        UserLoginRequest req, Dictionary<string, string>? requestHeaders, CancellationToken ct)
    {
        var s = settings.Value;
        var emailChannel = s.IsRegistrationChannelEnabled("email");
        var msisdnChannel = s.IsRegistrationChannelEnabled("msisdn");
        if (!s.IsRegistrable || (!emailChannel && !msisdnChannel)) return null;

        string dest;
        bool isEmail;
        if (!string.IsNullOrEmpty(req.Email))
        {
            if (!emailChannel) return null;
            dest = req.Email.ToLowerInvariant();
            isEmail = true;
        }
        else if (!string.IsNullOrEmpty(req.Msisdn))
        {
            if (!msisdnChannel) return null;
            dest = req.Msisdn;
            isEmail = false;
        }
        else return null;

        // Both cheap checks run BEFORE the code is consumed. Every failure
        // here returns null, which the caller reports as a generic
        // INVALID_USERNAME_AND_PASS — so consuming first meant a user whose
        // registration lost a race, or who hit an exhausted shortname space,
        // had their still-valid code silently burned and had to wait out
        // AllowOtpResendAfter and spend another slot from the daily cap to try
        // again, with nothing to tell them why. Neither check has side
        // effects: AllocateImplicitShortnameAsync only probes for an unused
        // random name.
        var taken = isEmail
            ? await users.GetByEmailAsync(dest, ct)
            : await users.GetByMsisdnAsync(dest, ct);
        if (taken is not null) return null;

        var shortname = await AllocateImplicitShortnameAsync(ct);
        if (shortname is null) return null;

        if (!await otp.VerifyAndConsumeAsync(dest, OtpPurpose.Login, req.Otp!, s.MaxOtpVerifyAttempts, ct))
            return null;

        // Re-checked after the consume as well, and deliberately: the code was
        // minted before this call, so a concurrent signup for the same contact
        // can land in the window the verify itself opens. Checking only up
        // front would trade a burned code for a silent overwrite, which is the
        // worse of the two.
        var existing = isEmail
            ? await users.GetByEmailAsync(dest, ct)
            : await users.GetByMsisdnAsync(dest, ct);
        if (existing is not null) return null;

        var user = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = MgmtSpace,
            Subpath = "/users",
            OwnerShortname = "dmart",
            Email = isEmail ? dest : null,
            Msisdn = isEmail ? null : dest,
            Password = null,
            ForcePasswordChange = true,
            Language = Language.En,
            Roles = DefaultAccessOrEmpty(s.UserCreateDefaultRole),
            Groups = DefaultAccessOrEmpty(s.UserCreateDefaultGroup),
            Type = UserType.Web,
            IsActive = true,
            IsEmailVerified = isEmail,
            IsMsisdnVerified = !isEmail,
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        };
        await users.UpsertAsync(user, ct);

        return await ProcessLoginAsync(user, req, requestHeaders, ct, created: true);
    }

    // Mints an unused 8-hex shortname, matching the "auto" shortname scheme
    // self-registration uses (RequestHandler.ResolveAutoShortname). Null
    // after exhausting attempts — astronomically unlikely at 32 bits of
    // entropy per try.
    private async Task<string?> AllocateImplicitShortnameAsync(CancellationToken ct)
    {
        for (var i = 0; i < 5; i++)
        {
            var candidate = Guid.NewGuid().ToString("N")[..8];
            if (await users.GetByShortnameAsync(candidate, ct) is null)
                return candidate;
        }
        return null;
    }

    // Shared inactive-user gate for LoginAsync / LoginWithOtpAsync.
    // Returns a pre-built rejection Result when
    // the account is deactivated (user was never verified, or was manually
    // disabled), null when the user can proceed. Centralizing the message
    // prevents drift between the two login paths — clients branch on
    // the exact string via tsdmart.
    // Python parity: `api/user/router.py::login` raises
    // USER_ACCOUNT_LOCKED(110) "Account has been locked." when is_active=false
    // (router.py:504-508). USER_ISNT_VERIFIED is a separate code Python uses
    // only on the verify-otp / registration flow — not on login. Callers that
    // still want the "verified" distinction live outside this helper.
    private static Result<(string Access, string Refresh, User User, bool Created)>? RejectIfNotActive(User user)
    {
        // A soft-deleted account gets the generic credential failure a
        // non-existent identifier gets — never "locked" (which implies
        // recoverable) — so it's indistinguishable from one that never existed.
        if (user.IsDeleted)
            return Result<(string, string, User, bool)>.Fail(
                InternalErrorCode.INVALID_USERNAME_AND_PASS, "Invalid username or password", ErrorTypes.Auth);
        // Deactivated / attempt-locked → Python parity USER_ACCOUNT_LOCKED so
        // the cxb login UI can show "your account is locked".
        return user.IsActive
            ? null
            : Result<(string, string, User, bool)>.Fail(
                InternalErrorCode.USER_ACCOUNT_LOCKED, "Account has been locked.", ErrorTypes.Auth);
    }

    // Channel-specific verification gate applied to BOTH login methods
    // (password + OTP): a login via email requires is_email_verified, via
    // msisdn requires is_msisdn_verified, while a shortname login carries no
    // verification requirement (the identifier isn't a contact channel).
    // Callers invoke this AFTER the credential check succeeds so an
    // unauthenticated caller can't turn it into a verification oracle.
    private static Result<(string Access, string Refresh, User User, bool Created)>? RejectIfContactUnverified(
        User user, UserLoginRequest req)
    {
        // Channel follows the same precedence ResolveUserAsync uses to pick the
        // user (shortname > email > msisdn): a shortname login carries no
        // verification requirement even if the body also echoes an email/msisdn.
        if (!string.IsNullOrEmpty(req.Shortname)) return null;
        if (!string.IsNullOrEmpty(req.Email) && !user.IsEmailVerified)
            return Result<(string, string, User, bool)>.Fail(
                InternalErrorCode.USER_ISNT_VERIFIED, "Email is not verified.", ErrorTypes.Auth);
        if (!string.IsNullOrEmpty(req.Msisdn) && !user.IsMsisdnVerified)
            return Result<(string, string, User, bool)>.Fail(
                InternalErrorCode.USER_ISNT_VERIFIED, "MSISDN is not verified.", ErrorTypes.Auth);
        return null;
    }

    // Mirrors RejectIfNotActive but for the auto-lockout counter — surfaces
    // USER_ACCOUNT_LOCKED before any credential check so a correct credential
    // can't bypass the lock and OTP-issuing flows don't burn a one-shot code
    // on a guaranteed-fail attempt. attempt_count >= max means a prior run of
    // HandleFailedLoginAttemptAsync (or an admin/test setting the counter
    // directly) already locked the account.
    // Returns a rejection when the account is attempt-locked, plus the User to
    // continue with — which may be an in-memory-unlocked copy when the cool-down
    // (LockoutCooldownSeconds) has elapsed since the last failed/blocked attempt.
    // The cool-down window is measured from LastFailedLogin and refreshed on every
    // blocked attempt (reset-on-every-attempt), so a persistent attacker never
    // auto-unlocks — only a genuinely idle account does. Applies ONLY to the
    // attempt-counter lock; a manually-deactivated / never-verified account
    // (attempt_count < max) is left to RejectIfNotActive and never auto-unlocks.
    private async Task<(Result<(string Access, string Refresh, User User, bool Created)>? Rejection, User User)>
        RejectIfAttemptLockedAsync(User user, CancellationToken ct)
    {
        var maxAttempts = settings.Value.MaxFailedLoginAttempts;
        if (maxAttempts <= 0 || user.AttemptCount is not int count || count < maxAttempts)
            return (null, user); // not attempt-locked

        var cooldown = settings.Value.LockoutCooldownSeconds;
        if (cooldown > 0 && user.LastFailedLogin is DateTime lastFailed
            && (TimeUtils.Now() - lastFailed).TotalSeconds > cooldown)
        {
            // Cool-down elapsed → auto-unlock and let the normal credential check run.
            await users.UnlockAfterCooldownAsync(user.Shortname, ct);
            return (null, user with { AttemptCount = 0, IsActive = true, LastFailedLogin = null });
        }

        // Still locked → refresh the cool-down anchor (so ongoing attacks keep the
        // window from ever elapsing) and reject. Message is kept identical to the
        // fresh-lock path (generic — no remaining-time leak, no message drift).
        await users.TouchLastFailedLoginAsync(user.Shortname, TimeUtils.Now(), ct);
        return (Result<(string, string, User, bool)>.Fail(
            InternalErrorCode.USER_ACCOUNT_LOCKED,
            "Account has been locked due to too many failed login attempts.",
            ErrorTypes.Auth), user);
    }

    // Read-style "is this account locked?" check for gates that are NOT
    // themselves login attempts (currently /user/otp-request when a JWT is
    // present). Uses the same "locked" determination as /user/login —
    // attempt-counter lock honoring the cool-down auto-unlock, or a deactivated
    // account (IsActive==false) — but, unlike RejectIfAttemptLockedAsync, it
    // does NOT refresh the cool-down anchor, because merely requesting an OTP is
    // not a failed login attempt and shouldn't extend a lockout window.
    public async Task<bool> IsLockedAsync(User user, CancellationToken ct = default)
    {
        var maxAttempts = settings.Value.MaxFailedLoginAttempts;
        if (maxAttempts > 0 && user.AttemptCount is int count && count >= maxAttempts)
        {
            var cooldown = settings.Value.LockoutCooldownSeconds;
            if (cooldown > 0 && user.LastFailedLogin is DateTime lastFailed
                && (TimeUtils.Now() - lastFailed).TotalSeconds > cooldown)
            {
                // Cool-down elapsed → auto-unlock, mirroring RejectIfAttemptLockedAsync.
                await users.UnlockAfterCooldownAsync(user.Shortname, ct);
                return false;
            }
            return true; // attempt-locked, cool-down still in effect (or no anchor set)
        }
        // Not attempt-locked → a manually deactivated OR soft-deleted account is
        // still locked.
        return !user.IsUsable;
    }

    // Public wrapper around the private failed-attempt counter so out-of-class
    // callers (currently only OtpHandler./password-reset-confirm) can apply the
    // same account-lockout discipline /user/login enforces on wrong OTPs.
    // Returns true when this attempt caused the account to lock.
    public Task<bool> RecordFailedAttemptAsync(User user, CancellationToken ct = default)
        => HandleFailedLoginAttemptAsync(user, ct);

    private async Task<bool> HandleFailedLoginAttemptAsync(User user, CancellationToken ct)
    {
        await users.IncrementAttemptAsync(user.Shortname, TimeUtils.Now(), ct);

        var maxAttempts = settings.Value.MaxFailedLoginAttempts;
        if (maxAttempts <= 0) return false;

        // Load fresh — the attempt count in `user` is pre-increment and stale.
        var refreshed = await users.GetByShortnameAsync(user.Shortname, ct);
        if (refreshed is null) return false;
        if (refreshed.AttemptCount is not int count || count < maxAttempts) return false;
        if (!refreshed.IsActive) return true; // already locked by a prior attempt

        var locked = refreshed with { IsActive = false, UpdatedAt = TimeUtils.Now() };
        await users.UpsertAsync(locked, ct);
        // Python: db.remove_user_session(shortname) — every active session is
        // invalidated so an already-logged-in tab can't keep making requests
        // after the account is auto-disabled.
        await users.DeleteAllSessionsAsync(user.Shortname, ct);
        return true;
    }

    // Shared post-authentication flow. Mirrors Python's process_user_login().
    // Internal rather than private: OAuth handlers (GoogleProvider / Facebook /
    // Apple) resolve a User on their own, then need to issue session + JWT
    // through the same code path as password/OTP login. Keeping it
    // internal localizes exposure to the assembly while allowing reuse.
    internal async Task<Result<(string Access, string Refresh, User User, bool Created)>> ProcessLoginAsync(
        User user, UserLoginRequest req,
        Dictionary<string, string>? requestHeaders, CancellationToken ct, bool created = false)
    {
        await users.ResetAttemptsAsync(user.Shortname, ct);

        // Python parity: bot users are completely outside the session-inactivity
        // machinery (utils/jwt.py:78,114 short-circuit set_user_session and
        // get_user_session for them). They neither populate nor consume entries
        // in MaxSessionsPerUser. Without this guard, a CI/MCP bot logging in
        // would churn the eviction queue and silently kick out human sessions
        // — and the matching read-side bypass in JwtBearerSetup.OnTokenValidated
        // would still reject the bot's token on its next request.
        var isBot = user.Type == Dmart.Models.Enums.UserType.Bot;

        // Python: max_sessions_per_user enforcement — check session count before
        // creating a new one. If at capacity, the oldest session should be evicted
        // or the login should fail. Python's get_user_session checks the count;
        // we limit by deleting oldest sessions when over the limit.
        var maxSessions = settings.Value.MaxSessionsPerUser;
        if (!isBot && maxSessions > 0)
        {
            // Evict excess sessions (keep newest maxSessions-1 to make room for the new one)
            await users.EvictExcessSessionsAsync(user.Shortname, maxSessions - 1, ct);
        }

        // Sync the in-memory copy with ResetAttemptsAsync so callers see the
        // post-login counter even though we don't replay the full row to PG.
        var updatedUser = user with { AttemptCount = 0, LastFailedLogin = null };
        string? newDeviceId = null;
        if (!string.IsNullOrEmpty(req.DeviceId) && req.DeviceId != user.DeviceId)
        {
            newDeviceId = req.DeviceId;
            updatedUser = updatedUser with { DeviceId = req.DeviceId };
        }

        // Python tracks last_login = {timestamp, headers} on every successful login.
        var loginTimestamp = LoginTimestamp();
        // Captured from the PRE-login row, before the assignment below replaces
        // it — this is the audit row's "old" side.
        var previousLogin = PreviousLoginTimestamp(user);
        Dictionary<string, object>? loginInfo = null;
        if (requestHeaders is not null)
        {
            loginInfo = new Dictionary<string, object>
            {
                ["timestamp"] = loginTimestamp,
                ["headers"] = requestHeaders,
            };
            updatedUser = updatedUser with { LastLogin = loginInfo };
        }

        // Targeted UPDATE rather than UpsertAsync: a plugin's after-hook
        // (e.g. OAuth → update_user attaching a Payload via
        // UpsertWithPriorAsync) may have written between the auth check
        // and this point; replaying the pre-login in-memory row would
        // erase those changes.
        if (newDeviceId is not null || loginInfo is not null)
        {
            await users.TouchLoginAsync(user.Shortname, newDeviceId, loginInfo, ct);
            updatedUser = updatedUser with { UpdatedAt = TimeUtils.Now() };
        }

        var access = jwt.IssueAccess(updatedUser.Shortname, updatedUser.Roles, updatedUser.Type);
        var refresh = jwt.IssueRefresh(updatedUser.Shortname, updatedUser.Type);

        // Create session row (Python: db.set_user_session). If the client
        // supplied a firebase_token on the login body, persist it on the
        // session row so a future push plugin can discover it via
        // UserRepository.GetSessionFirebaseTokensAsync. Python parity.
        // Skip for bots — utils/jwt.py:114 in Python doesn't create a row
        // for them at all (matching the bypass on the read side).
        if (!isBot)
            await users.CreateSessionAsync(updatedUser.Shortname, access, req.FirebaseToken, ct);

        // Last, so the trail records only logins that actually completed: an
        // exception from the session write above must not leave behind an audit
        // row for a login the caller never got tokens for.
        await AppendLoginHistoryAsync(
            updatedUser.Shortname, previousLogin, loginTimestamp, requestHeaders, ct);

        return Result<(string, string, User, bool)>.Ok((access, refresh, updatedUser, created));
    }

    // The previous login's timestamp, or null when the account has never logged
    // The stamp written into `last_login.timestamp` and into the audit row.
    //
    // A naive local DateTime, so it renders exactly like `created_at` /
    // `updated_at` do — "2026-07-29T03:14:05.1234567", no offset. Those columns
    // are TIMESTAMP (without time zone) and read back Kind=Unspecified, so
    // TimeUtils.Naive is what makes this field match them character for
    // character rather than merely look similar.
    //
    // DELIBERATE DIVERGENCE from Python dmart, which writes
    // `int(datetime.now().timestamp())` — epoch seconds (api/user/router.py:1220).
    // Nothing reads this field across the two implementations: QueryService's
    // user mapper omits `last_login` entirely, so it reaches no API response in
    // either port, and its only consumer here is the login audit trail, which
    // exists to be read by a human. An epoch integer is the wrong shape for
    // that. The cost is that a deployment sharing one database with the Python
    // implementation would see both shapes in this field — see
    // PreviousLoginTimestamp, which is why it stays typed as `object?`.
    private static DateTime LoginTimestamp() => TimeUtils.Naive(TimeUtils.Now());

    // in. Read off the JSONB `last_login` column, so it comes back as a
    // JsonElement — a string for anything written since the change above, a
    // NUMBER for rows written before it (or by Python). Deliberately untyped
    // and passed through verbatim: the audit row records what the previous
    // login actually said, and re-interpreting a legacy epoch as a date here
    // would invent precision the stored value never had. Readers of
    // `histories.diff.last_login.old` must therefore tolerate both.
    private static object? PreviousLoginTimestamp(User user)
        => user.LastLogin is not null
            && user.LastLogin.TryGetValue("timestamp", out var prev)
            ? prev
            : null;

    // The only headers worth keeping on an audit row: who/where the login came
    // from. An allowlist rather than "everything except authorization/cookie"
    // (what AuthHandler strips for last_login), because these rows are
    // append-only and unpruned — a deployment that carries credentials in a
    // non-standard header (x-api-key, x-auth-token) would otherwise persist
    // them forever, in every export. `referer` is deliberately absent: on the
    // OAuth paths it can carry query-string secrets.
    private static readonly HashSet<string> AuditedLoginHeaders =
        new(["user-agent", "x-forwarded-for", "x-real-ip", "origin", "host", "accept-language"],
            StringComparer.OrdinalIgnoreCase);

    // Append-only login audit trail. `users.last_login` only ever holds the
    // MOST RECENT login — every successful auth overwrites it — so after the
    // fact there is no way to answer "when else did this account sign in".
    // Mirroring each login into `histories` gives that trail a home that
    // /managed/query?type=history already exposes and already indexes —
    // idx_histories_lookup is (space_name, subpath, shortname, timestamp DESC),
    // precisely this access pattern.
    //
    // Two things to be clear-eyed about on the read side:
    //
    //   * Exposure. The gate is QueryService.cs:430 —
    //     CanQueryAsync(actor, Content, space, subpath) — which is SUBPATH
    //     level, with no per-record filtering afterwards. So anyone who can
    //     read history under management//users can enumerate EVERY account's
    //     login times, not just those of users they can see. That is a wider
    //     audience than `last_login` had (QueryService's user mapper omits the
    //     column entirely), and is the deliberate trade for having a trail.
    //   * Headers. HistoryMapper.ToRecord drops `request_headers`, so the
    //     column is not readable through /managed/query at all — only via
    //     export (ImportExportService.cs:396) or direct SQL. That is upstream
    //     behaviour, not an oversight: Python deletes the key outright for
    //     history queries (adapter.py:3030), and HistoryQueryShapeTests pins
    //     it. So these headers are written for a forensic read that has to go
    //     through an admin channel — worth knowing before relying on them.
    //
    // Deliberate divergence from Python dmart, which writes last_login through
    // internal_sys_update_model (api/user/router.py:1265) and therefore records
    // no history row at all. Nothing upstream to stay in parity with here.
    //
    // Headers ride in the `request_headers` column instead of being duplicated
    // into `diff` — with no pruning policy rows live forever, and storing the
    // header dict twice per login is what would actually make this table hurt.
    // Retention is still unsolved: a bot authenticating on a schedule appends a
    // row per login, forever. The allowlist above bounds the bytes per row, not
    // the row count — a prune policy is the follow-up.
    //
    // Appended on EVERY completed login, including those whose caller passed no
    // headers (/oauth/authorize does: OAuthEndpoints.cs:223). Gating the row on
    // header presence would let an optional parameter decide whether a
    // credential-verified sign-in is auditable at all.
    //
    // Fail-open, and the only place in this file that swallows a history-write
    // failure — UpdateUserAsync deliberately lets AppendAsync throw. A login is
    // not a profile edit: a degraded `histories` table must not be able to lock
    // every user out of the system. The cost is that the trail can have gaps,
    // which is why the failure is logged at Error rather than ignored — that
    // log line is the thing to alert on.
    private async Task AppendLoginHistoryAsync(
        string shortname, object? previousTimestamp, DateTime timestamp,
        Dictionary<string, string>? requestHeaders, CancellationToken ct)
    {
        try
        {
            var diff = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                // The {field: {old, new}} wrapper matches what ComputeUserDiff
                // produces, so existing history consumers can walk these rows
                // unchanged. The values are bare timestamps rather than the
                // {timestamp, headers} dict the column holds — the headers are
                // already on the row, and HistoryMapper would strip them from a
                // nested old/new object anyway (QueryService.cs:2153).
                //
                // `new` is always a naive-local DateTime string; `old` is
                // whatever the previous login stored, which for a row written
                // before this change (or by Python) is an epoch NUMBER. The
                // first login per account after an upgrade therefore produces
                // one mixed-type row. That is deliberate — see
                // PreviousLoginTimestamp — and harmless: this pair is display
                // data, never a join key.
                ["last_login"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["old"] = previousTimestamp,
                    ["new"] = timestamp,
                },
            };

            // Filtered by walking the source rather than probing it per allowed
            // name, so the result doesn't depend on the caller's comparer.
            // Keys are lowercased on the way in — JSONB has no case-insensitive
            // lookup, so a reader shouldn't have to guess "User-Agent" vs
            // "user-agent".
            var headers = new Dictionary<string, object>(StringComparer.Ordinal);
            if (requestHeaders is not null)
                foreach (var (name, value) in requestHeaders)
                    if (AuditedLoginHeaders.Contains(name) && !string.IsNullOrEmpty(value))
                        headers[name.ToLowerInvariant()] = value;

            // owner_shortname == subject: a login is always self-initiated.
            await history.AppendAsync(
                MgmtSpace, "/users", shortname, shortname, headers, diff, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex,
                "login history append failed for {Shortname} — login allowed, audit row lost",
                shortname);
        }
    }

    // Validate a password against the stored hash (Python: POST /validate_password).
    public async Task<bool> ValidatePasswordAsync(string shortname, string password, CancellationToken ct = default)
    {
        var user = await users.GetByShortnameAsync(shortname, ct);
        if (user is null || string.IsNullOrEmpty(user.Password)) return false;
        return hasher.Verify(password, user.Password);
    }

    public async Task<Result<User>> UpdateProfileAsync(
        string shortname, Dictionary<string, object> patch,
        string? sessionToken = null, CancellationToken ct = default)
    {
        var user = await users.GetByShortnameAsync(shortname, ct);
        if (user is null)
            return Result<User>.Fail(
                InternalErrorCode.SHORTNAME_DOES_NOT_EXIST, "user missing", ErrorTypes.Db);
        // Deleted is a dead end — no edits, ever, by anyone.
        if (user.IsDeleted)
            return Result<User>.Fail(
                InternalErrorCode.NOT_ALLOWED, "account has been deleted", ErrorTypes.Request);

        // Reject patches targeting protected payload-body fields. Python
        // (api/user/router.py:623-633) walks `attributes.payload.body.<field>`
        // against settings.user_profile_payload_protected_fields — it is
        // payload content that's restricted, not top-level Record attributes.
        var protectedCsv = settings.Value.UserProfilePayloadProtectedFields;
        // Screen the SAME body shapes PayloadMerge would later merge (JsonElement or
        // Dictionary), so a protected field can't slip in via a form the check missed.
        if (!string.IsNullOrWhiteSpace(protectedCsv)
            && PayloadMerge.ExtractBody(patch.GetValueOrDefault("payload"))
                is { ValueKind: JsonValueKind.Object } bodyProtEl)
        {
            var protectedFields = protectedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var prop in bodyProtEl.EnumerateObject())
            {
                if (protectedFields.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    return Result<User>.Fail(
                        InternalErrorCode.PROTECTED_FIELD,
                        "Attempt to update a protected field", ErrorTypes.Restriction);
            }
        }

        // Password change: Python requires old_password unless force_password_change.
        string? newPasswordHash = null;
        if (patch.TryGetValue("password", out var pwObj) && pwObj is not null)
        {
            var newPw = pwObj.ToString();
            // Python parity: profile endpoint rejects a password that fails
            // the PASSWORD regex with INVALID_PASSWORD_RULES under type=jwtauth.
            if (!string.IsNullOrEmpty(newPw) && !Auth.PasswordRules.IsValid(newPw))
                return Result<User>.Fail(
                    InternalErrorCode.INVALID_PASSWORD_RULES, "Invalid username or password", ErrorTypes.JwtAuth);
            // Python parity (api/user/router.py:665-682). old_password is
            // only required when ALL THREE hold:
            //   * client is setting a new password (guaranteed by the outer if)
            //   * the user already has a password in the DB (user.Password
            //     non-empty) — a user who never set one can freely set their
            //     first without prior-secret knowledge
            //   * force_password_change is NOT set — an admin-reset user
            //     mid-flow bypasses the old-password check so they can pick
            //     a fresh password without proving the old one.
            //     A side-effect of this gate: force_password_change=true users
            //     can't trip the lockout counter via /user/profile because
            //     the wrong-old_password branch never runs for them.
            if (!string.IsNullOrEmpty(user.Password) && !user.ForcePasswordChange)
            {
                //   * missing old_password   → 403 PASSWORD_RESET_ERROR, type=auth,
                //     message "Wrong password have been provided!"
                //   * old_password mismatch  → 401 UNMATCHED_DATA, type=request,
                //     message "mismatch with the information provided"
                if (!patch.TryGetValue("old_password", out var oldPwObj) || oldPwObj is null)
                    return Result<User>.Fail(
                        InternalErrorCode.PASSWORD_RESET_ERROR,
                        "Wrong password have been provided!", ErrorTypes.Auth);
                if (!hasher.Verify(oldPwObj.ToString()!, user.Password))
                {
                    // Wrong old_password counts toward the lockout threshold.
                    // Without this, an attacker who hijacks a session can brute
                    // the original password indefinitely on the change-password
                    // path while never tripping the login-side counter.
                    var locked = await HandleFailedLoginAttemptAsync(user, ct);
                    if (locked)
                        return Result<User>.Fail(
                            InternalErrorCode.USER_ACCOUNT_LOCKED,
                            "Account has been locked due to too many failed login attempts.", ErrorTypes.Auth);
                    return Result<User>.Fail(
                        InternalErrorCode.UNMATCHED_DATA,
                        "mismatch with the information provided", ErrorTypes.Request);
                }
            }
            newPasswordHash = string.IsNullOrEmpty(newPw) ? null : hasher.Hash(newPw!);
        }

        // Str only accepts scalar strings. When a client sends a non-string
        // (object, array, number, bool) for a field declared as a string —
        // e.g. {"email": {"foo":"bar"}} — preserve the existing value instead
        // of stuffing a JSON literal into the column. Matches Python's
        // Pydantic string-field validation outcome: bad type → don't apply.
        static string? Str(Dictionary<string, object> d, string k, string? fallback)
        {
            if (!d.TryGetValue(k, out var v) || v is null) return fallback;
            if (v is string s) return s;
            if (v is JsonElement el)
                return el.ValueKind == JsonValueKind.String ? el.GetString() : fallback;
            return v.ToString() ?? fallback;
        }

        // force_password_change is NOT self-settable via /user/profile — Python
        // intentionally comments out the patch assignment (api/user/router.py:683-684).
        // A user could otherwise clear an admin-mandated reset on themselves. The flag
        // is only auto-cleared when they actually change their password (it's
        // meaningless once they've picked one); otherwise it carries through unchanged.
        var resolvedForcePasswordChange = newPasswordHash is not null ? false : user.ForcePasswordChange;

        // Email confirm/change:
        //   * `new_email` + `email_otp` → change to a new address. OTP must
        //     be issued to the new address (verify-contact purpose);
        //     uniqueness-checked; on success: lowercase, replace, flip
        //     is_email_verified=true.
        //   * `email` (== stored) + `email_otp` → confirm the address already
        //     on the row: consumes the code at the stored address and flips
        //     is_email_verified.
        //   * `email` (== stored) alone → no-op.
        //   * `email` != stored → rejected; use `new_email` to change it.
        // All OTP checks are capped verify-and-consume.
        string? resolvedEmail = user.Email;
        bool resolvedIsEmailVerified = user.IsEmailVerified;
        var rawNewEmail = Str(patch, "new_email", null);
        var rawEmail = Str(patch, "email", null);
        if (!string.IsNullOrEmpty(rawNewEmail))
        {
            var newEmail = rawNewEmail.ToLowerInvariant();
            if (regexConfig.ValidateEmailFormat(newEmail) is { } emailFormatError)
                return Result<User>.Fail(InternalErrorCode.INVALID_DATA, emailFormatError, ErrorTypes.Request);
            var emailOtp = Str(patch, "email_otp", null);
            if (string.IsNullOrEmpty(emailOtp))
                return Result<User>.Fail(InternalErrorCode.SESSION,
                    "Email OTP is required to update your email", ErrorTypes.Create);
            // Collision checked BEFORE the code is redeemed. Losing the address
            // to another account is a recoverable error the caller can act on,
            // but consuming first spent their still-valid code on the way to
            // telling them so — and a retry inside AllowOtpResendAfter answers
            // a silent 200 with no message sent. The check has no side effects.
            var collision = await users.GetByEmailAsync(newEmail, ct);
            if (collision is not null && !string.Equals(collision.Shortname, user.Shortname, StringComparison.Ordinal))
                return Result<User>.Fail(InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                    $"Entry properties should be unique: @email:{newEmail} ", ErrorTypes.Request);
            if (!await otp.VerifyAndConsumeAsync(newEmail, OtpPurpose.VerifyContact,
                    emailOtp, settings.Value.MaxOtpVerifyAttempts, ct))
                return Result<User>.Fail(InternalErrorCode.SESSION,
                    "Invalid Email OTP", ErrorTypes.Create);
            resolvedEmail = newEmail;
            resolvedIsEmailVerified = true;
        }
        else if (!string.IsNullOrEmpty(rawEmail))
        {
            var suppliedEmail = rawEmail.ToLowerInvariant();
            // OrdinalIgnoreCase, not Ordinal: `suppliedEmail` has been
            // lowercased but `user.Email` is whatever case it was stored with —
            // RequestHandler keeps the admin's spelling when provisioning,
            // OAuthUserResolver keeps the provider's. An Ordinal compare is
            // therefore ALWAYS false for a stored address carrying any
            // uppercase, so its owner could never confirm the contact they
            // already hold: posting their own address came back "email does not
            // match the stored address", and `new_email` would have been a lie.
            //
            // Same defect the reset flow had — see OtpHandler.EmailDest. The
            // OTP lookup below already uses the lowercased form, matching what
            // /otp-request stored, so only this guard was wrong.
            //
            // The stored column is deliberately left as-is rather than
            // normalised here: confirming a contact should not quietly rewrite
            // it, and every lookup that matters is already case-insensitive.
            if (!string.Equals(suppliedEmail, user.Email, StringComparison.OrdinalIgnoreCase))
                return Result<User>.Fail(InternalErrorCode.INVALID_DATA,
                    "email does not match the stored address; use new_email to change it",
                    ErrorTypes.Request);
            var emailOtp = Str(patch, "email_otp", null);
            if (!string.IsNullOrEmpty(emailOtp))
            {
                if (!await otp.VerifyAndConsumeAsync(suppliedEmail, OtpPurpose.VerifyContact,
                        emailOtp, settings.Value.MaxOtpVerifyAttempts, ct))
                    return Result<User>.Fail(InternalErrorCode.SESSION,
                        "Invalid Email OTP", ErrorTypes.Create);
                // Flags never regress: confirming only ever sets the flag.
                resolvedIsEmailVerified = true;
            }
        }

        // Msisdn confirm/change — same gating as email.
        string? resolvedMsisdn = user.Msisdn;
        bool resolvedIsMsisdnVerified = user.IsMsisdnVerified;
        var rawNewMsisdn = Str(patch, "new_msisdn", null);
        var rawMsisdn = Str(patch, "msisdn", null);
        if (!string.IsNullOrEmpty(rawNewMsisdn))
        {
            if (regexConfig.ValidateMsisdnFormat(rawNewMsisdn) is { } msisdnFormatError)
                return Result<User>.Fail(InternalErrorCode.INVALID_DATA, msisdnFormatError, ErrorTypes.Request);
            var msisdnOtp = Str(patch, "msisdn_otp", null);
            if (string.IsNullOrEmpty(msisdnOtp))
                return Result<User>.Fail(InternalErrorCode.SESSION,
                    "MSISDN OTP is required to update your msisdn", ErrorTypes.Create);
            // Same ordering as the email branch above, for the same reason.
            var collision = await users.GetByMsisdnAsync(rawNewMsisdn, ct);
            if (collision is not null && !string.Equals(collision.Shortname, user.Shortname, StringComparison.Ordinal))
                return Result<User>.Fail(InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                    $"Entry properties should be unique: @msisdn:{rawNewMsisdn} ", ErrorTypes.Request);
            if (!await otp.VerifyAndConsumeAsync(rawNewMsisdn, OtpPurpose.VerifyContact,
                    msisdnOtp, settings.Value.MaxOtpVerifyAttempts, ct))
                return Result<User>.Fail(InternalErrorCode.SESSION,
                    "Invalid MSISDN OTP", ErrorTypes.Create);
            resolvedMsisdn = rawNewMsisdn;
            resolvedIsMsisdnVerified = true;
        }
        else if (!string.IsNullOrEmpty(rawMsisdn))
        {
            if (!string.Equals(rawMsisdn, user.Msisdn, StringComparison.Ordinal))
                return Result<User>.Fail(InternalErrorCode.INVALID_DATA,
                    "msisdn does not match the stored number; use new_msisdn to change it",
                    ErrorTypes.Request);
            var msisdnOtp = Str(patch, "msisdn_otp", null);
            if (!string.IsNullOrEmpty(msisdnOtp))
            {
                if (!await otp.VerifyAndConsumeAsync(rawMsisdn, OtpPurpose.VerifyContact,
                        msisdnOtp, settings.Value.MaxOtpVerifyAttempts, ct))
                    return Result<User>.Fail(InternalErrorCode.SESSION,
                        "Invalid MSISDN OTP", ErrorTypes.Create);
                resolvedIsMsisdnVerified = true;
            }
        }

        // Python parity: deep-merge patch.payload.body into user.payload.body
        // (creating a default Payload if the user had none), via the shared
        // PayloadMerge used by the managed-user and entry update paths.
        var resolvedPayload = PayloadMerge.MergeBody(user.Payload, patch.GetValueOrDefault("payload"));

        // Validate the MERGED payload against its declared schema, matching the
        // admin /managed/request User-update path (RequestHandler.DispatchUpdateAsync).
        // MergeBody honors a patch-declared schema_shortname, so a self-service
        // profile update can (re)declare a schema; gate it the same way every other
        // write path does. No-ops when the payload carries no schema_shortname/body.
        var profilePayloadSchemaError = await schemas.ValidatePayloadAsync(
            user.SpaceName, ResourceType.User, resolvedPayload, ct);
        if (profilePayloadSchemaError is not null)
            return Result<User>.Fail(InternalErrorCode.INVALID_DATA, profilePayloadSchemaError, ErrorTypes.Request);

        // Roles and Groups are intentionally absent from this `with` block: a
        // user can never change their own access via self-service
        // /user/profile, so any `roles`/`groups` in the patch body is ignored
        // and the stored values carry through unchanged. Mirrors the create
        // path, which also refuses client-supplied roles/groups.
        var updated = user with
        {
            Email = resolvedEmail,
            Msisdn = resolvedMsisdn,
            IsEmailVerified = resolvedIsEmailVerified,
            IsMsisdnVerified = resolvedIsMsisdnVerified,
            Language = patch.TryGetValue("language", out var l) && l is not null
                ? ParseLanguage(l.ToString())
                : user.Language,
            Displayname = patch.TryGetValue("displayname", out var dn) && dn is not null
                ? ParseTranslation(dn) ?? user.Displayname
                : user.Displayname,
            Description = patch.TryGetValue("description", out var desc) && desc is not null
                ? ParseTranslation(desc) ?? user.Description
                : user.Description,
            DeviceId = Str(patch, "device_id", user.DeviceId),
            ForcePasswordChange = resolvedForcePasswordChange,
            Password = newPasswordHash ?? user.Password,
            // Python's set_user_profile calls db.clear_failed_password_attempts
            // after hashing a new password — a user who just reset their own
            // password shouldn't be one mistyped login away from being locked.
            AttemptCount = newPasswordHash is not null ? 0 : user.AttemptCount,
            Payload = resolvedPayload,
            UpdatedAt = TimeUtils.Now(),
        };
        await users.UpsertAsync(updated, ct);

        // Python: if password changed and logout_on_pwd_change, delete all sessions.
        if (newPasswordHash is not null && settings.Value.LogoutOnPwdChange)
            await users.DeleteAllSessionsAsync(shortname, ct);

        // Python: if is_active set to false, delete all sessions.
        if (patch.TryGetValue("is_active", out var ia) && ia is false)
            await users.DeleteAllSessionsAsync(shortname, ct);

        // Python parity: store_entry_diff — record what changed so
        // /managed/query?type=history surfaces the audit trail for self-service
        // profile updates the same way it does for entry updates. Runs AFTER
        // the session-cleanup branches above so a transient history-write
        // failure can't leave logout_on_pwd_change un-applied. The diff
        // intentionally omits secrets and bookkeeping (password hash, attempt
        // counter, updated_at noise); password changes still appear via the
        // boolean force_password_change flip when relevant. Actor == target
        // shortname is sound for the self-service /user/profile path; an admin
        // path that updates someone else's profile must thread its own actor.
        var historyDiff = HistoryDiffUtil.ComputeUserDiff(user, updated);
        if (historyDiff.Count > 0)
            await history.AppendAsync(MgmtSpace, "/users", shortname, shortname, null,
                historyDiff, ct);

        // Python parity: `firebase_token` on the patch body writes onto the
        // caller's CURRENT session row (matched by shortname+token), not onto
        // every session. Without the caller's token we can't identify the
        // session, so a missing sessionToken is just a silent skip — same
        // outcome as Python when no auth_token is threaded through.
        if (patch.TryGetValue("firebase_token", out var ft) && ft is not null
            && !string.IsNullOrEmpty(sessionToken))
        {
            var ftStr = ft.ToString();
            if (!string.IsNullOrEmpty(ftStr))
                await users.UpdateSessionFirebaseTokenAsync(shortname, sessionToken, ftStr, ct);
        }

        // After-hook fires only when something actually changed — symmetric
        // with the history.AppendAsync guard above, so a no-op patch produces
        // neither a history row nor a plugin event. The payload carries the
        // same {field_path: {old, new}} diff persisted to history; plugins
        // (e.g. action_log) read field-level deltas from there. Mirrors
        // EntryService.UpdateAsync:372-374 — single source of truth.
        if (historyDiff.Count > 0)
        {
            var afterEvent = new Event
            {
                SpaceName = MgmtSpace,
                Subpath = "/users",
                Shortname = updated.Shortname,
                ActionType = ActionType.Update,
                ResourceType = ResourceType.User,
                UserShortname = shortname,
            };
            afterEvent.Attributes["history_diff"] = historyDiff;
            await plugins.AfterActionAsync(afterEvent, ct);
        }

        return Result<User>.Ok(updated);
    }

    // Single delete path for both self-delete (POST /user/profile/delete) and
    // admin-delete (via /managed/request, where the caller has already run
    // CanDeleteAsync). Mode is a global config value (UserDeletionMode), not a
    // per-request choice. `actor` is who performed it — it owns the soft-delete
    // audit history row, which is the record of who did it. dryRun only means
    // anything in hard mode (it projects the cascade); a soft dryRun is a no-op.
    /// <param name="force">
    /// Permission to take everything the user owns, in HARD mode only. Without
    /// it, deleting a user who has created records is refused — the guard that
    /// existed before soft delete did, kept because the mode and this flag
    /// answer different questions: the mode picks soft-vs-hard, force says "yes,
    /// I know this user owns records". Ignored in soft mode, which touches
    /// nothing the user owns, and by self-delete, which has no way to pass it
    /// and whose owner has already asked.
    /// </param>
    public async Task<Result<DeleteReport>> DeleteUserAsync(
        string shortname, string actor, bool dryRun = false, bool force = true,
        CancellationToken ct = default)
    {
        if (!settings.Value.IsHardUserDeletion)
        {
            if (dryRun) return Result<DeleteReport>.Ok(DeleteReport.Empty);

            // Snapshot before SoftDeleteAsync nulls email/msisdn, for the audit
            // diff. No row → nothing to delete (idempotent).
            var before = await users.GetByShortnameAsync(shortname, ct);
            if (before is null) return Result<DeleteReport>.Ok(DeleteReport.Empty);

            await users.SoftDeleteAsync(shortname, ct);

            // Audit row owned by `actor`; ComputeUserDiff records
            // is_deleted:{false→true} and email/msisdn:{old→null}. Non-
            // transactional with SoftDeleteAsync, matching UpdateProfileAsync.
            var after = before with { Email = null, Msisdn = null, IsDeleted = true };
            var diff = HistoryDiffUtil.ComputeUserDiff(before, after);
            await history.AppendAsync(MgmtSpace, "/users", shortname, actor, null, diff, ct);

            return Result<DeleteReport>.Ok(DeleteReport.Empty);
        }

        if (!force && !dryRun && await users.OwnsAnyRecordsAsync(shortname, ct))
            return Result<DeleteReport>.Fail(InternalErrorCode.CANNT_DELETE,
                $"user '{shortname}' has created records; pass force=true to delete the user "
                + "and everything they own", ErrorTypes.Request);

        // Never let a hard delete wipe the management space, even if this user
        // owns it — it holds all users/roles/permissions. The guard also applies
        // to a dryrun projection: an impossible delete shouldn't report a count.
        if (!dryRun && await users.OwnsSpaceAsync(shortname, MgmtSpace, ct))
            return Result<DeleteReport>.Fail(InternalErrorCode.CANNT_DELETE,
                $"cannot delete user '{shortname}': they own the management space '{MgmtSpace}'",
                ErrorTypes.Request);

        return Result<DeleteReport>.Ok(await users.ForceDeleteAsync(shortname, dryRun, ct));
    }

    public async Task LogoutAsync(string? shortname, string? token, CancellationToken ct = default)
    {
        // Python: db.remove_user_session() — delete the specific session row.
        // The repository hashes the raw JWT and looks up the row by
        // (shortname, hashed_token); both inputs must be non-empty for the
        // lookup to identify a single row, so a missing actor or token is a
        // silent no-op rather than a wildcard delete.
        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(shortname))
        {
            await users.DeleteSessionAsync(shortname, token, ct);
            return;
        }
        // Surface the silent-skip path so an expired-cookie /user/logout
        // (where the JWT principal didn't validate) doesn't disappear from
        // the trace. Token-without-actor shouldn't happen in normal flow —
        // JwtBearer would have rejected the call before we got here.
        log.LogInformation(
            "logout: no-op (actor={ActorPresent}, token={TokenPresent})",
            !string.IsNullOrEmpty(shortname),
            !string.IsNullOrEmpty(token));
    }

    private async Task<User?> ResolveUserAsync(UserLoginRequest req, CancellationToken ct)
    {
        return req.Shortname is not null ? await users.GetByShortnameAsync(req.Shortname, ct)
             : req.Email is not null     ? await users.GetByEmailAsync(req.Email, ct)
             : req.Msisdn is not null    ? await users.GetByMsisdnAsync(req.Msisdn, ct)
             : null;
    }

    private static Language ParseLanguage(string? code) => code?.ToLowerInvariant() switch
    {
        "ar" or "arabic"  => Language.Ar,
        "ku" or "kurdish" => Language.Ku,
        "fr" or "french"  => Language.Fr,
        "tr" or "turkish" => Language.Tr,
        _                 => Language.En,
    };
}
