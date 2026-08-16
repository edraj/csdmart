# Changelog

## Unreleased

- `prune-empty-histories` now **deletes** rows with a NULL diff instead of
  reporting and skipping them. A NULL predates the `{}` convention but means the
  same thing — an audit row recording no change — so leaving them behind meant
  the cleanup only half-worked. The count is still broken out separately.

## v1.2.1 — 2026-08-15

A patch release: one new maintenance command, and the RPM build repaired.

### New

- **`dmart prune-empty-histories [--space <name>] [--dry-run]`** — deletes
  history rows whose `diff` is an empty object. Those are audit records that
  nothing changed, written before the empty-diff append was fixed in 1.2.0; no
  current writer produces them. Run it **once** after upgrading.

  Deletes are **tombstoned**, so an incremental Parquet consumer learns the rows
  are gone rather than silently keeping them — which means a large prune writes
  as many rows into `deletions` as it removes, and `prune-tombstones` drains
  those once your increments have caught up. Rows with a NULL diff are a
  different, older shape and are **reported rather than removed**.

- **`docs/maintenance.md`** — operator guide for both prune commands, including
  the one thing neither of them says on its own: nothing runs them for you.
  There is no scheduler and no background service; they do what you ask, when
  you ask.

### Fixed

- **The RPM build.** Two bugs, both introduced with the 1.2.0 SQLite packaging
  work, failed the RHEL 9 and Fedora jobs of the 1.2.0 release build:
  a `%files` entry stranded inside `%install` (so rpm's shell tried to *execute*
  a path), and `libe_sqlite3.so` never staged into the source tarball while
  `%files` lists it unconditionally.

  **The RPMs published on the v1.2.0 release are not affected** — they were
  built with these fixes applied and attached by hand, and their binaries report
  `v1.2.0-0-g832bbbe`. This release makes the build work from a clean checkout
  again.

### CI

- CI now **builds the Fedora RPM** on every push and asserts its payload. Both
  1.2.0 packaging bugs were invisible to CI because RPMs were only ever built by
  the release workflow, so a broken spec could sit on master for a whole release
  cycle. A parse check would not have caught either — `rpmspec -P` reports the
  spec as valid — so the job does a real `rpmbuild`.

## v1.2.0 — 2026-08-15

Two large pieces of work land here: **SQLite as a second database backend**,
and **Parquet as a backup/restore format**. Everything else is fixes and
packaging.

### SQLite backend

dmart now runs on SQLite as well as PostgreSQL. Set `DATABASE_DRIVER=sqlite`
(inferred when unset, from whichever connection settings are present).

This is a **tier, not parity** — the target is development, CI, single-node and
edge deployments. PostgreSQL remains the production backend, and its code path
is unchanged: the same SQL is emitted and the same API responses come back.

- All repositories, the search grammar, aggregations, joins and sorts now emit
  through an `ISqlDialect` seam rather than PostgreSQL-specific SQL.
- SQLite-specific handling where the engines genuinely differ: `hstore` mapped
  onto JSON for OTP, a lock strategy that does not depend on `xmax`, `text[]`
  reads, provider-neutral scalar reads, and an FTS5 trigram index (declined for
  non-ASCII, where it cannot work — JSON columns are stored as literal UTF-8 so
  SQLite can index Arabic).
- `dmart import` rebuilds the SQL store from the flat files under
  `SPACES_FOLDER` on SQLite too. The storage design's premise is that the SQL
  store is a rebuildable index; that was previously unbacked on this tier.
- SQLite errors are classified at the HTTP boundary, so they surface as the
  same envelopes PostgreSQL produces.
- CI runs as a **driver matrix** — the whole integration suite against both
  backends — and publishes a Native AOT binary on every push.
- 17 tests skip on SQLite, each gated with a stated reason rather than weakened
  to pass everywhere: query-plan assertions, GIN and server-notice
  observability, the SDK adapter (scoped PostgreSQL-only by the audit), and the
  PostgreSQL-only fast-import path. Nothing fails on SQLite.

See `docs/sqlite-backend-audit.md`.

### Parquet export and import

A columnar backup format alongside the existing zip. Written by hand — no
library meets the 100%-AOT rule — and verified against pyarrow in both
directions.

```
dmart export <space> --parquet [--subpath <p>] [--since <dir>] [--output <dir>]
dmart export --all --parquet                   # full backup, verified
dmart import <dir> --parquet [-r] [--no-verify] [--drop-indexes]
dmart prune-tombstones --older-than <days> [--dry-run]
```

- **Every table**: entries, attachments, histories, spaces, users, roles,
  permissions, and a deletions (tombstone) table.
- **Scope**: one space, one subfolder, or everything. Scoped exports
  deliberately omit users/roles/permissions — the users table holds password
  hashes, and writing those to disk should follow from asking for a backup, not
  from exporting one folder.
- **Attachment media** is stored content-addressed as
  `blobs/<sha256[0:2]>/<sha256>`, so an unchanged attachment ships zero bytes
  in an increment and identical files are stored once.
- **Incremental** via `--since <previous-export-dir>`, with tombstones so a
  deletion is not indistinguishable from "unchanged".
- **Verified on write** (`--all`) and **on restore** (now the default).
- **Hive-partitioned** (`space_name=<s>/`), so DuckDB and Spark read it
  directly: `read_parquet('entries/**/*.parquet', hive_partitioning=true)`.
- Bulk restore reuses the zip importer's COPY path — 54× faster for entries,
  64× with history included; the user restore is batched and shares the SQL
  clause that preserves existing password hashes.
- `--drop-indexes` drops the GIN indexes for the load and rebuilds them after
  (PostgreSQL only). A large-restore lever: it *costs* a few percent below
  ~200k rows.

See `docs/parquet-export-design.md` and `bench/REPORT-backup-formats.md`.

### Behaviour changes

- **`import --parquet` now verifies by default.** `--no-verify` opts out;
  `--verify` is still accepted. Previously export verified unless you opted out
  while restore verified only if you opted in, which put the weaker default on
  the more dangerous operation.
- **A partial zip export no longer exits 0.** A backup pipeline reads the exit
  code, not the wording.
- **A zip export that drops attachment media now warns.** Zip names media after
  `payload.body`; an attachment with bytes but no such filename exports its
  metadata and not its bytes. That behaviour is unchanged and deliberate, but
  it is no longer silent. Parquet has no such hole.
- **PostgreSQL session timezone is pinned to the app host's.** dmart stores
  local-naive timestamps, and columns defaulting to `NOW()` were being stamped
  in the *server's* zone. Rows written before this fix cannot be repaired — see
  the upgrade note below.

### Fixes

- Export silently truncated at 100,000 rows.
- Export buffered the whole archive in memory; it streams now.
- Aggregation reducers emitted invalid SQL on SQLite.
- `db_size_info` answered dishonestly on SQLite.
- Three clock bugs in incremental export: a UTC watermark compared against
  local-naive columns, `deletions.deleted_at` relying on the server's `NOW()`,
  and a manifest mixing the two.
- Folder-content violations were untranslated for SQLite.
- Three defects that blocked building and running the container image.
- History: skip the append when the diff is empty.

### Packaging and container

- The RPM, deb and apk packages ship a **SQLite-backed default config**, so an
  install runs without a database server.
- The container image **drops PostgreSQL** and runs dmart alone on SQLite.
  See `docs/container.md`.
- CI gives each job its own smoke port instead of scanning for a free one.

### Upgrade note

**Do not chain a Parquet increment across this upgrade.** `updated_at` defaults
to `NOW()`, which the database server evaluated in *its* timezone; on a UTC
server under a +03 host, rows written before the timezone pin are stamped three
hours behind every host-local watermark, so an increment can read them as older
than they are and skip them.

No migration can repair this — a stamp three hours low is indistinguishable
from a row genuinely written three hours earlier. After upgrading, take **one
full export** and start the increment chain from it. Increments taken wholly
after that point are unaffected.

Separately, `deletions` is append-only and was never pruned before this
release. If it has grown, `dmart prune-tombstones --older-than <days>` bounds
it — choose a window **longer** than your incremental export interval.

## v1.1.5 and earlier

See the git history.
