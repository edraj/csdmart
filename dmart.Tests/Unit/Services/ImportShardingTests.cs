using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for how `dmart import --fast --fast-parallelism=N` partitions work
// into shards, and for the fingerprint that guards the intra-shard resume
// offsets.
//
// The property that matters: shards are balanced by ENTRY COUNT, not by space
// count. Real deployments have wildly unequal spaces — a 4-space tree where one
// space holds three quarters of the entries used to get one shard per space, so
// the three small spaces finished in minutes and the dominant space then ran
// single-threaded for hours with the other workers idle.
public sealed class ImportShardingTests
{
    // Tail metas the importer recognises: "{space}/{subpath}/.dm/{sn}/meta.content.json".
    private static List<ImportEntryRef> Metas(string space, int count) =>
        Enumerable.Range(0, count)
            .Select(i => ImportEntryRef.FromFile($"{space}/items/.dm/e{i}/meta.content.json", $"/src/{space}/{i}"))
            .ToList();

    private static List<IGrouping<string, ImportEntryRef>> Groups(params (string Space, int Count)[] spaces) =>
        spaces.SelectMany(s => Metas(s.Space, s.Count))
              .GroupBy(e => e.FullName[..e.FullName.IndexOf('/')], StringComparer.Ordinal)
              .ToList();

    [Fact]
    public void DominantSpace_IsSplitAcrossEveryWorker()
    {
        // 3.4M-of-4.5M shape from the field report, scaled down.
        var shards = ImportExportService.BuildShards(
            Groups(("galleon", 3400), ("purchase", 700), ("mbb", 300), ("products", 100)),
            workers: 4, allowSubSharding: true);

        var galleonShards = shards.Where(s => s.Key.StartsWith("galleon", StringComparison.Ordinal)).ToList();
        galleonShards.Count.ShouldBeGreaterThan(1,
            "the space holding most of the tree must not be pinned to one worker");
        galleonShards.Count.ShouldBeLessThanOrEqualTo(4, "never more sub-shards than workers");

        // Small spaces stay whole — splitting them costs a session and buys nothing.
        shards.ShouldContain(s => s.Key == "products");
        shards.ShouldContain(s => s.Key == "mbb");
    }

    [Fact]
    public void EvenlySizedSpaces_StayOneShardEach()
    {
        var shards = ImportExportService.BuildShards(
            Groups(("a", 1000), ("b", 1000), ("c", 1000), ("d", 1000)),
            workers: 4, allowSubSharding: true);

        shards.Count.ShouldBe(4);
        shards.Select(s => s.Key).OrderBy(k => k).ShouldBe(new[] { "a", "b", "c", "d" });
    }

    [Fact]
    public void SingleSpace_SaturatesTheWorkerPool()
    {
        // The `--space=X --subpath=Y` remap case: one space, N workers.
        var shards = ImportExportService.BuildShards(
            Groups(("galleon", 4000)), workers: 4, allowSubSharding: true);

        shards.Count.ShouldBe(4);
        shards.Sum(s => s.Entries.Count).ShouldBe(4000, "partitioning must not lose or duplicate entries");
    }

    [Fact]
    public void Partitioning_IsTotalAndDisjoint()
    {
        var shards = ImportExportService.BuildShards(
            Groups(("galleon", 3400), ("purchase", 700), ("mbb", 300), ("products", 100)),
            workers: 4, allowSubSharding: true);

        var all = shards.SelectMany(s => s.Entries.Select(e => e.FullName)).ToList();
        all.Count.ShouldBe(4500);
        all.Distinct().Count().ShouldBe(4500, "no entry may land in two shards");
    }

    [Fact]
    public void SubShardingDisabled_KeepsOneShardPerSpace()
    {
        // Zip sources: an entry's body may live in a sibling archive member, so
        // splitting a space could strand it in another shard.
        var shards = ImportExportService.BuildShards(
            Groups(("galleon", 3400), ("products", 100)), workers: 4, allowSubSharding: false);

        shards.Select(s => s.Key).OrderBy(k => k).ShouldBe(new[] { "galleon", "products" });
    }

    [Fact]
    public void SerialRun_KeepsOneShardPerSpace()
    {
        var shards = ImportExportService.BuildShards(
            Groups(("galleon", 3400), ("products", 100)), workers: 1, allowSubSharding: true);

        shards.Select(s => s.Key).OrderBy(k => k).ShouldBe(new[] { "galleon", "products" });
    }

    [Fact]
    public void ShardKeys_AreStableAcrossRuns()
    {
        // Resume matches shards by key, so the same input + the same
        // --fast-parallelism must always produce the same partitioning.
        var a = ImportExportService.BuildShards(Groups(("galleon", 2000), ("mbb", 200)), 4, true);
        var b = ImportExportService.BuildShards(Groups(("galleon", 2000), ("mbb", 200)), 4, true);

        a.Select(s => s.Key).ShouldBe(b.Select(s => s.Key));
        foreach (var (left, right) in a.Zip(b))
            left.Entries.Select(e => e.FullName).ShouldBe(right.Entries.Select(e => e.FullName),
                "membership AND order must be reproducible — the resume offsets are positional");
    }

    // ---- fingerprint --------------------------------------------------

    [Fact]
    public void ShardShape_CountsEntryAndAttachmentMetas()
    {
        var shard = Metas("galleon", 3);
        shard.Add(ImportEntryRef.FromFile(
            "galleon/items/.dm/e0/attachments.media/meta.pic.json", "/src/pic"));

        var (fingerprint, entryMetas, attachmentMetas) = ImportExportService.ShardShape(shard, withFingerprint: true);

        entryMetas.ShouldBe(3);
        attachmentMetas.ShouldBe(1);
        fingerprint.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Fingerprint_IsStableForTheSameList()
    {
        ImportExportService.ShardShape(Metas("galleon", 500), true).Fingerprint
            .ShouldBe(ImportExportService.ShardShape(Metas("galleon", 500), true).Fingerprint);
    }

    [Fact]
    public void Fingerprint_ChangesWhenTheListChanges()
    {
        var baseline = ImportExportService.ShardShape(Metas("galleon", 500), true).Fingerprint;

        // An extra file — a re-walk that picked up new entries.
        var grown = Metas("galleon", 500);
        grown.Add(ImportEntryRef.FromFile("galleon/items/.dm/new/meta.content.json", "/src/new"));
        ImportExportService.ShardShape(grown, true).Fingerprint.ShouldNotBe(baseline);

        // Same files, different order — the offsets are positional, so this
        // must invalidate them too.
        var reordered = Metas("galleon", 500);
        reordered.Reverse();
        ImportExportService.ShardShape(reordered, true).Fingerprint.ShouldNotBe(baseline);
    }

    [Fact]
    public void Fingerprint_IsSkippedWhenNotResuming()
    {
        // A non-resume import shouldn't pay to hash millions of paths nobody reads.
        var (fingerprint, entryMetas, _) = ImportExportService.ShardShape(Metas("galleon", 10), withFingerprint: false);

        fingerprint.ShouldBeEmpty();
        entryMetas.ShouldBe(10, "the counts are still needed for the progress denominator");
    }
}
