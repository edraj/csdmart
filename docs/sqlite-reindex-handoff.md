# SQLite reindex path — handoff

The last open item in Phase 3 of the SQLite backend. Everything else on
`feat/sqlite-backend-seam` is done and verified; this is deliberately *not*
started, because a half-built rebuild path is worse than none — see "Why this
was deferred".

Read `docs/sqlite-backend-audit.md` first for the tier's scope. This document
only covers what is needed to finish the reindex.

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

## 2. What is missing

`dmart import` — the path that rebuilds the SQL store from the flat files under
`SPACES_FOLDER` — is PostgreSQL-only. On SQLite it fails when
`ImportFolderAsync` opens its import session.

This matters more than a normal missing feature: the whole premise of the tier
is that the SQL store is a *rebuildable index* over the flat files. Without a
rebuild path, that premise is unbacked on SQLite.

## 3. Why it cannot simply be switched on

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

## 4. Recommended design

A separate `SqliteReindexService`, **not** a translation of the COPY importer.
This mirrors the call already made for the bulk path in the audit (§2.9, §7.3):
the two backends want genuinely different strategies, and one parameterized
importer would obscure that rather than manage it.

1. **Enumerate** with the existing `.dm/meta.*.json` convention. `ImportWorkList`
   already persists and reads that list — reuse it rather than re-walking.
2. **Materialize** each meta file into the domain record using whatever
   `ImportExportService` already uses; do not duplicate that parsing.
3. **Write** through the repositories in batches (a few thousand rows), each
   batch inside one `BEGIN IMMEDIATE` transaction. SQLite's bottleneck is fsync,
   not statement parsing, so a batched prepared insert recovers most of COPY's
   advantage. `SqliteRetry` already handles `SQLITE_BUSY` around a whole
   transaction.
4. **Skip** `--fast` and `--drop-indexes` entirely; both are listed as
   PostgreSQL-only in audit §9. Reject them with a clear message rather than
   ignoring them.
5. **Route** on the driver in the CLI's `import` handler (`Program.cs:589`), the
   same way `Program.cs` already picks the connection factory and dialect.

Note the FTS5 triggers fire on every `entries` insert, so the wildcard index
populates automatically during a reindex. No separate index build is needed.

## 5. Tests the change must carry

The failure mode here is a *silently incomplete* index — it looks like working
software and quietly returns fewer rows. Assert on content, not just counts.

- **Idempotence.** Reindex the same tree twice; the second run must be a no-op
  and the row counts identical. This is what makes a rebuild trustworthy.
- **Over a partially-populated database.** Reindex onto a store that already
  holds a subset, including one entry whose flat file has since changed — the
  changed one must be updated, not duplicated.
- **Every table.** Entries, attachments, users, roles, permissions, groups,
  spaces. A walker that silently skips a resource type is the likely bug.
- **Wildcard search after reindex**, to prove the FTS triggers fired.
- **A deleted flat file** — decide and then test whether reindex prunes rows
  with no backing file, or only adds/updates. Python dmart's behaviour is the
  reference; if it prunes, that path needs its own test.

## 6. Environment notes that cost time last session

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

- **JSON encoder.** `System.Text.Json` escapes non-ASCII to `\uXXXX`.
  PostgreSQL's `jsonb` decodes escapes on ingest so `payload::text` holds real
  characters; SQLite stores the document verbatim and its `json()` does not
  normalize them either. The wildcard prefilter therefore cannot match Arabic on
  SQLite and is deliberately declined for non-ASCII patterns (correct, just
  unindexed). Setting `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` on
  `DmartJsonContext` would fix it — but that context also serializes HTTP
  responses, so it changes response bytes on both backends. Wire-format call.

- **Single-file AOT.** Publish emits `libe_sqlite3.so` beside the binary;
  SQLitePCLRaw ships a static archive only for `browser-wasm`. Packaging
  (build.sh, rpm, deb, apk) already carries the sidecar. Static linking would
  need a self-built amalgamation plus `DirectPInvoke`, and has not been
  attempted.

## 8. After this: Phase 4

Not started. Parameterize the suite over both drivers with an explicit skip
list, add a CI matrix entry per driver plus an AOT publish job, benchmark cold
read / filtered search / bulk rebuild / concurrent write on both, and document
the tier and its limits in the readme.
