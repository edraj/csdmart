using System.Net;
using System.Net.Http.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Python parity: /user/password-reset-request mints an invitation token and
// delivers a reset link via the channel matching the supplied identifier
// (msisdn/shortname → SMS, email → Email). Previously the C# port generated
// an OTP, stored it under `reset:{dest}`, and never sent it — the SMS gateway
// was never invoked and no consumer read the row. These tests assert the
// invitation row that the new flow writes, since SmsSender/SmtpSender
// short-circuit silently in mock mode and have no observable side effect.
public sealed class PasswordResetRequestTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PasswordResetRequestTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task ShortnameOnly_Mints_Sms_Invitation_For_Users_Msisdn()
    {
        var (shortname, _, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            // InvitationService writes a row keyed by the JWT, valued
            // "SMS:{msisdn}" — assert that exactly one such row exists for
            // this user's destination.
            var hits = await CountInvitationsAsync($"SMS:{msisdn}");
            hits.ShouldBe(1);
        }
        finally { await CleanupAsync(shortname); }
    }

    [FactIfPg]
    public async Task ShortnameOnly_NoMsisdn_DoesNotMint_Anything()
    {
        // Python's reset_password takes the SMS branch unconditionally for the
        // msisdn/shortname identifier; if the resolved user has no msisdn it
        // logs a warning and silently no-ops — no fallback to email. csdmart
        // previously fell back; that's a divergence we're closing here.
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CountInvitationsAsync($"EMAIL:{email}")).ShouldBe(0,
                "shortname-only must NOT fall back to email when msisdn is missing");
            (await CountInvitationsForUserAsync(shortname)).ShouldBe(0);
        }
        finally { await CleanupAsync(shortname); }
    }

    [FactIfPg]
    public async Task ShortnameOnly_UnknownUser_Returns_Ok_AndMints_Nothing()
    {
        // Anti-enumeration: should not leak whether the shortname exists.
        var unknown = $"definitely_not_a_user_{Guid.NewGuid():N}".Substring(0, 30);
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/user/password-reset-request",
            new PasswordResetRequest(unknown, null, null),
            DmartJsonContext.Default.PasswordResetRequest);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        // No invitation row could possibly exist — there's no user to mint for.
        (await CountInvitationsForUserAsync(unknown)).ShouldBe(0);
    }

    [FactIfPg]
    public async Task EmailDirect_Mints_Email_Invitation()
    {
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(null, email, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CountInvitationsAsync($"EMAIL:{email}")).ShouldBe(1);
            (await CountInvitationsAsync($"SMS:{email}")).ShouldBe(0,
                "email path must not mint an SMS invitation");
        }
        finally { await CleanupAsync(shortname); }
    }

    [FactIfPg]
    public async Task MsisdnDirect_Mints_Sms_Invitation()
    {
        var (shortname, _, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(null, null, msisdn),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CountInvitationsAsync($"SMS:{msisdn}")).ShouldBe(1);
        }
        finally { await CleanupAsync(shortname); }
    }

    [FactIfPg]
    public async Task EmailDirect_Mismatched_Email_Mints_Nothing()
    {
        // Python parity: the email branch only mints when user.email matches
        // the request's email value. A mismatched email must not leak.
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var stranger = $"someone_else_{Guid.NewGuid():N}@test.local".Substring(0, 40);
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(null, stranger, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CountInvitationsAsync($"EMAIL:{email}")).ShouldBe(0);
            (await CountInvitationsAsync($"EMAIL:{stranger}")).ShouldBe(0);
        }
        finally { await CleanupAsync(shortname); }
    }

    // ---- helpers ----

    private async Task<int> CountInvitationsAsync(string invitationValue)
    {
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM invitations WHERE invitation_value = $1", conn);
        cmd.Parameters.Add(new() { Value = invitationValue });
        var raw = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(raw);
    }

    private async Task<int> CountInvitationsForUserAsync(string shortname)
    {
        // Match either channel by suffix on identifier — sufficient to detect
        // any mint that referenced this user, even if the test didn't know
        // the exact email/msisdn at assertion time.
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM invitations WHERE invitation_value LIKE $1", conn);
        cmd.Parameters.Add(new() { Value = $"%{shortname}%" });
        var raw = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(raw);
    }

    private async Task<(string Shortname, string Email, string Msisdn)> CreateUserAsync(bool withMsisdn)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"pr_test_{suffix}";
        var email = $"{shortname}@test.local";
        var msisdn = $"+9650000{suffix[..8]}";

        var users = _factory.Services.GetRequiredService<UserRepository>();
        var user = new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Email = email,
            Msisdn = withMsisdn ? msisdn : null,
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await users.UpsertAsync(user);
        return (shortname, email, msisdn);
    }

    private async Task CleanupAsync(string shortname)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAsync(shortname);
            // Drop any invitation rows minted under this user's email/msisdn so
            // back-to-back test runs don't accumulate noise that future
            // CountInvitationsForUserAsync probes would see.
            var db = _factory.Services.GetRequiredService<Db>();
            await using var conn = await db.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "DELETE FROM invitations WHERE invitation_value LIKE $1", conn);
            cmd.Parameters.Add(new() { Value = $"%{shortname}%" });
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort cleanup */ }
    }
}
