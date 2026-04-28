using System;
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

// Folder-level uniqueness (port of dmart/backend/data_adapters/sql/
// adapter.py::validate_uniqueness). The folder under which an entry is
// being created/updated can declare a `payload.body.unique_fields` list
// of compound keys; EntryService.CreateAsync and UpdateAsync now check
// each compound and reject the write with DATA_SHOULD_BE_UNIQUE on
// collision.
public class UniqueFieldsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UniqueFieldsTests(DmartFactory factory) => _factory = factory;

    private (SpaceRepository spaces, EntryRepository entries, EntryService entryService) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<SpaceRepository>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<EntryService>());
    }

    private async Task<string> SeedSpaceWithFolderAsync(SpaceRepository spaces, EntryRepository entries, string uniqueFieldsJson)
    {
        var spaceName = $"uniq_{Guid.NewGuid():N}"[..16];
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

        // Folder lives at subpath="/" with shortname="people"; entries written
        // under subpath="/people" pull their unique_fields from this folder's
        // payload.body (mirrors the os.path.split walk in Python).
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "people",
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

    private static Entry MakeContent(string space, string subpath, string shortname, object body)
        => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = subpath,
            ResourceType = ResourceType.Content,
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
    public async Task Scalar_Field_Create_Rejects_Collision()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, """[["payload.body.email"]]""");
        try
        {
            var first = await entryService.CreateAsync(
                MakeContent(space, "/people", "alice", new { email = "a@x.com" }), "dmart");
            first.IsOk.ShouldBeTrue($"first create failed: {first.ErrorMessage}");

            var second = await entryService.CreateAsync(
                MakeContent(space, "/people", "alice2", new { email = "a@x.com" }), "dmart");
            second.IsOk.ShouldBeFalse("colliding create should be rejected");
            second.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
            second.ErrorType.ShouldBe(ErrorTypes.Request);
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task Scalar_Field_Update_Allows_Self_And_Rejects_Other_Collision()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, """[["payload.body.email"]]""");
        try
        {
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "alice", new { email = "a@x.com" }), "dmart"))
                .IsOk.ShouldBeTrue();
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "bob", new { email = "b@x.com" }), "dmart"))
                .IsOk.ShouldBeTrue();

            // Update bob to keep his own email — must not flag self-collision.
            var selfPatch = new System.Collections.Generic.Dictionary<string, object>
            {
                ["payload"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["body"] = JsonDocument.Parse("""{"email": "b@x.com"}""").RootElement.Clone(),
                },
            };
            var sameEmailUpdate = await entryService.UpdateAsync(
                new Locator(ResourceType.Content, space, "/people", "bob"), selfPatch, "dmart");
            sameEmailUpdate.IsOk.ShouldBeTrue($"self-email update should pass: {sameEmailUpdate.ErrorMessage}");

            // Update bob to alice's email — must collide.
            var stealEmail = new System.Collections.Generic.Dictionary<string, object>
            {
                ["payload"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["body"] = JsonDocument.Parse("""{"email": "a@x.com"}""").RootElement.Clone(),
                },
            };
            var collide = await entryService.UpdateAsync(
                new Locator(ResourceType.Content, space, "/people", "bob"), stealEmail, "dmart");
            collide.IsOk.ShouldBeFalse("update onto another row's value should be rejected");
            collide.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task ListOfStrings_Field_Rejects_Any_Element_Collision()
    {
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedSpaceWithFolderAsync(spaces, entries, """[["payload.body.ids"]]""");
        try
        {
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "first", new { ids = new[] { "a", "b" } }), "dmart"))
                .IsOk.ShouldBeTrue();

            // Overlap on "b" — second create must be rejected.
            var overlap = await entryService.CreateAsync(
                MakeContent(space, "/people", "second", new { ids = new[] { "b", "c" } }), "dmart");
            overlap.IsOk.ShouldBeFalse("overlapping list element should be rejected");
            overlap.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);

            // Disjoint ids — must succeed.
            var disjoint = await entryService.CreateAsync(
                MakeContent(space, "/people", "third", new { ids = new[] { "c", "d" } }), "dmart");
            disjoint.IsOk.ShouldBeTrue($"disjoint ids should pass: {disjoint.ErrorMessage}");
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task No_UniqueFields_On_Folder_Allows_Any_Duplicate()
    {
        var (spaces, entries, entryService) = Resolve();
        // Folder body has no unique_fields → validator returns Ok unconditionally.
        var space = await SeedSpaceWithFolderAsync(spaces, entries, """[]""");
        try
        {
            (await entryService.CreateAsync(
                MakeContent(space, "/people", "x1", new { email = "dup@x.com" }), "dmart"))
                .IsOk.ShouldBeTrue();
            var twin = await entryService.CreateAsync(
                MakeContent(space, "/people", "x2", new { email = "dup@x.com" }), "dmart");
            twin.IsOk.ShouldBeTrue("no unique_fields → duplicates allowed");
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }
}
