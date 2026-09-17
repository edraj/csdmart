using Dmart.Utils;
using System;
using System.Linq;
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

// The probe cap, end to end through EntryService.
//
// A uniqueness compound over a list-valued path runs ONE search per element,
// and the element count comes from the request body. Before the cap, a write
// carrying a large enough array turned a single request into millions of
// serial searches — the folder config decides how many paths multiply, but the
// caller decides how big each one is.
//
// The cap REFUSES such a write. It deliberately does not truncate: the probes
// are the only thing that establishes uniqueness, so running a prefix of them
// and accepting would admit precisely the duplicate the constraint exists to
// stop. These tests pin the refusal, and that it doesn't cost anything for the
// ordinary list sizes real folders use.
public class UniqueFieldsProbeCapTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public UniqueFieldsProbeCapTests(DmartFactory factory) => _factory = factory;

    private (SpaceRepository spaces, EntryRepository entries, EntryService entryService) Resolve()
    {
        _factory.CreateClient();
        var sp = _factory.Services;
        return (
            sp.GetRequiredService<SpaceRepository>(),
            sp.GetRequiredService<EntryRepository>(),
            sp.GetRequiredService<EntryService>());
    }

    // Folder at "/" named "things"; entries live under "/things" and inherit
    // its unique_fields — same shape as UniqueFieldsTests.
    private async Task<string> SeedAsync(SpaceRepository spaces, EntryRepository entries, string uniqueFieldsJson)
    {
        var spaceName = $"uqcap{Guid.NewGuid():N}"[..16];
        await spaces.UpsertAsync(new Space
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = spaceName,
            SpaceName = spaceName,
            Subpath = "/",
            OwnerShortname = "dmart",
            IsActive = true,
            Languages = new() { Language.En },
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
        });
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = "things",
            SpaceName = spaceName,
            Subpath = "/",
            ResourceType = ResourceType.Folder,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse($$"""{"unique_fields": {{uniqueFieldsJson}}}""").RootElement.Clone(),
            },
        });
        return spaceName;
    }

    private static Entry WithIds(string space, string shortname, int count)
        => new()
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = space,
            Subpath = "/things",
            ResourceType = ResourceType.Content,
            IsActive = true,
            OwnerShortname = "dmart",
            CreatedAt = TimeUtils.Now(),
            UpdatedAt = TimeUtils.Now(),
            Payload = new Payload
            {
                ContentType = ContentType.Json,
                Body = JsonDocument.Parse(JsonSerializer.Serialize(
                    new { ids = Enumerable.Range(0, count).Select(i => $"id{i}").ToArray() })).RootElement.Clone(),
            },
        };

    [FactIfPg]
    public async Task A_List_Past_The_Cap_Is_Refused_Not_Probed()
    {
        // UniquenessMaxProbes defaults to 1000; 1500 elements on a single path
        // is 1500 searches, so the write must be refused outright.
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedAsync(spaces, entries, """[["payload.body.ids"]]""");
        try
        {
            var res = await entryService.CreateAsync(WithIds(space, "huge", 1500), "dmart");

            res.IsOk.ShouldBeFalse("a compound past the probe cap must not be accepted");
            res.ErrorCode.ShouldBe(InternalErrorCode.INVALID_DATA);

            // And nothing was written — refusing must not half-apply.
            (await entries.GetAsync(space, "/things", "huge", ResourceType.Content))
                .ShouldBeNull();
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }

    [FactIfPg]
    public async Task An_Ordinary_List_Still_Validates_Normally()
    {
        // The regression guard: the cap must not cost real folders anything.
        // 5 ids is 5 probes, well under it — the create succeeds AND a genuine
        // collision on one of those ids is still caught.
        var (spaces, entries, entryService) = Resolve();
        var space = await SeedAsync(spaces, entries, """[["payload.body.ids"]]""");
        try
        {
            (await entryService.CreateAsync(WithIds(space, "first", 5), "dmart"))
                .IsOk.ShouldBeTrue("a small list must still be allowed");

            // Same ids again under a different shortname — still a violation.
            var dup = await entryService.CreateAsync(WithIds(space, "second", 5), "dmart");
            dup.IsOk.ShouldBeFalse("the cap must not have disabled the constraint");
            dup.ErrorCode.ShouldBe(InternalErrorCode.DATA_SHOULD_BE_UNIQUE);
        }
        finally
        {
            await spaces.DeleteAsync(space);
        }
    }
}
