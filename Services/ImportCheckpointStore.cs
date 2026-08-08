using System.Text.Json;
using System.Text.Json.Serialization;
using Dmart.Models.Json;

namespace Dmart.Services;

// Sidecar JSON file that records pass-level completion markers for
// `dmart import`. Lets a crashed import resume from the last committed
// pass instead of replaying the whole 24M-entry tree.
//
// The five-pass architecture in ImportExportService.ImportFromEntriesAsync
// is already idempotent under preserveExisting=true (ON CONFLICT DO
// NOTHING on insert; per-space FastImportScope commits independently
// in the parallel path). What it lacks is a record of "I finished
// pass X for space Y". CheckpointStore writes a marker after each
// commit so the next `--resume` run skips the pass entirely instead
// of re-doing the bulk COPY just to land zero new rows.
//
// File layout:
//   {
//     "started_at": "2026-05-26T14:48:19Z",
//     "source_path": "/var/lib/dmart/spaces",
//     "passes_done": ["head"],          // head = users + spaces + roles + permissions
//     "tail_done":   ["applications", "products"],  // per-shard tail markers
//     "tail_progress": {                // committed prefix of an UNFINISHED shard
//       "galleon#2": { "fingerprint": "…", "entries": 1250000, "attachments": 0 }
//     }
//   }
//
// Atomic writes via `.tmp` + rename — same pattern as PreflightService's
// JSON rewriter and the prototype regen-*.sh scripts.
//
// Scope today:
//   * Filesystem imports only. Zip imports don't get resume because the
//     natural sidecar location (next to the zip file) is operator-
//     unfriendly when the zip is on remote storage. Reaching the 24M
//     target via fs+--fast is the canonical path; zip is a smaller-
//     scale convenience.
//   * Tail-pass resume requires `--fast --fast-parallelism=N>1`. The
//     serial path doesn't have per-space transaction boundaries, so a
//     mid-run crash can leave partial state for the active space —
//     resume would re-do that space which is fine, but there's no
//     speed-up because the entire pass replays anyway.
public sealed class ImportCheckpointStore
{
    // How far into an unfinished shard the last run got. Positional: the
    // counts index into the shard's ordered work-list, so they only mean
    // anything against that exact list — hence the fingerprint, which the
    // reader must match before trusting the offsets.
    public sealed class ShardProgress
    {
        [JsonPropertyName("fingerprint")] public string Fingerprint  { get; set; } = "";
        [JsonPropertyName("entries")]     public int    Entries      { get; set; }
        [JsonPropertyName("attachments")] public int    Attachments  { get; set; }
    }

    // An index `--drop-indexes` removed for the duration of the load, with the
    // exact `CREATE INDEX` needed to put it back. Written BEFORE the drop, so
    // a hard crash between drop and rebuild still leaves a durable record of
    // what is missing and how to restore it.
    public sealed class DroppedIndex
    {
        [JsonPropertyName("name")]       public string Name       { get; set; } = "";
        [JsonPropertyName("definition")] public string Definition { get; set; } = "";
    }

    [JsonIgnore] private readonly string _path;
    [JsonIgnore] private readonly object _lock = new();

    [JsonPropertyName("started_at")]   public string StartedAt   { get; set; } = "";
    [JsonPropertyName("source_path")]  public string SourcePath  { get; set; } = "";
    [JsonPropertyName("passes_done")]  public List<string> PassesDone { get; set; } = new();
    [JsonPropertyName("tail_done")]    public List<string> TailDone   { get; set; } = new();
    [JsonPropertyName("tail_progress")]
    public Dictionary<string, ShardProgress> TailProgress { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyName("dropped_indexes")]
    public List<DroppedIndex> DroppedIndexes { get; set; } = new();

    // Parameterless ctor for JSON deserialization; never use directly.
    public ImportCheckpointStore() { _path = ""; }

    private ImportCheckpointStore(string path)
    {
        _path = path;
    }

    // Load an existing checkpoint or return a fresh one. Path-aware:
    // when no file exists the returned store is empty and writes will
    // create the file on first MarkXxxDone().
    public static ImportCheckpointStore LoadOrCreate(
        string path, string sourcePath, Microsoft.Extensions.Logging.ILogger? log = null)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize(json, DmartJsonContext.Default.ImportCheckpointStore);
                if (loaded is not null)
                {
                    // The constructor used by JSON deserialization can't take
                    // _path (no [JsonConstructor] hook on the non-default
                    // ctor), so we patch it via reflection-free pattern: new
                    // wrapper that copies the lists into a path-aware store.
                    var store = new ImportCheckpointStore(path)
                    {
                        StartedAt = loaded.StartedAt,
                        SourcePath = loaded.SourcePath,
                        PassesDone = loaded.PassesDone,
                        TailDone = loaded.TailDone,
                        // A checkpoint written before tail_progress existed
                        // deserializes it as null — normalize so every caller
                        // can index it without a null check.
                        TailProgress = loaded.TailProgress is null
                            ? new(StringComparer.Ordinal)
                            : new(loaded.TailProgress, StringComparer.Ordinal),
                        DroppedIndexes = loaded.DroppedIndexes ?? new(),
                    };
                    return store;
                }
            }
            catch (Exception ex)
            {
                // Corrupt checkpoint — treat as a fresh start. The operator
                // can delete it manually if they want a clean run; we don't
                // touch the source files on a parse failure. Surface it so a
                // multi-hour import silently restarting from scratch is
                // visible rather than a mystery.
                if (log is not null)
                    Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(
                        log, ex,
                        "import: corrupt checkpoint at {Path} — ignoring it and starting fresh; delete the file to silence this",
                        path);
            }
        }
        return new ImportCheckpointStore(path)
        {
            StartedAt = DateTimeOffset.UtcNow.ToString("o"),
            SourcePath = sourcePath,
        };
    }

    // ---- Read-side queries used by the import orchestrator ---------

    public bool IsHeadDone() => PassesDone.Contains("head");

    public bool IsTailDone(string spaceName) => TailDone.Contains(spaceName);

    // Committed prefix of an unfinished shard: how many of its entry metas
    // (Pass 3) and attachment metas (Pass 4) an earlier run already committed.
    //
    // Returns (0, 0) — restart the shard — whenever the recorded fingerprint
    // doesn't match the caller's. The offsets are positions in an ordered list,
    // so they're only sound against the identical list in the identical order;
    // a different --from-list, a re-walk that picked up new files, or a
    // different --fast-parallelism (which re-partitions a space into different
    // sub-shards) all invalidate them. Restarting is merely slow — honouring a
    // stale offset would silently skip real entries, so mismatch always loses.
    public (int Entries, int Attachments) TailProgressFor(string shardKey, string fingerprint)
    {
        lock (_lock)
        {
            if (!TailProgress.TryGetValue(shardKey, out var p)) return (0, 0);
            if (!string.Equals(p.Fingerprint, fingerprint, StringComparison.Ordinal)) return (0, 0);
            return (p.Entries, p.Attachments);
        }
    }

    // Indexes an earlier run dropped and did not put back. Non-empty here means
    // the database is currently missing them — either this run dropped them, or
    // a previous one died before rebuilding.
    public IReadOnlyList<DroppedIndex> PendingDroppedIndexes()
    {
        lock (_lock) return DroppedIndexes.ToList();
    }

    // ---- Write-side markers ----------------------------------------

    // Record what is about to be dropped. MUST be called and flushed BEFORE the
    // DROP runs — the whole point is that a crash in between leaves evidence.
    public void MarkIndexesDropped(IEnumerable<DroppedIndex> indexes)
    {
        lock (_lock)
        {
            foreach (var ix in indexes)
                if (!DroppedIndexes.Any(d => string.Equals(d.Name, ix.Name, StringComparison.Ordinal)))
                    DroppedIndexes.Add(ix);
            FlushUnsafe();
        }
    }

    // Clear the record once the indexes are actually back.
    public void MarkIndexesRestored(IEnumerable<string> names)
    {
        lock (_lock)
        {
            var done = new HashSet<string>(names, StringComparer.Ordinal);
            DroppedIndexes.RemoveAll(d => done.Contains(d.Name));
            FlushUnsafe();
        }
    }

    // Record the committed prefix of a still-running shard. Called after each
    // batch commit lands, so a crash loses at most one batch instead of the
    // whole shard. Monotonic: a lower offset than the one already recorded is
    // ignored, so an out-of-order write can never move a shard backwards.
    public void MarkTailProgress(string shardKey, string fingerprint, int entries, int attachments)
    {
        lock (_lock)
        {
            if (TailProgress.TryGetValue(shardKey, out var p)
                && string.Equals(p.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                if (entries <= p.Entries && attachments <= p.Attachments) return;
                p.Entries = Math.Max(p.Entries, entries);
                p.Attachments = Math.Max(p.Attachments, attachments);
            }
            else
            {
                TailProgress[shardKey] = new ShardProgress
                {
                    Fingerprint = fingerprint, Entries = entries, Attachments = attachments,
                };
            }
            FlushUnsafe();
        }
    }

    public void MarkHeadDone()
    {
        lock (_lock)
        {
            if (!PassesDone.Contains("head")) PassesDone.Add("head");
            FlushUnsafe();
        }
    }

    public void MarkTailDone(string spaceName)
    {
        lock (_lock)
        {
            if (!TailDone.Contains(spaceName)) TailDone.Add(spaceName);
            // The shard finished, so its intra-shard offsets are dead weight —
            // dropping them keeps the sidecar from growing a row per shard for
            // the life of the import.
            TailProgress.Remove(spaceName);
            FlushUnsafe();
        }
    }

    // Best-effort cleanup once the whole import completed successfully.
    // Operator may also delete this file manually if they want.
    public void Clear()
    {
        lock (_lock)
        {
            // Refuse while indexes are still dropped: the sidecar is the only
            // durable record of what is missing and how to rebuild it. Deleting
            // it on an otherwise-successful import would strand the database
            // without its indexes and without the recovery SQL.
            if (DroppedIndexes.Count > 0) return;
            try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        }
    }

    // ---- Internal helpers ------------------------------------------

    // Called with _lock held. Atomic via .tmp + rename so a crash
    // mid-write can't leave a half-written checkpoint that fails to
    // parse on next startup (which the LoadOrCreate path would treat
    // as "start over").
    private void FlushUnsafe()
    {
        if (string.IsNullOrEmpty(_path)) return;
        var json = JsonSerializer.Serialize(this, DmartJsonContext.Default.ImportCheckpointStore);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    // Default checkpoint file location for filesystem imports.
    public static string DefaultPathFor(string sourceFolder)
        => Path.Combine(sourceFolder, ".dmart-import-checkpoint.json");
}
