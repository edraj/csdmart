using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Attachments hanging off a USER entry.
//
// /managed/entry short-circuits the non-entry resource types (space, user,
// role, permission) into a direct serialization of their own table row —
// which used to mean `retrieve_attachments=true` was silently ignored for
// exactly those four. Python's retrieve_entry_meta has no such branch: it
// loads the meta for any resource class and always composes
// `{**meta, "attachments": ...}`. cxb's EntryRenderer reads
// `entry.attachments`, so the Attachments tab on a user rendered empty no
// matter what was attached.
//
// /user/profile is deliberately NOT covered here: it returns the avatar only
// (Python parity — get_profile passes filter_shortnames=["avatar"]), and
// ProfileAvatarAttachmentTests owns that contract.
public sealed class UserAttachmentsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UserAttachmentsTests(DmartFactory factory) => _factory = factory;

    // Users live at management:/users/<shortname>, so their attachments carry
    // subpath "/users/<shortname>" — the same "<parent subpath>/<parent
    // shortname>" convention every other parent uses.
    private async Task<(Attachment Att, Func<Task> Cleanup)> AttachCommentAsync(string userShortname)
    {
        var attachments = _factory.Services.GetRequiredService<AttachmentRepository>();
        var att = new Attachment
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "note_" + Guid.NewGuid().ToString("N")[..8],
            SpaceName = "management",
            Subpath = $"/users/{userShortname}",
            ResourceType = ResourceType.Comment,
            OwnerShortname = userShortname,
            IsActive = true,
            Tags = new(),
            Body = "hello from an attachment",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await attachments.UpsertAsync(att);
        return (att, async () =>
        {
            try { await attachments.DeleteAsync(Guid.Parse(att.Uuid)); } catch { }
        });
    }

    [FactIfPg]
    public async Task Managed_Entry_User_Returns_Attachments_When_Requested()
    {
        var user = await _factory.CreateLoggedInUserAsync();
        var (att, cleanupAtt) = await AttachCommentAsync(user.Shortname);
        try
        {
            var resp = await user.Client.GetAsync(
                $"/managed/entry/user/management/users/{user.Shortname}?retrieve_attachments=true");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            doc.RootElement.TryGetProperty("attachments", out var atts).ShouldBeTrue(
                $"user entry must carry `attachments` when retrieve_attachments=true — got {json}");
            atts.TryGetProperty("comment", out var comments).ShouldBeTrue(
                $"attachments must be grouped by resource_type — got {json}");
            comments.GetArrayLength().ShouldBe(1);

            var rec = comments[0];
            rec.GetProperty("shortname").GetString().ShouldBe(att.Shortname);
            // Python parity shape: meta fields stay inside `attributes`.
            rec.GetProperty("attributes").GetProperty("body").GetString()
                .ShouldBe("hello from an attachment");
        }
        finally
        {
            await cleanupAtt();
            await user.Cleanup();
        }
    }

    [FactIfPg]
    public async Task Managed_Entry_User_Omits_Attachments_When_Not_Requested()
    {
        // The empty `attachments: {}` is dropped by JsonStripEmptiesMiddleware,
        // so "not requested" must leave the key off entirely rather than
        // shipping the attachment list anyway.
        var user = await _factory.CreateLoggedInUserAsync();
        var (_, cleanupAtt) = await AttachCommentAsync(user.Shortname);
        try
        {
            var resp = await user.Client.GetAsync(
                $"/managed/entry/user/management/users/{user.Shortname}");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            doc.RootElement.TryGetProperty("attachments", out _).ShouldBeFalse(json);
        }
        finally
        {
            await cleanupAtt();
            await user.Cleanup();
        }
    }
}
