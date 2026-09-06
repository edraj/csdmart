using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Services;
using Microsoft.Extensions.Options;

namespace Dmart.Api.User;

public static class OtpHandler
{
    public static void Map(RouteGroupBuilder g)
    {
        // ── The single OTP issuing API ─────────────────────────────────────
        // `purpose` (login | reset | register | verify-contact) selects what
        // the code will be redeemable for — it scopes the stored row and
        // picks the per-purpose issuing gate below, never the response.
        //
        //   * Anti-enumeration: every well-formed request answers 200 Ok with
        //     no body, whether the identifier resolves to a user or not,
        //     whether the account is locked, whether a cooldown or the daily
        //     cap swallowed the send. Only malformed input (bad purpose,
        //     wrong identifier count, bad format) gets an error. A caller who
        //     resends inside the cooldown gets no feedback that it was a no-op.
        //   * Resend cooldown: AllowOtpResendAfter per destination, counted
        //     in two independent budgets — `reset` in one, every other
        //     purpose in the other. Switching purpose WITHIN a budget is not
        //     a bypass; the split exists so an anonymous flood of one cannot
        //     silence the other. See the note at the cooldown check.
        //   * Daily cap: MaxOtpRequestsPerDay per destination, in the same
        //     two budgets.
        //   * Rate limit: auth-by-ip.
        g.MapPost("/otp-request", async (SendOTPRequest req, OtpProvider otp, OtpRepository repo,
            UserRepository users, UserService userService, HttpContext http,
            RegexPatternsConfig regexConfig, IOptions<DmartSettings> settings,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (!OtpPurpose.IsValid(req.Purpose))
                return Response.Fail(InternalErrorCode.INVALID_DATA,
                    "invalid purpose", ErrorTypes.Request);

            // Every silent-Ok branch logs its reason before answering — the
            // wire response is uniform (anti-enumeration), so this is the
            // only place a swallowed send is visible. daily-cap logs at
            // Warning; everything else at Information.
            var log = loggerFactory.CreateLogger(typeof(OtpHandler));
            Response SilentOk(string reason, LogLevel level = LogLevel.Information)
            {
                log.Log(level,
                    "otp-request: silent no-op ({Reason}) purpose={Purpose} dest={Destination}",
                    reason, req.Purpose, req.Msisdn ?? req.Email ?? req.Shortname);
                return Response.Ok();
            }

            var provided = (string.IsNullOrEmpty(req.Shortname) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Msisdn) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Email) ? 0 : 1);
            if (provided == 0)
                return Response.Fail(InternalErrorCode.EMAIL_OR_MSISDN_REQUIRED,
                    "One of these [email, msisdn, shortname] should be set!", "OTP");
            if (provided > 1)
                return Response.Fail(InternalErrorCode.INVALID_STANDALONE_DATA,
                    "Too many input has been passed", "OTP");
            if (!string.IsNullOrEmpty(req.Msisdn) && regexConfig.ValidateMsisdnFormat(req.Msisdn) is { } msisdnFormatErr)
                return Response.Fail(InternalErrorCode.INVALID_DATA, msisdnFormatErr, ErrorTypes.Request);
            if (!string.IsNullOrEmpty(req.Email) && regexConfig.ValidateEmailFormat(req.Email) is { } emailFormatErr)
                return Response.Fail(InternalErrorCode.INVALID_DATA, emailFormatErr, ErrorTypes.Request);

            var s = settings.Value;
            var purpose = req.Purpose!;

            // Issuing gates — every rejection below is a silent Ok. One rule
            // per purpose:
            //   login / reset      → anonymous allowed; identifier must map
            //                        to a usable account. Exception: with
            //                        EnableOtpImplicitRegistration on, a
            //                        direct msisdn/email login request with
            //                        no matching user instead falls through
            //                        to the registration gate below, so
            //                        /user/login can create the account on
            //                        redemption.
            //   register           → anonymous allowed only while
            //                        self-registration is open and the
            //                        requested channel is enabled; no user
            //                        row required.
            //   verify-contact     → JWT required; serves the authenticated
            //                        profile confirm/change flows; no
            //                        user-row requirement for the contact.
            // A JWT caller with a locked account never gets a code, whatever
            // the purpose.
            var actor = http.Actor();
            if (actor is not null)
            {
                var jwtUser = await users.GetByShortnameAsync(actor, ct);
                if (jwtUser is not null && await userService.IsLockedAsync(jwtUser, ct))
                    return SilentOk("locked-account");
            }
            else if (purpose == OtpPurpose.VerifyContact)
            {
                return SilentOk("anonymous-verify-contact");
            }

            // Mirrors /user/create's gates (UserService.CreateAsync):
            // registration closed, no channels enabled, or the requested
            // channel disabled. Also backs the implicit-registration case
            // below (login purpose, no matching user).
            string? RegistrationGateFailure()
            {
                var emailChannel = s.IsRegistrationChannelEnabled("email");
                var msisdnChannel = s.IsRegistrationChannelEnabled("msisdn");
                if (!s.IsRegistrable || (!emailChannel && !msisdnChannel)) return "not-registrable";
                if (!string.IsNullOrEmpty(req.Msisdn) && !msisdnChannel) return "channel-disabled";
                if (!string.IsNullOrEmpty(req.Email) && !emailChannel) return "channel-disabled";
                return null;
            }

            if (purpose == OtpPurpose.Register && RegistrationGateFailure() is { } registerFailure)
                return SilentOk(registerFailure);

            // register and verify-contact address a contact directly; no
            // user row needs to exist.
            var contactPurpose = purpose is OtpPurpose.Register or OtpPurpose.VerifyContact;

            // Resolve the user (when one exists) and the delivery destination.
            Models.Core.User? user = null;
            string? dest = null;
            if (!string.IsNullOrEmpty(req.Shortname))
            {
                user = await users.GetByShortnameAsync(req.Shortname, ct);
                // register/verify-contact by shortname are silent no-ops —
                // those purposes address a contact directly.
                if (!contactPurpose && user is not null)
                {
                    // Prefer msisdn; reset falls back to email so the code
                    // still reaches a msisdn-less account.
                    dest = !string.IsNullOrEmpty(user.Msisdn) ? user.Msisdn
                         : purpose == OtpPurpose.Reset ? EmailDest(user.Email)
                         : null;
                }
            }
            else if (!string.IsNullOrEmpty(req.Msisdn))
            {
                user = await users.GetByMsisdnAsync(req.Msisdn, ct);
                dest = req.Msisdn;
            }
            else
            {
                var lower = EmailDest(req.Email!)!;
                user = await users.GetByEmailAsync(lower, ct);
                dest = lower;
            }

            // login and reset require an existing, usable account;
            // register/verify-contact do not. Exception: when
            // EnableOtpImplicitRegistration is on, a login-purpose request
            // for a direct msisdn/email with no matching user falls through
            // to the registration gate instead of a flat no-op, so
            // /user/login can implicitly create the account on redemption.
            // Shortname requests are unaffected — dest is null for an
            // unresolved shortname (no contact to gate), so they still hit
            // the no-destination branch below.
            // IsLockedAsync, not raw IsUsable. HandleFailedLoginAttemptAsync
            // persists IsActive=false when an account locks, and only
            // IsLockedAsync/RejectIfAttemptLockedAsync clear it once
            // LockoutCooldownSeconds has elapsed. Reading IsUsable directly
            // therefore keeps answering SilentOk("unusable-account") forever
            // after the cool-down expired — and for an account whose only
            // credential is an OTP (the password-less users this repo
            // provisions), nothing else on any path would ever unlock it. The
            // user asks for a code, gets 200, and no message ever arrives.
            // The JWT branch above already uses this check.
            var blocked = user is null || await userService.IsLockedAsync(user, ct);
            if (!contactPurpose && blocked)
            {
                var implicitEligible = purpose == OtpPurpose.Login && user is null
                    && s.EnableOtpImplicitRegistration && !string.IsNullOrEmpty(dest);
                if (!implicitEligible)
                    return SilentOk(user is null ? "unknown-user"
                        : !user.IsUsable ? "unusable-account" : "locked-account");
                if (RegistrationGateFailure() is { } implicitFailure)
                    return SilentOk(implicitFailure);
            }
            if (string.IsNullOrEmpty(dest))
                return SilentOk("no-destination");

            // Resend cooldown — per destination, in the same two buckets the
            // daily cap uses below.
            //
            // Across ALL purposes it was a denial-of-service with no account
            // needed: `register` requires no JWT and no existing user, so one
            // request every 59 seconds against a victim's msisdn — far under
            // the auth-by-ip limiter — held the cooldown permanently open and
            // silently swallowed every login and reset the victim asked for.
            // Before the routes were unified, reset codes lived under their
            // own key with their own cooldown, so login spam could not reach
            // them; collapsing the endpoints removed that separation without
            // replacing it.
            //
            // Splitting it the same way as the cap restores it: flooding one
            // bucket cannot silence the other, so sign-in and account recovery
            // can never both be closed by one attacker. Switching purpose
            // WITHIN a bucket is still not a bypass, which is what the
            // cross-purpose rule was there to prevent.
            var since = await repo.GetCreatedSinceBucketAsync(dest, purpose, ct);
            if (since is int elapsed && elapsed < s.AllowOtpResendAfter)
                return SilentOk("cooldown");

            // Daily cap — per destination, with a reserve for account
            // recovery.
            //
            // The cap is keyed on the DESTINATION and nothing else, which is
            // what makes it work as abuse control (it bounds what one contact
            // can be made to receive, and what it can cost to send) and also
            // what makes it an attack surface: the endpoint is anonymous, so
            // anyone who knows a victim's msisdn can spend the whole daily
            // budget on `login` requests. Counted across all purposes, that
            // also denied `reset` — locking the victim out of the one flow
            // that recovers an account, for 24 hours, with every response a
            // silent 200 so neither they nor the UI could tell why.
            //
            // So the budget splits in two, and the split cuts BOTH ways:
            // `reset` counts only reset, and everything else counts everything
            // EXCEPT reset. A one-directional reserve would have been theatre
            // — giving reset its own bucket while still counting reset rows
            // toward the shared one just moves the cheap attack from flooding
            // login to flooding reset, which would then take login down with
            // it. Two independent buckets means neither can close the other.
            //
            // Worst case rises from N messages a day to 2N. That is the price.
            //
            // NOT a complete fix, and worth stating plainly: an attacker can
            // still exhaust EITHER bucket for a destination they know, so a
            // targeted reset flood still denies reset. Closing that needs a
            // per-CALLER dimension the endpoint has no identity for — the
            // auth-by-ip limiter is the only caller signal, and distribution
            // defeats it. What this buys is that no single flood takes out
            // both sign-in and account recovery at once.
            if (s.MaxOtpRequestsPerDay > 0)
            {
                var isReset = purpose == OtpPurpose.Reset;
                var issued = await repo.CountIssuedSinceAsync(
                    dest, TimeUtils.Now().AddHours(-24), ct,
                    OtpPurpose.Reset, invertPurpose: !isReset);
                if (issued >= s.MaxOtpRequestsPerDay)
                    return SilentOk("daily-cap", LogLevel.Warning);
            }

            var code = otp.Generate(dest);
            var expiresAt = TimeUtils.Now().AddSeconds(s.OtpTokenTtl);
            await repo.IssueAsync(dest, purpose, code, expiresAt, ct);
            // Use the registered user's language when known; default to
            // English for destinations with no user yet.
            await otp.SendAsync(dest, code, user?.Language ?? Models.Enums.Language.En, ct);

            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        // Verifies (and consumes) the reset-purpose OTP issued by
        // /otp-request purpose=reset, then writes the new password hash.
        // {unknown user, no dest, mismatch, expired} all return the same
        // OTP_INVALID. Wrong OTPs count against the failed-attempt counter.
        g.MapPost("/password-reset-confirm", async (PasswordResetConfirm req,
            UserRepository users, OtpRepository repo, PasswordHasher hasher,
            UserService userService, IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            // Exactly one of {Shortname, Email, Msisdn}.
            var provided = (string.IsNullOrEmpty(req.Shortname) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Msisdn) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Email) ? 0 : 1);
            if (provided != 1 || string.IsNullOrWhiteSpace(req.Otp)
                || string.IsNullOrWhiteSpace(req.Password))
                return Response.Fail(InternalErrorCode.MISSING_DATA,
                    "exactly one of [shortname, email, msisdn] plus otp and password are required",
                    ErrorTypes.Request);

            // Resolve user via the typed identifier the caller supplied.
            Models.Core.User? user;
            if (!string.IsNullOrEmpty(req.Shortname))
                user = await users.GetByShortnameAsync(req.Shortname, ct);
            else if (!string.IsNullOrEmpty(req.Msisdn))
                user = await users.GetByMsisdnAsync(req.Msisdn, ct);
            else
                user = await users.GetByEmailAsync(EmailDest(req.Email!)!, ct);

            if (user is null)
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", ErrorTypes.Auth);

            // Validate password rules before the OTP probe — this branch
            // never touches the stored code, so it can't burn it.
            if (!PasswordRules.IsValid(req.Password))
                return Response.Fail(InternalErrorCode.INVALID_PASSWORD_RULES,
                    "password does not meet complexity rules", ErrorTypes.Request);

            // IsUsable, not IsActive: a soft-deleted row can still have
            // IsActive=true, so IsActive alone would let a reset resurrect a
            // deleted account.
            if (!user.IsUsable)
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", ErrorTypes.Auth);

            // Resolve the same (dest, reset) row the issuing call used:
            //   email-direct + match → user.Email
            //   msisdn-direct or shortname-with-msisdn → user.Msisdn
            //   shortname-only no msisdn → user.Email
            string? dest = null;
            if (!string.IsNullOrEmpty(req.Email))
            {
                if (!string.IsNullOrEmpty(user.Email)
                    && string.Equals(user.Email, req.Email, StringComparison.OrdinalIgnoreCase))
                    dest = EmailDest(user.Email);
            }
            else if (!string.IsNullOrEmpty(user.Msisdn))
            {
                dest = user.Msisdn;
            }
            else if (!string.IsNullOrEmpty(req.Shortname)
                     && !string.IsNullOrEmpty(user.Email))
            {
                dest = EmailDest(user.Email);
            }

            if (string.IsNullOrEmpty(dest))
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", ErrorTypes.Auth);

            var ok = await repo.VerifyAndConsumeAsync(
                dest, OtpPurpose.Reset, req.Otp, settings.Value.MaxOtpVerifyAttempts, ct);
            if (!ok)
            {
                // Wrong OTP counts against the failed-attempt counter, capping
                // brute-force guesses within the code's TTL.
                var locked = await userService.RecordFailedAttemptAsync(user, ct);
                return locked
                    ? Response.Fail(InternalErrorCode.USER_ACCOUNT_LOCKED,
                        "Account has been locked due to too many failed login attempts.",
                        ErrorTypes.Auth)
                    : Response.Fail(InternalErrorCode.OTP_INVALID,
                        "code mismatch or expired", ErrorTypes.Auth);
            }

            var updated = user with
            {
                Password = hasher.Hash(req.Password),
                ForcePasswordChange = false,
                // OrdinalIgnoreCase for the email: `dest` is normalised (see
                // EmailDest) while user.Email keeps its stored case, so an
                // ordinal `==` is false for every mixed-case address. That
                // would leave is_email_verified untouched on a SUCCESSFUL
                // reset — and an unverified row is refused at /user/login by
                // RejectIfContactUnverified, so the user would change their
                // password and still be unable to sign in.
                IsEmailVerified = string.Equals(dest, user.Email, StringComparison.OrdinalIgnoreCase)
                    ? true : user.IsEmailVerified,
                IsMsisdnVerified = dest == user.Msisdn ? true : user.IsMsisdnVerified,
                UpdatedAt = TimeUtils.Now(),
            };
            await users.UpsertAsync(updated, ct);
            await users.ResetAttemptsAsync(user.Shortname, ct);
            // A reset is the victim's remedy after a compromise, so a token
            // an attacker already holds must not survive it.
            if (settings.Value.LogoutOnPwdChange)
                await users.DeleteAllSessionsAsync(user.Shortname, ct);
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        // POST /user/verify-contact — prove control of an email or msisdn and
        // make it yours, verified. One operation, whether the address is the
        // one already on your row or a new one.
        //
        // Named for the outcome, like every other endpoint here (/user/login,
        // /user/create, /user/password-reset-confirm) and unlike the
        // `otp-confirm` it replaces, which was named for the token it consumes
        // — a description that fits every OTP redemption in the system and so
        // distinguished nothing.
        //
        // The caller does not declare whether this is a confirm or a change.
        // Which one it is depends on state the SERVER already has, so making
        // the client classify its own intent only creates a way to get it
        // wrong. That is also why the request takes plain `email`/`msisdn`
        // rather than the `new_email`/`new_msisdn` this used to need on
        // /user/profile: there is no profile representation being echoed back
        // here, so nothing to disambiguate against.
        g.MapPost("/verify-contact", async (VerifyContactRequest req, OtpRepository repo,
            UserRepository users, HistoryRepository history, RegexPatternsConfig regexConfig,
            HttpContext http, IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            // Authenticated, and checked before the store is touched: the only
            // effect is on the caller's own row, so an anonymous call achieves
            // nothing — but it would still spend attempts against a live code.
            var actor = http.Actor();
            if (actor is null)
                return Response.Fail(InternalErrorCode.NOT_AUTHENTICATED,
                    "login required", ErrorTypes.Auth);

            // `code` is a non-nullable positional parameter, but nothing
            // enforces that on the wire: there is no AddValidation() in the
            // pipeline and DmartJsonContext does not respect nullable
            // annotations, so an omitted `code` binds to null and would reach
            // OtpHasher.Hash as a null string. Checked here, with the other
            // shape validation and before the store is touched, so a malformed
            // request neither 500s nor spends an attempt on a live code.
            if (string.IsNullOrWhiteSpace(req.Code))
                return Response.Fail(InternalErrorCode.MISSING_DATA,
                    "code is required", ErrorTypes.Request);

            var provided = (string.IsNullOrEmpty(req.Msisdn) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Email) ? 0 : 1);
            if (provided == 0)
                return Response.Fail(InternalErrorCode.EMAIL_OR_MSISDN_REQUIRED,
                    "One of these [email, msisdn] should be set!", "OTP");
            if (provided > 1)
                return Response.Fail(InternalErrorCode.INVALID_STANDALONE_DATA,
                    "Too many input has been passed", "OTP");

            var isEmail = !string.IsNullOrEmpty(req.Email);
            if (isEmail && regexConfig.ValidateEmailFormat(req.Email!) is { } emailErr)
                return Response.Fail(InternalErrorCode.INVALID_DATA, emailErr, ErrorTypes.Request);
            if (!isEmail && regexConfig.ValidateMsisdnFormat(req.Msisdn!) is { } msisdnErr)
                return Response.Fail(InternalErrorCode.INVALID_DATA, msisdnErr, ErrorTypes.Request);

            // Normalised exactly as issuing normalised it, or the two halves
            // address different otps rows for a mixed-case address.
            var dest = isEmail ? EmailDest(req.Email)! : req.Msisdn!;

            var user = await users.GetByShortnameAsync(actor, ct);
            if (user is null)
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", ErrorTypes.Auth);

            var current = isEmail ? EmailDest(user.Email) : user.Msisdn;
            var isChange = !string.Equals(dest, current, StringComparison.Ordinal);

            // Uniqueness, before the code is spent. Losing the address to
            // another account is recoverable and the caller can act on it, but
            // consuming first would burn a still-valid code on the way to
            // saying so — and a retry inside AllowOtpResendAfter answers a
            // silent 200 with nothing sent. Carried over from the profile path
            // this replaces; a change without it could collide silently.
            if (isChange)
            {
                var collision = isEmail
                    ? await users.GetByEmailAsync(dest, ct)
                    : await users.GetByMsisdnAsync(dest, ct);
                if (collision is not null
                    && !string.Equals(collision.Shortname, user.Shortname, StringComparison.Ordinal))
                    return Response.Fail(InternalErrorCode.DATA_SHOULD_BE_UNIQUE,
                        $"Entry properties should be unique: @{(isEmail ? "email" : "msisdn")}:{dest} ",
                        ErrorTypes.Request);
            }

            if (!await repo.VerifyAndConsumeAsync(dest, OtpPurpose.VerifyContact,
                    req.Code, settings.Value.MaxOtpVerifyAttempts, ct))
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", ErrorTypes.Auth);

            // Flags never regress, and only the channel just proved is touched.
            //
            // The address itself is written only on a CHANGE. `dest` is the
            // normalised form, so writing it unconditionally would quietly
            // rewrite `Alice@Example.com` to lowercase the first time its owner
            // confirmed it — losing an admin-provisioned or OAuth-sourced
            // spelling. Confirming a contact should not restate it.
            var updated = user with
            {
                Email = isEmail && isChange ? dest : user.Email,
                Msisdn = !isEmail && isChange ? dest : user.Msisdn,
                IsEmailVerified = isEmail || user.IsEmailVerified,
                IsMsisdnVerified = !isEmail || user.IsMsisdnVerified,
                UpdatedAt = TimeUtils.Now(),
            };
            await users.UpsertAsync(updated, ct);

            // Same audit trail the /user/profile path this replaced produced:
            // HistoryDiffUtil covers email, msisdn and both verified flags, so
            // a contact change or a first confirmation stays visible under
            // /managed/query?type=history. Actor == target, as on every
            // self-service path. Runs after the upsert, so a history-write
            // failure cannot leave the verification itself un-applied.
            var historyDiff = HistoryDiffUtil.ComputeUserDiff(user, updated);
            if (historyDiff.Count > 0)
                await history.AppendAsync(settings.Value.ManagementSpace, "/users",
                    user.Shortname, user.Shortname, null, historyDiff, ct);
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        // Contact verification is /user/verify-contact above, and only there.
        // UserService.UpdateProfileAsync refuses every contact key by name.
    }

    // The otps table is keyed on (identifier, purpose) and looks the
    // identifier up by exact equality, so the two halves of a flow have to
    // spell an email destination the SAME way or they address different rows.
    // That is not automatic here: the USER lookup is case-insensitive
    // (UserRepository's EmailLookupWhere is `LOWER(email) = LOWER($1)`) and the
    // stored column keeps whatever case it was written with — admin-provisioned
    // rows keep the operator's spelling (RequestHandler), OAuth rows keep the
    // provider's (OAuthUserResolver). So `Alice@Example.com` resolves to a user
    // from either spelling while `user.Email` and a lowercased request value
    // are two different identifiers.
    //
    // Issuing a reset lowercased its destination; confirming it looked the code
    // up under the raw stored value. Every mixed-case address therefore got
    // OTP_INVALID for a code that was correct — and because each attempt runs
    // RecordFailedAttemptAsync, a user could lock themselves out of their own
    // account trying to use a working code.
    //
    // One rule, applied wherever an email becomes a destination: lowercase.
    // Msisdns are digits and need none of this.
    private static string? EmailDest(string? email) => email?.ToLowerInvariant();
}
