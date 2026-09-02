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
        //   * Resend cooldown: AllowOtpResendAfter per destination across all
        //     purposes (switching purpose is not a bypass).
        //   * Daily cap: MaxOtpRequestsPerDay per destination across all
        //     purposes.
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
                // The destination is a phone number or an email address, and
                // this line fires on EVERY no-op branch at Information — i.e.
                // in production, for traffic an anonymous caller controls. So
                // it goes in fingerprinted, not in clear: enough to correlate
                // repeated requests to one destination while investigating,
                // not enough to read the contact back out of the logs.
                // OtpProvider keeps even its destination-plus-code line at
                // Debug for the same reason.
                log.Log(level,
                    "otp-request: silent no-op ({Reason}) purpose={Purpose} dest={Destination}",
                    reason, req.Purpose, Fingerprint(req.Msisdn ?? req.Email ?? req.Shortname));
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

        // Contact confirm/change lives on POST /user/profile — `email` +
        // `email_otp` (or msisdn equivalents) confirms the address already on
        // the caller's row; `new_email`/`new_msisdn` + the OTP changes to a
        // new one. See UserService.UpdateProfileAsync.
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

    // A stable, non-reversible stand-in for a contact in log output. Truncated
    // to 8 hex characters: enough to tell "the same destination again" from "a
    // different one" while reading a log, short enough that it is not a
    // convenient lookup key, and lowercased first so the two spellings of one
    // address fingerprint alike.
    private static string Fingerprint(string? destination)
    {
        if (string.IsNullOrEmpty(destination)) return "(none)";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(destination.ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }
}
