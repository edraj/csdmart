using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dmart.Models.Core;
using Dmart.Models.Json;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Json;

// Lock in that every shipped seed/spaces/{space}/.dm/meta.space.json
// deserializes cleanly through the same path TryImportSpaceAsync uses, and
// that an omitted optional string arrives as the declared "" rather than null.
//
// `personal/.dm/meta.space.json` omits `root_registration_signature` and
// `icon`, and Space declares both as non-nullable strings with a `= ""`
// initializer. That initializer used to be discarded: with any init-only
// property, STJ source-gen routes deserialization through a
// parameterized-constructor path that assigns every such property from an args
// array, passing default(T) -- null here -- for whatever the payload omitted.
// The DB columns are NOT NULL, so the upsert blew up until
// SpaceRepository.UpsertAsync added `?? ""` coercions.
//
// This test used to assert the broken shape (omitted -> null), and said that if
// the initializer skip were ever fixed the backstops could go. Half of that has
// happened: the properties are `set` now, the initializers survive, and omitted
// fields arrive as "". The backstops still cannot go, because a payload that
// spells out `"icon": null` still lands a null -- System.Text.Json does not
// enforce nullability at runtime. See ModelDefaultsTests.
public sealed class SeedSpaceMetaTests
{
    [Theory]
    [InlineData("management")]
    [InlineData("applications")]
    [InlineData("personal")]
    public void Seed_SpaceMeta_Deserializes(string spaceName)
    {
        var path = Path.Combine(
            FindRepoRoot(), "seed", "spaces", spaceName, ".dm", "meta.space.json");
        File.Exists(path).ShouldBeTrue($"missing {path}");

        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json)?.AsObject();
        node.ShouldNotBeNull();

        // Capture which optional string fields the JSON omits so we can assert
        // the STJ-source-gen-skips-initializer behavior below.
        var jsonHasRrs = node!.ContainsKey("root_registration_signature");
        var jsonHasPw  = node.ContainsKey("primary_website");
        var jsonHasIcon = node.ContainsKey("icon");

        // Mirrors TryImportSpaceAsync's pre-deserialize fix-up.
        node["space_name"] = spaceName;
        node["shortname"] ??= spaceName;
        node["subpath"] = "/";
        if (string.IsNullOrEmpty(node["owner_shortname"]?.GetValue<string>()))
            node["owner_shortname"] = "dmart";

        Space? space = null;
        System.Exception? ex = null;
        try { space = node.Deserialize(DmartJsonContext.Default.Space); }
        catch (System.Exception e) { ex = e; }
        ex.ShouldBeNull($"deserialize threw for {spaceName}: {ex?.Message}");
        space.ShouldNotBeNull();
        space!.Shortname.ShouldBe(spaceName);

        // Present-in-JSON keeps the JSON value; omitted falls back to the
        // declared "" rather than null. Asserted per-field so a failure names
        // which one drifted.
        space.RootRegistrationSignature.ShouldNotBeNull(
            $"{spaceName}: root_registration_signature is null - the `= \"\"` initializer is being discarded again");
        space.PrimaryWebsite.ShouldNotBeNull(
            $"{spaceName}: primary_website is null - the `= \"\"` initializer is being discarded again");
        space.Icon.ShouldNotBeNull(
            $"{spaceName}: icon is null - the `= \"\"` initializer is being discarded again");

        if (!jsonHasRrs) space.RootRegistrationSignature.ShouldBe("");
        if (!jsonHasPw) space.PrimaryWebsite.ShouldBe("");
        if (!jsonHasIcon) space.Icon.ShouldBe("");
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "dmart.csproj")))
            d = d.Parent;
        d.ShouldNotBeNull();
        return d!.FullName;
    }
}
