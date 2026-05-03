using Dmart.Auth;
using Dmart.Config;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// Coordinates invitation minting: build the JWT, persist the lookup row,
// assemble the public invitation URL when INVITATION_LINK is configured,
// and attempt SMS delivery for SMS-channel invitations when the gateway is
// wired.
//
// Callers include:
//   * UserService.CreateAsync — auto-mints for new users whose email/msisdn
//     haven't been verified via OTP-on-create.
//   * PasswordResetHandler — admin endpoint that mints a fresh invitation on
//     demand for an existing user (Python /user/reset parity).
//
// The returned token is the full JWT string the caller presents on
// POST /user/login. We surface it directly in the HTTP response for admin
// copy/paste so invitations still work even without a mail/SMS gateway —
// Python's behaviour when `mock_smtp_api`/`mock_smpp_api` is true.
public sealed class InvitationService(
    InvitationJwt jwt,
    InvitationRepository repo,
    SmsSender sms,
    SmtpSender smtp,
    ShortLinkService shortLinks,
    IOptions<DmartSettings> settings,
    ILogger<InvitationService> log)
{
    public async Task<string?> MintAsync(User user, InvitationChannel channel, CancellationToken ct = default)
    {
        string? identifier = channel == InvitationChannel.Email ? user.Email : user.Msisdn;
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        // Mint, persist, and attempt delivery. We catch *non-cancellation*
        // failures here so callers can rely on "MintAsync never throws for
        // gateway/db hiccups" — this is what the comment in
        // RequestHandler.CreateUserAsync references. Cancellation must
        // propagate so the request pipeline sees a normal abort.
        string? token = null;
        try
        {
            token = jwt.Mint(user.Shortname, channel);
            var channelWire = channel == InvitationChannel.Email ? "EMAIL" : "SMS";
            await repo.UpsertAsync(token, $"{channelWire}:{identifier}", ct);

            // Assemble the public invitation URL the Python CXB/admin UI expects.
            // Format mirrors Python's repository.py template:
            //   {invitation_link}/auth/invitation?invitation={token}&lang={lang}&user-type={type}
            var url = BuildInvitationUrl(user, token);

            // Python parity: api/managed/utils.py wraps the long invitation URL
            // through repository.url_shortner before placing it in SMS / email
            // bodies — keeps SMS within length limits and gives every recipient
            // a stable {appUrl}/managed/s/{token} redirect.
            var deliverableLink = await ShortenAsync(url, ct) ?? url ?? token;

            if (channel == InvitationChannel.Sms)
            {
                // Python parity: api/managed/utils.py::send_sms_email_invitation
                // sends the localized invitation_message string with {link}
                // substituted. Look up by user.Language; fall back to English when
                // the language has no entry (Python would KeyError; defaulting is
                // the safer behaviour).
                var template = InvitationMessageFor(user.Language);
                var text = template.Replace("{link}", deliverableLink);
                var ok = await sms.SendAsync(identifier, text, ct);
                if (!ok)
                    log.LogWarning("invitation SMS for {Shortname} to {Msisdn} not delivered — returning token in response body",
                        user.Shortname, identifier);
            }
            else
            {
                // Python parity: send the activation HTML containing the invitation
                // link via SMTP. Mirrors generate_email_from_template("activation")
                // and generate_subject("activation"); both are English-only on the
                // Python side (single activation.html.j2 template, no per-language
                // variant), so we keep parity rather than localizing here.
                var body = ActivationEmailBody(user, deliverableLink);
                var ok = await smtp.SendEmailAsync(identifier, ActivationEmailSubject, body, ct);
                if (!ok)
                    log.LogWarning("invitation email for {Shortname} to {Email} not delivered — returning token in response body",
                        user.Shortname, identifier);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "invitation mint/delivery failed for {Shortname} via {Channel}",
                user.Shortname, channel);
        }
        return token;
    }

    // Python parity: utils/repository.py::url_shortner mints a fresh uuid4()[:8]
    // token per call without de-duping by target URL — every resend allocates
    // a new short-link row pointing at the same long URL. We deliberately
    // match that behaviour so a single invitation token can be invalidated
    // (e.g. on resend) without affecting prior tokens. Returns null when the
    // long URL is null, AppUrl is unconfigured, or persistence fails — caller
    // falls back to the long URL (or raw JWT) so a degraded shortener never
    // blocks delivery.
    private async Task<string?> ShortenAsync(string? longUrl, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(longUrl)) return null;
        var appUrl = settings.Value.AppUrl;
        if (string.IsNullOrWhiteSpace(appUrl)) return null;
        try
        {
            var token = await shortLinks.CreateAsync(longUrl, ct);
            return $"{appUrl.TrimEnd('/')}/managed/s/{token}";
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "url_shortner failed; falling back to long invitation URL");
            return null;
        }
    }

    // Null when INVITATION_LINK isn't configured — caller falls back to the
    // raw JWT, which still logs in via POST /user/login invitation path.
    private string? BuildInvitationUrl(User user, string token)
    {
        var baseUrl = settings.Value.InvitationLink;
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        var lang = JsonbHelpers.EnumMember(user.Language);
        var type = JsonbHelpers.EnumMember(user.Type);
        return $"{baseUrl.TrimEnd('/')}/auth/invitation?invitation={Uri.EscapeDataString(token)}&lang={lang}&user-type={type}";
    }

    // Python parity: backend/languages/{english,arabic,kurdish}.json
    // ["invitation_message"]. French/Turkish are not localized in Python
    // either — they fall through to the default English string here rather
    // than raise (Python's `languages[user.language]` would KeyError).
    internal static string InvitationMessageFor(Language lang) => lang switch
    {
        Language.Ar => "تهانينا، لقد تم الآن إنشاء حساب الخاص بك، يرجى اتباع هذا الرابط للتأكيد وتسجيل الدخول: {link} يمكن استخدام هذا الرابط مرة واحدة وخلال الـ 48 ساعة القادمة.",
        Language.Ku => "لە ڕێگەی ئەم بەستەرەوە ئەکاونتەکەت پشتڕاست بکەرەوە: {link} ئەم بەستەرە دەتوانرێت جارێک و لە ماوەی ٤٨ کاتژمێری داهاتوودا بەکاربهێنرێت.",
        _           => "Congratulations, your account is now created, please follow this link to confirm and login: {link} This link can be used once and within the next 48 hours.",
    };

    // Python parity: utils/generate_email.generate_subject("activation").
    internal const string ActivationEmailSubject = "Welcome to our Platform!";

    // Python parity: utils/templates/activation.html.j2 — same English body,
    // same field set (name / msisdn / shortname / link). Kept English-only to
    // match Python which has only one activation template (no per-language
    // variant). All interpolation values are HtmlEncoded to keep this safe
    // even if a malicious display name slips through validation upstream.
    internal static string ActivationEmailBody(User user, string link)
    {
        var enc = (string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
        // Python pulls displayname.en from the inbound record's attributes; we
        // use the persisted user.Displayname.En and fall back to shortname so
        // recipients always see a name.
        var name = user.Displayname?.En ?? user.Shortname;
        return "<!DOCTYPE html><html lang=\"en\"><head>"
            + "<meta charset=\"utf-8\" />"
            + "<title>Email</title></head>"
            + "<body style=\"margin:0;padding:0\">"
            + $"<p>Hi {enc(name)}</p>"
            + $"<p>MSISDN: {enc(user.Msisdn)}</p>"
            + $"<p>Username: {enc(user.Shortname)}</p>"
            + "<p>Welcome, we're happy to see you on board!</p>"
            + "<p>Only Few steps are left to activate your account, please use the below account activation link.</p>"
            + "<p>Activation Link:</p>"
            + $"<a href=\"{enc(link)}\">{enc(link)}</a>"
            + "<p>Regards,</p>"
            + "</body></html>";
    }
}
