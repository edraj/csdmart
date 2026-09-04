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

// GET /user/profile carries the caller's avatar under `attachments`, so a
// client that already renders `record.attachments` needs no second call.
//
// Two things about that are easy to get wrong and are pinned here, because
// both were wrong when the feature first landed:
//
//   * It must work for an ORDINARY user. A per-attachment CanReadAsync gate
//     looks prudent and is not: PermissionService.CanAsync returns false
//     outright when the actor holds no role permissions, and the implicit
//     `logged_in` role ships with an empty permission list — so gating would
//     hide the avatar from exactly the self-registered user the feature is
//     for, while this same handler hands back their email and msisdn ungated.
//   * It must be the AVATAR, not merely the newest media. ListForParentAsync
//     orders created_at DESC, so returning every attachment would make the
//     conventional `attachments.media[0]` resolve to whatever the user
//     uploaded last.
public sealed class ProfileAvatarAttachmentTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public ProfileAvatarAttachmentTests(DmartFactory factory) => _factory = factory;

    // NOTE the explicit empty role list. DmartFactory.CreateLoggedInUserAsync
    // defaults to `roles ?? new() { "super_admin" }`, and a super admin passes
    // every permission check — so the default user would pass this test even
    // with the read gate in place, proving nothing. The regression only shows
    // up for a user who holds no roles at all, which is what a self-registered
    // account is.
    [FactIfPg]
    public async Task An_Ordinary_Users_Own_Avatar_Comes_Back()
    {
        var user = await _factory.CreateLoggedInUserAsync(roles: new List<string>());
        try
        {
            await AttachAsync(user.Shortname, "avatar", ResourceType.Media);

            var record = await GetProfileRecordAsync(user.Client);

            record.Attachments.ShouldNotBeNull(
                "a user with no roles must still see their own avatar");
            record.Attachments!["media"].Single().Shortname.ShouldBe("avatar");
        }
        finally { await user.Cleanup(); }
    }

    [FactIfPg]
    public async Task A_Newer_Non_Avatar_Media_Does_Not_Displace_It()
    {
        var user = await _factory.CreateLoggedInUserAsync();
        try
        {
            await AttachAsync(user.Shortname, "avatar", ResourceType.Media,
                createdAt: DateTime.UtcNow.AddDays(-1));
            // Uploaded later, so it sorts FIRST under created_at DESC.
            await AttachAsync(user.Shortname, "passport_scan", ResourceType.Media);

            var record = await GetProfileRecordAsync(user.Client);

            var media = record.Attachments.ShouldNotBeNull()["media"];
            media.Count.ShouldBe(1, "only the avatar belongs on the profile");
            media[0].Shortname.ShouldBe("avatar",
                "media[0] must be the avatar, not the most recent upload");
        }
        finally { await user.Cleanup(); }
    }

    // A json attachment carries a `body`, which is why the whole row is not
    // returned wholesale: the profile is not the place to ship it.
    [FactIfPg]
    public async Task Other_Attachment_Types_Are_Left_Off_The_Profile()
    {
        var user = await _factory.CreateLoggedInUserAsync();
        try
        {
            await AttachAsync(user.Shortname, "notes", ResourceType.Json);

            var record = await GetProfileRecordAsync(user.Client);

            record.Attachments.ShouldBeNull(
                "no avatar means no attachments key at all");
        }
        finally { await user.Cleanup(); }
    }

    // ====================================================================

    private async Task<Record> GetProfileRecordAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/user/profile");
        var body = await resp.Content.ReadFromJsonAsync(DmartJsonContext.Default.Response);
        body!.Status.ShouldBe(Status.Success, body.Error?.Message);
        return body.Records.ShouldNotBeNull().Single();
    }

    private Task AttachAsync(string parentShortname, string shortname,
        ResourceType type, DateTime? createdAt = null)
    {
        var users = _factory.Services.GetRequiredService<UserRepository>();
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        return AttachCoreAsync(users, attachments, parentShortname, shortname, type, createdAt);
    }

    private static async Task AttachCoreAsync(UserRepository users,
        AttachmentRepository attachments, string parentShortname, string shortname,
        ResourceType type, DateTime? createdAt)
    {
        var parent = (await users.GetByShortnameAsync(parentShortname)).ShouldNotBeNull();
        var stamp = createdAt ?? DateTime.UtcNow;
        await attachments.UpsertAsync(new Attachment
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = parent.SpaceName,
            // Attachments hang off `{parent subpath}/{parent shortname}`.
            Subpath = $"{parent.Subpath.TrimEnd('/')}/{parent.Shortname}",
            ResourceType = type,
            IsActive = true,
            OwnerShortname = parentShortname,
            Body = type == ResourceType.Json ? "{\"k\":\"v\"}" : null,
            CreatedAt = stamp,
            UpdatedAt = stamp,
        });
    }
}
