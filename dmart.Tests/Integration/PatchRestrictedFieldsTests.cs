using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dmart.Models.Api;
using Dmart.Models.Enums;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Integration;

// Pins three pieces of Python parity in the /managed/request UPDATE flow:
//
//  1. `tags` arriving as a JSON array round-trips. After source-gen
//     deserialization, `Attributes["tags"]` is a JsonElement (not a
//     List<object>), and the prior `is IEnumerable<object>` pattern in
//     EntryService.ApplyPatch silently dropped it. PatchTags now handles
//     JsonElement explicitly.
//
//  2. `is_active` as a JSON bool round-trips, for the same reason as (1):
//     the value lands as a JsonElement of ValueKind.True/False, not a
//     native bool. PatchBool handles JsonElement now.
//
//  3. `owner_shortname` is silently ignored on regular UPDATE — Python
//     lists it in Meta.restricted_fields. Without this gate the new
//     `owner_shortname = EXCLUDED.owner_shortname` clause in
//     EntryRepository.UpsertAsync would let any authenticated caller
//     transfer ownership through their patch body.
public class PatchRestrictedFieldsTests : IClassFixture<DmartFactory>
{
    private readonly DmartFactory _factory;
    public PatchRestrictedFieldsTests(DmartFactory factory) => _factory = factory;

    [FactIfPg]
    public async Task Patch_Tags_Array_RoundTrips()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"tagstest-{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        await CreateContent(client, space, subpath, shortname,
            attributes: new() { ["displayname"] = "tags probe" });

        try
        {
            var update = BuildUpdate(space, subpath, shortname, new()
            {
                // List<string> serializes to JSON array, deserializes to JsonElement
                // on the server — exactly the path the PatchTags fix exercises.
                ["tags"] = new List<string> { "alpha", "beta" },
            });
            (await client.PostAsJsonAsync("/managed/request", update, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var attrs = await GetAttributes(client, space, subpath, shortname);
            // tags should be present and contain both values.
            attrs.ShouldContainKey("tags");
            var tagsEl = (JsonElement)attrs["tags"];
            tagsEl.ValueKind.ShouldBe(JsonValueKind.Array);
            var values = tagsEl.EnumerateArray().Select(e => e.GetString()).ToList();
            values.ShouldContain("alpha");
            values.ShouldContain("beta");
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task Patch_IsActive_Bool_RoundTrips()
    {
        var (client, _, _, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"activetst-{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        await CreateContent(client, space, subpath, shortname,
            attributes: new() { ["displayname"] = "is_active probe" });

        try
        {
            // Sanity: created entries default to is_active=true.
            var preAttrs = await GetAttributes(client, space, subpath, shortname);
            ExtractBool(preAttrs, "is_active").ShouldBe(true);

            var update = BuildUpdate(space, subpath, shortname, new()
            {
                ["is_active"] = false,
            });
            (await client.PostAsJsonAsync("/managed/request", update, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var postAttrs = await GetAttributes(client, space, subpath, shortname);
            ExtractBool(postAttrs, "is_active").ShouldBe(false);
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    [FactIfPg]
    public async Task Patch_OwnerShortname_Is_Silently_Ignored()
    {
        var (client, _, ownerShortname, cleanup) = await _factory.CreateLoggedInUserAsync();
        var shortname = $"ownrtest-{Guid.NewGuid():N}".Substring(0, 16);
        var space = "test";
        var subpath = "/itest";

        await CreateContent(client, space, subpath, shortname,
            attributes: new() { ["displayname"] = "owner probe" });

        try
        {
            // Sneak in a different owner_shortname via the patch body. Python's
            // Meta.restricted_fields blocks this; the C# port must too.
            var update = BuildUpdate(space, subpath, shortname, new()
            {
                ["owner_shortname"] = "intruder",
                ["displayname"] = "still updating",
            });
            (await client.PostAsJsonAsync("/managed/request", update, DmartJsonContext.Default.Request))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            var attrs = await GetAttributes(client, space, subpath, shortname);
            attrs.ShouldContainKey("owner_shortname");
            // Owner must remain the original user, not the value sent in the patch.
            // attrs[...] is a JsonElement; GetString() unwraps the JSON quotes.
            ((JsonElement)attrs["owner_shortname"]).GetString().ShouldBe(ownerShortname);
        }
        finally
        {
            await DeleteContent(client, space, subpath, shortname);
            await cleanup();
        }
    }

    // -- helpers --

    private static Request BuildUpdate(string space, string subpath, string shortname,
        Dictionary<string, object> attributes) =>
        new()
        {
            RequestType = RequestType.Update,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = shortname,
                    Attributes = attributes,
                },
            },
        };

    private static async Task CreateContent(HttpClient client, string space, string subpath,
        string shortname, Dictionary<string, object> attributes)
    {
        var req = new Request
        {
            RequestType = RequestType.Create,
            SpaceName = space,
            Records = new()
            {
                new Record
                {
                    ResourceType = ResourceType.Content,
                    Subpath = subpath,
                    Shortname = shortname,
                    Attributes = attributes,
                },
            },
        };
        var resp = await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException($"Create failed: {resp.StatusCode}\n{body}");
        }
    }

    private static async Task DeleteContent(HttpClient client, string space, string subpath, string shortname)
    {
        try
        {
            var req = new Request
            {
                RequestType = RequestType.Delete,
                SpaceName = space,
                Records = new()
                {
                    new Record
                    {
                        ResourceType = ResourceType.Content,
                        Subpath = subpath,
                        Shortname = shortname,
                    },
                },
            };
            await client.PostAsJsonAsync("/managed/request", req, DmartJsonContext.Default.Request);
        }
        catch { /* best-effort cleanup */ }
    }

    private static async Task<Dictionary<string, object>> GetAttributes(
        HttpClient client, string space, string subpath, string shortname)
    {
        var resp = await client.GetAsync($"/managed/entry/content/{space}/{subpath.TrimStart('/')}/{shortname}");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        // /managed/entry returns a single record's attributes flattened at
        // the top level (not wrapped in Response). Parse as a JsonElement
        // and project into Dictionary<string, object>.
        var root = JsonDocument.Parse(json).RootElement;
        var attrs = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var prop in root.EnumerateObject())
            attrs[prop.Name] = prop.Value;
        return attrs;
    }

    private static bool ExtractBool(Dictionary<string, object> attrs, string key)
    {
        attrs.ShouldContainKey(key);
        var el = (JsonElement)attrs[key];
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new Xunit.Sdk.XunitException($"{key} is not bool: ValueKind={el.ValueKind}"),
        };
    }
}
