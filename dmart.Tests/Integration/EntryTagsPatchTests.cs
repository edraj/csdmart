using System.Text.Json;
using Dmart.DataAdapters.Sql;
using Dmart.Models.Core;
using Dmart.Models.Enums;
using Dmart.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins EntryService.PatchTags's contract now that its parsing delegates to
// AttrHelper.ParseStringList — specifically the one deliberate divergence
// from the other call sites: a MALFORMED tags patch falls back to the
// existing tags instead of wiping them, while `"tags": null` explicitly
// clears and an array replaces.
public sealed class EntryTagsPatchTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public EntryTagsPatchTests(DmartFactory factory) => _factory = factory;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<(EntryService Service, EntryRepository Entries, Locator Locator)> SeedAsync()
    {
        _factory.CreateClient(); // ensure AdminBootstrap ran (dmart user exists)
        var entries = _factory.Services.GetRequiredService<EntryRepository>();
        var shortname = $"tagpatch_{Guid.NewGuid():N}"[..20];
        var now = DateTime.UtcNow;
        await entries.UpsertAsync(new Entry
        {
            Uuid = Guid.NewGuid().ToString(),
            Shortname = shortname,
            SpaceName = "management",
            Subpath = "/tags_patch_tests",
            ResourceType = ResourceType.Content,
            OwnerShortname = "dmart",
            IsActive = true,
            Tags = new List<string> { "keep1", "keep2" },
            CreatedAt = now,
            UpdatedAt = now,
        });
        return (_factory.Services.GetRequiredService<EntryService>(), entries,
            new Locator(ResourceType.Content, "management", "/tags_patch_tests", shortname));
    }

    [FactIfPg]
    public async Task Malformed_Tags_Patch_Keeps_Existing_Tags()
    {
        var (service, entries, locator) = await SeedAsync();
        try
        {
            var result = await service.UpdateAsync(locator,
                new Dictionary<string, object> { ["tags"] = Json("42"), ["slug"] = "bumped" }, "dmart");

            result.IsOk.ShouldBeTrue(result.ErrorMessage);
            var reloaded = await entries.GetAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type);
            // A malformed tags patch must fall back to the existing tags, never wipe them.
            reloaded!.Tags.ShouldBe(new[] { "keep1", "keep2" });
        }
        finally
        {
            try { await entries.DeleteAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type); } catch { }
        }
    }

    [FactIfPg]
    public async Task Array_Tags_Patch_Replaces_And_Null_Clears()
    {
        var (service, entries, locator) = await SeedAsync();
        try
        {
            var replaced = await service.UpdateAsync(locator,
                new Dictionary<string, object> { ["tags"] = Json("""[" new1 ", "new2", ""]""") }, "dmart");
            replaced.IsOk.ShouldBeTrue(replaced.ErrorMessage);
            (await entries.GetAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type))!
                .Tags.ShouldBe(new[] { "new1", "new2" });  // array replaces, trimmed, empties dropped

            var cleared = await service.UpdateAsync(locator,
                new Dictionary<string, object> { ["tags"] = Json("null") }, "dmart");
            cleared.IsOk.ShouldBeTrue(cleared.ErrorMessage);
            (await entries.GetAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type))!
                .Tags.ShouldBeEmpty("explicit null clears");
        }
        finally
        {
            try { await entries.DeleteAsync(locator.SpaceName, locator.Subpath, locator.Shortname, locator.Type); } catch { }
        }
    }
}
