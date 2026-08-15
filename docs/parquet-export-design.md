# Parquet export/import — design

A second export format alongside the current JSON-tree zip, for installations
where the data spans gigabytes, with **incremental** exports so a daily or
weekly pipeline ships only what changed.

Status: **design only, nothing implemented.** Decisions taken so far are
recorded in §6. Everything marked *[measured]* was run, not estimated.

> Assumes the SQLite backend (#152, merged). The exporter has to work on both
> backends, which is why the dual-backend obligations — tombstone DDL in two
> schema files, one canonical array encoding — are called out throughout.

> **Read §2 before anything else.** No third-party Parquet library meets
> csdmart's 100%-AOT / no-reflection rule, at any version — measured, not
> assumed. Parquet remains possible only by writing the encoder in-house
> (§2.2, ~900 lines, sized). PR #75 dropped Parquet for a narrower version of
> the same reason, and §2.1 explains why the "put it in a subproject like
> `Dmart.SqlAdapter`" idea does not work either.

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

**Verdict: no third-party Parquet library qualifies. Write the encoder.**

csdmart's rule is 100% AOT — no JIT, no reflection — and the build enforces it
rather than trusting convention:

```
PublishAot=true                 IlcTreatWarningsAsErrors=true
TrimmerSingleWarn=false         JsonSerializerIsReflectionEnabledByDefault=false
```

**The codebase carries zero trim/AOT suppressions today.** Every `IL2026` /
`IL3050` mention in the source is a comment explaining how code was
*restructured to avoid* reflection — never a `SuppressMessage` silencing one.
That is the standard this work has to meet.

Parquet.Net uses reflection internally in every version. Measured on the schema
this design actually needs, via the low-level column API (never
`ParquetSerializer`, which is the reflection front door):

| | 5.6.1 | 6.0.3 |
|---|---|---|
| AOT diagnostics, no list columns | 1 (`IL3050`) | 3 |
| AOT diagnostics, **with** list columns | **3** (`IL3050` + 2× `IL2070`) | **3** |
| links and runs natively | yes | yes |
| nullable value-type column | yes | yes |
| `list<string>` round-trip | yes | **no — throws** |
| bytes/row (50k entries) | 26.9 | 25.7 |

Those diagnostics are the compiler reporting that it **cannot prove** the
reflection is safe under trimming. Suppressing them does not remove the
reflection — it removes the warning. That the probes ran correctly means the
types happened to survive trimming for the schemas tested: a runtime
observation, not a guarantee, and not the same property as "no reflection".

**So Parquet.Net is disqualified at any version.** PR #75 reached the same
conclusion (§2.1) by a narrower route — it tested the reflection serializer —
but the broader reason holds regardless of API or version.

Recorded because it is easy to re-derive and wrong: an earlier reading of this
section concluded "exactly one `IL3050`, therefore one narrow suppression". That
was measured on a schema *without* list columns, and the design needs them —
adding a `ListField` brings two `IL2070`s on both versions. The count was never
the point: one suppression violates the rule as surely as three.

### 2.1 Prior art: PR #75 was dropped over exactly this, and why that changes

**This ground has been walked before.** PR #75 (2026-05-26, closed the same
day) added `dmart archive` / `dmart unarchive` — per-folder Parquet archival of
the flat files, in a non-AOT `Dmart.ParquetAdapter` subproject mirroring
`Dmart.SqlAdapter`. It was closed deliberately, not abandoned. The reasoning,
verbatim:

> Parquet.Net 6 is reflection-heavy and not AOT-compatible. Pulling it into the
> main project's AOT publish graph fails ilc with IL2104/IL3053. The SqlAdapter
> pattern (Compile Remove + UndefineProperties PublishAot) only works because
> SqlAdapter is structurally orphaned from main's call graph (NuGet-only).
> Archive/unarchive would need a separate framework-dependent companion binary,
> which exceeds the value vs. just using `tar -czf` for file count reduction +
> dmart's existing JSON export for offline query.

Two things in there are load-bearing and should not be re-derived:

1. **The subproject escape hatch does not generalise.** `Dmart.SqlAdapter` gets
   away with `Compile Remove` + `UndefineProperties PublishAot` only because
   nothing in the main call graph reaches it — it is a NuGet-only distributable.
   A CLI subcommand or an API handler *is* in the call graph, so the exclusion
   collapses and ilc pulls the dependency in anyway. Anyone proposing "just put
   Parquet in a subproject like SqlAdapter" is proposing the thing that was
   already tried.
2. **A framework-dependent companion binary was judged not worth it.** That
   judgement was about ARCHIVAL, whose alternative is `tar -czf` plus the
   existing JSON export. It does not automatically carry to incremental export,
   which `tar` cannot do — but it is the bar this work has to clear.

**What is different now** is the API, not the willingness to suppress. #75
measured Parquet.Net 6 through `ParquetSerializer` — the reflection path, which
maps rows to a POCO and is exactly what IL2104/IL3053 fire on. §2 above measures
the LOW-LEVEL column API (`ParquetWriter` + `DataColumn` + explicit
`ParquetSchema`), which does no such mapping: one IL3050, verified at runtime as
a false positive for statically-declared schemas. If that holds, there is no
companion binary and no orphaning — the dependency lives in the main assembly
like any other.

**This has now been measured on both versions (§2), and the answer is not the
one this section originally anticipated.** The low-level API does link and run
under AOT on 5.6.1 and 6.0.3 alike — so #75's *specific* blocker, the
reflection serializer, is genuinely avoidable. But avoiding it still leaves
reflection inside the library, which the project rule forbids outright. #75's
conclusion therefore stands, for a broader reason than the one it gave.

For context, #75 was Phase C of a 24M-entry / 400 GB legacy migration. Phase A
(PR #73, preflight) and Phase B (PR #74, `import --resume`) shipped and covered
the migration's must-haves. That scale also dwarfs the "several gigabytes"
framing this document was written against, and is worth revisiting against §4.2
row-group sizing and §4.3 blob-store sharding before Phase 2.

### 2.2 Option B: write the encoder in-house — sizing

The remaining way to have Parquet under this rule is to emit the format
directly with no reflection anywhere. That is more tractable than it sounds,
because **the schema is fixed and known at compile time** and Parquet is a
documented open format.

Sized against what the constrained subset actually requires — verified by
emitting exactly that subset with pyarrow and inspecting the resulting chunk
metadata, rather than from memory of the spec:

- **3 physical types** — `BYTE_ARRAY` (strings, JSON), `BOOLEAN`, `INT64`
  (timestamps). `INT32`/`DOUBLE` only if a numeric column appears.
- **2 encodings** — `PLAIN` for values, `RLE`/bit-packed hybrid for definition
  levels. No dictionary encoding, no statistics, no bloom filters, no column
  indexes: all optional in the format.
- **No repetition levels**, by dropping native `list<string>` (trade below) —
  which removes the fiddliest encoder entirely.
- **zstd** through `ZstdSharp.Port`, already proven pure-managed and AOT-clean.

| Component | ~lines | Risk |
|---|---:|---|
| Thrift compact protocol writer (varint, zigzag, field deltas) | 250 | **medium** — spec-fiddly, self-contained, unit-testable |
| Parquet metadata structs (FileMetaData, SchemaElement, RowGroup, ColumnChunk, ColumnMetaData, PageHeader) | 200 | low — plain data |
| RLE/bit-packed hybrid encoder, definition levels only (0/1 values) | 120 | **medium** — the simplest case of the encoding |
| PLAIN value encoders per physical type | 120 | low |
| Page → chunk → file assembly, zstd framing | 200 | low-medium |
| **Writer total** | **~900** | |
| *Reader, only if round-trip restore is required* | *+550* | *medium — Thrift parsing, page and level decoding* |

**The verification strategy is the non-negotiable part.** A hand-rolled format
writer validated only by our own reader is worthless — both sides can share the
same misunderstanding and agree with each other indefinitely. Every test must
round-trip through an **independent** implementation. `pyarrow` is present on
the build host, so the loop is: write from C#, read with pyarrow, assert
values, nulls and logical types. Property-style over generated data covering
nulls and every supported type, not a fixed golden file.

**What this gives up versus a library:**

- **Native `list<string>`** for `tags` / `query_policies`; they become JSON
  strings. Consistent with `payload` / `acl` / `relationships`, which §4.2
  already keeps opaque, and still queryable in DuckDB via `json_extract`. This
  is the trade that removes repetition levels.
- **Dictionary encoding** — larger files on low-cardinality columns. Worth
  measuring against the 27 B/row baseline before assuming it does not matter.
- **Statistics** — no min/max predicate pushdown, so analytics scans more.
  Affects speed, not correctness; addable later.

### 2.3 The reader is not optional — and it is smaller than a general one

*Decision taken: writer AND reader (§6).*

An export that cannot be imported is not a backup, and §6 already chose
round-trip restore as the primary consumer. So the reader is in scope, and the
"use a library just for reading" escape does not exist: `dmart import` is a CLI
subcommand in the same binary, which is exactly the call-graph position §2.1
shows the `Dmart.SqlAdapter` exclusion cannot cover.

**But a reader for our own dialect is much smaller than a general Parquet
reader.** We control what we emit: always `PLAIN` values, always `RLE`
definition levels, always zstd, no dictionary pages, no statistics, v1 data
pages, a known row-group layout. Decoding that is close to symmetric with
encoding it:

| Component | ~lines | Note |
|---|---:|---|
| Thrift compact **parser** (same field subset) | 200 | mirrors the writer |
| Page header + zstd decompression | 80 | `ZstdSharp` decompresses too |
| `PLAIN` value decoders per physical type | 100 | mirrors the writer |
| RLE/bit-packed **decoder**, definition levels | 100 | mirrors the writer |
| **Reader total** | **~480** | |
| **Writer + reader** | **~1,400** | |

A *general* Parquet reader — every encoding, dictionary pages, v2 pages, nested
repetition — is a different and much larger project, and is explicitly **not**
what this is. Which forces one rule:

**Refuse what we did not write, loudly.** On encountering a dictionary page, a
v2 data page, an unsupported encoding or a schema shape outside our dialect,
the reader must fail with a message naming what it found — never guess, never
partially decode. Silently misreading someone else's Parquet file would be the
same failure shape as the 100,000-row truncation (#156): plausible output,
quietly wrong. A file produced by Spark or pandas is therefore *not* importable
by design, and says so.

**The testing consequence is the important one.** Round-tripping our own writer
through our own reader proves almost nothing: both sides can share a
misunderstanding of the spec and agree with each other perfectly. The
verification has to cross an independent implementation in **both** directions:

- ours → `pyarrow`  (does anyone else understand what we wrote?)
- `pyarrow` → ours  (do we understand what the format actually says?)

The second direction is the one that catches a writer and reader agreeing on
the same bug, and it is the one that is easy to skip.

**And the bar from #75 still applies.** It judged a Parquet archival path not
worth a companion binary *versus `tar -czf` plus the existing JSON export*. A
~900-line hand-written encoder is a different cost with a different payoff —
incremental export, which `tar` cannot do — but it has to clear that bar
explicitly rather than inherit a pass.

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

### 4.1.1 Scope, and what each scope carries

| Command | Carries |
|---|---|
| `export <space> --parquet` | that space's entries, attachments, histories |
| `export <space> --parquet --subpath /docs` | that subtree only |
| `export management --parquet` | the above **plus** users, roles, permissions |
| `export --all --parquet` | every space, plus the global tables once, verified |

The global tables are **not** written by a scoped export. Two reasons, and the
second is the one that matters: repeating the whole user table in every scoped
export is waste, and the users table holds **password hashes**. Writing those
to disk should follow from asking for a backup or for management — not from
exporting one folder.

A scoped export therefore restores INTO AN EXISTING SYSTEM; it is not a
standalone backup, and the CLI says so after every one.

`--all` verifies by default: every file is re-read through the reader and every
blob is rehashed against its own name. It roughly doubles read I/O, which is
the right trade — a backup nobody has read is one you are guessing about.
`--no-verify` opts out.

A restore is `dmart import <dir> --parquet [-r] [--verify] [--drop-indexes]`.
`--drop-indexes` drops the secondary indexes on `entries`/`attachments` for the
duration of the load and rebuilds them afterwards, which is the same lever the
zip importer offers, and it is a **large-restore** lever only: measured on a
21,843-entry restore it cost 3% (1632 ms vs 1578 ms), because the one-off GIN
rebuild dominates a small table. The crossover is around 200k rows (~1.2x);
at 4M rows load-then-rebuild came out ~5.6x ahead. Those indexes are also
**missing while it runs**, so it belongs in a maintenance window, and it is
PostgreSQL-only (ignored, with a warning, on SQLite). Unlike the zip importer
this path keeps **no checkpoint**: a hard kill between the drop and the rebuild
leaves them gone with no durable record, so the rebuild SQL is logged *before*
the drop and an operator can replay it from the log.

`space_name=<s>` is Hive-style partitioning — what DuckDB and Spark expect
(`read_parquet('entries/**/*.parquet', hive_partitioning=true)`) and also the
natural unit for a per-space restore. A restore takes each file's space from
its PATH rather than from the manifest: a full backup holds many spaces in one
archive, and a manifest-level space name would restore all of them under one
name — silently merging spaces, which is unrecoverable without the original. This is the "restore first, analytics
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
- **Array columns become JSON strings** — `tags`, `query_policies`. §2.2 is the
  binding decision here; the earlier text in this section called for native
  `list<string>` and was wrong, because native lists need repetition levels and
  dropping those is what kept the encoder small enough to hand-write at all.
  Consistent with `payload` / `acl` / `relationships`, and still queryable in
  DuckDB via `json_extract`. This is also the one place the two backends already
  differ (`text[]` vs a JSON array in TEXT), so the exporter writes one
  canonical form regardless of driver.

- **Attachments store `media_sha256` and `media_size`, never the bytes.** The
  blob lives at `blobs/<sha256[0:2]>/<sha256>` and the name IS the checksum, so
  corruption is detectable by rehashing rather than by trusting a size field.
  A restore verifies every blob against its own name and FAILS the row if it
  disagrees — an attachment silently restored with wrong or empty bytes is
  undetectable afterwards, because the bytes are opaque and nothing downstream
  checks them.

  Export memory is bounded at ONE blob: the listing selects `length(media)` but
  not the bytes, and each blob is fetched by uuid, hashed, written and released
  before the next. The cost is one query per attachment that has media;
  attachments without media skip it, which is why the size is selected.

- **The global tables carry `space_name` as a column.** `spaces`, `users`,
  `roles` and `permissions` are written flat at `<table>/part-00000.parquet`
  with no `space_name=` directory (§4.1), so there is no partition key to
  collide with. Users, roles and permissions all live in the management space,
  and spaces span every space by definition — partitioning either would produce
  a single directory or one per row.

- **`users` carries the Argon2 password hash.** Without it a restore leaves
  every user unable to log in, which is the line between a backup and a content
  archive. The consequence is that an export directory is credential material
  and needs the handling a database dump gets: restricted permissions, and
  encryption if it leaves the host. The CLI prints this at export time.

  This DIVERGES from the zip export, where `User.Password` is `[JsonIgnore]`
  and therefore silently absent.

- **`space_name` is NOT a column** in `entries`. It is the Hive partition key in the
  directory name, and a Hive partition column lives in the path, not in the
  file. Writing both makes every partition-inferring reader — DuckDB, Spark,
  pyarrow — fail outright with `Field space_name has incompatible types: string
  vs dictionary`, which defeats the compatibility §4.1 is asking for. The value
  is carried in the manifest and restored from there.

  Found by the cross-reader test, not by review: the files were valid Parquet
  and read fine individually. Only reading the export the way a consumer
  actually would — as a partitioned dataset — surfaced it.
- **Timestamps as Parquet TIMESTAMP (micros, UTC)**, not strings.

  **Known limit — microsecond precision.** Parquet `TIMESTAMP_MICROS` stores 6
  decimal places; .NET `DateTime` stores 7 (100 ns ticks). On PostgreSQL this
  is invisible: its `timestamp` column is already microsecond, so a value is
  rounded before it ever reaches the file, and the round trip is exact. SQLite
  keeps the full tick, so a restored timestamp there can differ from the
  original by up to 999 ns.

  Accepted deliberately rather than worked around. The alternatives were a
  parallel raw-ticks column (two sources of truth to keep in agreement) or
  `TIMESTAMP_NANOS` via `LogicalType` (drops the legacy `ConvertedType`
  annotation that every reader understands, and needs encoder and decoder work).
  Neither is worth it for a difference no consumer of these timestamps depends
  on. Tests compare at microsecond granularity for this reason, which still
  catches the failure that matters — a row re-stamped at restore time is off by
  milliseconds at least.

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

**IMPLEMENTED** — `dmart export <space> --parquet --since <previous-export-dir>`.

`--since` takes a DIRECTORY, not a timestamp: the watermark that makes two runs
overlap correctly is recorded in the previous run's manifest, and asking an
operator to retype it is asking them to get it wrong.

Three clock traps were found building this, all of the same shape — dmart
stores timestamps LOCAL-NAIVE in `timestamp without time zone` columns, so any
value compared against them must be in the same clock:

1. The watermark was stamped `DateTime.UtcNow`. On a host AHEAD of UTC that
   makes every increment a full export (wasteful, safe); on a host BEHIND UTC
   it silently skips every row changed inside the offset. Now `TimeUtils.Now()`.
2. `deletions.deleted_at` relied on the column's `NOW()` default, which the
   DATABASE SERVER evaluates in ITS timezone. On a UTC server with a +03 host
   the tombstones landed three hours behind everything else and an incremental
   scan saw none of them. Now bound explicitly, as `histories` already does.
3. The manifest mixed a local-naive `watermark` with a UTC `created_at`, making
   the pair incomparable. Both are now local-naive.

**Selection cannot use `Query.FromDate`**, which filters on `created_at`. An
entry EDITED since the last run still has its original `created_at`, so
reusing it would miss exactly the rows an increment exists to carry.
`EntryRepository.ListForSpaceUpdatedSincePagedAsync` scans `updated_at` instead.

**An increment only sees writers that maintain `updated_at`.** `UpsertAsync`
honours whatever the caller passes and only defaults when it is unset, so a
writer that preserves an old stamp is invisible to increments.

**Incremental refuses an actor.** It selects straight from the repositories,
bypassing the row-level ACL gate a full export applies; returning rows the
actor cannot see would be worse than refusing.

**This needs an index that does not exist.** `entries` had indexes on
`space_name`, `subpath`, `owner_shortname`, `resource_type` and four GIN
indexes, but **none on `updated_at`**. Added as a prerequisite, not an
optimization: `idx_entries_updated_at`, `idx_attachments_updated_at`, and
`idx_histories_timestamp` (the append-only equivalent — `idx_histories_lookup`
leads with `space_name` and cannot serve a scan keyed on time alone).

### 5.2 Deletions — a tombstone table

*Decision taken: tombstone table (§6). **IMPLEMENTED** — see
`DataAdapters/Sql/Tombstones.cs` and `TombstoneTests`.*

The shipped table differs from the sketch below in one way: it carries
`table_name`, so one table serves entries, attachments, histories, spaces,
users, roles and permissions rather than needing seven. `locks` is deliberately
excluded — transient coordination state, not replicated content.

Every content-removing path is covered: single entry and attachment deletes,
the folder-subtree cascade, the space cascade, the by-owner cascade inside
`ForceDeleteAsync`, and the user/role/permission deletes. The insert runs over
the SAME PREDICATE as the delete it accompanies, so the two cannot disagree
about what a cascade took.

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
| Parquet code | **Hand-written writer AND reader** (§2.2, §2.3) | ~1,400 lines, no third-party library, no reflection; reads only our own dialect and refuses anything else |
| Increments | **Chained, not independent** | `chain_id`/`sequence`/`parent` enforced; whole-chain verify before apply; documented re-base cadence |

## 7. Open questions

1. **Does the ACL apply?** The current export filters rows by the actor's
   policies when `actor` is non-null; the CLI passes null for a full dump. A
   Parquet export is an operator tool — it should be explicitly actor-null and
   refuse an actor argument, rather than quietly producing a filtered "backup".
2. ~~**Does the low-level API stay AOT-clean on Parquet.Net 6.x?**~~ Measured
   on both versions (§2). Wrong question, as it turns out: both link and run,
   and both still contain reflection, which the project rule forbids. Replaced
   by the two live questions below.
2a. ~~**Writer-only, or writer + reader?**~~ Both — an export that cannot be
   imported is not a backup (§2.3). ~1,400 lines total.
2b. **Does a hand-written encoder clear #75's bar?** It judged Parquet not
   worth a companion binary versus `tar -czf` + the JSON export. The payoff
   here is incremental export, which `tar` cannot do — but the bar is not
   inherited.
3. **Compression codec.** zstd measured here; snappy is faster to write and
   more widely supported by older readers. Worth measuring both at GB scale
   before pinning.
4. ~~**Where does the 100k `QueryLimit` fix land?**~~ Resolved: fixed in the
   existing exporter (#156) rather than only in the new path.
5. **Re-base cadence and tombstone retention.** Both answer "how far back can
   we recover", and they must be chosen together — retention shorter than the
   chain's reach loses deletions silently (§5.2).

## 8. Suggested phasing

Mirrors how the SQLite backend was staged, for the same reason: each phase is
independently verifiable and the risky part is not last.

1. **This document.** Stop, agree the shape.
2. ~~**Settle the AOT question.**~~ Measured (§2): no library qualifies. The
   replacement gate is a **decision, not an experiment**, and half of it is
   now settled: writer AND reader (§2.3, ~1,400 lines). What remains is whether
   that clears #75's bar — *not worth it versus `tar -czf` + the JSON export* —
   for a payoff `tar` cannot give: incremental export.
3. **Full export, streaming, no incremental.** Prove the round trip:
   export → import into an empty store → the two stores are equal. Land the AOT
   suppression with its evidence. (The `MemoryStream` and `QueryLimit` ceilings
   are already gone — #156.)
4. **Tombstones.** Schema on both backends, write on every delete path
   including cascades, tests that pin folder-cascade and `--fast`.
5. **Incremental.** Watermark, `updated_at` indexes, blob dedup across
   manifests, apply-order semantics, and the chain enforcement in §5.4 — the
   `chain_id`/`sequence` refusal is worth landing *with* the first increment,
   not after, since it is the thing that makes a wrong apply loud.
6. **Scale.** Benchmark at multi-GB with the harness pattern in
   `bench/sqlite-vs-postgresql.py`; document the tier in the readme.
