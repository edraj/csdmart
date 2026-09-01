# Changelog

## Unreleased

### Security

- **A query could return entries from subpaths the caller has no permission
  on.** The hierarchical subpath filter was built as
  `subpath = $n OR subpath LIKE $n || '/%'` with the caller's subpath bound raw
  and no `ESCAPE` clause. LIKE reads `_` as "any one character", so a query
  scoped to `space/my_folder` also matched `space/myXfolder`, `space/my-folder`
  and every other one-character sibling — and underscores in a folder name are
  the house style, not an edge case.

  It was invisible until v1.3.0 because the ACL predicate cleaned up after it:
  an actor's policy IS escaped on its way to a LIKE pattern, so the
  over-matched rows carried a `query_policies` token the actor's pattern did
  not match and were dropped before anyone saw them. v1.3.0 added a tautology
  skip that omits the ACL predicate when the actor's policies provably cover
  the requested scope — correct in itself, but it removed the masking, and the
  sibling rows started coming back to actors holding no permission on them.

  Both query paths now escape the bound prefix (`\`, `%`, `_`) and match under
  `ESCAPE '\'`: `DataAdapters/Sql/QueryHelper.BuildWhereClause`, which the
  count, aggregation and semi-join paths all share, and the packaged
  `Dmart.SqlAdapter`. The escaping is emitted in SQL rather than applied to the
  bound value so one parameter still serves both halves of the predicate,
  leaving every positional parameter after it unmoved.

  **Operators:** any deployment on v1.3.0 with sibling subpaths whose names
  differ by a single character at an underscore position should treat reads
  against those subpaths as having been unrestricted. Subpaths without `_` or
  `%` in their names were never affected.

### Fixed

- **A blank `otp_email_subject` override sent a blank Subject header.** The
  fallback to the `"OTP"` literal was `?? "OTP"`, which only fires on a missing
  key. An operator overlay at `~/.dmart/languages/<locale>.json` containing
  `"otp_email_subject": ""` — the natural way to write "no subject" by hand —
  went straight through to the mail server. Now guarded with
  `IsNullOrWhiteSpace`, which is what the comment above it always claimed.

- **`pruneEmptyFormValues` silently dropped `Date`, `File`, `Map` and `Set`.**
  The recursive branch tested `typeof value === 'object'`, which is true of
  every one of them, and `Object.keys()` on them is `[]` — so the function
  returned `undefined` and the value vanished from the payload with no error.
  Latent rather than live (today's schema forms only produce primitives, arrays
  and plain objects), but the first file-upload or date-valued field would have
  hit it. The branch now tests for a plain object by prototype.

### Changed

- **Wildcard policy expansion is capped at 256 exact tokens.** A policy with a
  wildcard resource type enumerates all 30 resource types — doubled, for the
  four-segment shape, across both `is_active` values — so a single
  `space:subpath:*:*` is 60 bind parameters, and an actor inheriting one per
  group multiplies that by their group count. Past the cap the remaining
  policies keep the LIKE form they always had, which matches exactly the same
  rows; only the spelling changes.

### Internal

- **The keyset-cursor index test now guards what its comment claims.** It
  sorted each `UNIQUE (...)` column list before comparing, so
  `UNIQUE (space_name, subpath, shortname)` would have passed while quietly
  restoring the per-batch full-table sort that `update_query_policies`' keyset
  cursor exists to avoid. The order is asserted as written, and both schemas
  are checked — `update_query_policies` runs against SQLite too, and the two
  DDL files are maintained separately.

- **`RETRIEVE_TOTAL_DEFAULT` documents its one exception.** Both the setting's
  comment and `config.env.sample` said an absent `retrieve_total` resolves to
  the setting. `/public/query` rewrites it to `false` before the query reaches
  `QueryService`, deliberately, so the setting never governed public traffic.

## v1.3.0 — 2026-08-25

### Migration required

- **Run `dmart update_query_policies --all-tables` as part of this upgrade.**
  Skipping it causes **access loss**, not degraded matching.

  `QueryPolicies.Generate` now emits the owner-unscoped literal
  (`{space}:{subpath}:{resource_type}:{is_active}`) unconditionally. It
  previously *replaced* that literal with an owner-group-scoped one whenever a
  row had an `owner_group_shortname`, so rows written before this release do
  not carry it.

  That matters because the new indexable filter rewrites a wildcarded
  permission into the exact strings a row can carry: `{key}:true:*` becomes
  exactly `{key}:true`, and `{key}:*` becomes `{key}:true` / `{key}:false`.
  Neither ever matches an owner- or group-scoped literal. So a caller whose
  permission carries an `is_active` condition, or no conditions at all, sees
  **zero** rows where they previously saw the group's — until the rows are
  rewritten.

  The command covers all six tables that carry `query_policies`: `entries`,
  `users`, `roles`, `groups`, `permissions`, `spaces`. `fix_query_policies` is
  **not** sufficient — it heals rows whose array is *empty*, and these rows
  have a stale non-empty one.

  **How long it takes.** Measured on a 2,000,000-row table in the worst case,
  where every row needs rewriting: **71 seconds**. The command pages by keyset
  over the `(shortname, space_name, subpath)` unique index, so cost is linear
  in rows — scale that figure by your row count rather than assuming it
  degrades. It is idempotent: a second pass updates 0 rows and returns in
  seconds, so it is safe to re-run if interrupted.

  That figure comes from a 24-core machine with PostgreSQL tuned so the table
  was fully cached (`shared_buffers=8GB`). **Rehearse against a copy of your
  own data before the maintenance window** — write throughput, not read cost,
  is what dominates here, and yours will differ.

  Rows are unreadable to wildcarded permissions until it finishes, so run it in
  the same maintenance window as the deploy, not after it.

### Performance

- **The read-time ACL filter is now indexable.** The two row tests in the
  visibility predicate were written in forms no index can serve — `unnest` +
  `LIKE` over `query_policies`, and `jsonb_array_elements` + `->>` over `acl`,
  both per-row subplans — so `idx_entries_query_policies_gin` and
  `idx_entries_acl_gin` were never used and every branch of the `OR` forced a
  sequential scan. They are now `&&` array overlap and `@>` jsonb containment,
  which the planner can combine under a `BitmapOr`.

  A caller's wildcard policies are expanded into the exact strings a row can
  carry. Anything that does not fit one of the three enumerable shapes is
  **not** guessed at and keeps the original `LIKE` test — narrowing a policy
  silently would deny access that should be granted.

  Measured end to end on a 2M-row folder, tuned PostgreSQL, 100 concurrent
  callers: **14.0 → 36.5 req/s**, mean **6773 → 2668 ms**, p99 **10006 → 4582
  ms**. Single request: **833 → 111 ms**.

  Two things worth knowing about where that gain comes from. It is **entirely
  in `COUNT(*)`** — with `retrieve_total` false both the old and new code serve
  ~10,400 req/s, identical within noise. And it is CPU work avoided rather than
  I/O: raising `shared_buffers` from 128 MB to 8 GB, enough to cache the whole
  table, moved the ratio by less than a factor of two.

- **The ACL predicate is skipped entirely when it is a tautology** — when the
  caller's permissions already cover every row the query can reach, the
  predicate can only cost a scan. `entries` only.

- **Planner statistics for `(space_name, subpath)`** ship in the schema
  (`entries_space_subpath_stx`, and the equivalent on `attachments`).
  PostgreSQL assumed the two columns were independent and multiplied their
  marginal selectivities; on a real instance that under-estimated one folder by
  **6.9x**, which was shaping every plan on the largest table.

### Added

- **`RETRIEVE_TOTAL_DEFAULT`** decides what a query means when it omits
  `retrieve_total` entirely. The field is tri-state: an explicit `false` always
  skips the count, an explicit `true` always performs it, and *absent* now
  resolves to this setting. Defaults to `true`, which is the existing
  Python-parity behaviour, so nothing changes unless you set it.

  Counting is what the request costs: on the same 2M-row folder at 100
  concurrent, the endpoint served 36.5 req/s with the count and **10458 req/s**
  without it. `QUERY_TOTAL_CAP` bounds that work; this removes it for callers
  that never asked.

  **Before setting it false:** when the count is skipped, `total` is reported
  as **-1** — not 0, and not absent. Set it only once your clients either send
  `retrieve_total: true` where they need a count, or ignore `total` entirely.

- **`dmart update_query_policies --all-tables`** widens the recompute from
  `entries` to every table carrying `query_policies`. The default scope stays
  `entries` for Python parity.

- **OTP emails carry a localized subject.** `otp_email_subject` resolves
  through the same `LanguageLoader` path as the message body, so it is served
  in the recipient's language and stays operator-overridable at
  `~/.dmart/languages/<locale>.json` without a rebuild. English, Arabic and
  Kurdish ship. The code is deliberately **not** substituted into the subject —
  that would leak it to lock-screen notification previews and mail-server logs.

### Fixed

- **`groups` was reachable by no backfill command.** Six tables carry
  `query_policies`; `fix_query_policies` listed five, omitting `groups`, and
  `update_query_policies` was `entries`-only. A `groups` row with an owner
  group therefore had no path to gain the literal the new filter needs. Both
  commands now cover it.

- **`ISqlDialect.ArrayOverlapAny` has a default implementation.**
  `Dmart.QueryGrammar` is a published package, so adding it as a bare abstract
  member would have broken every third-party dialect at compile time. The
  default delegates to `ArrayAnyLike` — same rows, not indexable; both in-tree
  dialects override it.

- **`PermissionFilter.Append` keeps a five-parameter overload.** Adding
  optional parameters to a published API is source-compatible but not
  binary-compatible: C# bakes optional-argument values into the *caller's* IL,
  so an assembly built against the old signature would throw
  `MissingMethodException` while still compiling from source.

- **`update_query_policies` no longer degrades quadratically.** It paged with
  `LIMIT/OFFSET` ordered by `(space_name, subpath, shortname)`, which matches no
  index — so every batch sorted the whole table to disk and discarded
  everything before the offset, at 1575 ms per 1000 rows. Because both the
  per-batch cost and the batch count scaled with table size, a 23M-row table
  projected to roughly two days. It now pages by keyset over the
  `(shortname, space_name, subpath)` unique index every affected table already
  carries: **0.216 ms per batch, and 71 s for the same 2M-row migration that
  previously projected to ~22 minutes.** This matters because the migration
  above is not optional.

- **The skipped-count sentinel no longer reaches page arithmetic in cxb or
  catalog.** Every read of `total` used an idiom that misses `-1` — `?? 0` does
  not catch it because it is not nullish, and `|| records.length` does not
  because `-1` is truthy — so it flowed into pagers that rendered "1 of 0
  pages". All 12 read sites now go through `ui-shared/query-total.ts`.

- **Empty form values are pruned before create in catalog**, so a schema-driven
  form no longer submits blank strings and empty objects the server then
  rejects.

### Known issues

- **`ORDER BY updated_at DESC LIMIT n` still defeats the ACL indexes when the
  predicate is selective** ([#213](https://github.com/edraj/csdmart/issues/213)).
  The planner drives the page fetch from `idx_entries_updated_at` and applies
  the visibility predicate as a filter, walking the whole table. Measured at
  1512 ms against 0.069 ms for the same predicate when the indexes are used.
  This release does not address it; the gains above are in `COUNT(*)`.

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
