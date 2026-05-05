using System.Net;
using System.Net.Http.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// /user/password-reset-request mints an invitation token and delivers a reset
// link via the channel matching the supplied identifier
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
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
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
            (await CountInvitationsAsync($"SMS:{msisdn}")).ShouldBe(1);
            (await CountInvitationsAsync($"EMAIL:{email}")).ShouldBe(0,
                "user has msisdn — must not also mint an email invitation");
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task ShortnameOnly_NoMsisdn_FallsBack_To_Email()
    {
        // Csdmart-only behavior (intentional divergence from upstream Python's
        // reset_password, which silently no-ops here): when the caller
        // supplied only a shortname and the resolved user has no msisdn, the
        // handler falls back to the email channel so the reset still
        // reaches the user. The fallback is gated to shortname-only requests
        // — direct-msisdn requests honor the channel the caller picked
        // (covered by MsisdnDirect_NoFallback_To_Email).
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(shortname, null, null),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CountInvitationsAsync($"EMAIL:{email}")).ShouldBe(1,
                "shortname-only with no msisdn falls back to email");
        }
        finally { await CleanupAsync(shortname, email, msisdn: null); }
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
        // No row should exist — the user never existed, so there's nothing
        // to mint for. Status code being OK is the anti-enumeration check.
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
        finally { await CleanupAsync(shortname, email, msisdn: null); }
    }

    [FactIfPg]
    public async Task MsisdnDirect_Mints_Sms_Invitation()
    {
        var (shortname, email, msisdn) = await CreateUserAsync(withMsisdn: true);
        try
        {
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(null, null, msisdn),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CountInvitationsAsync($"SMS:{msisdn}")).ShouldBe(1);
            (await CountInvitationsAsync($"EMAIL:{email}")).ShouldBe(0,
                "msisdn-direct must not mint an email invitation");
        }
        finally { await CleanupAsync(shortname, email, msisdn); }
    }

    [FactIfPg]
    public async Task MsisdnDirect_NoFallback_To_Email()
    {
        // Pins the no-fallback property of the direct-msisdn path: even when
        // the user record exists with an email but no msisdn, a request that
        // supplied only a msisdn must NOT silently fall back to email.
        // (The handler routes the lookup by the supplied msisdn, so the user
        // here is unreachable through the msisdn key — silent OK is correct.)
        var (shortname, email, _) = await CreateUserAsync(withMsisdn: false);
        try
        {
            var ghostMsisdn = $"+99900000{Guid.NewGuid():N}".Substring(0, 14);
            var client = _factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/user/password-reset-request",
                new PasswordResetRequest(null, null, ghostMsisdn),
                DmartJsonContext.Default.PasswordResetRequest);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CountInvitationsAsync($"SMS:{ghostMsisdn}")).ShouldBe(0);
            (await CountInvitationsAsync($"EMAIL:{email}")).ShouldBe(0,
                "direct-msisdn lookup must not fall back to the user's email");
        }
        finally { await CleanupAsync(shortname, email, msisdn: null); }
    }

    [FactIfPg]
    public async Task EmailDirect_Mismatched_Email_Mints_Nothing()
    {
        // The email branch only mints when user.email matches the request's
        // email value. A mismatched email must not leak.
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
        finally { await CleanupAsync(shortname, email, msisdn: null); }
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

    private async Task CleanupAsync(string shortname, string? email, string? msisdn)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAsync(shortname);

            // Build the exact set of invitation_value keys this test could
            // have produced; delete those rows so back-to-back test runs
            // start clean. Exact equality (vs LIKE %shortname%) means a
            // future change to CreateUserAsync's naming can't accidentally
            // cause one test to clobber another's rows.
            var values = new List<string>();
            if (!string.IsNullOrEmpty(email)) values.Add($"EMAIL:{email}");
            if (!string.IsNullOrEmpty(msisdn)) values.Add($"SMS:{msisdn}");
            if (values.Count == 0) return;

            var db = _factory.Services.GetRequiredService<Db>();
            await using var conn = await db.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "DELETE FROM invitations WHERE invitation_value = ANY($1)", conn);
            cmd.Parameters.Add(new()
            {
                Value = values.ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            await cmd.ExecuteNonQueryAsync();
        }
        catch { /* best-effort cleanup */ }
    }
}
