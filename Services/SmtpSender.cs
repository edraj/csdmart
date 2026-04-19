using System.Net;
using System.Net.Mail;
using Dmart.Config;
using Microsoft.Extensions.Options;

namespace Dmart.Services;

// Thin wrapper around System.Net.Mail.SmtpClient (AOT-safe — no reflection).
// Mirrors Python's aiosmtplib send_email() path in backend/api/user/service.py:
// delivers an HTML message to the caller's email, returns false on any error
// so the caller can fall back to on-server logging the same way the SmsSender
// degrades when the SMS gateway isn't configured.
public sealed class SmtpSender(IOptions<DmartSettings> settings, ILogger<SmtpSender> log)
{
    public async Task<bool> SendEmailAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
    {
        var s = settings.Value;
        if (s.MockSmtpApi)
        {
            log.LogWarning("MOCK_SMTP_API=true — not sending to {To}: {Subject}", to, subject);
            return true;
        }
        if (string.IsNullOrWhiteSpace(s.MailHost))
        {
            log.LogWarning("SMTP gateway not configured (MailHost blank) — dropping message to {To}", to);
            return false;
        }

        try
        {
            using var client = new SmtpClient(s.MailHost, s.MailPort)
            {
                EnableSsl = s.MailUseTls,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 15_000,
            };
            if (!string.IsNullOrWhiteSpace(s.MailUsername))
                client.Credentials = new NetworkCredential(s.MailUsername, s.MailPassword);

            var fromName = string.IsNullOrWhiteSpace(s.MailFromName) ? s.MailFromAddress : s.MailFromName;
            using var msg = new MailMessage
            {
                From = new MailAddress(s.MailFromAddress, fromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
            };
            msg.To.Add(to);

            await client.SendMailAsync(msg, ct);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "SMTP send to {To} failed", to);
            return false;
        }
    }
}
