# SQLite reindex path — handoff

> **STATUS: DONE.** Landed on `feat/sqlite-backend-seam`. `dmart import`
> rebuilds the SQL store from the flat files on both drivers, verified
> end-to-end through the CLI and covered by `ReindexFromFlatFilesTests`.
> Kept as a record of the design decision and the tier's limits; section 4
> below describes what was NOT built and why.


---

## 1. What already works

`DATABASE_DRIVER=sqlite` boots and serves. Verified against a running server:
login, `/user/profile`, `managed/request` (create), subpath query, search,
`filter_tags`, JSON payload filters, numeric comparison, wildcard, counters,
tags, and aggregation with `group_by`.

- All ten repositories are backend-neutral (`IDbConnectionFactory` + `DbParams`).
- The search grammar emits no PostgreSQL-specific SQL; `ISqlDialect` covers the
  differences.
- `SqliteSchema` creates the full schema, including the FTS5 trigram wildcard
  index and its sync triggers.
- PostgreSQL is unchanged throughout: `SqlEmissionGoldenTests` passes with no
  regeneration, and 1836 tests pass with zero skipped.

## 2. What was missing

`dmart import` — the path that rebuilds the SQL store from the flat files under
`SPACES_FOLDER` — was PostgreSQL-only. On SQLite it failed when
`ImportFolderAsync` opened its import session.

This mattered more than a normal missing feature: the whole premise of the tier
is that the SQL store is a *rebuildable index* over the flat files. Without a
rebuild path, that premise was unbacked on SQLite.

## 3. Why it could not simply be switched on

`Services/ImportExportService.cs` is built around PostgreSQL bulk loading:

| Line | Construct | Status on SQLite |
|---|---|---|
| 1389-1390 | `db.BeginFastImportSessionAsync` / `BeginBatchImportSessionAsync` | `Db`-only; the session's reconnect/replay logic is PostgreSQL SQLSTATE classification |
| 2587, 2680 | `conn.BeginBinaryImportAsync` (binary COPY) | no SQLite equivalent |
| 956, 942 | index drop/restore around the load | GIN-specific |
| 998 | `--fast` head session | `session_replication_role`, superuser-context GUC |

The **writing** half is already done, though: the per-row path at
`ImportExportService.cs:1647` (`entries.UpsertAsync`), `:1679`
(`attachments.UpsertAsync`), `:1899` (`spaces.UpsertAsync`) and `:1928`
(`users.UpsertAsync`) goes through the now backend-neutral repositories. It
exists only as the fallback after a bulk batch fails — it is not a selectable
mode. That is the gap.

## 4. What was built — and why NOT the recommended design

The recommendation here was a separate `SqliteReindexService`. **That was the
wrong call, and it was not followed.** Recording why, because the reasoning
generalizes:

A separate service would have had to duplicate `ImportExportService`'s
enumeration, meta parsing, owner remapping, uuid dedup, schema validation,
payload-body inlining, ticket handling, tag stamping and the issues sidecar —
roughly 1500 lines of *semantics*, none of it backend-specific. Duplicating
semantics to avoid duplicating a strategy is a bad trade: the two copies drift,
and the drift shows up as a rebuild that quietly indexes a slightly different
thing than the live write path does.

What was actually backend-specific turned out to be small and already isolated:
the bulk COPY session, and nothing else. The `Try*` helpers had carried a
per-row contract for a null connection since before the seam existed. So:

  * `ImportExportService` takes `IDbConnectionFactory` instead of `Db`, and
    exposes `Bulk => db as Db`. That one cast both answers "can we bulk-load?"
    and hands over the object needed to do it.
  * `ImportShardTailAsync` runs `RunTailPassesAsync(session: null, …)` when
    `Bulk` is null — the per-row path, writing through the same repositories the
    HTTP layer uses. Every backend-specific concern (JSON storage shape, text[]
    encoding, FTS5 trigger fan-out) is therefore handled exactly once.
  * The PostgreSQL-only load OPTIONS — `--fast`, `--drop-indexes`,
    `--fast-parallelism` — are refused with a reason rather than ignored.
  * `CliBootstrap.BuildFactoryOrExit` resolves the driver for `dmart import`,
    and creates the SQLite schema if the file does not exist yet: requiring a
    separate migrate step first would make "rebuild the index" a two-command
    operation for no reason.

The original design points that DID survive:

reuse the existing enumeration and parsing; reject the PostgreSQL-only flags
rather than ignoring them; route on the driver in the CLI handler.

One point was dropped: **batching writes inside an explicit `BEGIN IMMEDIATE`.**
The repositories open their own connection per call, so batching would mean
threading a connection through every repository signature — a wide change to
buy throughput on a tier whose stated scope is dev, CI, single-node and edge.
Under WAL with `synchronous=NORMAL` a commit is a WAL append, not an fsync, so
the per-row path is not the fsync-per-row disaster it would be under the
rollback journal. If a bulk rebuild ever becomes the bottleneck, this is the
first thing to revisit — and the benchmark in Phase 4 is what should decide it,
not intuition.

The FTS5 triggers fire on every `entries` insert, so the wildcard index
populates during a reindex with no separate build step. Verified directly:
after a CLI reindex, `SELECT rowid FROM entries_fts WHERE payload LIKE …`
returns the imported row.

### Prerequisite, on BOTH drivers

`owner_shortname` is a NOT NULL foreign key to `users(shortname)` in both
schemas, so importing into a schema-only database whose `users` table is empty
fails every row. Confirmed identical on PostgreSQL (23503) and SQLite (FK
constraint failed) against freshly-migrated databases — this is a pre-existing
property of the import, not a SQLite limitation. Either the source tree carries
its users (they land in the head pass, before entries) or the database already
has them.

## 5. Tests the change carries

All in `dmart.Tests/Integration/ReindexFromFlatFilesTests.cs`, running on both
drivers. The failure mode is a *silently incomplete* index — it looks like
working software and quietly returns fewer rows — so they assert on content,
not just counts.

- **Idempotence.** Reindex the same tree twice; the second run must be a no-op
  and the row counts identical. This is what makes a rebuild trustworthy.
- **Over a partially-populated database.** Reindex onto a store that already
  holds a subset, including one entry whose flat file has since changed — the
  changed one must be updated, not duplicated.
- **Every table.** Entries, attachments, users, roles, permissions, groups,
  spaces. A walker that silently skips a resource type is the likely bug.
- **Wildcard search after reindex**, to prove the FTS triggers fired.
- **A deleted flat file.** Decided: reindex is **additive** — it adds and
  updates, it does not prune. This matches Python dmart, where removal happens
  through the delete API, which unlinks the file and the row together. Pinned
  as a test because the opposite assumption ("reindex makes the store match the
  disk") is the natural one to make and is wrong.

Beyond these, the 17 pre-existing import tests now run on SQLite instead of
skipping. The wildcard test deliberately uses the FIELD-SCOPED form
(`@payload.body.note:*needle*`), not a bare term: only that emits the prefilter
conjunct, so only that fails when the index is empty. A bare term falls back to
an unindexed scan of `payload` and would pass against an empty FTS table.

## 6. Environment notes

- **Scratch PostgreSQL** for the existing suite:
  ```
  podman run -d --rm --name dmart-scratch-pg \
    -e POSTGRES_USER=dmart -e POSTGRES_PASSWORD=scratchpw \
    -e POSTGRES_DB=dmarttest -p 55432:5432 docker.io/library/postgres:18
  podman exec dmart-scratch-pg psql -U dmart -d dmarttest -c "ALTER ROLE dmart SUPERUSER;"
  export DMART_TEST_PG_CONN="Host=127.0.0.1;Port=55432;Username=dmart;Password=scratchpw;Database=dmarttest"
  export DMART_TEST_PWD=Test1234
  ```
  Without it 688 tests skip silently.

- **`dotnet build -c Release` was repeatedly killed before writing the
  assembly**, so the server ran a stale binary while its log was being read as
  evidence. This produced several confident but wrong conclusions. Build in the
  background and poll `bin/Release/net10.0/dmart.dll`'s timestamp until it
  actually changes before trusting anything from a server run.

- **Run the server as a tracked background task.** A detached child started
  inside a tool call gets reaped when the call ends.

- **A SQLite config for manual runs:**
  ```
  DATABASE_DRIVER="sqlite"
  SQLITE_PATH="/tmp/sqliteboot/dmart.db"
  SPACES_FOLDER="/tmp/sqliteboot/spaces"
  LISTENING_PORT=5199
  JWT_SECRET="<48+ chars, not the placeholder>"
  ADMIN_PASSWORD="Test1234"
  ```
  Point at it with `BACKEND_ENV=/path/to/config.env`; it is not found from CWD.

## 7. Open decisions, unrelated to the reindex

- ~~**JSON encoder.**~~ RESOLVED. Non-ASCII is now written literally at the
  storage boundary only (`JsonbHelpers`, `JavaScriptEncoder.Create(UnicodeRanges.All)`),
  so the wildcard prefilter serves Arabic and HTTP response bytes are unchanged
  on both backends.

- **Single-file AOT.** Publish emits `libe_sqlite3.so` beside the binary;
  SQLitePCLRaw ships a static archive only for `browser-wasm`. Packaging
  (build.sh, rpm, deb, apk) already carries the sidecar. Static linking would
  need a self-built amalgamation plus `DirectPInvoke`, and has not been
  attempted.

## 8. After this: Phase 4

Mostly done. The suite is parameterized over both drivers and green on each —
PostgreSQL 1842 pass / 0 skip, SQLite 1827 pass / 15 skip, every skip carrying
a one-line reason at its call site.

Left: a CI matrix entry per driver plus an AOT publish job; benchmarks for cold
read, filtered search, bulk index rebuild and concurrent write; and the readme
section describing the tier and its limits.
