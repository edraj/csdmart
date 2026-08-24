# Changelog

## v1.2.9 — 2026-08-24

### Security

- **The .NET runtime compiled into the binary is now checked for CVEs, in the
  shipped artifact.** dmart publishes self-contained with `PublishAot`, so the
  runtime lives inside `/usr/bin/dmart` and a user cannot patch it by updating
  their distro's .NET. It was also invisible to every existing check: the
  runtime pack is SDK-injected rather than a `PackageReference`, so it appeared
  in neither `dist/deps/*.lock.json` nor the CycloneDX SBOM.

  **v1.2.7 and v1.2.8 shipped runtime 10.0.10**, carrying CVE-2026-62901
  (HIGH, denial of service) plus CVE-2026-62899 and CVE-2026-62909. **v1.2.9 is
  the first release built on runtime 10.0.11**, where all three are fixed.

  The check reads the runtime version out of the finished binary — the AOT
  publish, the binaries inside both the Fedora and EL9 RPMs, and each release
  tarball — and refuses to pass if it cannot determine one. Scanning the build
  tree instead would have described the toolchain rather than the artifact:
  every artifact here is produced by a different SDK (the runner's own for the
  Fedora RPM, a container's for EL9 and the tarballs, a floating one for
  Windows and macOS), and none of them is `dist/LOCKFILE_SDK`.

- **CycloneDX SBOMs now list the runtime packs.** `Microsoft.NETCore.App.Runtime.<rid>`
  and `Microsoft.AspNetCore.App.Runtime.<rid>` are compiled into the binary and
  ship inside it, but were absent from every SBOM — v1.2.7's listed 467
  components and neither of them. The versions come from MSBuild rather than
  being assumed to follow the SDK, and generation now fails rather than emit an
  SBOM that omits the runtime.

- **The EL9 builder container no longer freezes its toolchain.** It installed
  the SDK once, at creation, and never looked again — a builder created
  2026-08-15 was still on `dotnet-sdk 10.0.110` in late August, so every EL9
  RPM built in between shipped the vulnerable runtime. The SDK is now refreshed
  when an existing container is reused.

### Performance

- **The planner is told that `space_name` and `subpath` correlate.** Every
  query's `WHERE` leads with the pair, and PostgreSQL was estimating it as two
  independent selectivities — but a subpath belongs to exactly one space, so
  `/orders` occurs only inside `purchase`. Measured on a 22.7M-row instance,
  `purchase/orders` was estimated at 375,397 rows against an actual 2,589,782:
  **6.9x low**. With extended statistics it estimates 2,560,137, an error of
  1.1%.

  This is plan quality, not counting. A 6.9x underestimate on the largest table
  shapes join order, scan choice and memory sizing for every query that touches
  it. Upgrades pick it up at the next autovacuum `ANALYZE`, or immediately with
  `ANALYZE entries; ANALYZE attachments;`.

### Documentation

- **`QUERY_TOTAL_CAP` is documented in `config.env.sample`.** The setting that
  bounds a pagination count shipped in v1.2.8 without a line in any sample an
  operator would read. Both sample configs are now pinned against
  `DmartSettings` by tests, because an unrecognised key there is not a soft
  failure: dmart exits on it, so a stale key in the sample hands an operator a
  file that refuses to boot, and one in the packaged config breaks a fresh
  install.

### CI

- **Superseded pull-request runs are cancelled instead of queueing.** Neither
  workflow declared a concurrency group, so every push started a full run and
  the obsolete ones kept their runners. Pushes to `master` still run to
  completion — that run is the record for a commit that has already landed.

- **The one required status check moved to a hosted runner.** `build-and-test`
  reads a `needs` result and echoes it — about six seconds — but was pinned to
  the self-hosted pool, where it queued behind 8-10 minute build jobs and lost
  the race to every newer run. Merges were waiting twenty minutes on an `echo`.

## v1.2.8 — 2026-08-23

### Performance

- **A query's `total` no longer counts every matching row.** `total` is a
  pagination count and counting is O(matching rows) whatever the indexes look
  like. On a production instance one subpath holds 2,589,782 rows, and every
  page request re-counted all of them: an Index Scan over 2.59M entries with a
  heap visit each, 558,866 buffer hits (~4.4 GB), 2,435 ms warm — and far worse
  under concurrency, where it produced a p50 of 17s and thousands of client
  cancellations in an hour.

  With the new `QueryTotalCap` setting above 0, the count is emitted as
  `SELECT COUNT(*) FROM (SELECT 1 FROM t WHERE <filters> LIMIT cap+1) c`, so the
  scan stops as soon as `cap+1` rows qualify. Measured on that same production
  table: **2,435 ms → 29 ms, 558,866 → 10,006 buffers.**

  `QueryTotalCap` defaults to **0 (unlimited)**, which is byte-identical to the
  previous behaviour and preserves Python parity — a deployment must opt in.
  Above the cap the response reports `total` as the cap AND sets
  `total_is_lower_bound`, because a client reading a clamped total as exact is
  the failure this would otherwise introduce. The `LIMIT` is applied after the
  ACL predicate, so a cap can never count rows the actor cannot see.

### Security / CI

- **The self-hosted security gate now actually runs all three scanners.** Steps
  execute under `bash -e`, so a non-zero exit from gitleaks/trivy/semgrep
  aborted the step at the scanner invocation: the `rc=$?` capture that followed
  was dead code, the remaining scanners were skipped, and the gate's own result
  step never reported. Scanner status is now captured with `|| rc=$?`, and the
  gate result runs even after a failed scanner.

- **`.gitleaks.toml` allowlist paths are anchored.** gitleaks matches path
  regexes as unanchored substrings, so `README\.md$` exempted all eleven READMEs
  in the tree rather than the intended root one, and `dmart.Tests/` and `seed/`
  matched at any nesting depth. A credential pasted into a nested README would
  have passed the gate silently.

- **The .NET dependency graph is scanned for CVEs.** trivy detects NuGet by the
  filename `packages.lock.json`; this repository deliberately keeps that content
  as `dist/deps/<slug>.lock.json`, outside the build, so trivy walked past every
  .NET dependency and reported only the JavaScript lockfiles. 93 NuGet packages
  across five projects had never been checked against a vulnerability database.
  The gate now materialises the recorded graph under the expected name — outside
  the worktree, since a `packages.lock.json` in the tree is what breaks the
  distro builders — and scans it. No findings today.

- **trivy no longer scans its own binaries.** The gate downloads gitleaks and
  trivy into `.cigate/`, which was neither gitignored nor skipped, so trivy's
  gobinary analyzer reported their embedded Go stdlib CVEs — fixable ones, which
  `--ignore-unfixed` does not suppress.

- **Semgrep is version-pinned**, and its exit codes are distinguished: 1 means
  findings, ≥2 means the scanner itself failed. Both fail the gate, but a
  crashed scanner is no longer reported as "found security issues".

## v1.2.7 — 2026-08-22

### Fixed

- **The release's aggregate `SHA256SUMS-all` job can find the release again.**
  Its "download every asset" step ran `gh release download` with no repository
  context and failed on v1.2.6; the step now passes `GH_REPO`, so the signed
  aggregate checksum manifest is produced with the rest of the artifacts.
- **The query-search feature-matrix timestamp test no longer fails on non-UTC
  machines under the SQLite driver.** The fixture stamped rows with
  `DateTime.UtcNow` while dmart's timestamps are naive LOCAL wall clock
  (`TimeUtils.Now()`); SQLite's lexicographic text comparison exposed the
  offset, while PostgreSQL masked it through the session-timezone coercion.
  Test-only fix, plus new regression pins (`SqliteTimestampRangeTests`) that
  hold the SQLite timestamp storage format, the epoch-ms bound expression,
  and the server binding path together.
- **An empty `filter_tags` set emits a safe constant-false predicate.** The
  PostgreSQL containment seam produced an empty `()` for a zero-length value
  list (a syntax error); the sole caller guards on a non-empty set, but the
  seam now returns `FALSE`, matching the SQLite dialect which already did.

### Performance

- **`@tags:` / `@roles:` / `@groups:` searches are now index-served.** The
  positive emission used to OR the containment with a `jsonb_typeof`-guarded
  object-ILIKE fallback; PostgreSQL can only BitmapOr an OR whose every arm is
  indexable, so the fallback arm forced a **sequential scan** on each such
  search. Positives now emit one bare `col @> '["x"]'::jsonb`, served straight
  from the existing `jsonb_path_ops` GIN indexes. Semantics note: a row whose
  tags/roles/groups column holds a JSON *object* (a shape the models never
  write) no longer substring-matches. Negated selectors keep the old emission
  (NOT-containment can't use an index anyway).
- **`filter_tags` no longer sequential-scans.** It compiled to `tags ?| $1`,
  but `?|` is not in the `jsonb_path_ops` operator class, so
  `idx_entries_tags_gin` never served it. It now compiles to
  `(tags @> '["a"]' OR tags @> '["b"]')` — equivalent for arrays of strings —
  which the GIN index serves as a BitmapOr.
- **Composite `(space_name, subpath)` indexes** on `entries` and `attachments`
  replace the single-column `space_name` indexes (whose leading-column role
  the composites cover). Every query's WHERE leads with exactly this pair.
- **Npgsql automatic statement preparation** (`DATABASE_MAX_AUTO_PREPARE`,
  default 200): the hot statements were parsed and planned by PostgreSQL from
  scratch on every execution.
- **Creates issue three fewer SQL statements.** The duplicate-shortname probe
  went typed-then-untyped, and the typed leg always misses on a create; it is
  now a single untyped lookup. The parent folder consulted by the uniqueness
  gate and the folder-content gate was loaded twice, identically, on two
  connections; it is now loaded once and shared.
- **Opt-in auth read cache** (`AUTH_CACHE_TTL`, default 0 = off): caches the
  per-request user row + session-validity pair for the configured seconds.
  Off, behavior is unchanged. On, single-node revocations still take effect
  immediately (writes evict), and other replicas converge within the TTL.
- **`JsonbHelpers.EnumMember` no longer reflects per call** — the
  `[EnumMember]` map is built once per enum type; the helper runs several
  times on every request.

## v1.2.6 — 2026-08-22

### Security

- **Frontend dependency advisories cleared.** `yarn audit --groups dependencies`
  flagged esbuild (<0.25.0, GHSA-67mh-4wv8-2f99) and @tootallnate/once (<2.0.1)
  in the embedded cxb/catalog SPAs; both are pinned forward via `resolutions`.
  The audit is now clean and both SPAs still build.

### Changed

- **The published SBOM now covers the embedded frontends.** dmart compiles the
  cxb and catalog Svelte SPAs into the AOT binary, so their npm dependencies
  ship inside the executable. `dist/frontend-sbom.sh` reads them from `yarn.lock`
  (the resolution the build installs from) and merges them into every per-RID
  CycloneDX document — the SBOM went from the .NET graph alone to the full
  server-plus-frontend inventory.

## v1.2.5 — 2026-08-18

### Security

- **`filter_fields_values` now constrains every branch of a caller's search.**
  The permission clause was concatenated onto the caller's expression as bare
  tokens, giving it no special standing in the grammar. Because AND binds
  tighter than OR, a caller-supplied `or` split the expression and left the
  clause governing only the right-hand branch — `(k=v) OR (k=w AND dept=sales)`,
  where the left side is reachable without satisfying the permission. A second
  route needed no boolean keyword at all: an alternation on the constrained
  field (`@dept:sales|ops`) accumulated into the permission's own selector,
  yielding `dept IN (sales, ops)` and returning exactly the rows the restriction
  existed to hide. The caller's search is now parenthesised before the clause is
  appended, and unbalanced parens are normalised first so a stray `)` cannot
  close the wrapper early. The query-policy gate is a separate clause and always
  held, so this widened a row-level field restriction inside an
  already-granted subpath rather than reaching ungranted rows.

  One behaviour change worth knowing: negating the field a permission
  constrains (`-@dept:sales` under an FFV of `@dept:sales`) now returns nothing
  instead of every `sales` row. The two used to land in one leaf run where the
  last sign won and the caller's negation was silently discarded.

### Fixed

- **`@query_policies:…` searches no longer fail on SQLite.** The text-array
  predicate referenced the bare iteration alias, which resolves under
  PostgreSQL's `unnest` (a column) but not SQLite's `json_each` (a table), so
  every such search raised `no such column: elem`.
- **Array searches with a numeric value no longer abort on a non-numeric
  element.** Elements of a scalar array are text, and the cast was applied to
  all of them, so `-@tags[]:100` over `["red","blue"]` failed the whole query on
  PostgreSQL. Guarded for the equality, comparison and `BETWEEN` forms.
- **A plugin that fails to load is now visible.** The scan runs before the
  logger exists, so a failure produced one line on stderr and startup carried
  on — a deployment that lost a plugin looked completely healthy, and the only
  symptom was behaviour that quietly stopped happening. Failures are now
  replayed through the logging pipeline at Error with a summary line, and
  reported by `GET /info/plugins` as records with `status: "failed"` and a
  `reason`. The silent case is covered too: a plugin directory holding a
  `config.json` but no binary (missing or misnamed) used to be skipped without
  a word.

- **Repeated selectors are no longer collapsed across `or` or paren groups.**
  Deduplication is a cosmetic shortening, but across a boolean it moved a
  restriction rather than shortening it, and could drop an injected permission
  token outright.

### New

- **`MAX_PASSWORD_RECORDS_PER_REQUEST`** (default 50) bounds how many records in
  one `/managed/request` may carry a password. Each costs an Argon2id hash at
  m=100 MB, and the batch was otherwise unbounded. Records without a password
  are not counted; `0` disables the check.
- `/managed/request` accepts `password` when creating a user, validated against
  the password rules and hashed with the shared hasher. The update path still
  rejects it.

### Documentation

- The native-plugin `config.json` example in `README.md`, `docs/plugins-and-mcp.md`
  and `docs/contributing.md` was unusable. `"subpaths": ["__ALL__"]` is the
  legacy flat form, which dmart rejects at load with a migration error, and
  `"schema_shortnames": ["__ALL__"]` is matched as a literal schema name — so
  even after fixing the first, the plugin would load and never fire. Both now
  match the shipped samples: a `{ "__all_spaces__": ["__all_subpaths__"] }`
  dict, and an empty list to mean every schema.
- `docs/query.md` documents array-field predicates (including that `-@` makes a
  value-level operator inert) and same-field accumulation — the contracts the
  query-search regression tests defend.

## v1.2.4 — 2026-08-17

**User deletion is now soft by default.** Deleting a user no longer removes the
row: it stays so foreign keys keep resolving, marked deleted, with email,
msisdn and password cleared. Nothing the user owns is touched.

### New

- **`USER_DELETION_MODE`** — `"soft"` (default) or `"hard"`, applied uniformly
  to self-delete (`POST /user/profile/delete`) and admin delete. Hard mode is
  the previous behaviour: the row and everything the user personally owns go,
  and structural objects they owned (spaces, roles, groups, permissions, other
  users) are reassigned to the `dmart` sentinel. Histories are never deleted in
  either mode.
- Two columns on `users`: `is_deleted` and `deleted_at`. Added automatically on
  upgrade for both backends.

### Behaviour

- **A deleted account cannot log in, refresh, or be edited.** The check is
  `IsUsable` (`is_active && !is_deleted`), applied at JWT validation, WebSocket
  upgrade, OAuth refresh, OTP request and password-reset-confirm. `is_active`
  alone would have let a password reset revive a deleted account, since soft
  delete does not touch it.
- **Login is anti-enumerating**: a deleted account gets the generic "invalid
  username or password", never "account locked" — which would imply
  recoverable, and would confirm the account existed.
- **Creating a user with a soft-deleted shortname resurrects the name.** Soft
  delete ends the ACCOUNT, not the NAME. Without this the shortname was
  unusable forever — create refused it as taken, update refused it as deleted —
  which would have stranded system accounts like `anonymous`. The create writes
  every other column, so nothing survives from the deleted account but the
  name.
- **`force` still applies in hard mode.** Deleting a user who has created
  records is refused unless `force=true`, exactly as before this release. The
  mode picks soft-vs-hard; `force` answers "yes, I know this user owns records".
  Soft mode ignores it, having nothing to guard.
- **Soft delete writes one history row**, recording who did it and what changed
  (`is_deleted` false→true, `email` old→null). Hard delete still writes none —
  there the row is genuinely gone.

### Upgrade note

Deleting a user is **irreversible**. Nothing sets `is_deleted` back to false
except creating a new account under the same shortname; both upsert paths pin
the flag to its existing value precisely so an unrelated write cannot revive an
account by accident. If you want the old destructive behaviour, set
`USER_DELETION_MODE="hard"`.

## v1.2.3 — 2026-08-16

Packaging and CI only — no changes to dmart itself. The container image is
rebuilt on a different base, so it is worth taking.

### Container

- The container image now **installs the Alpine package** instead of compiling
  dmart in a `dotnet/sdk:10.0-alpine` stage. That stage was a second
  `linux-musl-x64` AOT build of exactly what the APK job already produces —
  ~5 minutes of a shared 3-runner pool on every release, for a byte-identical
  binary. The image now ships the same artifact an Alpine user installs, so it
  doubles as a test of that package, and the release job smoke-runs it before
  pushing.
- The container base is pinned to **`alpine:3.24`**, matching the Alpine the
  binary is compiled against (`dotnet/sdk:10.0-alpine` is 3.24 / musl 1.2.6).
  It was `alpine:edge` — a rolling pre-release whose musl can drift ahead of
  the compiler's — with no recorded reason. `Dockerfile.runtime` is pinned to
  the same base.

  **If you run the image:** the base moved from a rolling pre-release
  (`3.25.0_alpha`) to Alpine 3.24, so the OS packages inside it change version
  accordingly. dmart itself is byte-identical to 1.2.2 — same binary, same
  behaviour.

### CI

- CI now **builds the container image and serves it**, on packaging changes and
  on every push to master. The image was previously built only by the release
  workflow, so a broken Dockerfile or package layout reached a tag before
  anyone saw it. Serving it matters more than building it: with
  `libe_sqlite3.so` removed from the image, `podman build` still succeeds and
  only the readiness check catches it.

## v1.2.2 — 2026-08-16

A performance release for the Parquet export, plus one cleanup-command change.

**Note for anyone comparing archives across this upgrade:** attachment archive
bytes differ from earlier releases. The streamed reader emits PostgreSQL's JSON
key order where the old one emitted C#'s, so the same attachment produces
different — equivalent — text. Verified by importing both archives into fresh
databases and diffing all 60,000 restored rows: identical. Only a byte-level
comparison of the archives themselves will notice.

- Parquet export now streams **histories and attachments** through `COPY` as
  well as entries, and stops parsing their JSON columns into objects only to
  serialise them straight back. Attachments **16x faster** (4131 ms to 258 ms on
  60,000); histories take a full-space export of 21,843 entries + 40,000
  histories from 350 ms to **304 ms**, and unlike the entries change this one
  pays at any size. Media bytes are still fetched per row — streaming them
  inline would hold every blob in memory at once. Same column-type guard and
  fallback as the entries reader.

- `prune-empty-histories` now **deletes** rows with a NULL diff instead of
  reporting and skipping them. A NULL predates the `{}` convention but means the
  same thing — an audit row recording no change — so leaving them behind meant
  the cleanup only half-worked. The count is still broken out separately.

- Parquet export reads entries through a streaming `COPY` on PostgreSQL instead
  of walking the table with `LIMIT/OFFSET`. **2.6x faster on 218,430 entries**
  (4034 ms to 1571 ms); no measurable change at 21,843, where three pages leave
  nothing to win. `OFFSET` makes PostgreSQL scan and discard everything before
  it, so the paged reader is quadratic in table size while the streamed one is
  linear — the gap widens as the install grows. Guarded by a column-type check
  against the live catalog; on a mismatch it falls back to the paged reader with
  a warning rather than failing, because a schema change should make an export
  slower, not impossible.

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
