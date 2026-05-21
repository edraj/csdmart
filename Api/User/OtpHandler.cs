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
        // All OTP endpoints share the "auth-by-ip" rate limit: they can trigger
        // SMS/email sends or verify codes — both vectors attackers exploit to
        // enumerate accounts or burn through OTP search space.
        //
        // All endpoints also share a single TTL (settings.OtpTokenTtl, default
        // 300s) — Python uses one global value rather than per-endpoint
        // minutes.
        // Python parity: SendOTPRequest.check_fields() requires exactly one of
        // {shortname, msisdn, email}. Handler then looks up the user, enforces
        // a per-destination resend cooldown (allow_otp_resend_after), and
        // dispatches the OTP over SMS (msisdn) or email. Shortname-only
        // requests currently no-op on the send side — matches get_otp_key()
        // returning "" for shortname.
        g.MapPost("/otp-request", async (SendOTPRequest req, OtpProvider otp, OtpRepository repo,
            UserRepository users, IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            var provided = (string.IsNullOrEmpty(req.Shortname) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Msisdn) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Email) ? 0 : 1);
            if (provided == 0)
                return Response.Fail(InternalErrorCode.EMAIL_OR_MSISDN_REQUIRED,
                    "One of these [email, msisdn, shortname] should be set!", "OTP");
            if (provided > 1)
                return Response.Fail(InternalErrorCode.INVALID_STANDALONE_DATA,
                    "Too many input has been passed", "OTP");

            Models.Core.User? user;
            string? dest = null;
            if (!string.IsNullOrEmpty(req.Shortname))
            {
                user = await users.GetByShortnameAsync(req.Shortname, ct);
            }
            else if (!string.IsNullOrEmpty(req.Msisdn))
            {
                user = await users.GetByMsisdnAsync(req.Msisdn, ct);
                dest = req.Msisdn;
            }
            else
            {
                var lower = req.Email!.ToLowerInvariant();
                user = await users.GetByEmailAsync(lower, ct);
                dest = lower;
            }

            var s = settings.Value;
            if (user is null && !s.IsRegistrable)
                return Response.Fail(InternalErrorCode.USERNAME_NOT_EXIST,
                    "No user found with the provided information", ErrorTypes.Request);

            if (dest is not null)
            {
                var since = await repo.GetCreatedSinceAsync(dest, ct);
                if (since is int elapsed && elapsed < s.AllowOtpResendAfter)
                    return Response.Fail(InternalErrorCode.OTP_RESEND_BLOCKED,
                        $"Resend OTP is allowed after {s.AllowOtpResendAfter - elapsed} seconds", ErrorTypes.Request);

                var code = otp.Generate(dest);
                var expiresAt = TimeUtils.Now().AddSeconds(s.OtpTokenTtl);
                await repo.StoreAsync(dest, code, expiresAt, ct);
                // Use the registered user's language when known; default to
                // English for the registrable-anonymous path (no user yet).
                await otp.SendAsync(dest, code, user?.Language ?? Models.Enums.Language.En, ct);
            }

            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        // Python parity: accepts shortname/msisdn/email (exactly one), looks up
        // the user, and sends an OTP for login. Key-scheme is the same as
        // /otp-request so /user/login's OTP verification finds the code at the
        // same destination key (Python: both endpoints call send_otp which
        // writes to `users:otp:otps/{msisdn}` — no `login:` prefix).
        //
        // Anti-enumeration: when the user isn't found, Python still returns
        // {status: success} so callers can't probe which identifiers exist.
        g.MapPost("/otp-request-login", async (SendOTPRequest req, OtpProvider otp, OtpRepository repo,
            UserRepository users, IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            var provided = (string.IsNullOrEmpty(req.Shortname) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Msisdn) ? 0 : 1)
                         + (string.IsNullOrEmpty(req.Email) ? 0 : 1);
            if (provided != 1)
                return Response.Fail(InternalErrorCode.OTP_ISSUE,
                    "one of msisdn, email or shortname must be provided", "auth");

            Models.Core.User? user;
            string? dest;
            if (!string.IsNullOrEmpty(req.Shortname))
            {
                user = await users.GetByShortnameAsync(req.Shortname, ct);
                // Shortname path: OTP is sent to the user's msisdn (Python parity).
                dest = user?.Msisdn;
            }
            else if (!string.IsNullOrEmpty(req.Msisdn))
            {
                user = await users.GetByMsisdnAsync(req.Msisdn, ct);
                dest = req.Msisdn;
            }
            else
            {
                var lower = req.Email!.ToLowerInvariant();
                user = await users.GetByEmailAsync(lower, ct);
                dest = lower;
            }

            // Anti-enumeration: missing user, or shortname lookup with no
            // msisdn to SMS, both return silent success.
            if (user is null || !user.IsActive || string.IsNullOrEmpty(dest))
                return Response.Ok();

            var s = settings.Value;
            var code = otp.Generate(dest);
            var expiresAt = TimeUtils.Now().AddSeconds(s.OtpTokenTtl);
            await repo.StoreAsync(dest, code, expiresAt, ct);
            await otp.SendAsync(dest, code, user.Language, ct);
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        g.MapPost("/password-reset-request", async (PasswordResetRequest req,
            UserRepository users, OtpProvider otp, OtpRepository repo,
            IOptions<DmartSettings> settings, CancellationToken ct) =>
        {
            Models.Core.User? user = null;
            if (!string.IsNullOrEmpty(req.Shortname))
                user = await users.GetByShortnameAsync(req.Shortname, ct);
            else if (!string.IsNullOrEmpty(req.Msisdn))
                user = await users.GetByMsisdnAsync(req.Msisdn, ct);
            else if (!string.IsNullOrEmpty(req.Email))
                user = await users.GetByEmailAsync(req.Email, ct);

            // Anti-enumeration: response is identical whether the user exists
            // or not. All silent-no-op branches below also fall through to Ok().
            if (user is null) return Response.Ok();

            // Pick the destination using the same routing rules the prior
            // invitation flow applied:
            //   - Email-direct path: only when the request was email-only AND
            //     the supplied email matches the user record (case-insensitive
            //     equality guards against a mismatched email leaking through).
            //   - Msisdn / shortname path: prefer the user's msisdn; the msisdn
            //     equality check blocks an attacker probing whether a known
            //     shortname belongs to a specific msisdn.
            //   - Csdmart-only fallback: shortname-only request + user has no
            //     msisdn → fall back to email so the reset still reaches them.
            string? dest = null;
            var emailDirect = string.IsNullOrEmpty(req.Shortname)
                && string.IsNullOrEmpty(req.Msisdn)
                && !string.IsNullOrEmpty(req.Email);
            if (emailDirect)
            {
                if (!string.IsNullOrEmpty(user.Email)
                    && string.Equals(user.Email, req.Email, StringComparison.OrdinalIgnoreCase))
                    dest = user.Email;
            }
            else if (!string.IsNullOrEmpty(user.Msisdn)
                     && (string.IsNullOrEmpty(req.Msisdn)
                         || string.Equals(user.Msisdn, req.Msisdn, StringComparison.Ordinal)))
            {
                dest = user.Msisdn;
            }
            else if (!string.IsNullOrEmpty(req.Shortname)
                     && string.IsNullOrEmpty(req.Msisdn)
                     && !string.IsNullOrEmpty(user.Email))
            {
                dest = user.Email;
            }

            if (string.IsNullOrEmpty(dest)) return Response.Ok();

            // OTP delivery: OtpProvider.SendAsync renders the body from the
            // `otp_message` language template, so the reset message uses the
            // same wording as /otp-request.
            var s = settings.Value;
            var code = otp.Generate(dest);
            var expiresAt = TimeUtils.Now().AddSeconds(s.OtpTokenTtl);
            await repo.StoreAsync(dest, code, expiresAt, ct);
            await otp.SendAsync(dest, code, user.Language, ct);

            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");

        // Python's otp-confirm verifies the OTP and then marks the email/msisdn
        // as verified on the user row. We need the user context to do that.
        g.MapPost("/otp-confirm", async (ConfirmOTPRequest req, OtpRepository repo,
            UserRepository users, HttpContext http, CancellationToken ct) =>
        {
            var dest = req.Msisdn ?? req.Email ?? "";
            var ok = await repo.VerifyAndConsumeAsync(dest, req.Code, ct);
            if (!ok)
                return Response.Fail(InternalErrorCode.OTP_INVALID,
                    "code mismatch or expired", ErrorTypes.Auth);

            // If the caller is authenticated, update their verified flags.
            var actor = http.Actor();
            if (actor is not null)
            {
                var user = await users.GetByShortnameAsync(actor, ct);
                if (user is not null)
                {
                    var updated = user with
                    {
                        IsEmailVerified = !string.IsNullOrEmpty(req.Email) || user.IsEmailVerified,
                        IsMsisdnVerified = !string.IsNullOrEmpty(req.Msisdn) || user.IsMsisdnVerified,
                        UpdatedAt = TimeUtils.Now(),
                    };
                    await users.UpsertAsync(updated, ct);
                }
            }
            return Response.Ok();
        }).RequireRateLimiting("auth-by-ip");
    }
}
