using System.Text.Json;
using Dmart.Services;
using Shouldly;
using Xunit;

namespace Dmart.Tests.Unit.Services;

// Unit tests for the resume sidecar used by `dmart import --resume`.
// Three properties under test:
//   1. Markers persist atomically (write to .tmp + rename — a crash
//      mid-write must not leave a half-written JSON that fails parse
//      on the next LoadOrCreate).
//   2. LoadOrCreate round-trips a written checkpoint without loss.
//   3. Corrupt sidecar falls back to a fresh checkpoint (so the next
//      `--resume` run isn't blocked by a half-written file).
public sealed class ImportCheckpointStoreTests : IDisposable
{
    private readonly string _dir;

    public ImportCheckpointStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ckpt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void FreshCheckpoint_HasNoMarkers()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");

        store.IsHeadDone().ShouldBeFalse();
        store.IsTailDone("any-space").ShouldBeFalse();
        store.PassesDone.ShouldBeEmpty();
        store.TailDone.ShouldBeEmpty();
    }

    [Fact]
    public void MarkHeadDone_Persists()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkHeadDone();

        File.Exists(path).ShouldBeTrue("the marker should be flushed to disk on Mark");
        var reload = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        reload.IsHeadDone().ShouldBeTrue("reloaded store should see the head marker");
    }

    [Fact]
    public void MarkTailDone_PerSpaceRoundTrip()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkTailDone("applications");
        store.MarkTailDone("products");

        var reload = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        reload.IsTailDone("applications").ShouldBeTrue();
        reload.IsTailDone("products").ShouldBeTrue();
        reload.IsTailDone("management").ShouldBeFalse();
    }

    [Fact]
    public void MarkHeadDone_IsIdempotent()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkHeadDone();
        store.MarkHeadDone();  // calling twice must not duplicate the entry
        store.PassesDone.Count.ShouldBe(1, "head marker should be deduped");
    }

    [Fact]
    public void Clear_RemovesSidecar()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkHeadDone();
        File.Exists(path).ShouldBeTrue();

        store.Clear();
        File.Exists(path).ShouldBeFalse("Clear should delete the sidecar on a clean import");
    }

    [Fact]
    public void CorruptSidecar_FallsBackToFreshStore()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        // Write a deliberately broken JSON.
        File.WriteAllText(path, "{this is not, valid json");

        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.IsHeadDone().ShouldBeFalse("a corrupt sidecar should be treated as no checkpoint");
        store.PassesDone.ShouldBeEmpty();
        // Writing to the recovered store should land atomically and become parseable.
        store.MarkHeadDone();
        var reload = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        reload.IsHeadDone().ShouldBeTrue();
    }

    [Fact]
    public void CorruptSidecar_LogsWarning_WhenLoggerProvided()
    {
        var path = Path.Combine(_dir, ".dmart-import-checkpoint.json");
        File.WriteAllText(path, "{this is not, valid json");

        var logger = new CapturingLogger();
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart", logger);

        store.IsHeadDone().ShouldBeFalse();
        logger.Warnings.ShouldNotBeEmpty("a discarded corrupt checkpoint must be surfaced, not silent");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    // ---- intra-shard progress (per-batch resume) ----------------------
    //
    // A shard that dies at 90% used to replay from entry zero. These cover the
    // offsets that stop that, and the fingerprint that keeps them honest when
    // the work-list underneath them changes.

    [Fact]
    public void TailProgress_RoundTripsAndResumesMidShard()
    {
        var path = Path.Combine(_dir, ".ckpt.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkTailProgress("galleon#2", "fp-a", entries: 1_250_000, attachments: 40);

        var reload = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        reload.TailProgressFor("galleon#2", "fp-a").ShouldBe((1_250_000, 40));
    }

    [Fact]
    public void TailProgress_FingerprintMismatch_RestartsShard()
    {
        var path = Path.Combine(_dir, ".ckpt.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkTailProgress("galleon#2", "fp-a", entries: 1_250_000, attachments: 40);

        // Different work-list (new --from-list, a re-walk, a different
        // --fast-parallelism) ⇒ the offsets index into a list that no longer
        // exists. Restarting is slow; honouring them would skip real entries.
        store.TailProgressFor("galleon#2", "fp-b").ShouldBe((0, 0));
    }

    [Fact]
    public void TailProgress_UnknownShard_StartsAtZero()
    {
        var path = Path.Combine(_dir, ".ckpt.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.TailProgressFor("never-seen", "fp-a").ShouldBe((0, 0));
    }

    [Fact]
    public void TailProgress_NeverMovesBackwards()
    {
        var path = Path.Combine(_dir, ".ckpt.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkTailProgress("galleon", "fp-a", entries: 900, attachments: 10);
        // Pass 3's "entries complete" marker passes attachments: 0 — it must not
        // wipe the attachment offset Pass 4 already recorded.
        store.MarkTailProgress("galleon", "fp-a", entries: 900, attachments: 0);
        store.MarkTailProgress("galleon", "fp-a", entries: 500, attachments: 5);

        store.TailProgressFor("galleon", "fp-a").ShouldBe((900, 10));
    }

    [Fact]
    public void MarkTailDone_DropsIntraShardProgress()
    {
        var path = Path.Combine(_dir, ".ckpt.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkTailProgress("galleon", "fp-a", entries: 900, attachments: 10);
        store.MarkTailDone("galleon");

        var reload = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        reload.IsTailDone("galleon").ShouldBeTrue();
        reload.TailProgressFor("galleon", "fp-a").ShouldBe((0, 0),
            "a finished shard's offsets are dead weight and should not linger in the sidecar");
    }

    [Fact]
    public void LegacySidecar_WithoutTailProgress_LoadsCleanly()
    {
        // A checkpoint written by a build that predates tail_progress must not
        // fail to parse — that would silently restart a multi-hour import.
        var path = Path.Combine(_dir, ".ckpt.json");
        File.WriteAllText(path, """
            {"started_at":"2026-05-26T14:48:19Z","source_path":"/var/lib/dmart/spaces",
             "passes_done":["head"],"tail_done":["products"]}
            """);

        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.IsHeadDone().ShouldBeTrue();
        store.IsTailDone("products").ShouldBeTrue();
        store.TailProgressFor("galleon", "fp-a").ShouldBe((0, 0));
        // And it must still be writable afterwards.
        store.MarkTailProgress("galleon", "fp-a", 10, 0);
        ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart")
            .TailProgressFor("galleon", "fp-a").ShouldBe((10, 0));
    }

    // ---- --drop-indexes recovery record ------------------------------
    //
    // The sidecar is the ONLY durable evidence that indexes are missing after a
    // hard kill between DROP and rebuild. These pin that contract.

    private static ImportCheckpointStore.DroppedIndex Ix(string name) => new()
    {
        Name = name,
        Definition = $"CREATE INDEX {name} ON public.entries USING gin (payload jsonb_path_ops)",
    };

    [Fact]
    public void DroppedIndexes_RoundTripWithDefinitions()
    {
        var path = Path.Combine(_dir, ".ckpt.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkIndexesDropped([Ix("idx_entries_payload_gin"), Ix("idx_entries_tags_gin")]);

        var reload = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        var pending = reload.PendingDroppedIndexes();
        pending.Count.ShouldBe(2);
        pending[0].Definition.ShouldContain("CREATE INDEX",
            customMessage: "the rebuild SQL must survive the crash, not just the name");
    }

    [Fact]
    public void MarkIndexesDropped_IsIdempotent()
    {
        var path = Path.Combine(_dir, ".ckpt.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkIndexesDropped([Ix("a"), Ix("b")]);
        store.MarkIndexesDropped([Ix("b"), Ix("c")]);   // a resumed run re-records

        store.PendingDroppedIndexes().Select(i => i.Name).OrderBy(n => n)
             .ShouldBe(new[] { "a", "b", "c" });
    }

    [Fact]
    public void MarkIndexesRestored_RemovesOnlyWhatCameBack()
    {
        var path = Path.Combine(_dir, ".ckpt.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkIndexesDropped([Ix("a"), Ix("b"), Ix("c")]);
        store.MarkIndexesRestored(["a", "c"]);          // "b" failed to rebuild

        store.PendingDroppedIndexes().Select(i => i.Name).ShouldBe(new[] { "b" });
        ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart")
            .PendingDroppedIndexes().Count.ShouldBe(1, "the survivor must persist for the next run");
    }

    [Fact]
    public void Clear_RefusesWhileIndexesAreStillDropped()
    {
        // A successful import must NOT delete the sidecar while the database is
        // missing indexes — that would strand it with no record and no SQL.
        var path = Path.Combine(_dir, ".ckpt.json");
        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.MarkIndexesDropped([Ix("idx_entries_payload_gin")]);

        store.Clear();
        File.Exists(path).ShouldBeTrue("sidecar is the only recovery record — Clear must refuse");

        store.MarkIndexesRestored(["idx_entries_payload_gin"]);
        store.Clear();
        File.Exists(path).ShouldBeFalse("once the indexes are back, Clear behaves normally");
    }

    [Fact]
    public void LegacySidecar_WithoutDroppedIndexes_LoadsCleanly()
    {
        var path = Path.Combine(_dir, ".ckpt.json");
        File.WriteAllText(path, """
            {"started_at":"2026-05-26T14:48:19Z","source_path":"/var/lib/dmart/spaces",
             "passes_done":["head"],"tail_done":["products"]}
            """);

        var store = ImportCheckpointStore.LoadOrCreate(path, "/var/lib/dmart");
        store.PendingDroppedIndexes().ShouldBeEmpty();
        store.IsTailDone("products").ShouldBeTrue();
    }

    [Fact]
    public void DefaultPathFor_IsSidecarOfFolder()
    {
        var p = ImportCheckpointStore.DefaultPathFor("/var/lib/dmart/spaces");
        p.ShouldBe(Path.Combine("/var/lib/dmart/spaces", ".dmart-import-checkpoint.json"));
    }
}
