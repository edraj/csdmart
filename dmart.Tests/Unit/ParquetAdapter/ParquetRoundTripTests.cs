using System.Text.Json;
using Dmart.ParquetAdapter;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.ParquetAdapter;

// Round-trip tests for the Phase C parquet archival adapter.
// Seeds a tmp terminal folder with a handful of dmart entries (mixed
// resource types, inlined and non-inlined bodies), packs into a
// .parquet file via ParquetArchiver, then extracts back into a fresh
// tmpdir via ParquetExtractor and asserts the meta + body match.
//
// What we DON'T test here:
//   * DuckDB readability of the produced .parquet (shells out; we'd
//     need duckdb on PATH for the test host). The archive/unarchive
//     loop is the load-bearing assertion; external readability is a
//     property of Parquet.Net's serializer which is already tested.
//   * Binary attachment handling — Phase C deliberately leaves
//     binary attachments on disk untouched, so there's nothing for
//     archive/unarchive to round-trip on the attachment side.
public sealed class ParquetRoundTripTests : IDisposable
{
    private readonly string _srcRoot;
    private readonly string _archiveOut;
    private readonly string _extractOut;

    public ParquetRoundTripTests()
    {
        _srcRoot    = Path.Combine(Path.GetTempPath(), $"parq-src-{Guid.NewGuid():N}");
        _archiveOut = Path.Combine(Path.GetTempPath(), $"parq-arc-{Guid.NewGuid():N}.parquet");
        _extractOut = Path.Combine(Path.GetTempPath(), $"parq-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_srcRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_srcRoot, recursive: true); } catch { }
        try { File.Delete(_archiveOut); } catch { }
        try { Directory.Delete(_extractOut, recursive: true); } catch { }
    }

    [Fact]
    public async Task ArchiveAndExtract_RoundTripsTwoEntries()
    {
        SeedEntry("first", "content", uuid: "11111111-1111-1111-1111-111111111111",
            body: """{"hello":"world"}""");
        SeedEntry("second", "ticket", uuid: "22222222-2222-2222-2222-222222222222",
            body: """{"state":"open"}""");

        var archiver = new ParquetArchiver();
        var arc = await archiver.ArchiveFolderAsync(_srcRoot, _archiveOut);
        arc.EntryCount.ShouldBe(2);
        arc.ParquetBytes.ShouldBeGreaterThan(0);
        File.Exists(_archiveOut).ShouldBeTrue("parquet file should be written");

        var extractor = new ParquetExtractor();
        var ext = await extractor.ExtractToFolderAsync(_archiveOut, _extractOut);
        ext.EntriesExtracted.ShouldBe(2);

        // Confirm both meta files round-tripped: parse + check uuid + shortname + resource_type.
        var firstMeta = LoadMeta("first", "content");
        firstMeta.GetProperty("uuid").GetString().ShouldBe("11111111-1111-1111-1111-111111111111");
        firstMeta.GetProperty("shortname").GetString().ShouldBe("first");
        firstMeta.GetProperty("resource_type").GetString().ShouldBe("content");

        var secondMeta = LoadMeta("second", "ticket");
        secondMeta.GetProperty("uuid").GetString().ShouldBe("22222222-2222-2222-2222-222222222222");

        // Inlined body should be reconstructed alongside the .dm/ folder.
        var firstBody = File.ReadAllText(Path.Combine(_extractOut, "first.json"));
        firstBody.ShouldContain("hello");
    }

    [Fact]
    public async Task ArchiveSkipsEntriesWithMissingUuid()
    {
        // Entry missing uuid: deliberately broken, should be skipped
        // and counted in the skipped list — not crash the run.
        var brokenDir = Path.Combine(_srcRoot, ".dm", "broken");
        Directory.CreateDirectory(brokenDir);
        File.WriteAllText(Path.Combine(brokenDir, "meta.content.json"),
            """{"shortname":"broken","resource_type":"content"}""");

        // One valid entry to confirm we still produce a non-empty parquet.
        SeedEntry("good", "content", uuid: Guid.NewGuid().ToString(),
            body: """{"ok":true}""");

        var arc = await new ParquetArchiver().ArchiveFolderAsync(_srcRoot, _archiveOut);
        arc.EntryCount.ShouldBe(1, "only the valid entry should be packed");
        arc.SkippedFiles.Count.ShouldBe(1, "the missing-uuid entry should land in the skipped list");
    }

    [Fact]
    public async Task ArchiveThrows_WhenSourceHasNoDmFolder()
    {
        // No .dm/ subdir → not a terminal folder. The archiver must
        // refuse rather than write an empty parquet file the operator
        // would later confuse with a successful run.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            new ParquetArchiver().ArchiveFolderAsync(_srcRoot, _archiveOut));
    }

    [Fact]
    public async Task ArchiveThrows_WhenSourceFolderMissing()
    {
        await Should.ThrowAsync<DirectoryNotFoundException>(() =>
            new ParquetArchiver().ArchiveFolderAsync(
                Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}"), _archiveOut));
    }

    // ---- helpers --------------------------------------------------

    private void SeedEntry(string shortname, string resourceType, string uuid, string? body)
    {
        var dmDir = Path.Combine(_srcRoot, ".dm", shortname);
        Directory.CreateDirectory(dmDir);
        var meta = new Dictionary<string, object>
        {
            ["uuid"] = uuid,
            ["shortname"] = shortname,
            ["resource_type"] = resourceType,
            ["subpath"] = "/",
            ["is_active"] = true,
            ["owner_shortname"] = "dmart",
            ["payload"] = new Dictionary<string, object>
            {
                ["content_type"] = "json",
                ["schema_shortname"] = "",
                ["body"] = new Dictionary<string, object>(),
            },
        };
        File.WriteAllText(Path.Combine(dmDir, $"meta.{resourceType}.json"),
            JsonSerializer.Serialize(meta));

        if (body is not null)
            File.WriteAllText(Path.Combine(_srcRoot, $"{shortname}.json"), body);
    }

    private JsonElement LoadMeta(string shortname, string resourceType)
    {
        var path = Path.Combine(_extractOut, ".dm", shortname, $"meta.{resourceType}.json");
        File.Exists(path).ShouldBeTrue($"meta should exist at {path}");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }
}
