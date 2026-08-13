# Parquet export/import — design

A second export format alongside the current JSON-tree zip, for installations
where the data spans gigabytes, with **incremental** exports so a daily or
weekly pipeline ships only what changed.

Status: **design only, nothing implemented.** Decisions taken so far are
recorded in §6. Everything marked *[measured]* was run, not estimated.

> Assumes the SQLite backend (#152, merged). The exporter has to work on both
> backends, which is why the dual-backend obligations — tombstone DDL in two
> schema files, one canonical array encoding — are called out throughout.

---

## 1. The current export did not scale, for three reasons — and only one was the format

Worth separating, because switching to Parquet addresses only the third. **The
first two were bugs, and are fixed** (#156, merged) — recorded here because
they explain why the format was never the first thing in the way.

**~~It buffered the whole archive in RAM.~~** `ExportAsync` built the entire
`ZipArchive` in a `MemoryStream`, so a 4 GB export needed 4 GB of memory. It
now spools to a temp file and offers `ExportToAsync(Stream, …)` for callers
with a destination. Note this was never a format problem: Parquet written into
a `MemoryStream` OOMs just as well.

**~~It silently truncated at 100,000 entries.~~** `QueryLimit = 100_000` bounded
the query driving the export, so a larger space exported *partially* and
reported success. It now pages to exhaustion, forcing a `uuid` sort because the
default `updated_at DESC` is not a total order and paging on it would drop rows
just as quietly.

**Attachment media is still loaded whole, per parent** — the one of the three
that remains, and the one this design actually addresses.
`ListForParentWithMediaAsync` is required here (the metadata-only projection
returns `NULL::bytea` and would ship attachment metadata with no file behind
it — a bug the comment there records as already fixed). But it means every
attachment's bytes land in a `byte[]`, then in the in-memory zip. In a
multi-GB installation `attachments.media` is most likely the bulk of those
gigabytes, since it is a `bytea` column in the database rather than files on
disk.

The new path must stream, page, and never hold more than one row group plus
one blob in memory.

## 2. Is Parquet even possible here? *[measured]*

The constraint that could have killed this: Native AOT, zero new
IL2026/IL2027/IL3050, no reflection-based serialization.

**Parquet.Net 5.6.1 works, with one caveat.**

- Its compression dependency is `ZstdSharp.Port` — **pure managed**. No native
  sidecar to place, unlike `libe_sqlite3.so`. One less packaging problem.
- Publishing with `IlcTreatWarningsAsErrors=true` produces **exactly one**
  error: `IL3050` on `Type.MakeGenericType` in
  `Parquet.TypeExtensions.GetNullable(Type)`.
- That warning is a **false positive for statically-declared schemas**.
  Verified by building a native binary with a `DataField<DateTime?>` column —
  the precise `Nullable<T>` path the flag is about — and running it: 50,000
  rows, 16,667 nulls, read back correctly. The instantiation exists in the
  compiled code because it is statically referenced.

  It would only bite if the schema were built from a type unknown at compile
  time. We never do that: every column is declared in C#.
- **Use the low-level API only.** `ParquetWriter` + `DataColumn` + explicit
  `ParquetSchema`. `ParquetSerializer.SerializeAsync<T>` is the reflection
  path and is exactly what the project's constraints exclude.

So this needs a **narrowly scoped `IL3050` suppression with the runtime
evidence in its justification**. That is a real cost and should be a conscious
decision, not a footnote — it is the first suppression of an AOT analyzer in
this codebase rather than of a security analyzer.

## 3. The size win *[measured]*

50,000 entries, each with a small JSON payload, in the layout the current
export writes:

| | total | per row |
|---|---:|---:|
| raw JSON tree on disk | 16.1 MB | 322 B |
| **current `.zip` export** | **20.2 MB** | **404 B** |
| **Parquet + zstd** | **1.3 MB** | **27 B** |

**15× smaller than the current zip.** Note the zip is *larger than the tree it
compresses*: 50,000 per-file local headers and central-directory entries, and
deflate compressing each tiny file independently with no shared dictionary. At
this scale the container format is working against you.

The 27 B/row figure is for metadata. Media bytes are incompressible and are
handled separately — see §4.3.

## 4. Format

### 4.1 A directory, not a file

```
dmart-export-<space>-<utc-timestamp>/
  manifest.json
  entries/space_name=<s>/part-00000.parquet
  attachments/space_name=<s>/part-00000.parquet
  histories/space_name=<s>/part-00000.parquet
  spaces/part-00000.parquet
  users/part-00000.parquet
  roles/part-00000.parquet
  permissions/part-00000.parquet
  deletions/part-00000.parquet          # increments only
  blobs/<sha256[0:2]>/<sha256>
```

A directory rather than one archive because all three of the things we need
fall out of it: writers stream row group by row group with bounded memory,
readers project single columns without decompressing the rest, and increments
reference blobs by hash without re-shipping them.

`space_name=<s>` is Hive-style partitioning — what DuckDB and Spark expect
(`read_parquet('entries/**/*.parquet', hive_partitioning=true)`) and also the
natural unit for a per-space restore. This is the "restore first, analytics
later" compromise: one layout serves both.

Partitioning by date is deliberately **not** used. Increments are already
separate directories; adding a date dimension inside a full export would
fragment row groups for no gain.

### 4.2 Schema

Mirror the SQL columns, one Parquet table per SQL table, with three rules:

- **JSON columns stay opaque strings** — `payload`, `acl`, `relationships`.
  Lossless for restore, and DuckDB reads them fine with `json_extract`.
  Exploding payload into typed columns would require per-schema knowledge and
  break round-tripping; if analytics later wants that, it is a view over this,
  not a change to it.
- **Array columns become native Parquet lists** — `tags`, `query_policies` as
  `list<string>`. Better for analytics than a JSON string, and still lossless.
  This is also the one place the two backends already differ (`text[]` vs a
  JSON array in TEXT), so the exporter reads them through `DbParams.ReadTextArray`
  and writes one canonical form.
- **Timestamps as Parquet TIMESTAMP (micros, UTC)**, not strings.

Row group target ~50–100k rows. That is the unit of both column projection and
writer memory, so it is what bounds the export's footprint.

### 4.3 Blobs are content-addressed, and that is the incremental lever

`attachments.media` bytes do not belong in Parquet row groups: they are
incompressible, they destroy row-group size predictability, and they defeat
column projection.

Instead: each blob is written to `blobs/<sha256[0:2]>/<sha256>`, and the
attachment row carries `media_sha256` and `media_size` instead of the bytes.

The payoff is on increments. **An attachment whose metadata changed but whose
bytes did not ships zero blob bytes**, and an unchanged attachment ships
nothing at all. For the stated goal — a lightweight daily or weekly pipeline
over a multi-GB store — this matters more than the 15× metadata win, because
media is where the gigabytes are.

It also deduplicates within a single export: the same file attached to twenty
entries is stored once.

## 5. Incremental

### 5.1 Watermark

The manifest carries the watermark and the id of the manifest it follows.
Selection is `updated_at >= watermark` per table.

**Inclusive, deliberately overlapping.** The import is an idempotent upsert, so
re-shipping a boundary row is free; missing one is silent corruption. Given
that asymmetry, bias every ambiguity toward overlap — including taking the
watermark from the *start* of the previous export rather than its end.

**This needs an index that does not exist.** `entries` has indexes on
`space_name`, `subpath`, `owner_shortname`, `resource_type` and four GIN
indexes, but **none on `updated_at`**. An incremental scan seq-scans today.
Adding `idx_entries_updated_at` (and the same on `attachments`) is a
prerequisite, not an optimization.

### 5.2 Deletions — a tombstone table

*Decision taken: tombstone table (§6).*

A row deleted since the last run is simply absent, and absence is
indistinguishable from unchanged. Without tombstones an incremental consumer
drifts from source permanently and never notices.

```sql
CREATE TABLE deletions (
  id             BIGSERIAL PRIMARY KEY,
  space_name     TEXT NOT NULL,
  subpath        TEXT NOT NULL,
  shortname      TEXT NOT NULL,
  resource_type  TEXT NOT NULL,
  deleted_at     TIMESTAMP NOT NULL DEFAULT NOW()
);
CREATE INDEX idx_deletions_deleted_at ON deletions (deleted_at);
```

Four things this must get right, all of them easy to get wrong:

1. **Written in the same transaction as the delete.** A crash between the
   `DELETE` and the tombstone loses the deletion forever — the worst outcome,
   because it is invisible.
2. **Written in code, not by a trigger.** `dmart import --fast` sets
   `session_replication_role='replica'`, which bypasses triggers. A
   trigger-based tombstone would be silently skipped exactly during bulk
   operations.
3. **Cascades must tombstone every descendant.** Deleting a folder removes its
   subtree and its attachments; each removed row needs a row here. This is the
   likeliest bug, and the one a test must pin.
4. **Retention must exceed the increment cadence.** Pruning tombstones older
   than the gap between runs silently loses deletes. The pruning job and the
   pipeline cadence are coupled, and that coupling should be documented and
   ideally asserted at export time ("oldest retained tombstone is newer than
   your watermark — deletions may have been lost").

Both backends need it, so the DDL goes in `SqlSchema.cs` **and**
`SqliteSchema.cs`.

**`histories` needs no tombstones** *[verified]*. It has no foreign key to
`entries` and no cascade; the only `DELETE FROM histories` in the codebase is
in `UserRepository` (user deletion). History rows therefore survive the
deletion of the entry they describe, which makes incremental history a pure
append — `WHERE timestamp >= watermark`, no deletion stream at all.

One caveat if history is enabled: `idx_histories_lookup` leads with
`space_name`, so a scan keyed on `timestamp` alone will not use it. Enabling
history in a pipeline means adding an index on `timestamp`.

### 5.3 What an increment contains

```
increment-2026-08-13T00:00Z/
  manifest.json      # watermark, previous manifest id, counts per table
  entries/…          # rows with updated_at >= watermark
  attachments/…
  deletions/…        # tombstones with deleted_at >= watermark
  blobs/…            # ONLY hashes not present in any prior manifest
```

Applying an increment is: upsert every row, then apply every deletion, then
record the new watermark. Order matters — a row deleted and recreated within
one window appears in both, and upsert-then-delete would wrongly remove it.
**Sort the combined stream by timestamp**, or scope deletions to keys absent
from the upsert set.

### 5.4 Increments chain, and the chain is enforced

*Decision taken: chained, not independently restorable (§6).*

Increment N carries only what changed since N−1, so the base plus every
increment in order reconstructs the store. That is what keeps a daily run at
megabytes instead of the ~270 MB a full 10M-entry metadata snapshot would cost.

A chain is only safe if it cannot be applied wrongly, and "applied wrongly"
here is silent — an out-of-order or skipped increment produces a store that
looks fine and is missing writes. Three requirements, none optional:

1. **Every manifest carries `chain_id`, `sequence`, and `parent_manifest_id`,
   and the target store records the position it is at.** Restore refuses
   anything that is not the next link, and refuses a `chain_id` it has not
   seen. A re-run of the full export starts a *new* chain — increments from the
   old lineage must not silently apply to it.
2. **Verify the whole chain before applying any of it.** Walk the manifests
   first, confirm the links and the per-table row counts and checksums, and
   fail before touching the target. Failing halfway through a chain is the
   outcome that costs an operator a restore from scratch.
3. **Re-base on a documented cadence.** Chains grow without bound and replay
   time grows with them; a periodic full export caps replay length and lets
   old increments be pruned. The cadence and the tombstone retention (§5.2)
   should be chosen together — both are "how far back can we still recover".

The one property this gives up is self-healing: a lost or corrupted increment
makes every later one unusable. That is the accepted cost, and (2) is what
turns it from a silent corruption into a loud, early failure.

Blobs are unaffected: they are content-addressed and shared across the whole
chain, so an increment stores only hashes no earlier manifest listed.

### 5.5 History is opt-in

*Decision taken: optional, default off (§6).*

`--with-history`. Off by default because `histories` is append-only, unbounded,
and usually the largest table, and a pipeline rarely wants the audit trail
replayed nightly.

Two consequences to be explicit about:

- **The default export is not a complete backup.** Restoring it loses the audit
  trail. "Export" reads as "everything" to most operators, so the manifest
  records the choice and the CLI says so on completion rather than leaving it
  to be discovered at restore time.
- **The choice is a property of the chain, not of each run.** Pin it in the
  base manifest and refuse an increment that disagrees. A with-history
  increment on a without-history base is merely additive, but the reverse
  leaves a hole in the trail that nothing later can fill.

## 6. Decisions taken

| Question | Decision | Consequence |
|---|---|---|
| Consumer | **Round-trip first, analytics-readable** | Opaque JSON columns; Hive partitioning; native lists for arrays |
| Deletions | **Tombstone table** | Schema change on both backends; write on every delete path; retention policy |
| History | **Optional, default off** | `--with-history`; pinned per chain; needs a `timestamp` index when enabled |
| Increments | **Chained, not independent** | `chain_id`/`sequence`/`parent` enforced; whole-chain verify before apply; documented re-base cadence |

## 7. Open questions

1. **Does the ACL apply?** The current export filters rows by the actor's
   policies when `actor` is non-null; the CLI passes null for a full dump. A
   Parquet export is an operator tool — it should be explicitly actor-null and
   refuse an actor argument, rather than quietly producing a filtered "backup".
2. **Compression codec.** zstd measured here; snappy is faster to write and
   more widely supported by older readers. Worth measuring both at GB scale
   before pinning.
3. ~~**Where does the 100k `QueryLimit` fix land?**~~ Resolved: fixed in the
   existing exporter (#156) rather than only in the new path.
4. **Re-base cadence and tombstone retention.** Both answer "how far back can
   we recover", and they must be chosen together — retention shorter than the
   chain's reach loses deletions silently (§5.2).

## 8. Suggested phasing

Mirrors how the SQLite backend was staged, for the same reason: each phase is
independently verifiable and the risky part is not last.

1. **This document.** Stop, agree the shape.
2. **Full export, streaming, no incremental.** Prove the round trip:
   export → import into an empty store → the two stores are equal. Land the AOT
   suppression with its evidence. (The `MemoryStream` and `QueryLimit` ceilings
   are already gone — #156.)
3. **Tombstones.** Schema on both backends, write on every delete path
   including cascades, tests that pin folder-cascade and `--fast`.
4. **Incremental.** Watermark, `updated_at` indexes, blob dedup across
   manifests, apply-order semantics, and the chain enforcement in §5.4 — the
   `chain_id`/`sequence` refusal is worth landing *with* the first increment,
   not after, since it is the thing that makes a wrong apply loud.
5. **Scale.** Benchmark at multi-GB with the harness pattern in
   `bench/sqlite-vs-postgresql.py`; document the tier in the readme.
