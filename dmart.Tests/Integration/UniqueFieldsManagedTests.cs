using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Api;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins the non-Entry uniqueness behavior added by PR #5's commit-2.
// Exercises UniquenessValidator.ValidateRawAsync directly with seeded
// users and attachments, since the wiring inside RequestHandler is
// just a pass-through:
//   * proves ProbeAsync routes User → users, Attachment → attachments
//     (not the buggy fallthrough to entries.QueryAsync that shipped
//     in the original commit-2);
//   * proves the create/update self-filter behaves the same way it
//     does for the Entry overload;
//   * pins the path-convention quirk that bites operators silently:
//     for User folders, unique_fields must use FLAT paths
//     (`["email"]`), NOT `["payload.body.email"]` — the latter form
//     resolves to no values from the request's flat attrs and
//     produces no probe.
public class UniqueFieldsManagedTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UniqueFieldsManagedTests(DmartFactory factory) => _factory = factory;

    private (
        SpaceRepository spaces,
        EntryRepository entries,
        UserRepository users,
        AttachmentRepository attachments,
        UniquenessValidator validator) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<SpaceRepository>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<UserRepository>(),
            sp.GetRequiredService<AttachmentRepository>(),
            sp.GetRequiredService<UniquenessValidator>());
    }

    private static async Task<string> SeedSpaceWithFolderAsync(
        SpaceRepository spaces, EntryRepository entries,
        string folderShortname, string uniqueFieldsJson)
    {
        var spaceName = $"uniqm_{Guid.NewGuid():N}"[..16];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName,
            SpaceName = spaceName,
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = folderShortname,
            SpaceName = spaceName,
            Subpath = "/",
            ResourceType = ResourceType.Folder,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse($$"""{"unique_fields": {{uniqueFieldsJson}}}""").RootElement.Clone(),
            },
        });

        return spaceName;
    }

    private static User MakeUser(string space, string subpath, string shortname, string email) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = space,
        Subpath = subpath,
        IsActive = true,
        OwnerShortname = "dmart",
        Email = email,
        Type = UserType.Web,
        Language = Language.En,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static Attachment MakeComment(string space, string subpath, string shortname, object body) => new()
    {
        Uuid = Guid.NewGuid().ToString(),
        Shortname = shortname,
        SpaceName = space,
        Subpath = subpath,
        ResourceType = ResourceType.Comment,
        IsActive = true,
        OwnerShortname = "dmart",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Payload = new Payload
        {
            ContentType = ContentType.Json,
            Body = JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement.Clone(),
        },
    };

    [FactIfPg]
    public async Task User_Create_DuplicateEmail_Rejected_With_FlatPath()
    {
        var (spaces, entries, users, _, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "people", """[["email"]]""");
        try
        {
            await users.UpsertAsync(MakeUser(space, "/people", "alice", "a@x.com"));

            var attrs = new Dictionary<string, object>
            {
                ["email"] = "a@x.com",
            };
            var result = await validator.ValidateRawAsync(
                space, "/people", "alice2", ResourceType.User, attrs, ActionType.Create);

            result.IsOk.ShouldBeFalse("colliding user email should be rejected");
            result.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
            result.ErrorType.ShouldBe(ErrorTypes.Request);
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task User_Update_SelfShortname_AllowsSameEmail()
    {
        var (spaces, entries, users, _, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "people", """[["email"]]""");
        try
        {
            await users.UpsertAsync(MakeUser(space, "/people", "alice", "a@x.com"));

            // Update Alice with her own email — self-filter must drop the hit.
            var attrs = new Dictionary<string, object>
            {
                ["email"] = "a@x.com",
            };
            var result = await validator.ValidateRawAsync(
                space, "/people", "alice", ResourceType.User, attrs, ActionType.Update);

            result.IsOk.ShouldBeTrue($"self-update should pass: {result.ErrorMessage}");
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task User_Update_OtherShortname_RejectedSameEmail()
    {
        var (spaces, entries, users, _, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "people", """[["email"]]""");
        try
        {
            await users.UpsertAsync(MakeUser(space, "/people", "alice", "a@x.com"));
            await users.UpsertAsync(MakeUser(space, "/people", "bob", "b@x.com"));

            // Bob trying to claim Alice's email — must fail.
            var attrs = new Dictionary<string, object>
            {
                ["email"] = "a@x.com",
            };
            var result = await validator.ValidateRawAsync(
                space, "/people", "bob", ResourceType.User, attrs, ActionType.Update);

            result.IsOk.ShouldBeFalse("bob taking alice's email should be rejected");
            result.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task User_PayloadBodyPath_IsSilentNoOp_DocumentsConvention()
    {
        // Folder declares the WRONG path shape for a User folder. Users have
        // promoted columns (`email`, `msisdn`); the request payload is flat,
        // so `payload.body.email` resolves to no values and the gate doesn't
        // fire — even when an actual collision exists. The class-level
        // comment in ValidateRawAsync calls this out, and the LogDebug on
        // zero-value compounds surfaces it in logs. This test is the
        // executable spec.
        var (spaces, entries, users, _, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "people",
            """[["payload.body.email"]]""");
        try
        {
            await users.UpsertAsync(MakeUser(space, "/people", "alice", "a@x.com"));

            var attrs = new Dictionary<string, object>
            {
                ["email"] = "a@x.com",
            };
            var result = await validator.ValidateRawAsync(
                space, "/people", "alice2", ResourceType.User, attrs, ActionType.Create);

            // The wrong path shape silently no-ops — this is the footgun the
            // class comment warns about. If we ever auto-translate path
            // shapes per resource type, this test should flip to expect
            // a Fail and the comment should be revised.
            result.IsOk.ShouldBeTrue(
                "wrong path shape for User folder should silently no-op (path-convention quirk)");
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task Attachment_Create_DuplicatePayloadField_Rejected()
    {
        // This is the canary for the ProbeAsync attachment dispatch fix.
        // Before commit B, ProbeAsync routed all non-User/Role/Permission
        // types to entries.QueryAsync, which probed the wrong table for
        // attachment types and silently let duplicates through. With the
        // fix, the probe routes Comment → AttachmentRepository.QueryAsync
        // and the collision is found.
        var (spaces, entries, _, attachments, validator) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, "feed",
            """[["payload.body.tracking_id"]]""");
        try
        {
            await attachments.UpsertAsync(
                MakeComment(space, "/feed", "c1", new { tracking_id = "T1" }));

            // A new comment with the same tracking_id under the same folder
            // must collide.
            var attrs = new Dictionary<string, object>
            {
                ["payload"] = new Dictionary<string, object>
                {
                    ["body"] = JsonDocument.Parse("""{"tracking_id": "T1"}""").RootElement.Clone(),
                },
            };
            var result = await validator.ValidateRawAsync(
                space, "/feed", "c2", ResourceType.Comment, attrs, ActionType.Create);

            result.IsOk.ShouldBeFalse("duplicate tracking_id on comment should be rejected");
            result.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }
}
