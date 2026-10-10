using System.Net;
using System.Net.Http.Json;
using Dmart.Auth;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Dmart.Services;
using Dmart.Tests.Infrastructure;
using Dmart.Utils;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Password self-service for a suite user, which is what replaces LTB's
// Self Service Password: the user knows their hosted mailbox (the address they
// sign in to mail with), the reset code goes to their CONTACT channel, and the
// new password is the one LDAP binds check, since the face reads the same row.
public sealed class MailboxSelfServiceTests(DmartFactory factory) : IClassFixture<DmartFactory>
{
    private const string OldPassword = "OldPass1234";
    private const string NewPassword = "NewPass1234";

    private async Task<(string Shortname, string Mailbox, string Contact)> CreateAsync()
    {
        var sn = $"mss{Guid.NewGuid():N}"[..14];
        var mailbox = $"{sn}@hosted.test";
        var contact = $"{sn}@elsewhere.test";
        var hasher = factory.Services.GetRequiredService<PasswordHasher>();
        await factory.Services.GetRequiredService<UserRepository>().UpsertAsync(new User
        {
            Uuid = Guid.NewGuid().ToString(), Shortname = sn, SpaceName = "management", Subpath = "/users",
            OwnerShortname = sn, IsActive = true, Password = await hasher.HashAsync(OldPassword), Type = UserType.Web,
            Language = Language.En, Email = contact, IsEmailVerified = false,
            Mailbox = mailbox, Services = ["mail"],
            Roles = new(), Groups = new(), CreatedAt = TimeUtils.Now(), UpdatedAt = TimeUtils.Now(),
        });
        return (sn, mailbox, contact);
    }

    private async Task<bool> LiveCodeAsync(string identifier, string purpose)
    {
        var db = factory.Services.GetRequiredService<IDbConnectionFactory>();
        await using var conn = await db.OpenAsync();
        await using var cmd = conn.Command(
            "SELECT 1 FROM otps WHERE identifier = $1 AND purpose = $2 AND consumed_at IS NULL LIMIT 1");
        DbParams.Add(cmd, identifier);
        DbParams.Add(cmd, purpose);
        return await cmd.ExecuteScalarAsync() is not null and not DBNull;
    }

    [FactIfPg]
    public async Task A_Reset_Named_By_The_Mailbox_Goes_To_The_Contact_Email_And_Changes_The_Bind_Password()
    {
        var (sn, mailbox, contact) = await CreateAsync();
        try
        {
            var client = factory.CreateClient();
            (await client.PostAsJsonAsync("/user/otp-request",
                new SendOTPRequest(Msisdn: null, Email: mailbox.ToUpperInvariant(), Shortname: null, Purpose: OtpPurpose.Reset),
                DmartJsonContext.Default.SendOTPRequest)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // The code went to the contact email; nothing was sent to the
            // mailbox, which is the account being recovered.
            (await LiveCodeAsync(contact, OtpPurpose.Reset)).ShouldBeTrue();
            (await LiveCodeAsync(mailbox, OtpPurpose.Reset)).ShouldBeFalse();

            // Codes are stored hashed: issue a known one where the real one went.
            const string code = "482915";
            await factory.Services.GetRequiredService<OtpRepository>()
                .IssueAsync(contact, OtpPurpose.Reset, code, TimeUtils.Now().AddMinutes(5));
            (await client.PostAsJsonAsync("/user/password-reset-confirm",
                new PasswordResetConfirm(Shortname: null, Email: mailbox, Msisdn: null, Otp: code, Password: NewPassword),
                DmartJsonContext.Default.PasswordResetConfirm)).StatusCode.ShouldBe(HttpStatusCode.OK);

            // What the LDAP face asks on every bind.
            var svc = factory.Services.GetRequiredService<UserService>();
            (await svc.VerifyDirectoryBindAsync(sn, NewPassword)).ShouldNotBeNull();
            (await svc.VerifyDirectoryBindAsync(sn, OldPassword)).ShouldBeNull();
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, sn);
        }
    }

    [FactIfPg]
    public async Task The_Mailbox_Signs_In_With_A_Password_But_Not_With_A_One_Time_Code()
    {
        var (sn, mailbox, contact) = await CreateAsync();
        try
        {
            var client = factory.CreateClient();
            async Task<Response> Login(UserLoginRequest req)
            {
                var resp = await client.PostAsJsonAsync("/user/login", req, DmartJsonContext.Default.UserLoginRequest);
                return (await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response))!;
            }

            // The mailbox is the address the user signs in to mail with, and
            // it is not a contact channel, so its owner's unverified contact
            // email does not stand in the way.
            (await Login(new UserLoginRequest(null, mailbox, null, OldPassword, null))).Status.ShouldBe(Status.Success);
            // The contact email still has to be verified to sign in with it.
            (await Login(new UserLoginRequest(null, contact, null, OldPassword, null)))
                .Error!.Code.ShouldBe(InternalErrorCode.USER_ISNT_VERIFIED);

            // Even holding a login code issued to the mailbox address, nobody
            // signs in as its owner: a code proves control of an address, and
            // reading the mailbox is not owning the account.
            const string code = "731406";
            await factory.Services.GetRequiredService<OtpRepository>()
                .IssueAsync(mailbox, OtpPurpose.Login, code, TimeUtils.Now().AddMinutes(5));
            (await Login(new UserLoginRequest(null, mailbox, null, null, code))).Status.ShouldBe(Status.Failed);
        }
        finally
        {
            await TestUserCleanup.DeleteUserAndOwnedAsync(factory.Services, sn);
        }
    }
}
