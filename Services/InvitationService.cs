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
    IOptions<DmartSettings> settings,
    ILogger<InvitationService> log)
{
    public async Task<string?> MintAsync(User user, InvitationChannel channel, CancellationToken ct = default)
    {
        string? identifier = channel == InvitationChannel.Email ? user.Email : user.Msisdn;
        if (string.IsNullOrWhiteSpace(identifier))
            return null;

        var token = jwt.Mint(user.Shortname, channel);
        var channelWire = channel == InvitationChannel.Email ? "EMAIL" : "SMS";
        await repo.UpsertAsync(token, $"{channelWire}:{identifier}", ct);

        // Assemble the public invitation URL the Python CXB/admin UI expects.
        // Format mirrors Python's repository.py template:
        //   {invitation_link}/auth/invitation?invitation={token}&lang={lang}&user-type={type}
        var url = BuildInvitationUrl(user, token);

        // Try to deliver. Email delivery is not implemented (SMTP gateway
        // hasn't been ported). SMS uses the configured SEND_SMS_API endpoint.
        if (channel == InvitationChannel.Sms)
        {
            var text = url ?? $"Your invitation: {token}";
            var ok = await sms.SendAsync(identifier, text, ct);
            if (!ok)
                log.LogWarning("invitation SMS for {Shortname} to {Msisdn} not delivered — returning token in response body",
                    user.Shortname, identifier);
        }
        else
        {
            log.LogWarning(
                "invitation minted for {Shortname} (EMAIL) — email delivery not implemented; token returned in HTTP response only",
                user.Shortname);
        }
        return token;
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
}
