using System.Net;
using System.Net.Http.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the Python-parity auto-mint behaviour: when /managed/request creates a
// User whose email or msisdn isn't yet verified, send_sms_email_invitation
// runs after the row is persisted. The C# port mirrors that in
// RequestHandler.CreateUserAsync — these tests guard against that call being
// dropped, the unverified-flag conditions inverting, or MintAsync starting to
// throw and breaking the user-create flow.
public sealed class InvitationAutoMintTests : IClassFixture<DmartFactory>, IAsyncLifetime
{
    private readonly DmartFactory _factory;
    public InvitationAutoMintTests(DmartFactory factory) => _factory = factory;

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [FactIfPg]
    public async Task CreateUser_With_Unverified_Email_AutoMints_Invitation()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"inv_auto_{Guid.NewGuid():N}".Substring(0, 18);
        var email = $"{shortname}@test.local";

        try
        {
            await CreateUserViaApi(client, shortname, email: email, msisdn: null);

            // Auto-mint should have written one invitation row keyed by the
            // EMAIL identifier; verify directly via the repository so we don't
            // depend on the response body shape.
            (await CountInvitationsForAsync($"EMAIL:{email}")).ShouldBeGreaterThan(0);
        }
        finally
        {
            await DeleteUserAsync(shortname);
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task CreateUser_With_Verified_Email_DoesNotMint()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"inv_skip_{Guid.NewGuid():N}".Substring(0, 18);
        var email = $"{shortname}@test.local";

        try
        {
            await CreateUserViaApi(client, shortname, email: email, msisdn: null, isEmailVerified: true);
            (await CountInvitationsForAsync($"EMAIL:{email}")).ShouldBe(0);
        }
        finally
        {
            await DeleteUserAsync(shortname);
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task MintAsync_PropagatesCancellation()
    {
        // Issue #2 contract: every other failure inside MintAsync is swallowed
        // and logged, but OperationCanceledException must propagate so the
        // request pipeline can shut down cleanly when the client disconnects.
        var (shortname, _) = await CreateUserDirectAsync();
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            var svc = _factory.Services.GetRequiredService<InvitationService>();
            var user = (await users.GetByShortnameAsync(shortname))!;

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(
                async () => await svc.MintAsync(user, InvitationChannel.Email, cts.Token));
        }
        finally
        {
            await DeleteUserAsync(shortname);
        }
    }

    private async Task<int> CountInvitationsForAsync(string value)
    {
        // No public list-by-value query on InvitationRepository — go direct
        // through the Db service so we count rows under the same value the
        // service should have written ("EMAIL:..." / "SMS:...").
        var db = _factory.Services.GetRequiredService<Db>();
        await using var conn = await db.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT COUNT(*) FROM invitations WHERE invitation_value = $1", conn);
        cmd.Parameters.Add(new() { Value = value });
        var raw = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(raw);
    }

    private static async Task CreateUserViaApi(HttpClient client, string shortname,
        string? email, string? msisdn, bool isEmailVerified = false, bool isMsisdnVerified = false)
    {
        var attrs = new Dictionary<string, object>
        {
            ["is_email_verified"] = isEmailVerified,
            ["is_msisdn_verified"] = isMsisdnVerified,
        };
        if (email is not null) attrs["email"] = email;
        if (msisdn is not null) attrs["msisdn"] = msisdn;

        var req = new Request
        {
            RequestType = RequestType.Create,
            SpaceName = "management",
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.User,
                    Subpath = "users",
                    Shortname = shortname,
                    Attributes = attrs,
                },
            },
        };
        var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"create user failed: {resp.StatusCode}\n{body}");
        }
    }

    private async Task<(string Shortname, string Email)> CreateUserDirectAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var shortname = $"inv_can_{suffix}";
        var email = $"{shortname}@test.local";
        var users = _factory.Services.GetRequiredService<UserRepository>();
        await users.UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/users",
            OwnerShortname = shortname,
            IsActive = true,
            Email = email,
            Type = UserType.Web,
            Language = Language.En,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        return (shortname, email);
    }

    private async Task DeleteUserAsync(string shortname)
    {
        try
        {
            var users = _factory.Services.GetRequiredService<UserRepository>();
            await users.DeleteAsync(shortname);
        }
        catch { /* best-effort cleanup */ }
    }
}
