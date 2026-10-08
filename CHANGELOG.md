# Changelog

## Unreleased

### Added

- **dmart publishes a website.** `dmart website build` renders the `website`
  space into a static site, and the server serves it under `WEBSITE_URL`
  (default `/website`) from `WEBSITE_DIR` (default `~/.dmart/website`). The
  pages are real HTML with their text in the markup, plus `sitemap.xml`,
  `robots.txt`, canonical and Open Graph tags. The build reads **as an
  anonymous visitor**, so it publishes exactly what the `world` permission
  lets the public see: a fresh install publishes nothing, drafts
  (`is_active: false`) never go out, and with nothing public the build
  refuses rather than overwrite a good site with an empty one. A rebuild
  replaces the live site atomically and is served on the next request, with
  no restart. See `docs/website.md`.
- **The bundled `website` space is now all of dmart.cc.** Alongside the 14
  docs pages it holds the landing page (`/site/home`) and the navigation and
  footer (`/site/config`), each validated by a schema in the space
  (`landing_page`, `site_config`). The built-in template ports dmart.cc's
  design (light and dark themes, the docs sidebar, the landing layout and
  its architecture figure). `--template <dir>` swaps in your own.
- Markdown is rendered with Markdig (new dependency), with raw HTML and
  generic attributes disabled and link targets limited to http(s), mailto,
  tel and relative URLs. The site gets its own Content-Security-Policy:
  scripts from `'self'` plus one pinned, integrity-checked mermaid build on
  jsdelivr, which loads only on pages that draw a diagram. There is no
  `'unsafe-inline'` for scripts, so script in an `html`-typed page doesn't
  run in the origin the admin UI shares.

### Fixed

- **Ten of the bundled website pages lost structure in their conversion
  from dmart.cc's Svelte markup.** The HTML-to-markdown converter treated
  `<div>` as transparent, so sibling cards ran together into one paragraph
  (features, data model, plugins, tickets, access control and more). It
  also dropped `<br/>` inside mermaid labels ("① Metaidentity, ownership"),
  and turned the drivers page's install commands into plain prose. The
  converter is fixed. The pages were updated by merging in only the
  converter's change, which keeps every edit made to them since (#345,
  #357). Tables, diagrams and headings are unchanged in count, and no
  words were lost.

### Removed

- `catalog/ssg/generate.mjs` and the catalog's `ssg:dmart` script. The Node
  generator was the prototype for `dmart website build`, which replaces it
  inside the binary.

### Tooling

- **The catalog is `any`-free too**: 667 `no-explicit-any` warnings are
  gone (shared types in `lib/types.ts`, a generic `DataTable`, typed
  messaging and schema-form shapes) and the rule is an error. The typing
  exposed real bugs on the retrieved-entry shape (flat, not under
  `attributes`) and on the server's stripped empties, all fixed: assigning
  a role replaced every role the user had; template-based entries never
  rendered; the users folder's CSV flags and configured columns never
  loaded; the admin schema title always fell back to the shortname and the
  author rows always showed "inactive" with no date; group-message
  attachments threw (they were uploaded to a null user's folder); a
  refreshed message with attachments crashed the preview; the create-group
  modal showed "[object Object]" participants; editing a group opened an
  empty description; bulk delete/trash toasts showed a literal "{count}";
  saving column settings threw on localized column names or with no folder
  loaded; a missing space ordinal produced a NaN sort.
- **cxb is `any`-free**: the 156 remaining `@typescript-eslint/no-explicit-any`
  warnings are gone (SDK types, small interfaces for the per-resource-type
  form shapes in `utils/entryShapes.ts`, `unknown` plus an `isRecord` guard
  for dynamic JSON) and the rule is an error. The typing exposed nine
  latent crashes on absent data — the server strips empty arrays and
  objects — which are guarded now: a workflow without `states`, a schema or
  workflow save without a payload, a null entry in the permission and role
  explorers, `.splice`/`.filter` on a non-array reached by path, seeding
  defaults into an array payload, and a null spaces query. The events list
  header showed the raw key `timestamp`; it reads "Timestamp".
||||||| parent of 86550b1 (test(e2e): Playwright suite for cxb and the catalog, run in CI)
- **A Playwright end-to-end suite drives both SPAs through a real browser**
  (`e2e/`): sign-in, the spaces, folder, post, admin, users, My Entries,
  entry view/edit/create, notifications/polls/surveys/messaging pages, the
  language and theme switches, dark mode and phone width, against a server
  seeded with the bundled sample spaces (`e2e/run-server.sh`, the same
  script locally and in CI). Every test fails on an uncaught exception or
  an unexpected `console.error`. CI runs it on a hosted runner as
  `E2E browser tests (Playwright)`.
- cxb no longer depends on `vite-plugin-static-copy`: Vite's public
  directory already ships `config.json`, and the plugin was the only
  dependent of the chokidar 3 chain that pinned `braces` <= 3.0.3
  (GHSA-vfj7-8cjw-p6xm, no patched release) — the last open Dependabot
  alert. Sixteen packages leave the lockfile.

## v1.5.21 — 2026-10-08

### Changed

- **Both frontends were overhauled on one design system.** `cxb` (admin) and
  `catalog` (public) now share the same idea of a page: design tokens
  (surfaces, borders, text, semantic colours with soft variants, radius,
  shadows) with a complete dark palette — cxb's `.dark` class, catalog's
  `[data-theme]` plus the OS preference with a light/dark/system toggle —
  and a small ui kit in each (`PageHeader`, `Toolbar`/`CatalogToolbar`,
  `Card`, `Badge`, `EmptyState`, `ErrorState` with retry, `LoadingState` that
  keeps existing rows under an overlay, `ConfirmDialog`, `IconButton`,
  `Pagination`/`DropdownMenu`). Every page was rebuilt on it: cxb's shell,
  login (stacks on phones), spaces, sidebars, list view (a real table with
  sentence-case headers, formatted dates, row links, selection), entry
  tabs, forms, modals, renderers and tools pages; catalog's header
  (accessible language menu, theme toggle, phone drawer), breadcrumbs, 404,
  auth/profile pages and the four browse pages (which went from 7,438 to
  2,646 lines). Native `alert`/`confirm`/`prompt` are gone in favour of
  dialogs and toasts; destructive actions name what they delete ("Delete 3
  entries") and confirm through one dialog.
- **Arabic is a real UI now.** cxb translated 14 of 77 components (switching
  to AR flipped the direction and nothing else); every user-visible string
  in both SPAs goes through the translation layer, physical direction
  classes were replaced by logical ones (`ms-`/`me-`/`margin-inline`…),
  `<html lang dir>` follow the locale, first-time visitors get
  `default_language`, and the locale files are at parity — cxb en/ar
  719 keys each, catalog en/ar/ku 1,790 each, both pinned by a test (same
  keys, same ICU placeholders, no English left as an Arabic value). Catalog
  gained 61 Arabic and 121 Kurdish Sorani translations and lost 458
  unreferenced keys; its fallback locale is English.
- **Accessibility.** No nested interactive elements, every icon-only
  control named, cards and rows that navigate are links (middle-click and
  open-in-new-tab work), inputs labelled, dialogs with focus trap and
  Escape, `aria-pressed`/`aria-current`/`aria-sort` where state exists; the
  build-time suppression of every a11y warning in both `vite.config.ts` is
  gone and svelte-check runs with `--fail-on-warnings`.
- **cxb keeps the session in the HttpOnly cookie only.** The access token
  is no longer written to localStorage (nor the second copy inside the
  persisted user record); the cookie already authenticated every route
  including `/ws`.
- **cxb: the Form tab's "Update" button next to the shortname was a rename,
  not a save** — it is an explicit "Rename shortname" dialog now; the
  PlantUML server is configurable (`website.plantuml_server`) instead of
  hard-wired to plantuml.com.

### Fixed

- **`GET /user/google/login` and `/user/facebook/login` refuse to start the
  web flow when the callback URL is not configured.** A deployment set up
  for the mobile id-token flow has a client id and no
  `GOOGLE_OAUTH_CALLBACK`; the web start used to redirect to Google with an
  empty `redirect_uri`, and the user got Google's "Missing required
  parameter: redirect_uri" page. The start now answers with the name of
  the missing setting (it must be the absolute URL of the provider callback,
  registered with the provider as an authorized redirect URI).
- **Seventeen `JsonDocument.Parse(…).RootElement` sites no longer leak a pooled
  buffer per call.** A `JsonDocument` rents its backing buffer from the
  `ArrayPool` and returns it only on `Dispose`; the chained form never disposes,
  so each parse handed one buffer to the GC instead of the pool. Found by the
  .NET 11 SDK's new CA2026 rule while trialling RC1 (see
  `bench/REPORT-net11-rc1.md`); invisible to the .NET 10 analyzers. All sites
  now go through `JsonUtil.ParseElement`, which disposes the document and hands
  back a standalone element — the shape `JsonElement.Parse` will have once the
  target framework moves to 11. Affected: CSV import's per-row body and the
  boolean constants, jq result parsing, MCP tool/registry descriptors, schema
  seeding at start-up, the CLI's response parsing and `--version`/manifest
  printing, and `JsonMerge`.
- **catalog could not reach its own server unless it ran on `:8282`** —
  `public/config.json` shipped that backend; it is `""` now. With no
  config.json on disk the server used to hand the bundle's file to the
  browser verbatim, where the global empty-property strip removed the
  `backend` key; the embedded file is now the last source in the lookup
  chain and goes through the same rewrite, so both SPAs always receive the
  request origin unless an admin configured a backend.
- **cxb bulk operations sent the wrong subpath** (delete/move used the
  list's subpath for every record, restore mis-sliced the leading slash so
  the destination space became "trash", trashing a folder sent `-a` for
  `/a/b`); one `utils/subpath.ts` with tests behind every path builder.
- **cxb CSV export was not CSV** (`JSON.stringify` of the `text/csv` body,
  and the list's `offset`/`limit` copied in so "download all" skipped rows).
- **cxb list paging**: changing rows-per-page kept the page number, emptying
  the last page stranded the user on "This folder is empty" with no pager,
  a failed query left the skeleton forever, a slow old response could
  overwrite a newer one, delayed counters could show "1 to -1 of -1", the
  History pager's offset arithmetic was wrong, "1 of 0 pages" on empty.
- **cxb**: password field capped at 24 characters (the server accepts
  longer); health-check page fetched "management" instead of the checked
  space; tools' subpath lookup used the shortname instead of the full path;
  form validators were bound but never run on save; breadcrumb crash on
  duplicate path segments; schema column keyed as owner; sidebar cache keys
  written and read in two spellings (new folders invisible until reload);
  `error.response.data` without optional chaining in six catch blocks;
  unsaved-changes guard did not cover in-app navigation; an entry nobody
  touched prompted "Leave site?" after opening its Form tab; Routify
  layouts and links regressed during the overhaul and were caught in the
  browser walk (layouts must keep `<slot />`; links take a node path and
  params).
- **catalog**: the folder listing's "Report" only showed `prompt()` and
  `alert()` and never called the API; every share link and breadcrumb
  404'd (missing resource-type segment, no `/catalogs` prefix, root-absolute
  hrefs escaping `<base href="/cat/">`); sorting re-sorted only the 20
  loaded rows while the server sorted by shortname ("newest first" could
  never show the newest); search queried every space and opened results in
  the current one, parked the page on an error screen on failure and let
  stale responses win; reporting from a sub-folder clobbered the listing's
  subpath; unknown totals showed "folder empty" and hid the search box;
  tag-mode Load-more checked the wrong flag; retry/CSV-upload refresh
  appended instead of resetting; one failing space zeroed every space's
  stats; `isOwner` compared the wrong field; the entry page's error state
  showed nothing and reactions reloaded the whole page; first-time visitors
  always got English; the entry description rendered raw markdown; fake
  "🔥 Hot" badge, "1 min read" and always-on status dot; "New post" had no
  handler and the Category filter nothing behind it; the admin pages could
  not list users, roles or permissions after a reload because the SDK was
  never told about the session and sent those queries to the public scope.
- **Lint and types**: cxb 244 and catalog 563 ESLint errors to 0 (keyed
  each-blocks, 57 bare `$goto;` statements, unused symbols, stale
  svelte-ignores, Svelte 4 syntax); 1,650 lines of never-imported catalog
  components, 250 lines of never-opened cxb modals, debug pages ("Welcome
  to the Svelte App", "We shouldn't be here"), 48 `console.log`s (two
  logged chat content, one the WebSocket URL with the token) removed; four
  helpers that had drifted between the SPAs live once in `ui-shared/`.

### Performance

- **Hashed SPA assets are cached for a year and served pre-compressed.**
  Both embedded SPAs' static files carried no `Cache-Control`, so every
  visit re-validated ~40 files and every 400 kB chunk was compressed on
  the fly. `assets/*` are `public, max-age=31536000, immutable`, entry
  points are `no-cache`, and the builds emit `.br`/`.gz` that the server
  sends as-is to clients that accept them.
- **cxb: a content page pulls ~90 kB of JavaScript instead of ~1.2 MB.**
  svelte-jsoneditor (421 kB) and @codemirror (410 kB) have no static
  importer left; marked, DOMPurify, typewriter-editor and plantuml-encoder
  load only when their editor/diagram opens; every entry tab's component
  is code-split and mounted only when active (hidden tabs no longer fetch
  schemas, roles, groups, permissions, history and spaces on every view);
  flowbite is split per route instead of one chunk each; `config.json` is
  preloaded. Every list click used to re-fetch the folder entry and rebuild
  the whole entry view, and a cold load built every page twice — both gone.
  The `SELECT *` per data-asset attachment (result never read) is gone; the
  tools' preview is bounded (100 entries / 200 kB); the dirty check runs on
  a timer instead of per keystroke.
- **catalog: entry chunk 324 kB → 35 kB, vendor chunk 430 kB → 184 kB.**
  The three locale JSONs are lazy chunks instead of ~298 kB in the entry;
  the catch-all vendor chunk no longer defeats the lazy editor import. A
  20-item listing made 40–60 requests (two avatar lookups per card, one
  attachment-count aggregation per item, a re-render after each); avatars
  are cached per shortname with one lookup per page and counts come from
  the attachments already in the response. The landing page's per-space
  counters and tags run in parallel without payloads; sign-in and expiry
  redirects navigate instead of reloading the app; `marked` is configured
  once per session instead of per component (its extension chain grew for
  the life of the page); images are lazy with cookie auth instead of
  fetched into blobs up front.

### Tooling

- Both SPAs have real `check` (svelte-check with the project tsconfig) and
  `lint` (ESLint 10 flat configs with the Svelte and TypeScript plugins)
  scripts, and CI runs them after the unit tests — cxb's check used
  `--no-tsconfig` and skipped every `.ts` file, catalog's lint crashed for
  lack of a config, cxb had none. Dependabot: brace-expansion,
  postcss-selector-parser, vitest and @vitest/mocker moved to patched
  versions (4 of the 11 open alerts; the rest are build-only with no
  upstream fix). Dead tooling removed (babel configs, cxb's never-run
  `.github` workflow, an unregistered service worker, localhost sitemap,
  unused markdown pipelines and dependencies).

## v1.5.20 — 2026-10-07

### Added

- **`move` renames a user.** A `move` request on a `user` record used to fall
  through to the entry mover and fail with "source entry missing" for every
  user. It is now a rename — users always live at `management:/users`, so
  only the shortname may change — done in one transaction over everything the
  user owns (entries, attachments, spaces, roles, groups, permissions, with
  `query_policies` rewritten for the new owner), the rows keyed on the user's
  own path (its attachments and the history of both) and the locks it holds.
  Sessions are dropped, so the user signs in again under the new name. Gated
  like an entry move (`update` on the source, `create` at the target), and
  the gate answers *before* anything about the target does: a caller without
  move access learns neither whether the account exists nor whether it was
  deleted. The `dmart` and `anonymous` sentinels cannot be renamed; a move
  onto the same name is a no-op success (the shape cxb's bulk move sends).
  History *authorship* (`histories.owner_shortname`) deliberately stays under
  the old name — it is an audit record of who did what at the time; the
  rename itself is recorded as a history row at the new coordinates.
- **A failing `jq_filter` is logged server-side.** The client has received a
  generic "jq_filter failed to evaluate" since V-20; jq's stderr now goes to
  the server log at Warning, tagged with the request's `X-Correlation-ID`, on
  both jq paths (top-level filter and join sub-query). What is read from jq's
  stderr — and so what is logged — is capped at 4 KB: `error("x" * 10000000)`
  passes filter validation and would otherwise write a 10 MB line per request
  from anonymous `/public/query`. Control characters are escaped so a filter
  cannot forge log lines.

### Fixed

- **cxb: a new search starts from page 1** instead of keeping the previous
  page offset, which landed past the end of a smaller result set.
- **A user move's after-action event is keyed on the destination**, with
  `src_shortname`/`src_subpath` in attributes as an entry move's is, so
  webhooks and realtime hooks learn the new name instead of being told about
  a record that no longer exists.
- **Renaming a user purges orphan locks and attachments at the destination.**
  Force-delete removes only what the deleted user *owned*, so another actor's
  lock on, or comment under, a long-deleted user survived at coordinates
  nothing lives at (and nothing can reach) and turned a rename onto that name
  into "destination already occupied" for a user that does not exist. The
  purged attachments are tombstoned like any other delete. Relocated
  attachments also get `updated_at` bumped and their old coordinates
  tombstoned, so an incremental export drops the old key and picks the new one
  up.
- **Force-delete's owner reassignment** (spaces/roles/groups/permissions the
  user owned go to `dmart`) now shares the rename's set-based rewrite and
  bumps `updated_at` on the reassigned rows, so an incremental export sees the
  owner change.

## v1.5.19 — 2026-10-07

### Changed

- **Query totals are bounded by default: `QUERY_TOTAL_CAP` is now `100000`
  (was `0` = unlimited).** Counting is O(matching rows) whatever the indexes
  look like, so an exact `total` was a full scan of the result set on *every*
  page request — measured at ~2.4 s per request on a 2.59 M-row folder. Totals
  stay exact for every ordinary folder; above the cap the response reports
  `total` as the cap and `total_is_lower_bound: true`, which clients paging by
  total must honour (the shipped UIs do not read the flag yet). `0` restores
  the unlimited behaviour.
- **Per-request auth lookups are cached for 5 s (`AUTH_CACHE_TTL`, was `0`).**
  Every authenticated call paid a full `users` row read plus a `sessions` probe
  before reaching its handler. The node that revokes a session or deactivates a
  user still evicts immediately; other replicas may honour the old state for at
  most 5 s. `0` restores the always-hit-the-database behaviour.
- **The MCP surface is now off by default (`ENABLE_MCP`, default `false`).**
  With it unset, none of the Model Context Protocol routes are mapped — the
  `/mcp` endpoints, the OAuth 2.1 authorization server (`/oauth/authorize`,
  `/oauth/token`, `/oauth/register`) and its `/.well-known/oauth-*` discovery
  documents are absent (an unmapped route, i.e. `INVALID_ROUTE`/422), not
  merely gated — and the OAuth store sweeper and the `mcp_sse_bridge` hook
  plugin stand down, so nothing ticks or fans out for a surface nobody can
  reach. The gate logs one line at
  startup in either state, so an upgrade that flips the default leaves a
  breadcrumb next to the first `Route not found: POST /mcp`. MCP tools always
  run the caller's own permission walk, so
  exposing them was never a privilege escalation, but a deployment that does
  not use MCP has no reason to carry the extra internet-reachable surface —
  including an anonymous (if bounded) Dynamic Client Registration endpoint.
  **This is a behaviour change:** deployments that use MCP clients must set
  `ENABLE_MCP=true`. Prefer also restricting those routes to the networks that
  actually need them.

### Security

- **`apply-alteration` can no longer enumerate other spaces.** The saved
  `target_query` ran through the repository's actor-less (server-unrestricted)
  overload — no row ACL, no `MaxQueryLimit` clamp, not even pinned to the
  route's space — and the handler echoed the `shortname`/`subpath` of every
  row the caller was then *denied* on. Anyone who could create an alteration
  anywhere could enumerate, and probe payload values of, every entry in every
  space. The alteration is now loaded read-gated, the query is pinned to the
  alteration's own space and executed through `QueryService` under the caller's
  ACL and limit clamp, so `matched`/`failed` can only name rows the caller
  could already read.
- **Operator routes are global-admin only.** `/managed/health/{type}/{space}`
  (returned broken-entry shortnames for any space), `/managed/reload-security-data`
  (flushed the permission + schema caches — a cache-stampede lever),
  `/send-message/{user}`, `/broadcast-to-channels` and `/ws-info` (forge
  realtime events, push into another user's socket, list who is connected)
  carried only `RequireAuthorization()`. All now sit behind `GlobalAdminFilter`.
- **Anonymous multipart uploads obey the submit allow-list.**
  `/public/resource_with_payload` took `space_name` from the form with no
  allow-list, and `/public/attach/{space}`'s `request_record` back-compat branch
  returned *before* the allow-list check. Both now enforce `AllowedSubmitModels`,
  pin the form space to the route space, force the server-minted shortname and
  strip caller-supplied `acl`/`owner_group_shortname`/`relationships` — the
  policy `/public/submit` already applied.
- **Identifier validation on every create path.** The `RequestRegex` gate lived
  only in `/managed/request`; the multipart (managed and public) and
  `/public/submit` paths persisted unvalidated `space_name`/`shortname`/`subpath`.
  A `/` in a shortname made an attachment's authorizing parent resolve to a
  different row than the create gate checked. The same gate now runs there too.
- **Social-SSO web login is bound to a `state` nonce.** The Google and Facebook
  GET callbacks exchanged any `code` they were handed, so a victim navigated to
  the callback with an attacker's code was silently logged into the attacker's
  account (login-CSRF). New `GET /user/{google,facebook}/login` endpoints mint
  the nonce into a short-lived cookie and redirect to the provider; the
  callbacks refuse a missing or mismatched `state` before contacting it.
  **Web clients must start the flow at the new endpoint.** The mobile
  `id_token`/`access_token` paths and Apple's `form_post` callback are unchanged.
- **dompurify 3.4.13 → 3.4.16** in both served UIs (two low-severity sanitizer
  advisories). The other open dependency alerts are all build/lint tooling
  (`brace-expansion`, `braces`, `vitest`, `postcss-selector-parser`) that never
  ships to a browser.
- **`/public/query` honours row-level ACL on attachment and history metadata.**
  An anonymous `type=attachments` or `type=history` query skipped the per-row
  ACL entirely — the `attachments`/`histories` branch of `QueryHelper
  .AppendAclFilter` returned early (Python parity), and the `actor is null`
  guard in `QueryService.QueryHistoryAsync` never fired for the public route,
  whose actor is the literal `"anonymous"`. At the tree root (`subpath=/`),
  where the permission walk falls back to space-level access, that exposed the
  filenames, checksums, content types, owner references and history diff fields
  of *every* entry in a space with any world-readable content — including
  entries the caller could never read. Binary payloads were unaffected
  (`PayloadHandler` runs its own `CanReadAsync`), so the leak was metadata only.

  Both tables now authorize each row against the record it belongs to, reusing
  the same owner/acl/query_policies predicate the `entries` plane uses: an
  attachment against its parent entry, a history row against whichever Metas
  table (entries, users, roles, permissions, groups, spaces) holds its record,
  or — for attachment history — the parent entry, guarded by the attachment
  existing. The parent is resolved with the cross-engine folder-split idiom
  already proven in `HealthCheckRepository`, so `entries` index lookups stay
  index-friendly. A null actor keeps the internal unrestricted path
  (UniquenessValidator, export). Verified on PostgreSQL and SQLite: an
  is_active=false entry's attachment and history no longer appear for an
  anonymous caller, while a world-readable sibling's still do.

- **`/public/excute` no longer discriminates saved-query existence or shape.**
  The anonymous task-runner returned a distinct error for "task absent" (404),
  "present but not a Query" (400 invalid Query) and "unknown task type",
  echoing framework type names — an existence/shape oracle. Every resolution
  failure on the public route now returns one uniform `task not found`. The
  authenticated `/managed/excute` route keeps its specific diagnostics.

- **`jq_filter` failures no longer echo jq's stderr.** A runtime jq error
  returned the raw stderr — which included the server-side `map(...)` wrapper
  and a jq engine fingerprint — to the (possibly anonymous) caller. It now
  returns a generic `jq_filter failed to evaluate`. The builtin denylist also
  closes the `input` family (`inputs`, `input_filename`, `input_line_number`),
  which slipped past the `\binput\b` word boundary; they are inert under the
  single-array `map()` invocation but are now rejected before a future caller
  can reach them.

### Fixed

- **Aggregation crashed on SQLite whenever it carried a filter.**
  `BuildAggregationSql` built its WHERE clause through the overload that
  hard-codes the PostgreSQL dialect, so `filter_types`, `filter_tags`,
  `search` (or a permission's `filter_fields_values`) emitted `= ANY($n)` /
  `@>` / `ILIKE` into SQLite — a `SqliteException` and an HTTP 500.
- **Wildcard searches over values containing `_` returned nothing on SQLite.**
  The search parser backslash-escapes `_`/`%`, PostgreSQL honours that by
  default, but SQLite's `LIKE` has no escape character unless one is declared.
  `ILIKE`-style sites and the FTS prefilter now declare `ESCAPE '\'`, as the
  policy filter always did.
- **SQLite dropped the whole first day of a timestamp bound written with an
  ISO `T` separator** (`@created_at:>=2024-03-01T00:00:00`), because it compared
  the text against the stored space-separated format. The separator is folded.
- **NULL placement under `sort_by` was the opposite on each engine** — PostgreSQL
  treats NULL as largest, SQLite as smallest, so a sort over any nullable
  column or JSON path paged differently per backend. Every emitted sort key now
  spells `NULLS FIRST/LAST` (PostgreSQL's defaults, so existing PostgreSQL
  deployments are unchanged). Numeric-looking text (`"10"`) also sorts
  numerically on SQLite as it already did on PostgreSQL.
- **Moving an entry now carries its history.** `MoveOnceAsync` re-keyed
  entries, attachments and locks but never `histories`, orphaning every
  pre-move row; once history became parent-ACL filtered those orphans were
  invisible to everyone, so a move silently truncated the audit trail.
- **An MCP SSE reconnect no longer gets a dead channel.** `GET /mcp` completed
  the session's outbox on every exit, so one dropped stream (proxy idle timeout)
  killed pushes for that session until a fresh `initialize`. The channel now
  lives as long as the session.
- **`import --resume` could silently skip entries after re-partitioning.**
  Sub-shard "done" markers were matched by bare key, so a run with a different
  `--fast-parallelism`, `--spaces` or source file set reused a `space#i` marker
  computed over a different entry set. Sub-shard markers now carry the shard's
  fingerprint and are trusted only when it matches; whole-space markers are
  unaffected.
- **Authorization cache entries expire (`AUTHZ_CACHE_TTL`, default 60 s).**
  Invalidation was process-local with no TTL, so a role removed on one replica
  kept granting on the others until they restarted. A user write now evicts
  only that user's bundle instead of clearing every actor.
- History paging has a `uuid` tie-breaker (same-millisecond rows could land on
  two pages or none); the inner-join pushdown fast path honours
  `QueryTotalCap` and sets `total_is_lower_bound` like the main path.

### Performance

- **A composite index serves the default listing.** `WHERE space_name AND
  subpath ORDER BY updated_at DESC LIMIT n` sorted the whole folder on every
  page (SQLite had no composite index at all). `idx_entries_space_subpath_updated`
  on `(space_name, subpath, updated_at DESC)` turns a page into an index range
  scan; built `CONCURRENTLY` on PostgreSQL.
- **`events.jsonl` is rolled over and read bounded.** The per-space log grew
  without bound and `type=events` materialised a `Record` for every line per
  request. The writer now rolls at `EVENTS_LOG_MAX_BYTES` (default 50 MB, one
  previous generation kept) and the reader retains only what the page can
  reach while still counting an exact `total`.
- **Large JSON responses stream instead of being buffered.** The empty-key
  strip middleware buffered every JSON body in full (2–3× its size on the LOH)
  even when it was already past the 1 MB strip limit; it now hands the held
  bytes through and switches to passthrough the moment a body crosses it.
- SQLite's per-connection page cache drops from 64 MiB to 8 MiB (a request
  holds two connections from an uncapped pool; the shared 256 MiB mmap is
  untouched).

## v1.5.18 — 2026-10-06

### Added

- **An MCP persona, scoped by region rather than by subpath.** `ai_ops_south` is
  an ordinary dmart user holding a read-only `datamart_ai_ops_south` role — MCP
  has no authorization surface of its own, so an AI client authenticates as a
  dmart user and every tool runs the same permission walk as any other caller.

  Region is a payload *field*, not a subpath, so the subpath grant cannot
  express "south only". `filter_fields_values` can, and the permission carries
  `@payload.body.region:south`. **It only works with explicit subpaths**: the
  filter is applied by matching the permission's `space:subpath:resource_type`
  key as a prefix of the request's query policy, so `__all_subpaths__` never
  matches and the filter is dropped *silently* — measured as 8 rows across all
  three regions instead of the 2 southern ones. The permission lists its
  subpaths for that reason.

  Verified: 2 rows, both south; a write refused with `no create access`; and
  the refusal audited in the request log against `user_shortname:
  ai_ops_south`.

- **A CI gate for the packs.** A hosted job runs the update planner's 30
  assertions, checks the committed tree is what the generators produce, and
  runs `build.sh`'s own scope refusal over all eight packs. No build, no
  database, standard library only.

- **Two pack plugins, running as scoped service accounts.**
  `shanidar_case_assign` writes a new case's assignee from a routing table;
  `shanidar_kpi_rollup` recomputes a region-month summary whenever a KPI row
  changes. `install.sh --plugins` deploys them — off by default, since it writes
  an executable into `~/.dmart/plugins` and creates an account that writes
  unattended.

  Each goes through the REST API as its own account rather than using dmart's
  `save_entry` callback, for two reasons in dmart's source: the callback
  bypasses `EntryService` (so no validation, relationship integrity, permission
  check or folder policy) and attributes history to whoever triggered the hook.
  Verified — the assignment appears in history as `servicedesk_svc_assign`, not
  as the person who filed the case.

  The strict scope rule forced a better design. The first version read
  `org/sites` and `management/users`, which a pack may not grant; so the region
  is taken from the case (where the packs already duplicate it), and who covers
  a region became data at `servicedesk/routing/assignment`, generated from the
  personas. Re-routing is now editing an entry rather than an executable.

  Two details recorded in the README: the assignee is a payload field because
  `collaborators` is only settable via `request_type: "assign"`, which also
  transfers ownership and would hide a case from the customer who raised it;
  and the rollup recomputes rather than increments, so repeated or duplicate
  hook firings cannot double-count.

- **A pack can no longer reach outside itself.** `build.sh` refuses to build a
  pack whose permissions claim `__all_spaces__` or another pack's space, whose
  roles hold a permission it does not provide, or whose roles and permissions
  are not `<pack>_` prefixed — the last of which would let a pack ship a role
  called `super_admin` and **redefine dmart's own on import**. All four attacks
  are tested; every shipped pack already passes.

  `__all_subpaths__` within a pack's own space stays allowed, and several packs
  use it: the rule is about which space, not how much of it. The consequence is
  that a pack cannot grant read access to a space it merely `links` to, so
  cross-pack reads need an explicit grant.

- **The packs machinery is no longer telecom-specific.** A dataset is one module
  in `packs/lib/` plus a name: `PACKS_DATASET=school` or `--dataset school`. The
  name seeds the UUIDs so two storylines never collide.
  `packs/lib/example_dataset.py` documents every attribute the generators read
  and ships a one-site storyline that builds, as the starting point for a
  school, restaurant or ecommerce pack. Output for the existing `shanidar`
  dataset is byte-identical, so no UUID churn.

- **Solution packs can now be updated, not just installed.** Each pack carries
  a `version`, and `install.sh` stores a receipt at `management/packs/<name>`
  recording that version and the `updated_at` of every row it landed. The next
  install compares the three sides — what the pack shipped before, what it ships
  now, and what the database holds — and decides per row.

  The rule it enforces: **an update never overwrites or deletes a row the
  operator changed.** A pack change to an untouched row is applied; a pack
  change to a row the operator edited is skipped and named in the output; a row
  the new version dropped is deleted only if untouched. `--dry-run` prints the
  plan and changes nothing, and a downgrade is refused unless `--force`.

  Detection needs no checksums: the importer binds an entry's shipped
  `updated_at` while any write through `EntryService` replaces it with `Now()`,
  so a mismatch against what the pack ships *is* the signal. One carve-out —
  spaces, roles, permissions, groups and users always take `Now()` on write, so
  their timestamps carry nothing and they are always refreshed from the pack. A
  space is never auto-deleted.

  An install that predates receipts has no baseline and still does the safe
  thing: a row already present whose timestamp differs from the shipped one is
  adopted rather than overwritten.

- **A twelve-month history archive in the solution packs.** Six cases now ship
  *with* their `history.jsonl` — dated from 2025-11 to 2026-08 and attributed to
  the agent or supervisor who did the work — plus history on a non-ticket entry,
  the `erb_0142` generator going faulty, since history is not a ticket feature.

  This is what `packs/PLAN.md` §2 promised and could not deliver: the importer
  discarded the authored `uuid` and `timestamp`, so an earlier revision shipped
  no history at all. With that fixed, the archive lands dated and three
  successive installs leave it at 17 rows.

  The archive is deliberately separate from the four cases `demo.sh` drives, so
  the demo shows both kinds of history side by side — authored data, and rows the
  workflow engine wrote live. Section 8 of the walkthrough separates them by
  date.

  Each authored transition is checked against the workflow's own transition
  table at generation time, so the archive cannot drift from the state machine:
  an event naming a transition `servicedesk_case` does not have, or a closing
  transition without a resolution reason, fails the build. `build.sh`'s history
  check inverted accordingly — it no longer refuses history, it refuses history
  that would silently lose its dates.

- **A public surface for the solution packs, opt-in.** Two packs now ship a
  public face and neither opens by default: `install.sh --public` is what grants
  the roles each pack lists under `provides.public_roles` to dmart's
  `anonymous` user.

  `kb_public` opens anonymous read of `kb/articles` — a help centre. Its
  permission deliberately carries **no** `conditions`: `is_active` looks like
  the careful choice but conditions are enforced on `view` and exempt on
  `query`, so it would pass `/public/query` and then fail the `/public/entry`
  read of the same article.

  `servicedesk_public` opens anonymous **create** in a new `servicedesk/intake`
  folder — a contact form, create-only, so a public caller cannot read back even
  the case they just filed. It has its own `intake_case` schema rather than
  reusing `case`, which requires region, severity and a report date that no
  member of the public can supply. Note `/public/submit` is gated by config too:
  it needs `ALLOWED_SUBMIT_MODELS="servicedesk.intake_case"` in `config.env`,
  and `install.sh` reports that rather than editing a file holding secrets.

  The seeded `world` permission is left inert and untouched — each pack owns its
  own permission, the grant unions onto the `anonymous` user's existing `world`
  role, and `reset.sh` revokes it.

  Alongside it, `servicedesk_customer` and two customer personas: a signed-in
  customer creates cases **owned by themselves** and queries only those, which
  is the half anonymous intake cannot give you since `/public/submit` owns the
  entry as `anonymous`.

- **Solution packs — ready-made dmart applications defined as data**, under
  `packs/`. A pack is a space, the folders inside it, and the roles and
  permissions that make it usable; nothing is compiled and core is untouched.
  Eight ship: `org`, `catalogue`, `assets`, `servicedesk`, `approvals`, `kb`,
  `datamart` and the optional `comms`.

  ```bash
  export BACKEND_ENV=/path/to/config.env
  ./packs/install.sh --packs servicedesk        # pulls in `org` too
  ./packs/reset.sh  --packs kb --url ... --yes
  ```

  A directory is a pack if and only if it holds a `pack.json`, so no script
  carries a hardcoded list. `depends` is resolved transitively; `links` is
  narrative only. The space trees and management overlays are generated
  deterministically (UUIDv5 from a fixed namespace, one fixed timestamp), so
  re-running the generators yields a byte-identical tree — the output is
  committed, and a churning diff would hide real change.

  Three shapes are forced by what dmart actually does rather than by taste.
  `install.sh` has a second, API-only phase because **groups do not round-trip**
  through import/export, which is the only reason it needs a URL at all. It
  ends by telling you to restart the server, because the authz cache is an
  in-process dictionary and a CLI import cannot invalidate a running server's
  copy. And `build.sh` refuses to build a pack carrying a `history.jsonl`,
  because re-import *appends* history rather than upserting it. `packs/PLAN.md`
  records each of these with citations and the experiment that established it.

  Installed clean on both drivers: 43 rows, 0 failed; 7 spaces, 20 folders, 8
  roles, 8 permissions, 3 groups. A second install skips all 43. Every folder
  returns a resolved `folder_rendering` payload, so CXB renders all of them.

- **A static-site generator over dmart content** — `catalog/ssg/generate.mjs`.
  Queries dmart and writes one real HTML file per entry with the prose **in the
  markup**, plus `sitemap.xml` and `robots.txt`:

  ```bash
  node ssg/generate.mjs --space website --subpath /pages --out dist/site \
    --base https://dmart.cc [--api URL] [--token-file F]
  ```

  dmart could already hold content but not render a page:
  `PayloadHandler.RendersInline` deliberately refuses `text/html` (user-uploaded
  documents on a cookie-authenticated origin would be stored XSS), and the
  catalog SPA pre-renders a shell only — `sanitize.ts` says it outright, "those
  build-time pages carry no user payload". A crawler that does not execute JS saw
  nothing. This closes that.

  Deliberately a standalone Node tool rather than routify/spank SSR: the catalog
  is client-only with `ssr: false` throughout, and nothing about emitting static
  pages requires making ~1,900 files SSR-clean. It never touches the SPA's render
  mode, so `/cat` as an admin UI cannot regress.

  URLs come from `Entry.Slug` when present, `shortname` otherwise. Shortnames
  cannot contain hyphens (`^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{1,64}$`), so a hyphenated URL
  can only come from a slug — relevant to any site with existing URLs to
  preserve. Mermaid fences emit `<pre class="mermaid">` for client-side
  hydration rather than rendered SVG, which would need a headless browser and
  drags ELK (1.4 MB) behind it. An empty result exits non-zero rather than
  publishing an empty site over a good one. `html` payloads pass through
  unsanitized, which is a trust boundary worth naming: whoever can write an
  entry in the space can put script on the published site.

- **A seeded `website` space** holding dmart.cc's 14 pages as markdown entries,
  converted by `tools/website-migrate/convert.py`. Seed grows 740K → 992K; as
  `seed/**` is an `EmbeddedResource` that is ~0.5% of the binary.

  **Seeding grants no public access.** On a fresh install `/public/query`
  returns 0 for the space while `/managed/query` returns 14, because
  `AdminBootstrap` ships `world` with empty `subpaths` and — per
  `docs/permissions.md` — its scope belongs to the operator once it exists.
  Opening it up is one deliberate API call, documented in
  `docs/catalog-ssg-plan.md`. Nothing serves the generated output yet; wiring it
  to a URL prefix needs a regeneration story first.

- **`resources_from_csv?first_row=N`** — resume a cut-off import by uploading
  the header plus only the rows still to come, saying which file row the first
  of them is. `start_row=N`, which skips to that row in a full re-upload, still
  works; the CLI and both upload dialogs now slice instead, so a file imported
  in _k_ parts is transferred once rather than _k_ times.

### Changed

- **CSV update (`resources_from_csv?is_update=true`) now validates each row
  against the schema picked for the import, and saves that schema on the
  entry**, as Python dmart does. It used to validate against whatever schema
  the entry was created under, and ignore the one picked.

  This is stricter. The merged body is checked as a whole, so an entry that had
  no schema, or that still carries fields the picked schema does not allow, now
  fails its row where it used to update. The failure names the field. An entry
  on another schema is moved to the picked one; a role whose
  `restricted_fields` covers `payload.schema_shortname` is refused on those
  rows. A schema that does not exist now stops the import before any row, in
  both modes.

### Fixed

- **A plugin hook's `schema_shortnames` filter was ignored for every resource
  type but `content`.** A hook narrowed to one ticket schema fired for all of
  them. The gate now applies to every resource type, and an event carrying no
  schema never matches a filter that lists them. An empty list still means
  "every schema", so a hook that declares none is unaffected — which is every
  plugin shipped in this repo.

- **Lock and unlock events did not describe the resource they were about**, which
  the change above turned from untidy into load-bearing. `LockService` built its
  event from the request Locator alone, so:

  - the payload schema was always absent, and a schema-filtered hook therefore
    silently stopped observing `lock` and `unlock` entirely;
  - the resource type on a self-unlock was whatever the route said, and the
    unlock route has no resource-type segment — `LockHandler` hardwires
    `content`. A hook filtering `resource_types` never matched a self-unlock of
    anything else, and the `.dm/events.jsonl` audit line misreported the type.

  Both now come from the entry. The force-unlock path already loaded it for the
  permission gate; that load moved up so the self-release path shares it, which
  is one query on a path that still skips the holder lookup and both permission
  walks. User create/update events carry the user row's schema for the same
  reason. Two cases keep a null schema because nothing else is knowable: a
  delete whose entry cannot be loaded, and oauth pre-create, which fires before
  the row exists. Both now say so in place.

- **CI could not upload anything: the Actions artifact storage quota was
  full.** Every run on every branch failed its `Upload test results` step with
  "Artifact storage quota has been hit", which failed the whole required
  `build-and-test` check even though the build, the test suite and the e2e
  smoke all passed on both drivers. The org is on the Free plan — 500 MB
  included — and the repo was holding **9.7 GB across 1,715 artifacts**, because
  no CI upload set `retention-days` and the default is 90.

  Every upload now sets one. The release artifacts get 1 day, which loses
  nothing: each is either consumed by a later job in the same run, or a
  duplicate of a file already attached to the GitHub Release, and release
  assets are permanent and do not count against this quota. `linux-x64-bin`
  was already on 1 day for exactly that reason — the other fifteen now match
  it. Test results get 7 days; nothing consumes them, they exist to
  post-mortem a red run, and the 90-day default had accumulated ~1,000 of
  them.

  Steady state goes from "grows until it breaks" to roughly one release run's
  output plus a week of test results.

  The upload step is also no longer a gate. It is diagnostic — those artifacts
  are read only when someone post-mortems a red run — so its failure says
  nothing about whether the code is good, and it should never have been able to
  fail the required check. While the quota was full it did exactly that to five
  PRs whose build, full suite and e2e smoke had all passed on both drivers.

- **`--scale large` crashed at generation 100.** The generated-clone suffix
  stripper was `_g\d{2}$`, so at ×250 it could not strip `_g100` and the region
  lookup died with `KeyError('erb_0142_g100')`. Now `_g\d+$`. The scale works:
  13,087 files generated in half a second, 6,620 rows imported in seven.

- **A pack update could not see a content change that left the timestamp
  alone.** The planner inferred "the pack changed this row" from the shipped
  `updated_at`, but the pack generators write a FIXED timestamp so their output
  stays byte-identical between runs — so editing a schema's rules moved no
  timestamp and the update silently skipped it. Found the hard way: an edited
  schema never reached the database, and a plugin then failed validation
  against the old copy.

  The receipt now records a content hash per entry as well as the timestamp,
  because the two answer different questions: the hash says whether the *pack*
  changed a row, the timestamp whether the *operator* did. A receipt written
  before this (format 1) has no hashes, so the first install after upgrading
  refreshes every operator-untouched row once to rebuild a real baseline, and
  says so in the plan.

- **The EL9 RPM would not install on RHEL 9 without EPEL.** Since 1.5.8 it
  declared `Requires: libargon2`, and on RHEL 9 that package exists only in
  EPEL — so `dnf` refused with `nothing provides libargon2`, and on air-gapped
  or Satellite-managed hosts where EPEL is not an option there was no way
  forward. The 1.5.8 note calling it a "Fedora/RHEL" package was wrong for RHEL.

  The EL9 RPM now bundles a private `libargon2.so.1` in `/usr/lib64/dmart/`
  (EPEL's own build, taken from the builder) and drops the dependency. dmart
  still loads the system library first, so a host that has EPEL's `libargon2`
  keeps receiving its updates through dnf; the bundled copy is used only when
  none is installed, and then patching it means upgrading dmart. The Fedora
  RPM, `.deb` and `.apk` are unchanged and still depend on the distro package.

- **The `.deb` did not declare `adduser`, which its `postinst` has always
  needed.** `postinst` calls `addgroup` and `adduser --system`, both from the
  `adduser` package. It is present on any normal Debian install, but not in
  minimal images: on `debian:stable-slim` the package unpacked and then failed
  with `addgroup: not found`, leaving dpkg half-configured and no service user.
  The control file now depends on it, matching the RPM's
  `Requires(pre): shadow-utils`.

- **Restoring a dmart export rewrote every history row's timestamp to the
  restore moment.** `history.jsonl` carries the `uuid` and `timestamp` the
  exporter wrote, and the importer threw both away: it called
  `HistoryRepository.AppendAsync`, the path for *new* events, which binds
  `Guid.NewGuid()` and `TimeUtils.Now()`. So a year of history came back stamped
  the same second and the original dates were simply gone — a silent loss of
  fidelity in backup/restore, not just in `dmart import`.

  `RestoreAsync` already existed for exactly this and the Parquet restore path
  already used it. The zip/fs importer now uses it too whenever the line
  supplies both fields, falling back to `AppendAsync` otherwise so a
  hand-written `history.jsonl` without them keeps working.

  Its `ON CONFLICT (uuid) DO NOTHING` also makes history import **idempotent**,
  which it never was: re-importing an archive used to append a second copy of
  every row. Measured before the fix as 2 → 4 → 6 on successive imports of one
  archive; now it stays at 2. Verified end to end: a space exported and restored
  into a fresh database comes back with all seven history rows identical to the
  tick.

  Two existing tests asserted the old duplicating behaviour and are updated —
  one was using "a row was inserted" as a proxy for "the path parsed", the other
  re-imported the same archive as its control leg and could no longer tell
  `--skip-history` apart from the dedupe.

- **Six docs files described two materialized views that do not exist.**
  `mv_user_roles` and `mv_role_permissions` appear in `GLOSSARY.md`,
  `docs/architecture.md`, `docs/data-model.md`, `docs/debugging.md`,
  `docs/permissions.md` and `docs/testing.md` — and nowhere in the code.
  `SqlSchema.CreateAll` emits tables, columns and indexes only; dmart creates no
  views at all. The authz flattening they describe is real but happens in C# in
  `PermissionService.ResolvePermissionsAsync`, cached per user shortname in the
  process-local `AuthzCacheRefresher`.

  Worst of it, `docs/debugging.md` handed operators a `REFRESH MATERIALIZED
  VIEW mv_user_roles;` to run after a manual permission edit — which errors on
  a missing relation and sends someone debugging permissions off after a
  non-existent problem. It now points at `GET /managed/reload-security-data`,
  which actually clears the cache. A stale comment in `Program.cs` claiming
  `migrate` creates the views is corrected too.

- **A space whose folder was named after one of its schemas imported
  unvalidated.** The import validator resolved a schema's externalized body by
  walking three levels up from `{space}/schema/.dm/{sn}/`, which lands in the
  space root rather than in `schema/`; it worked only because an explicit
  `{space}/schema/` candidate was tried when the first path did not exist. So a
  space holding both `schema/{x}.json` and `{x}.json` compiled the wrong file —
  and a folder's own `folder_rendering` body *is* such an `{x}.json`.

  A `cases` folder of `case` entries, an `equipment` folder of `equipment`
  entries: the natural shape. In that shape the compile threw on the folder
  body, the warning went to the log, `null` was cached for the schema, and
  every row imported **unvalidated** — the lenient "schema not found" path. The
  candidates are now ordered most-specific first, so the schema folder wins.

- **A search selector on any non-payload JSON column failed every query on
  SQLite.** `@relationships.attributes.relation:installed_at`, `@acl.foo:x` —
  anything dotted that is not `payload.*` — reached a helper that spelled
  PostgreSQL's `col::jsonb->>'key'` regardless of the dialect it was handed.
  SQLite tokenizes `::` as a named parameter, so the query died with
  `SQLite Error 1: 'unrecognized token: ":"'` and the caller got a 430 db
  error. The same selector worked on PostgreSQL, so the two drivers answered
  the same query differently rather than sharing a limitation.

  The helper now delegates to `ISqlDialect.JsonText`, whose PostgreSQL
  implementation emits byte-for-byte what the old code built — the PostgreSQL
  side is unchanged by construction.

  Two related limits are *not* fixed and are worth knowing: `relationships` is
  a JSON array, so an object-path selector against it parses and runs but can
  never match; and the `[]` iteration syntax is only implemented under
  `payload.`, so `@relationships[].related_to.shortname:x` is dropped from the
  WHERE clause silently and the query returns **unfiltered** rows with no
  error. Filter on a value copied into `payload.body` until relationship
  search exists.

- **`dmart import --skip-history` was silently ignored for zip archives.** The
  flag was parsed, printed in the usage line, and threaded into Pass 5 for
  `--type=fs` — but the zip branch called `ImportZipAsync`, which never took
  it. So re-importing an export appended its `history.jsonl` rows again on
  every run, which is the precise case the flag exists to prevent: a space
  re-imported three times carried three copies of its history. The two sibling
  flags the zip path cannot honour (`--drop-indexes`, `--space`/`--subpath`
  remap) refuse with a reason; this one quietly did the opposite of what was
  asked. Entries themselves were never affected — they are keyed and skipped.

- **The markdown editor could not insert media attachments.** The HTML editor
  has had a 📎 picker for a long time; markdown authors had to hand-type the
  `/managed/payload/{rt}/{space}/{subpath}/{parent}/{shortname}.{ext}` URL from
  memory. Same picker, same URL builder, markdown output.

  Images become `![label](url)` and everything else `[label](url)` — a PDF
  emitted as an image renders as a broken-image icon rather than a download.
  Labels have their brackets escaped, since a shortname containing `]` would
  terminate the link text early. And `ext` is normalised from `""` to `null`:
  tsdmart appends `` `.${ext}` `` unless `ext` is literally null, so an
  extension-less attachment produced a URL ending in a bare dot — invisible in
  the HTML editor's DOM node, visible as text in markdown.

- **A CSV upload from the cxb dialog carried no `Authorization` header.** cxb's
  axios instance has no request interceptor, so a deployment whose backend is a
  different origin fell back to the `auth_token` cookie — refused on a
  cross-site request — and every upload answered 401.
- **Both upload dialogs aborted before the server's 504 could arrive.** The
  apps' axios instances default to a 30-second timeout and `REQUEST_TIMEOUT`
  defaults to 35, so the answer carrying `resume_row` never reached the browser
  and "Continue from row N" could not appear. A CSV upload now waits out the
  server's own limit.
- A `resume_row` that does not advance past the row the part started at is no
  longer offered as "Continue from row N" — it re-imported the file from the
  top, duplicating every auto-shortname row.
- A row still being written when the time limit stopped the import now reports
  the import's progress and resume point instead of a bare timeout, and names
  that one row as needing a check.
- An import that crosses the 100,000-row cap after committing rows now reports
  those rows instead of a failure that says nothing was done.
- The per-row failure list in a timeout's `error.info` is capped at 1,000
  entries (`failed_count` still carries the true total, and `failed_truncated`
  marks the list as partial). A CSV whose every row fails produced a
  multi-megabyte error body on a request already over its time budget.
- The access log no longer records `504` for a timed-out request whose response
  had already begun: those connections are cut, not answered, and the line now
  carries `response_aborted` alongside `timed_out`.
- A request that reached the deadline and whose client disconnected immediately
  afterwards is classified as a timeout rather than a disconnect.
- The cxb upload dialog no longer reopens showing the previous upload's
  failures and a live "Continue from row N" for a file the operator has moved
  on from.

### Security

- **axios 1.19.0 → 1.20.0** — 7 CVEs, including CVE-2026-101907 (SSRF via a
  bypassed protocol check) and CVE-2026-101905 (request socket hijacking).
- **undici 7.29.0 → 7.30.0 and 8.10.0 → 8.11.2** — CVE-2026-19534 (DoS via an
  unrequested WebSocket) and CVE-2026-84961 (TLS certificate validation bypass).
- **devalue 5.9.1 → 5.9.4** — CVE-2026-92708 (HIGH, cross-request process
  memory disclosure) plus three advisories.

  All three are lockfile refreshes with no `package.json` change — every range
  already admitted the fixed version. All were newly published CVEs that began
  failing the Security Gate on every branch, not regressions introduced here.

### Removed

- **`catalog/package-lock.json`** — an npm lockfile inside a yarn workspace that
  was an input to nothing. `build-ui.sh` installs only at the workspace root, no
  workflow referenced it, `cxb` has no equivalent, and it could not be
  regenerated correctly anyway: resolving catalog standalone fails `ERESOLVE`
  because `@roxi/routify` peer-requires `@sveltejs/vite-plugin-svelte ^2–^6`
  while catalog is on `^7`. It was also being scanned alongside `yarn.lock`,
  double-counting every dependency finding.

### Documentation

- **Five of the eleven `actions` a plugin filter can name never fire.** Only
  `create`, `update`, `delete`, `move`, `lock` and `unlock` are ever used to
  build an event; `query`, `view`, `attach`, `assign` and `progress_ticket`
  construct one nowhere in the host. A filter naming them loads without
  complaint, registers, and then stays silent — indistinguishable from a
  condition that never matched. `README.md` and `docs/plugins-and-mcp.md` now
  say which six are real and how that was established. The behaviour is
  unchanged: making the other five fire means adding dispatch to the query,
  view, attach and assign paths, and on `query` that is a hook on every read.

## v1.5.17 — 2026-09-26

Re-release of v1.5.16, whose Windows build and signed checksum manifest did
not publish. **Same code, plus a fix to the release pipeline.**

### Fixed

- **v1.5.16 shipped without `dmart-*-win-x64.zip` and without
  `SHA256SUMS-all`.** Everything else published — RPMs, `.deb`, APKs,
  containers, the linux tarballs and their signatures — so Linux and macOS
  users were unaffected. Windows users, and anyone verifying against the signed
  manifest of all artifacts, were not. Use v1.5.17.

  v1.5.16 moved cosign's install directory to `runner.temp` so that ten
  concurrent signing jobs on three self-hosted runners would stop racing over
  one shared `$HOME/.cosign`. That worked — the collision is gone. But
  `runner.temp` on a Windows runner is a Windows path, and the installer's bash
  script was handed a target it cannot create:

      install-dir: D:\a\_temp/cosign
      mkdir -p D:\a\_temp/cosign

  cosign never reached `PATH`, the signing step exited 127, and
  `SHA256SUMS-all` was skipped because it depends on every build.

  The override was never needed on GitHub-hosted runners: each job there gets a
  fresh VM and cannot collide. It now applies only when the runner is
  self-hosted, which is where the shared `$HOME` actually is.

## v1.5.16 — 2026-09-25

### Fixed

- **Audio and video attachments now stream instead of downloading in full
  before they can play.** Playback starts on the file header rather than the
  last byte, and seeking works from the outset.

  The payload endpoint was already correct — it advertises
  `accept-ranges: bytes` and answers a ranged GET with `206` plus a
  `content-range`, on both the public and managed paths. `Media.svelte` threw
  that away: it `fetch`ed the whole attachment into a blob and handed the
  element a `blob:` URL, so the browser's media stack never issued a ranged
  request. An eight-minute recording had to arrive completely before the play
  button did anything, and seeking ahead was impossible until then.

  Audio, video and PDF now receive the URL directly. Measured on four entries
  in a live archive, authenticated, alternating which strategy ran first so
  cache warming favoured neither:

  | file | before | after |
  |---|---|---|
  | 6.2 MB | 1049 ms | 97 ms |
  | 9.9 MB | 1712 ms | 113 ms |
  | 12.6 MB | 2018 ms | 92 ms |
  | 12.9 MB | 1919 ms | 95 ms |

  The direct timing is flat in file size because only the header is fetched;
  the old timing scaled with the file, and scales again on a slower link. PDFs
  gain the same way — the viewer can page-stream rather than wait on a complete
  download.

  A media element cannot carry an `Authorization` header, which is why the blob
  path existed. It turned out not to be needed: dmart accepts the `auth_token`
  cookie and `CookieAuthAllowed` admits `Sec-Fetch-Site: same-origin`, which is
  exactly what a same-origin `<audio src>` sends. Verified against
  `managed/payload` with the cookie alone — `206`, `bytes 0-99/12965325`. The
  URL keeps using `getCurrentScope()`, so anonymous visitors stream from
  `public/payload` and signed-in users from `managed/payload`.

  Images are unchanged. There is nothing to stream, and the existing path keeps
  the `Authorization`-header route working for a deployment that has cookie
  auth disabled via `CsrfProtectCookieAuth`. No CSP change is needed:
  `media-src 'self'` and `frame-src 'self'` already cover a same-origin URL.

- **Release signing no longer races itself.** `sign-attest` installed cosign to
  `$HOME/.cosign`, and the self-hosted runners share one account and therefore
  one `$HOME`. `release.yml` calls that action from ten concurrent jobs, which
  raced to write and execute the same binary:

      /opt/actions-runners/.cosign/cosign: Text file busy

  It broke the v1.5.15 release twice — once across the two release workflows,
  once between two packaging jobs — and cost three reruns before completing by
  luck rather than by fix. Each job now installs into its own `runner.temp`.

### Fixed

- **A change to a composite action did not run the jobs that use it.** Both
  packaging-scope gates in `ci.yml` listed `.github/workflows/ci.yml` but not
  `.github/actions/`, so editing `podman-preflight` or `sign-attest` changed
  what a job does while matching none of the paths that trigger it. The podman
  preflight had in fact been edited without the container job ever running
  against it, and its description had gone stale against a runner home that
  moved on 2026-09-24. Both gates now include `.github/actions/`.

### Documentation

- **How to install from the dnf, deb and apk repositories** at
  `packages.imx.sh`, which carries the last five releases in all three formats,
  signed. Upgrades then arrive through the package manager, and the index is
  signed, so a tampered mirror fails loudly rather than installing quietly.
  Every command in the README was run against a clean container before being
  written down.

## v1.5.15 — 2026-09-25

Re-release of v1.5.14. **Same code, plus a fix to the release pipeline itself.**

### Fixed

- **v1.5.14 published no artifacts.** Its release page carries no binaries, no
  RPMs, no `.deb`, no APKs, no container images and no checksums. Use v1.5.15
  instead — the contents are identical.

  The pipeline failed at the first signing step. `cosign-installer` v4.1.2
  hardcodes a v3.0.6 bootstrap binary and uses it to verify the cosign release
  it installs; against the pinned v2.6.1 that verification now fails:

      ERROR: Unable to validate cosign version: 'v2.6.1'

  Ten of the twelve build jobs depend on the signed UI tarballs, so a single
  failure stopped all of them. Nothing in this repository changed — v1.5.13
  released cleanly with identical configuration, and the installer is pinned by
  commit SHA. The bootstrap it runs is baked into the action, and upstream moved.

  The installer is pinned back to v3.10.1, whose bootstrap is v2.6.1 — the same
  binary it installs — so the two agree. The v2 pin itself is unchanged and
  deliberate: cosign v3 refuses to emit the detached `.sig` + `.pem` pair this
  release chain publishes.

  **The NuGet packages for 1.5.14 did publish**, from commit `2049329`, before
  the failure. They are valid and identical in content to 1.5.15's. Nothing was
  withdrawn.

## v1.5.14 — 2026-09-25

### Added

- **The `anonymous` user, `world` role and `world` permission are now
  provisioned at startup**, so granting public read access is a one-field edit
  instead of a three-row archaeology exercise.

  Public access was already supported but required assembling three rows by
  hand, and all three are load-bearing:
  `ResolvePermissionsAsync` folds the `world` permission into an anonymous
  caller's set only when an `anonymous` user exists *and* resolves through at
  least one real role row (`if (isAnonymous && roles.Count > 0)`). Miss any one
  and public access resolves to nothing — silently, as `{total:0}` rather than
  an error, which is close to undiagnosable from outside the database.

  The triple ships **inert**: `world` has an empty `subpaths`, so it matches no
  space and grants nothing on a fresh deployment. An operator opens up exactly
  what they intend by scoping it, e.g. `{"archive": ["__all_subpaths__"]}`.
  Seeded actions are `view`/`query` with `conditions: ["is_active"]`, and the
  seeded `resource_types` deliberately exclude `user`, `group`, `role`,
  `permission`, `acl`, `log` and `history` — so scoping a space public cannot
  also publish the user list or the permission model.

  Create-if-missing only, the same contract as `logged_in`: once the rows
  exist, their scope belongs to the operator and bootstrap never repairs,
  widens or resets them. A restart that silently reset `subpaths` would revoke
  public access; one that widened them would publish more than was asked for.

  The `anonymous` row carries no password, email or msisdn and is
  `is_active: false`, which closes both login paths — `LoginAsync` bails on an
  empty stored hash, and the OTP path derives its destination from
  `user.Msisdn` and bails when empty. None of that affects public reads, since
  resolution skips the `IsUsable` check for the anonymous bucket specifically.

- **`dmart migrate` now also creates the two base schemas** — `meta_schema`
  and `folder_rendering` — from the canonical files, so a migrated database can
  validate its own content without a separate `seed` or a first `serve`.

  Create-if-missing, and it probes **both** spellings of the subpath. The rest
  of the codebase treats `management/schema` and `management/schemas` as
  equivalent, so checking only one would insert a second copy on a deployment
  using the other — and since lookup probes `/schema` first, that new copy
  would silently win over the operator's existing one.

  It deliberately does **not** bootstrap authorization data. An earlier
  revision created a `world` role, a `world` permission and an anonymous user
  carrying it; that would have attached a role to the anonymous user, which is
  the switch `ResolvePermissionsAsync` treats as the operator's opt-in
  (`if (isAnonymous && roles.Count > 0)`) — a schema-migration command
  silently changing authorization posture. Those rows belong to startup
  provisioning, which is where they now live.

  The schema rows are owned by `dmart`, and `migrate` runs the same
  create-if-missing admin bootstrap `serve` and `seed` use to guarantee that
  owner exists, rather than inventing a stand-in. `owner_shortname` is a
  foreign key to `users`, so without it the insert simply fails.

### Changed

- **`cxb` now requires `@edraj/tsdmart ^5.7.0`**, matching catalog. The two
  workspaces share one hoisted install, so the split pin left the tree carrying
  two copies of the client — 5.5.0 for cxb, 5.7.0 for catalog — and cxb kept
  running the build whose `getAttachmentUrl` defaults to `DmartScope.managed`.

  No behaviour change for cxb: it is the authenticated admin UI, so 5.7.0's
  scope auto-detection resolves to `managed` exactly as the old hardcoded
  default did, and its one explicit `DmartScope.managed` call site in
  `ListView.svelte` is unaffected either way. The lockfile now resolves a single
  `@edraj/tsdmart@^5.7.0` entry instead of two.

- **The seed no longer ships the `world` permission, the `world` role or the
  `anonymous` user.** Startup provisioning owns that triple now, and two
  components writing the same three rows meant the loser was silent.

  Bootstrap always won, because `seed` and `import` skip existing rows without
  `--force`/`-r` and the bootstrap runs first — on every `serve`, and now from
  `seed` itself. So the seed's scoped `world` (public view/query on `test` and
  three `applications` subpaths) had quietly stopped being reachable: a fresh
  install got the inert bootstrap row, and nothing said the seed's version had
  been skipped.

  `seed --force` did apply it, and that was the sharper problem. Forcing wrote
  the seed's `msisdn` onto the `anonymous` row — the field startup
  provisioning deliberately leaves empty, because `LoginWithOtpAsync` derives
  its destination from it. (The password hash was not carried, so the password
  path was never opened.) Removing the row removes that edge entirely.

  `access_applications_world` and `view_world` are untouched: different
  permissions, not created by bootstrap. Operators who scoped `world` by
  editing the seed should scope the provisioned row instead — its `subpaths`
  belong to them and bootstrap never resets them.

### Fixed

- **Attachments would not render for anyone — including a logged-in
  `super_admin` — because of the Content-Security-Policy.** The failure looked
  like an authorization problem and was not one.

  `Media.svelte` does not point an `<img>`/`<audio>` at the payload endpoint. It
  *fetches* the bytes, so it can attach the bearer token that a subresource load
  cannot carry, wraps them with `URL.createObjectURL`, and renders the `blob:`
  URL. The policy allowed `img-src 'self' data:` and declared no `media-src` at
  all, so `media-src` fell back to `default-src 'self'`. Neither admits `blob:`.

  It survived because it fails silently: the fetch returns 200, the blob is
  built correctly, and only the paint is refused. Images degrade to their alt
  text and `<audio>` reports `MEDIA_ELEMENT_ERROR: Media load rejected by URL
  safety check` with a 0:00 duration — while the network tab shows two clean
  200s carrying the full payload.

- **Anonymous visitors on public catalog pages got 401s for every attachment.**
  `getAttachmentUrl()` defaulted to the managed scope, which requires a bearer
  token, so a signed-out reader received a correctly rendered entry with broken
  images and audio:

      GET  /dmart/public/entry/content/archive/content/kud_rah_hp   200
      POST /dmart/public/query                                      200
      GET  /dmart/managed/payload/media/.../3ff3a488.jpg            401

  `Attachments.svelte` calls `getAttachmentUrl()` with no scope and there is no
  natural place to pass one, so the fix belongs in the library default:
  `@edraj/tsdmart` 5.7.0 derives the scope from whether a token is set, and the
  catalog now requires `^5.7.0`. The inline Media preview reuses
  `getAttachmentApiUrl()` rather than duplicating URL construction.

- **PDF and SVG attachments now render.** They were the two attachment kinds
  `<object>` was used for, and `object-src 'none'` blocked both — so fixing
  images and audio in v1.5.14 left these still broken.

  The policy was not loosened to accommodate them. `<object>` can instantiate
  plugins and execute script embedded in an SVG, which is a materially larger
  surface than displaying a document, so `object-src` stays `'none'` and the two
  branches moved to elements that are both safer and already permitted:

  - **SVG renders through `<img>`**, where browsers disable scripting in the
    image outright. The previous markup already carried this `<img>` as its
    `<object>` fallback. Verified: the SVG displays and a `<script>` inside it
    does not run.
  - **PDF renders in an `<iframe>`**, which `frame-src 'self' blob:` now admits.

  `Media.svelte` additionally pins the blob's own MIME type to
  `application/pdf` on that path rather than trusting the response header. The
  element is chosen from dmart's declared `content_type`, so a file whose stored
  bytes disagree with its metadata could otherwise be framed as `text/html` and
  executed in this origin. Verified: HTML bytes served as `application/pdf` load
  in the frame and do not execute. Image, audio and video keep the response's
  own type, which audio and video need for codec selection.

- **Multi-word tags matched nothing in the catalog tag filter.**
  `getSpaceContentsByTags` built `@tags:<a> OR <b>` with unquoted values. The
  search grammar splits an unquoted value on whitespace, so the tag
  `Hafiz Post` became `@tags:Hafiz` plus a free-text `Post` and matched nothing
  — reported to the reader as "no content found". `OR` is also not an
  alternation operator in that grammar, so combining selections silently
  dropped tags.

  It now uses `buildFieldFilterClause`, already used by the admin listing, which
  emits `@tags:a|b` for simple values and `(@tags:"a b" or @tags:c)` when
  quoting is needed.

## v1.5.13 — 2026-09-19

### Added

- **Startup warning when data would be written to non-persistent storage.**
  dmart now checks, at startup, whether `SPACES_FOLDER` (and the SQLite file,
  when SQLite is the active driver) sits on a RAM-backed filesystem, and logs
  `NON_DURABLE_STORAGE` at Warning if so.

  A directory on a `tmpfs` is writable, correctly permissioned and
  indistinguishable from real storage until the power cycles. The usual cause is
  not a misconfiguration but a mount that did not happen — a container volume
  that failed to attach, an fstab entry that lost a race at boot — which leaves
  the mount point's own empty directory exposed at exactly the configured path.
  Observed in the wild on a diskless board whose root is a tmpfs with the real
  partition mounted over `/var/lib/dmart`: the mount silently failed, dmart
  served happily, health was green, and every write was one power cut from
  being gone.

  It warns rather than refuses, because tmpfs is a legitimate choice for CI,
  test suites and ephemeral demo instances — the cost of the warning is a log
  line, the cost of a wrong refusal is a server that will not start. Linux-only
  (it reads `/proc/mounts`); anywhere else, and whenever `/proc` cannot be read,
  the check reports nothing rather than failing. `overlay` is deliberately not
  treated as RAM-backed: its upper layer is normally on disk, and flagging every
  container would turn the warning into noise.

- **`--color=always|never|auto` and `--no-color` as global CLI flags**, valid
  on every subcommand rather than only on `dmart cli`. `--color=always` keeps
  color through a pager (`dmart --color=always settings | less -R`);
  `--no-color` is a synonym for `--color=never`.

### Documentation

- **bench/REPORT-pi-zero-2w.md** corrects its idle-write figure. It reported
  **zero** idle writes from a 300-second sample; PostgreSQL's
  `checkpoint_timeout` on that board is 15 minutes, so a five-minute window can
  contain no checkpoint and read as zero. Re-measured over a full 15-minute
  window with no request traffic: **1.66 MB/h** idle, against 5.68 MB/h under a
  light synthetic load and 25.97 MB/h under a heavy one. The marginal cost of a
  write-op falls from ~86 KB to ~27 KB as the rate rises, because the
  per-checkpoint overhead is fixed and amortizes. "Wear scales with work, not
  uptime" was too strong; it scales *mostly* with work. The headline conclusion
  is unchanged — 0.61 GB/day at the heaviest load measured leaves a 100 TBW card
  far from being the constraint.
- **README** now carries a verified recipe for scraping `/info/metrics` without
  a human's token, using a **bot user** — the mechanism dmart already has for
  machine clients (no session rows, no `MAX_SESSIONS_PER_USER` slot, exempt from
  failed-attempt lockout), with a 30-day token by default. It also states
  plainly that `"roles":["super_admin"]` is a real grant, not boilerplate, so
  the bot's password is a production secret.
- **docs/debugging.md** gains `NON_DURABLE_STORAGE` — what the warning means,
  the `findmnt --target` / `mount | grep` commands that confirm it, and the
  instruction not to restart dmart before the volume is mounted, since every
  write since startup exists only in RAM.

### Fixed

- **The CLI no longer writes ANSI color into a redirected stdout**, and it
  honours [`NO_COLOR`](https://no-color.org). `dmart version` colorized its
  JSON unconditionally, so

  ```
  $ dmart version | jq -r .version
  (empty)
  ```

  The failure mode is the reason this is a bug and not a nuisance: jq does not
  reject SGR-laced input, it yields an empty string. Nothing exits non-zero,
  nothing appears on stderr, and a script that reads a version and compares it
  silently compares against `""`. The Raspberry Pi soak harness worked around
  it by piping through `sed 's/\x1b\[[0-9;]*m//g'` before every jq call.

  Color is now decided in one place (`Cli/CliColor.cs`) for every entry point:
  an explicit `--color`/`--no-color` wins, then a non-empty `NO_COLOR`, then
  whether the stream is a terminal. Per the spec any non-empty `NO_COLOR`
  value disables color — `NO_COLOR=0` means "set", not "off" — and a
  command-line option takes precedence over it.

  stdout and stderr are resolved independently, so `dmart version > out.json`
  run from a terminal still colors the diagnostics it writes to stderr.

  The same emitter now serves `version`, `settings`, and every `dmart cli`
  JSON path; `selfcheck`'s Spectre report is told about the decision too.
  Previously `dmart cli` checked redirection and the top-level subcommands
  did not, which is how the two drifted apart.

  While consolidating: the printer interpolated string values between two bare
  quote characters, so a value containing `"`, a backslash, or a control
  character produced a document that would not parse even with the escapes
  stripped. Strings and property names are now encoded through
  `Utf8JsonWriter`, and numbers are emitted via `GetRawText()` rather than
  round-tripped through `double`.


## v1.5.12 — 2026-09-19

### Added

- **`GET /info/metrics`** — garbage-collector and process telemetry: gen0/1/2
  collection counts, heap and committed bytes, total allocated, pause-time
  percentage, the heap hard limit, GC mode, plus working set, private bytes,
  CPU seconds, threads and uptime. JSON by default; `?format=prometheus`
  returns exposition format. Admin-only, like the rest of `/info`.

  It exists because RSS observed from outside the process cannot separate the
  three things that grow a managed service — a cache that never evicts, native
  memory the GC never sees (libargon2 mallocs its whole `m` per hash), and the
  GC simply not returning freed segments to the OS. Those have identical RSS
  signatures and completely different remedies, which is why the endpoint
  reports both `process_working_set_bytes` and `gc_heap_bytes`: the gap between
  them is the native allocation.

  It also makes `DOTNET_GCHeapHardLimit` verifiable. That setting is parsed as
  bare hex and a `0x`-prefixed value is silently ignored, so the documented way
  to confirm it had applied was to watch RSS under load;
  `gc_heap_hard_limit_bytes` now answers it directly, and `0` means unset.

- **Per-IP rate limiting on the whole `/public` group** (`PUBLIC_RATE_LIMIT_PER_MINUTE`,
  default 600, `0` disables). `/public` reaches real work without a credential —
  QueryService, attachment payloads, and with a `jq_filter`, a subprocess — and
  carried no limiter at all. v1.5.11's jq concurrency budget bounds how much
  memory concurrent work can hold; this bounds how many requests one address may
  make.

  Deliberately a separate policy from `AUTH_RATE_LIMIT_PER_MINUTE` rather than a
  reuse of it: that one is 10/min because a login attempt should be rare, and
  applying it to the anonymous read path — where one page view is legitimately a
  dozen requests — would throttle ordinary browsing. `/public/submit` and the
  anonymous attach routes keep the auth cap on top of this one, so both apply
  and the stricter binds.

  > **Behind a reverse proxy this depends on `TRUSTED_PROXIES`.** The partition
  > key is the peer address and `X-Forwarded-For` is only honoured from a
  > configured hop, so with `TRUSTED_PROXIES` unset every visitor shares one
  > bucket and this becomes a global cap rather than a per-client one. Set
  > `TRUSTED_PROXIES`, or set this to `0`. The same has always been true of the
  > auth cap; the blast radius is just larger here.

### Documentation

- **Corrected the small-device GC guidance, which was actively misleading.**
  The README recommended `DOTNET_GCHeapHardLimit` without saying what it
  bounds, and told the reader to verify it by watching RSS under load. Both
  were true when written and are not now: **the cap governs the managed heap
  only**, and since 1.5.8 this workload's memory is not there — Argon2 runs in
  libargon2, native and freed per hash, which the GC never sees.
  `PASSWORD_HASH_MEMORY_BUDGET_MB` is the control that bounds it. Anyone who
  set the cap expecting it to constrain hashing should know it does not, and
  that sizing it too low converts a non-problem into an `OutOfMemoryException`.
  `gc_heap_hard_limit_bytes` from the new endpoint now answers "did it apply"
  directly, where `0` means unset — which matters because the value is parsed
  as bare hex and a `0x`-prefixed one is ignored silently.

- **Measured dmart on a Raspberry Pi Zero 2 W** — 4 cores, 416 MB usable, with
  PostgreSQL on the same board: 106 MB PSS idle, ~14 ms warm reads, ~295 ms
  Argon2id logins, 444 rows/s bulk import, and four concurrent logins served
  with a bounded peak that is fully reclaimed. A 10-hour write soak ran 1,200
  cycles with zero errors; resident memory plateaus rather than leaking
  (quarterly deltas +4.29, +0.81, −1.21, −0.21 MB) and the high-water mark
  never moved. Wear is 26 MB/h under continuous writes against **zero at
  idle**. Method, caveats and the x86 comparison in
  `bench/REPORT-pi-zero-2w.md`, with a checked-in harness that asserts before
  it times.

- **`config.env.sample` now documents the bounds v1.5.11 shipped without.**
  `UNIQUENESS_MAX_PROBES`, `JQ_MAX_CONCURRENCY` and `JQ_QUEUE_TIMEOUT_SECONDS`
  were live and enforcing limits with no entry in the sample config explaining
  them. Each entry says what the bound *protects*, not just what it sets — all
  four exist to stop a request amplifying into work, so anyone raising one
  should know what they are re-opening.
- **The README links [dmart.cc](https://dmart.cc).** The repository had no link
  to its own site, and until recently the site's GitHub buttons pointed at the
  archived Python repo rather than here, so the two halves of the project
  referenced each other in neither direction.

### New settings

| setting | default | what it bounds |
| --- | --- | --- |
| `PUBLIC_RATE_LIMIT_PER_MINUTE` | 600 | requests one IP may make to `/public` per minute; 0 disables |

## v1.5.11 — 2026-09-17

Security and robustness fixes from a review of the codebase. Nothing here
changes a wire format or a stored hash; upgrading is a drop-in.

### Fixed

- **A single write could issue millions of database queries.** Folder-level
  uniqueness (`unique_fields`) expands a compound key into the *Cartesian
  product* of its per-path values and runs one search per combination. The
  number of paths comes from the folder's configuration, but the list lengths
  come from the request body — and nothing bounded them. One path over a 50MB
  array of short strings is roughly 5.8 million serial searches; a second path
  multiplies rather than adds.

  The expansion is now counted before any query runs and refused past
  `UNIQUENESS_MAX_PROBES` (default 1000). It refuses rather than truncates on
  purpose: the probes are the only thing that establishes uniqueness, so
  running a prefix of them and accepting the write would admit exactly the
  duplicate the constraint exists to prevent.

- **Uniqueness stopped being enforced when the database was struggling.** Four
  places treated "could not check" as "no violation" — both probe loops caught
  every exception and moved on to the next combination, and both parent-folder
  loads turned a failed load into `folder = null`, which reads downstream as
  "no folder declares unique_fields" and skips the check entirely. All four now
  refuse the write.

  The two defects composed: the unbounded expansion above is a way to *create*
  the load that makes probes fail, so an attacker could degrade the database
  until uniqueness quietly stopped applying. **If you see the new
  "uniqueness could not be verified" failures after upgrading, the database was
  already failing and duplicates were getting through silently before.**

- **A uniqueness path with nested arrays behaved differently on each engine.**
  `outer[].inner[].leaf` (two `[]` segments — one more than the query layer
  translates) matched nothing on PostgreSQL and raised
  `bad JSON path` on SQLite. Both looked like "no collision" only because the
  error was being swallowed. Such a path is now recognised as unsupported
  before a probe is built — skipped identically on both engines, and logged at
  warning, because a declared constraint that cannot be enforced is something
  an operator should be told rather than left to infer.

### Security

- **`jq_filter` could not import modules, but only by accident.** jq's module
  system — `import "name" as $x {search:"/dir"}; $x` — reads any
  `<dir>/<name>.json` the server process can open, which for dmart means entry
  payloads with no ACL on the path. It was not covered by the builtin
  blocklist. It was also not reachable, because both call sites wrap the
  caller's filter as `map(<filter>)` and jq only accepts a directive at the top
  of a program — so the wrapper was holding, not the validator. The validator
  now rejects module directives itself, anchored at the start of the program so
  filters that merely mention the word (`.import`, `test("include")`) still
  work.

  The filter is also passed after a `--` separator now, so one beginning with
  `-` is a filter rather than an option, whatever the installed jq does with
  it.

- **`jq_filter` had no concurrency limit on an unauthenticated endpoint.** Each
  use forks a `jq` and buffers its output in memory (up to 32MB). The path is
  reachable from `POST /public/query` — an empty result set is still a success
  with a non-null `records[]`, so the subprocess starts even for a caller who
  can see nothing — and neither `/public/query` nor `/managed/query` is rate
  limited. Concurrent requests meant that many processes and that much heap, on
  a server that targets 512MB boards.

  `JQ_MAX_CONCURRENCY` (default 4) now bounds the worst case at about 128MB
  regardless of arrival rate; callers past `JQ_QUEUE_TIMEOUT_SECONDS`
  (default 5) get a retryable failure instead of everyone getting slower. A
  rate limit was the obvious alternative and is the wrong tool: it bounds
  arrivals per minute, while the damage here is done by requests overlapping.

### Changed

- **Release artifacts for Windows and macOS are now executed before they
  ship.** The `win-x64` and `osx-arm64` zips were only ever compiled, which is
  how v1.5.8 shipped two artifacts that started and then failed on the first
  login. Each is now extracted and run — boot, hash a password, log in — before
  it is signed. The verifiable-release workflow also accepts a `tag` input, so
  a failed leg can be re-run against an existing tag instead of costing a
  version number.

### New settings

| setting | default | what it bounds |
| --- | --- | --- |
| `UNIQUENESS_MAX_PROBES` | 1000 | searches one uniqueness compound may run for a single write |
| `JQ_MAX_CONCURRENCY` | 4 | `jq` subprocesses alive at once, process-wide |
| `JQ_QUEUE_TIMEOUT_SECONDS` | 5 | how long a request waits for a `jq` slot |

## v1.5.10 — 2026-09-16

### Fixed

- **v1.5.8 and v1.5.9 shipped without their portable Linux tarballs.** Neither
  release carries `dmart-<v>-linux-x64.tar.gz`, `-linux-arm64.tar.gz`,
  `-linux-musl-x64.tar.gz` or `-linux-musl-arm64.tar.gz`, and neither carries
  the signed manifest the verifiable-release workflow produces. Use v1.5.10 if
  you install from a tarball; the RPMs, `.deb`, APKs, container images and
  NuGet packages in those releases are unaffected.

  When v1.5.8 moved Argon2id onto libargon2, the static musl builds gained a
  link-time dependency on `/usr/lib/libargon2.a`. `argon2-static` was added to
  the build image in `ci.yml` but not in `release-verifiable.yml`, so both musl
  legs failed with `clang: error: no such file or directory:
  '/usr/lib/libargon2.a'`.

  The two glibc legs built fine — but the jobs that checksum, sign and publish
  the tarballs run downstream of all four, so one failed leg skipped them and
  took the working artifacts down with it. That is why nothing appeared rather
  than two of four.

## v1.5.9 — 2026-09-16

### Fixed

- **The v1.5.8 Windows and macOS builds could not authenticate anyone.** v1.5.8
  moved Argon2id onto the reference C library and bound it by its **Linux**
  SONAME, `libargon2.so.1`. That name resolves to neither `libargon2.dylib` on
  macOS nor `argon2.dll` on Windows, so `dmart-1.5.8-win-x64.zip` and
  `dmart-1.5.8-osx-arm64.zip` started normally and then threw
  `DllNotFoundException` on the first login. Both artifacts have been withdrawn
  from the v1.5.8 release.

  Linux keeps hashing through libargon2 — that is where the 3x latency and flat
  resident set come from, and where the dependency is one package away. The
  Windows and macOS builds hash with the managed implementation instead, which
  is exactly what every dmart before 1.5.8 did on every platform. Bundling a
  native library for them was the alternative and was rejected: macOS could take
  Homebrew's, but Windows has no standard argon2 package, so it would mean
  adding vcpkg to the release path to produce a DLL for the one artifact class
  nobody deploys.

  The two implementations produce **byte-identical output** at every parameter
  set, so a password set on one platform verifies on any other. That is now
  pinned by a test rather than left as an assumption.

  **No Linux artifact was affected** — RPMs, `.deb`, both APKs, all tarballs,
  the static musl binary, the container images and the NuGet packages are all
  correct in v1.5.8 and unchanged here.

## v1.5.8 — 2026-09-16

### Changed

- **Argon2id now runs through the reference C library (`libargon2`) instead of
  a managed implementation.** Same algorithm, same output — verified
  byte-identical at both the new defaults and the legacy
  `m=102400,t=3,p=8`, so **no stored password is affected** — but the working
  buffer stops being the garbage collector's problem.

  Argon2 needs `m` KiB of scratch for the duration of one call. Managed, that
  is an allocation far past the 85 KB Large Object Heap threshold, so every
  hash handed the GC a 19 MiB (or, for a legacy hash, 100 MiB) object with a
  lifetime of milliseconds. Measured, five sequential hashes on an 8-core
  x86-64 host:

  | | managed | native |
  | --- | --- | --- |
  | `m=19456` | RSS 23 → 81 MB, 2 gen2 GCs | RSS 23 → 24 MB, 0 GCs |
  | `m=102400` | RSS 27 → 355 MB, 3 gen2 GCs | RSS 27 → 27 MB, 0 GCs |
  | latency `m=19456` | 45.8 ms | **14.9 ms** |

  The managed memory was never leaked — a forced collection returned it — but
  "the GC will get to it" is the wrong property under memory pressure, and it
  is why the hashing budget introduced above under-reported real RSS: the
  budget bounds what it admits, while buffers from finished hashes are still
  resident. Native `malloc`/`free` returns the memory when the call ends, so
  the budget and the resident set now agree.

  **Operators:** the packages declare the dependency for you — `libargon2`
  (Fedora/RHEL), `libargon2-1` (Debian/Ubuntu), `argon2-libs` (Alpine) — and
  the portable tarballs ship `libargon2.so.1` beside the binary. The fully
  static musl binary links it in and remains a single file with zero runtime
  dependencies. If you build from source, install your distro's argon2 runtime
  library. libsodium was evaluated and rejected: its `crypto_pwhash` matches
  byte-for-byte at `p=1` but cannot express `p` at all, so it could not verify
  the `p=8` hashes dmart 1.5.x wrote.

- **Every login used to allocate 100 MiB and take ~185 ms. It now allocates
  19 MiB and takes ~40 ms.** This is a general improvement, not a small-device
  one: the Argon2id parameters were hard-coded at `m=102400, t=3, p=8` for
  parity with dmart Python, and `m` is allocated outright on every hash. dmart
  idles at ~25 MB RSS; the first login took it to ~234 MB, on any host.

  Worse, nothing bounded concurrent hashes. `AUTH_RATE_LIMIT_PER_MINUTE` caps
  arrivals per IP, not how many hashes are resident at once, so ten simultaneous
  logins asked for a gigabyte of Argon2 working memory — an unauthenticated
  memory denial of service against a server of any size. Small hardware is
  simply where it turned fatal rather than merely wasteful: on a 512 MB board
  three concurrent logins were enough for the kernel to OOM-kill the process.

  The default is now OWASP's recommended Argon2id configuration — 19456 KiB,
  t=2, p=1 — and all three are configurable via `PASSWORD_HASH_MEMORY_KB`,
  `PASSWORD_HASH_ITERATIONS` and `PASSWORD_HASH_PARALLELISM`. They govern hash
  CREATION only; verification reads m/t/p from the stored string, so **every
  password already in your database keeps working, untouched**.

  **Operators:** no migration and no action required. Existing accounts still
  carry 100 MiB hashes until each one logs in again, at which point the hash is
  re-derived at the configured parameters — once, on a request that had already
  paid for the expensive verify (`PASSWORD_REHASH_ON_LOGIN`, default `true`).
  A rehash is not a password change: it invalidates no session, writes no
  history, and a failure never fails the login. Raising the cost back up on a
  large server is a config line; see `config.env.sample`.

- **Concurrent hashing is bounded by MEMORY, not by request count** — on every
  deployment, not only constrained ones. A new
  `PASSWORD_HASH_MEMORY_BUDGET_MB` (default `0` = auto: half of what the runtime
  reports available, which respects `DOTNET_GCHeapHardLimit` and container
  limits) caps the sum of Argon2 working memory in flight. Hashes past the
  ceiling queue rather than allocate; past `PASSWORD_HASH_QUEUE_TIMEOUT_SECONDS`
  (default 30) the request is answered **HTTP 503 with `Retry-After`** and the
  usual failure envelope, rather than the process dying.

  Counting requests would not have been enough: while legacy hashes are still
  around, one 100 MiB verify and five 19 MiB ones are the same number of
  requests and nearly twice the memory. A hash larger than the whole budget is
  clamped and runs alone, so a legacy hash stays verifiable on any budget.

### Fixed

- **`POST /user/login` returned HTTP 500 when the password field was omitted.**
  The timing-equalization decoy is fed `req.Password ?? string.Empty`, and the
  Argon2 library rejects a zero-length password with `ArgumentException` — so
  an unauthenticated request carrying only a shortname for an unknown user
  produced an unhandled exception. An empty password now does the full
  computation against fixed filler and returns false, which fixes the crash
  without opening the timing oracle that returning early would have.

- **Startup no longer pays for a password hash.** The login decoy was a
  `static readonly` computed during type initialization — 100 MiB allocated on
  every boot, before any request arrived, on a path that many deployments never
  take. It is built lazily now, from the configured parameters, so it also
  tracks the settings instead of drifting from them.

## v1.5.7 — 2026-09-16

### Changed

- **Timestamps now bind as the naive wall clock on both backends, not just on
  paper.** dmart's time model has always been naive local — every column is
  `timestamp without time zone`, `TimeUtils.Now()` is `DateTime.Now`, the
  PostgreSQL session timezone is pinned to the host so SQL `NOW()` reads the
  same clock, and a migration converts legacy `timestamptz` columns away. The
  binding was the one place that escaped it.

  Npgsql infers the PostgreSQL type from `DateTime.Kind`, and `Kind = Utc`
  infers `timestamptz` — which the server then converts into the naive column
  through that pinned session zone. Binding one 12:00 wall clock with the
  session on `Asia/Baghdad` stored `12:00` for `Unspecified`, `12:00` for
  `Local`, and **`15:00` for `Utc`**. SQLite writes the clock components
  verbatim and never did that, so the two backends disagreed about what a
  `Kind = Utc` value meant, by exactly the host's offset, with nothing to say so.

  Every bound `DateTime` is relabelled `Unspecified` at the single seam all
  PostgreSQL parameters pass through. The clock components are kept and only the
  label is dropped. `Local` and `Unspecified` already stored verbatim, so
  **nothing dmart itself writes changes value** — what changes is that a stray
  `Kind = Utc` is now stored as the clock it reads rather than silently shifted.

  **Operators:** no migration, and no rewrite of existing rows — those were
  written through the same conversion and are already local wall clocks. The one
  thing to check is *your own* code writing to a dmart database directly, or
  through `Dmart.SqlAdapter`: a `DateTime.UtcNow` you were passing in was being
  converted for you on PostgreSQL and is not any more. Pass local wall clocks
  (`DateTime.Now`). `Dmart.SqlAdapter`'s own `created_at` / `updated_at`
  defaults were on the wrong side of this and now read the local clock.

  Two guards assert it against a live database, on both backends: one binds all
  three `DateTime.Kind` values and requires a single wall clock back, the other
  refuses any `timestamp with time zone` column in PostgreSQL and any non-`TEXT`
  timestamp column in SQLite — which also catches a deployment whose
  `TIMESTAMPTZ → TIMESTAMP` migration never ran.

- **Implicitly registered accounts get their personal folders.** With
  `ENABLE_OTP_IMPLICIT_REGISTRATION` on, an OTP login for an unknown
  msisdn/email creates the account inline — but that path wrote the user row
  straight through the repository, so nothing fired the create hooks that
  `/user/create` fires. Those accounts came into existence without
  `personal/people/{shortname}` or any of its five sub-folders, and nothing
  created them later.

  The hooks now fire from the service, at the point the row is actually
  written, rather than from whichever handler asked for the login — so the
  explicit and implicit registration paths cannot drift apart again. The audit
  line for a new user also carries its `uuid` now, which was null for exactly
  the accounts these paths create, leaving nothing to join the trail back to
  the users row.

  **Operators:** accounts created by this path *before* upgrading still have no
  personal folders; this release does not backfill them.

### Added

- **CSV bulk import is schema-aware.** A CSV cell is always text, so a column
  the target schema declares `number`, `integer` or `boolean` failed validation
  on every row — the import could not populate a typed schema at all. Columns
  are now coerced to the declared type before validation, including types
  reached through `allOf` / `anyOf` / `oneOf` and local `$ref` into `$defs`,
  which a composed schema needs and which otherwise silently coerced nothing.

  Booleans accept the spellings a spreadsheet actually writes — `TRUE`/`FALSE`
  from Excel and LibreOffice, `yes`/`no`, `1`/`0` — not only the JSON literal.
  Anything else stays a string so the schema rejects it rather than quietly
  becoming `false`. A blank cell in a typed column is omitted rather than
  written as `""`: an empty string is not a number, and re-importing a sparse
  export failed every such row.

  `SchemaValidator` exposes the raw schema document for this, sharing one cache
  and one lookup with the compiled form.

### Fixed

- **Auto-generated CSV shortnames were invalid and failed every later
  operation.** An empty shortname cell produced `row-<8 hex>`, and the shortname
  regex has no `-`. The entry was created, then rejected by validation on every
  subsequent request against it, including its own deletion. Auto-generation now
  matches the `auto` sentinel used everywhere else — the first 8 hex characters
  of a fresh UUID, with that UUID reused as the entry's `uuid` — and goes
  through the same collision retry, which at 32 bits over a 100,000-row import
  is not theoretical.

  `auto` in a shortname cell is honoured on create. On **update** it is not:
  there the shortname is the row's address, and minting one aimed the patch at a
  name that cannot exist and reported `OBJECT_NOT_FOUND` for a string the
  operator never wrote. A blank shortname on an update row is now refused by
  name.

- **A malformed stored schema could take down a whole CSV import.** A boolean
  sub-schema — `{"properties": {"x": true}}`, valid JSON Schema and accepted on
  upload — raised an unhandled exception while the importer walked the schema,
  returning a 500 instead of importing the file.

- **`dmart fix-folder-rendering` stamped `updated_at` in UTC.** On PostgreSQL
  the conversion above hid it; on SQLite it wrote a row stamped hours off on any
  non-UTC host — enough to sort wrongly and to fall outside an incremental
  export's `updated_at >= watermark`, which is the silent row loss that
  mechanism exists to prevent.

- **A failed audit-log write could fail the action it was recording.** The
  per-space `events.jsonl` writer took its lock outside the guard that makes
  every other failure in it non-fatal, so a client disconnecting immediately
  after its write committed turned a succeeded action into an error response.
  The audit sink never decides whether an action succeeded.

### Internal

- The test suite seeded stored timestamps with `DateTime.UtcNow` in 421 places.
  PostgreSQL's conversion repaired them and UTC CI runners could not tell the
  difference, so 14 OTP tests failed only for developers outside UTC, and a
  Parquet watermark assertion failed only *west* of it. All of them now use the
  clock the code compares against.

- CI runs the SQLite tier in `Asia/Kolkata` — no DST, and a `:30` offset that
  also catches a rounded-away whole-hour skew. The PostgreSQL tier stays UTC,
  since it masks this class of bug regardless. A `DateTime.UtcNow` that owes a
  local wall clock now fails in CI instead of only for whoever sits outside UTC.

## v1.5.6 — 2026-09-09

### Security

- **The account lockout is the failed-attempt counter now, and nothing else.**
  It no longer flips `is_active` and no longer wipes every session the user
  holds. `is_active = false` means one thing — an admin deactivated the account.

  What this changes for a locked user:

  - their **already-issued access token keeps working** until it expires. A
    stranger guessing at someone's password no longer signs that person out of
    every device they own;
  - they cannot **refresh**. Both `/oauth/token` grants re-check the lock, so a
    locked session ends at the next refresh and the blast radius is bounded by
    `JWT_ACCESS_EXPIRES`;
  - the one session that *is* revoked is the one that made the attempt which
    tripped the lock, and only when that attempt came through an authenticated
    endpoint (`/user/profile`'s `old_password`, `/user/validate_password`).
    Someone brute-forcing from inside a session they already hold — a hijacked
    one — does not get to keep that token.

  `type = bot` accounts are exempt from the lock entirely. The counter still
  moves, so an attack stays visible in `attempt_count`; it just never trips. A
  bot authenticates with a machine credential nobody is guessing and never
  re-runs `/user/login`, so the cool-down that rescues a human is unreachable
  for it — locking one meant five requests from anyone who knew the shortname
  could take down a whole integration until an admin intervened.

  **Operators upgrading from v1.5.5 or earlier:** the old lockout wrote
  `is_active = false` alongside the counter, and those rows would now read as
  admin deactivations — which the cool-down deliberately refuses to undo,
  leaving every account the previous release auto-locked stuck for good. The
  server repairs them at startup, reactivating exactly the rows carrying the old
  signature and logging a warning with the count. The startup pass finishes
  before the host begins listening, which matters: a locked-out user who retries
  once past the cool-down has their counter cleared, and the row then no longer
  matches the signature the repair looks for. `dmart migrate` runs the same
  repair and `REPAIR_LEGACY_LOCKOUTS_ON_START=false` disables the startup pass —
  but if you go that route, run migrate *before* starting the upgraded server. The one
  case the repair cannot get right is inherent to the data: an account you
  deliberately deactivated *before* upgrading that was also at the threshold
  looks identical to an auto-lock and will be reactivated — audit those.

- **A user update now clears `attempt_count` when it deactivates an account**,
  the mirror of clearing it on reactivation. While an account is deactivated the
  counter means nothing (the active check rejects before any credential check),
  and leaving it set would recreate the exact row shape the old lockout used —
  which is what makes the upgrade repair above unambiguous.

- **`/oauth/token`'s `authorization_code` grant performed no account checks at
  all.** It looked up the user row, confirmed it existed, and issued an access
  token plus a live sessions row. An authorization code stays redeemable for its
  whole TTL, so an account locked, deactivated, or soft-deleted in that window
  still exchanged its code for a working token — while the `refresh_token` grant
  fifty lines below refused. Both grants run the same gate now.

- **The lock check answered "not locked" for deactivated and soft-deleted
  accounts.** When an account was at the attempt threshold *and* its cool-down
  had elapsed, the counter branch returned early and the `is_active` /
  `is_deleted` check below it never ran. Deactivating a compromised account
  therefore did not stop its refresh tokens from minting fresh access tokens,
  and `/user/otp-request` kept issuing login codes to it. Usability is checked
  first now.

- **Presenting a refresh token could clear a brute-force lockout.** The gate
  was a predicate with a write side effect: once the cool-down elapsed it reset
  `attempt_count` and dropped the anchor. An attacker holding a refresh token
  could brute-force a password to the threshold, wait out
  `LOCKOUT_COOLDOWN_SECONDS`, and spend one refresh to wipe the counter — the
  lock itself, and the only surviving record of the run — without ever
  presenting a credential. The gate is a pure read now; the durable unlock
  happens only on a real login attempt.

- **A routine admin edit could silently cancel a lockout.** The unlock gesture
  was keyed on the *presence* of `is_active` in a user update, and the admin UI
  emits that flag on every save — so fixing a typo in a locked user's display
  name cleared `attempt_count` with it, with nothing in the response to say so.
  The gesture is an explicit `attempt_count` attribute now, and a genuine
  `false → true` reactivation still clears the counter. Admin unlocks are
  written to the audit history, naming the actor.

  `attempt_count` is also returned on a user read, so an admin UI can show that
  an account is locked — the lock leaves `is_active` set, so the counter is the
  only thing that says so. cxb and catalog show it on the user form with a
  "clear on save" tick that sends the unlock, and strip the field from ordinary
  saves exactly as they already strip `password`.

- **A non-boolean `is_active` force-activated the account.** `{"is_active":
  "false"}` — a stringified boolean, the commonest client bug — fell through to
  the default arm and read as `true`, reactivating the very account the caller
  was trying to disable. Malformed values are rejected with `INVALID_DATA`
  rather than guessed at.

- **An admin user update rolled back concurrent failed-attempt increments.**
  The handler read the row, then wrote `attempt_count` back from that read —
  handing an in-flight brute-force run its attempts back. `attempt_count` is
  now preserved on write when the caller doesn't mean to change it, the same
  protection the password column already had, and the self-service profile
  update takes it too.

### Fixed

- **Duplicate push notifications when one device signed in more than once.**
  Every login writes a new `sessions` row carrying the body's `firebase_token`,
  so a phone that signed in five times left five rows holding the same token and
  the user got every notification five times.

  A session is per-login; an FCM token identifies a *device*; the two have
  different lifetimes, which is the whole problem. Three changes close it:

  - registering a token clears it from the user's other session rows, so
    duplicates stop accumulating;
  - `sessions` gains a **`device_id`** column, carried from the login body. FCM
    rotates a device's token (app reinstall, data restore, periodic refresh),
    and after a rotation the rows that phone left behind hold a *different*
    string — nothing about the two strings says they are one handset, but the
    device id does. Clearing now matches on either. Clients that send no
    `device_id` (web) and the OAuth grants fall back to token matching, exactly
    as before. The column is added by `dmart migrate` and at startup; no manual
    step;
  - a new **`invalidate_firebase_tokens`** plugin callback (and repository
    method) clears the tokens FCM's send response rejected. A token FCM has
    retired is dead for every account that shares the device, so this is not
    user-scoped. Without it a dead token sat on its row until
    `SESSION_INACTIVITY_TTL` aged the session out, which for a long-lived
    session is never.

  The read still collapses duplicates (`SELECT DISTINCT`) for rows written
  before any of this.

- **A payload URL pasted into a browser downloaded instead of rendering.**
  Images, PDFs, audio, video and plain text now come back with
  `Content-Disposition: inline`; `?download=1` (or `=true`) forces a save for
  callers that want one. `text/html` and `image/svg+xml` deliberately stay
  attachments — served inline they are documents that execute script on the
  API's own origin, and the CSP only attaches to HTML responses, so an SVG
  would carry no policy at all.

  Three things had to change for the inline path to actually work:

  - **`X-Frame-Options` on a payload response is now `SAMEORIGIN`** rather than
    the site-wide `DENY`. XFO blocks `<iframe>`, `<embed>` and `<object>` as surely as
    a cross-origin frame, so `DENY` stopped the in-app PDF viewers from
    rendering anything. Every other response keeps `DENY`; cross-origin framing
    of a payload is still refused.
  - **Audio and video are labelled by extension.** One stored `audio` value
    covers mp3/wav/ogg and one `video` covers mp4/webm/mov, so every clip used
    to be announced as `audio/mpeg` or `video/mp4` — harmless while it
    downloaded, fatal once a browser is asked to decode it.
  - **An upload the server cannot identify is stored as `binary`, not `json`.**
    The old fallback made a `.docx` or `.zip` come back as `application/json`
    with no disposition at all, dumping binary into the tab, and the MCP
    download tool label it the same way. Existing rows are untouched and still
    serve correctly; the new `binary` content type has no Python counterpart.

  Range requests are served from the database a slice at a time instead of
  materialising the whole blob, so seeking within a video no longer re-reads
  (and re-allocates) the entire attachment per request, and a `?download=1` can
  now be resumed.

- **The admin UI pointed at a hardcoded `localhost`.** The generated
  `config.json` pinned `backend` to a literal host, which is only ever right for
  a browser on the same machine as the server — reach the container on a LAN
  address, a different published port, or through a reverse proxy and every API
  call went somewhere the browser could not resolve. An empty `backend` now
  means "same origin as the page", resolved in the browser at use time.

  **Operators:** an existing `config.json` is not rewritten, so a deployment
  that deliberately points the SPA at a separate API host keeps working. New
  installs get the same-origin default. The retired `websocket` field is no
  longer written — the WebSocket URL has been derived from `backend` for some
  time.

## v1.5.5 — 2026-09-08

### Security

- **A permission grant scoped to one `resource_type` could read and overwrite
  rows of another.** Authorization was performed against the resource type the
  *caller declared*, while the lookup that found the row ignored it. Attachments
  are the clearest case: `AttachmentRepository` has no typed lookup at all —
  `(space, subpath, shortname)` is the whole identity — so the `{resource_type}`
  segment of a URL was an unverified claim.

  Concretely, a grant on `resource_types: ["comment"]` was enough to:

  - read a schema, ticket or any other row at a known address via
    `GET /managed/entry/content/...`, by naming it `content`;
  - download the bytes of a media attachment via
    `GET /managed/payload/comment/...`, or the MCP `download` tool;
  - overwrite a media attachment by *creating* a comment at its address — the
    attachment upsert rewrites `resource_type` along with the bytes, so the
    media row was replaced in place with no other trace.

  Every gate now authorizes against the `resource_type` of the row actually
  loaded, never the one supplied by the caller: `EntryService` for
  read/update/delete/move, and the attachment paths in `RequestHandler`,
  `PayloadHandler` and `McpTools`. The two attachment create paths additionally
  refuse an address already occupied by a different type, rather than upserting
  over it — same-type re-create stays idempotent as before.

  The write leg (update, delete, move) was fixed in an earlier release; this
  closes the read leg and the attachment paths. Covered by nine tests across
  every affected route.

  **Operators:** review whether any deployed permission relies on
  `resource_types` as an isolation boundary, and audit access to spaces where a
  narrow grant coexists with sensitive rows at predictable addresses. Requests
  that exploited this returned ordinary 200s and left no distinguishing trace.

- **Attachments uploaded over `/managed/resource_with_payload` were stored under
  an un-normalized subpath.** Every read of that table normalizes, but that
  write path passed the subpath through verbatim — so an attachment created with
  `subpath: "docs/x"` landed at `docs/x` and no later lookup could find it, the
  new cross-type collision guard included.

### Fixed

- **The OTP log line now shows the same spelling of an email that the rate
  limits key on.** The resend cooldown, the daily cap and the `otps` row are all
  keyed on the lowercased address, but the log recorded the raw request
  spelling. `Bob@Example.com` and `bob@example.com` therefore shared one budget
  while appearing as two destinations — so grepping the log to explain a
  `daily-cap` warning undercounted the requests that caused it, which defeats
  the point of logging the destination at all.

- **A shortname of only spaces logged a blank `dest=`.** The guard added
  alongside this checked for an empty string after stripping control
  characters, but spaces are not control characters and survive the strip — so
  `{"purpose":"login","shortname":"   "}` still produced a `dest=` an operator
  could not tell from an absent one. It checks for whitespace now, which covers
  non-breaking and zero-width spaces too.

- **The shortname branch undercounted the same way the email branch did.** The
  log reported what the request supplied, while the cooldown and daily cap key
  on the *resolved* contact — so a user hitting the cap by shortname and by
  email appeared as two destinations spending one budget. The line now reports
  the resolved contact wherever one has been determined, falling back to the
  request only on the branches that fire before resolution.

- **These three fixes are now pinned by tests.** `OtpLogDestinationTests`
  asserts the logged destination against the value the rate limits key on. That
  line had been wrong three times in three different ways without any test
  noticing, because every one of them still answered 200 Ok and still minted or
  withheld the code correctly.

- **An identifier made only of control characters logged a bare `dest=`.**
  `SanitizeDest` checked for empty input before stripping control characters but
  not after, and `shortname` is free-form and validated nowhere — so a shortname
  of two control characters counted as a provided field, stripped to nothing,
  and produced exactly the empty `dest=` the guard exists to prevent. It logs
  `(none)` now.

- **v1.5.4's changelog carried two contradictory copies of the release-build
  entry.** Merging #256 resurrected a superseded version alongside the corrected
  one: it claimed the builder image was a speed improvement (retracted — the
  medians are identical) and that the release workflow shares a NuGet cache,
  which `release-verifiable.yml` explicitly documents that it does not, having
  tried and removed it. The stale copy is gone. The published v1.5.4 release
  notes were written separately and were never wrong.

## v1.5.4 — 2026-09-08

### Changed

- **OTP silent-no-op logs record the destination in clear.**
  `/user/otp-request` answers `Ok` on nine no-op branches and logs one line for
  each. Since v1.4.0 that line carried an 8-character SHA-256 fingerprint of the
  destination; a partially-masked form (`****3344`) was tried and was still not
  enough to work a support ticket from. The full msisdn or email is now written.

  **Operators should understand what this means.** That line is emitted at
  `Information` by default, `/user/otp-request` requires no JWT for `login`,
  `reset` or `register`, and the log file is created `0644`. An anonymous caller
  looping requests therefore writes a list of contacts to disk — their own
  dictionary of numbers as much as any real user's — readable by anyone with
  shell access to the host. Treat these logs as containing personal data:
  restrict read access, and keep retention short.

  If that trade stops being acceptable, the cheaper change than re-masking is to
  keep `{Reason}` and `{Purpose}` at `Information` and drop this one line to
  `Debug`.

  Two protections that are **not** about privacy remain, because the input is
  anonymous and unvalidated. `Shortname` is free-form (`Msisdn` and `Email` are
  regex-checked; it is not) and the default log format is plain text whenever
  `INVOCATION_ID` is unset — dev, docker, any non-systemd host. Control
  characters are stripped, so a newline cannot forge log lines an investigator
  later greps, and the value is capped at 254 characters — above the longest
  valid email address, so no real destination is ever truncated — so a 100 KB
  identifier cannot inflate the log file. An absent or blank identifier logs
  `(none)` rather than an empty `dest=`.

- **The release build is faster where it was measured to be, and unchanged
  where it was not.** Consolidating the Linux packages onto one binary took
  `release.yml` from 24 minutes wall clock to 11 (the Fedora RPM went 5 min → 1,
  the `.deb` 4 → 1). That moved the bottleneck rather than removing it, so this
  release addresses where the time actually went:

  - **The two glibc legs of `release-verifiable.yml` ran
    `dnf install dotnet-sdk-10.0` on every release**, cold, on a hosted runner.
    They now build inside a published `dmart-el9-builder` image, multi-arch and
    pinned by digest, with the SDK and toolchain baked in.

    **This is not a speed improvement.** Over thirteen runs of the same leg —
    eight before the change, five after — the median is `584 s` on both sides.
    The spread within either group (523–651 s before) is far wider than any
    effect the change could have. Two earlier figures quoted for it, "~3
    minutes" and then "~50 seconds", were both single-sample comparisons drawn
    from that spread and neither survived being measured properly.

    The glibc legs do take ~10 minutes against ~6–7 for the musl legs. That gap
    is real and still unexplained; it is not the SDK install.

    The reason this change stays is the other one: it removes an **unpinned**
    `dnf install` from the path that produces signed artifacts. The SDK a
    release was built with used to be whatever the AlmaLinux mirrors served
    that day; it is now a recorded property of a digest-pinned image, which the
    image also reports at `/etc/dmart-builder-sdk-version`.

  - **A NuGet cache was added here and then removed again.** All four legs
    restore from scratch, which looked like obvious waste — but GitHub scopes
    caches by ref, and this workflow only runs on tag pushes and manual
    dispatch. It never runs on the default branch, so it never writes a cache
    another ref can read, and every tag is a new ref with a fresh scope. On a
    dry run it missed even its `restore-keys` prefix while still spending 3–5 s
    a leg saving an entry nothing would restore. A comment now records why
    there is no cache, so the cold restores are not mistaken for an oversight.

  And one that is worth knowing but is not a build cost at all: 424 s of the
  v1.5.3 release was the signing job waiting for the GitHub Release object to
  be created after the tag was pushed. Creating the release promptly removes
  it; no code is involved.

  Two things deliberately not changed. The Windows (9 min) and macOS (7 min)
  AOT builds are different RIDs with nothing to share. And the two workflows
  still compile the Linux targets separately: `release-verifiable.yml` exists
  to establish that an artifact was built by hosted CI from the tagged commit,
  and feeding it a self-hosted binary would forfeit exactly that.

## v1.5.3 — 2026-09-06

### Added

- **The container image is published for arm64 as well as amd64.** `latest` and
  `<version>` are now multi-arch manifest lists; each architecture also gets an
  explicit `<version>-amd64` / `<version>-arm64` tag. Previously the aarch64
  Alpine package was built on every release and then thrown away, because the
  container job consumed only the x86_64 one.

  The arm64 image is built on `ubuntu-24.04-arm` with docker, matching how the
  aarch64 APK is already built — `apk add` has to execute aarch64 binaries, so
  a native runner beats emulation. The manifest job then asserts the published
  index actually carries both architectures: an index listing one arch, or
  listing an arch whose manifest never pushed, otherwise succeeds silently.

### Changed

- **The Linux packages are built from one binary instead of three.** The Fedora
  RPM, the EL9 RPM and the `.deb` each ran their own `linux-x64` AOT compile —
  three builds of the same target, about 12 minutes of a three-runner pool, and
  three chances for the packages to quietly diverge.

  `build-el9-rpm` now publishes the binary it already compiles, and the other
  two consume it via `DMART_PREBUILT_BIN`. No new job: EL9 was already doing
  this compile in an AlmaLinux 9 container. `dmart.spec` needed no change
  either — its `%build` only compiles when `dmart.csproj` is present, which is
  the SRPM-rebuild path, so it was written for this from the start.

  **EL9 is the right one to build on.** AlmaLinux 9 has the oldest glibc of the
  three, so a binary built there is the one most likely to run everywhere the
  other two need to.

  **What this does NOT change is compatibility.** An earlier version of this
  entry claimed sharing the EL9 binary widens what the `.deb` supports. It does
  not: the v1.5.2 and v1.5.3 debs require an identical set of glibc symbols,
  floor `GLIBC_2.34` in both. A binary's floor is the highest symbol version it
  actually references, not the builder's glibc, and the Debian 12 builder was
  already producing 2.34. Every package supports exactly what it did before.

  The real gains are one binary instead of three, so they cannot diverge, and a
  much shorter build — with the binary supplied, the `.deb` job needs no .NET
  SDK, no clang and no Microsoft apt feed at all, just `dpkg-dev`.

  This is the same build-once-package-many move the container image made when
  it stopped compiling dmart and started installing the Alpine package.

- **dmart serves its UIs from inside the binary, and only from there.** The
  filesystem fallback in `CxbMiddleware`/`CatalogMiddleware` — `{BaseDir}/cxb`,
  `/usr/lib/dmart/cxb`, `/app/cxb` — is gone, along with the second copy of the
  assets the Alpine package laid down at that path.

  Both existed for one reason: `ManifestEmbeddedFileProvider` does not survive
  Native AOT on musl, so the middlewares fell through to disk and the APK
  shipped 5.9 MB of duplicate assets to catch them. That is what kept `/cxb`
  and `/cat` alive in the Alpine package and container while the static tarball
  404'd for two releases. v1.5.2 replaced the reader with one that works on
  glibc and musl alike, which made the duplicate dead weight.

  The consequence is worth stating plainly: **embedded is now the only path, on
  every artifact.** A regression 404s every UI everywhere rather than only on
  musl. That is why the release asserts `/cxb/` and `/cat/` actually serve — on
  all four tarball legs and on both container images — before anything is
  published.

- **The container image is 36% smaller — 92.3 MB to 58.9 MB.** Almost none of
  that was Alpine, whose base rootfs is 8.7 MB. It was waste:

  - **21.8 MB: the `.apk` was carried twice.** It was `COPY`ed to `/tmp` and
    deleted in the *next* `RUN` — but a delete in a later layer reclaims
    nothing, so the package stayed in the image alongside its installed copy.
    A `RUN --mount=type=bind` leaves no layer at all. This single mistake was
    over half the image's overhead.
  - **~7.5 MB: `bash` and `curl`, which nothing used.** `entrypoint.sh` is
    `#!/bin/sh` and shells out to only `tr`, `head` and `chmod`; there is no
    `HEALTHCHECK`, and the release smoke test curls from the host. Five
    requested packages pulled in 38; between them these two dragged `libcurl`,
    `brotli-libs`, `nghttp2`, `libidn2` and `libunistring`.
  - **5.9 MB: the SPAs were shipped twice.** The APK lays `cxb` and `catalog`
    down at `/usr/lib/dmart/` *and* they are embedded in the binary. That path
    is the filesystem fallback the middlewares used when the embedded reader
    failed under Native AOT on musl — it is what kept `/cxb` and `/cat` working
    in this image while the static tarball 404'd. Since v1.5.2 the embedded
    reader works, so the image drops the copy.

  `jq` stays — a join sub-query carrying a `jq_filter` shells out to it.
  `tzdata` stays at 433 KiB. `krb5-libs` stays: the GSSAPI PAL is disabled in
  this image, which *probably* makes it redundant, but that is not a reason to
  drop 1.7 MB from a supported auth path without testing it.

  Removing the SPA fallback means the primary path now has to be checked, so
  the release smoke test asserts `/cxb/` and `/cat/` return 200 before the
  image is pushed. It previously checked only `/health/ready` — which is how
  a container serving neither UI would have passed.

- **The container mocks SMS OTP by default.** It ships no SMS gateway, and
  `SmsSender`'s unconfigured path logs "SMS gateway not configured — dropping
  message" and returns false. At the previous `MOCK_SMPP_API=false` default
  that meant `/user/otp-request` minted a code and silently failed to deliver
  it, so OTP login could not be completed and nothing said why. The generated
  config now sets `MOCK_SMPP_API=true` with `MOCK_OTP_CODE=123456`, and the
  first-run banner says so. Configure `SEND_SMS_OTP_API` + `SMPP_AUTH_KEY` and
  unset it for real delivery. `MOCK_SMTP_API` is deliberately left alone —
  email OTP has the same gap, but that is a separate call.

## v1.5.2 — 2026-09-06

### Fixed

- **The fully static binary returned 404 for `/cxb` and `/cat`.** Both SPAs
  were unreachable on the musl artifact in v1.5.0 and v1.5.1 — the one build
  whose entire selling point is needing nothing beside it. The glibc builds
  were unaffected.

  The assets were embedded correctly the whole time; only the reader failed.
  `CxbMiddleware` and `CatalogMiddleware` loaded them through
  `ManifestEmbeddedFileProvider`, which does not work under Native AOT on musl.
  That throw was caught and fell through to a filesystem fallback looking for a
  `cxb/` directory next to the binary — which the static tarball deliberately
  does not ship, because it is one file. Both strategies failed, and
  `if (fileProvider is null) return app;` returned **silently**, so nothing in
  the logs or the build said anything was missing.

  Both middlewares now read the embedded manifest XML directly, the way
  `Cli/SeedCommand.cs` already did for seed spaces and `LanguageLoader` for
  translations — which is exactly why `languages loaded: 3 … from embedded`
  appeared in the static binary's startup log while the SPAs did not. That
  reader works on glibc and musl alike, so the two builds no longer diverge.
  The filesystem fallback stays for the Docker and RPM layouts.

  A missing bundle now logs a warning naming the URL that will 404. Silence is
  how this shipped twice.

  Verified on a locally built `linux-musl-x64` binary (0 `NEEDED`): both SPA
  roots, `index.html`, all 12 referenced JS/CSS/icon assets, the `<base href>`
  rewrite, the extensionless deep-route fallback, the root `/favicon.ico`
  redirect, and non-default `CXB_URL`/`CAT_URL` prefixes.

### Changed

- **The release now proves each binary actually serves its embedded SPAs.**
  `release-verifiable.yml` starts the freshly published binary against SQLite
  and requires `/cxb/` and `/cat/` to return 200 — inside busybox for the
  static legs. Nothing else could have caught the bug above: `curl.sh` check 49
  accepts a 404 as "SPA not built" and the test suite treats a missing bundle
  as skip, both correctly, since a dev build legitimately has no SPA. The
  release job is the only place that knows the bundle is present, having
  unpacked it two steps earlier.

## v1.5.1 — 2026-09-05

### Added

- **A fully static arm64 binary**, `dmart-<version>-linux-musl-arm64.tar.gz`,
  alongside the x86-64 one v1.5.0 introduced. Same construction: SQLite and
  OpenSSL bound at link time, zero `NEEDED` entries, no `libe_sqlite3.so`
  beside it — one file that runs on any arm64 Linux regardless of distro or
  glibc version.

  It is a new entry in the existing build matrix rather than any new
  machinery, so it goes through the same pin-to-the-tagged-commit check, SBOM,
  signing and SLSA attestation as everything else. It differs from the x86-64
  static leg **only in its runner**: the pinned Alpine SDK and busybox digests
  are both multi-arch manifest lists that already carry `linux/arm64`, and as
  with `linux-arm64`, it has to be a real arm64 machine because NativeAOT
  cannot cross-compile.

  `scripts/verify-release.sh` now requires it, so a release that lost it fails
  verification rather than passing with one artifact fewer. The `static-build`
  job in `ci.yml` still builds only x86-64 — the regression it exists to catch,
  a dependency reaching its native library through `dlopen`, is not
  architecture-specific.

## v1.5.0 — 2026-09-05

### Added

- **The fully static musl binary is now a release artifact.**
  `dmart-<version>-linux-musl-x64.tar.gz` ships on every `v*` tag alongside the
  glibc tarballs, with the same CycloneDX SBOM, keyless signature and SLSA
  provenance attestation as the rest. SQLite and OpenSSL are bound at link
  time, so it has zero `NEEDED` entries and runs on any x86-64 Linux regardless
  of distro or glibc version — one file, with **no `libe_sqlite3.so`** beside
  it.

  It rides the existing build matrix rather than a job of its own, so it goes
  through the same pin-to-the-tagged-commit check and the same signing path by
  construction. A separate job would have meant a second copy of the logic that
  guarantees nothing is signed that was not built from the tagged commit, and
  that is not a thing to keep two copies of. The container image, toolchain
  setup and build flags moved into matrix fields to make that possible.

  Two assertions run before it is signed: `readelf -d` must report zero
  `NEEDED` entries, and no `libe_sqlite3` / `libssl.so` / `libcrypto.so`
  strings may survive. It then reports `--version` from inside **busybox**
  rather than the Alpine image it was built in — proving it starts somewhere
  with none of its build-time surroundings. `scripts/verify-release.sh`
  requires the tarball, so a release that lost it fails verification instead of
  passing with one artifact fewer.

  `build.sh` gained `--static`, so the scripted build path is the one CI uses
  rather than a `dotnet publish` line duplicated into a workflow. It refuses a
  non-musl RID, since a static-pie is a musl construct that glibc cannot link
  into something that actually runs.

  **You own CVE patching for this artifact.** Statically linked OpenSSL and
  SQLite get no distro updates, so an advisory in either becomes a
  rebuild-and-reship. That is why the glibc tarballs still ship alongside it
  rather than being replaced by it.

**Plugins can call back into dmart.** Only in-process `.so` plugins could do
this before, via a C ABI struct — load an entry, run a query, send mail,
broadcast on a channel. Subprocess plugins, the mode the SDK recommended, had
none of it, so "recommended" came with a silent capability cliff and anything
needing a callback had to crash the host when it faulted. That gap is what kept
in-process plugins alive; closing it is what let them be removed.

A plugin may now interleave callback frames into an exchange before its final
response, and dmart answers each on stdin:

```
← {"type":"callback","id":1,"op":"query","args":{"type":"search","space_name":"acme"}}
→ {"type":"callback_result","id":1,"ok":true,"result":{...}}
← {"status":"ok"}
```

Ten ops are available: `load_entry`, `load_user`, `save_entry`, `update_user`,
`send_email`, `ws_broadcast`, `query`, `log`, `get_session_firebase_tokens` and
`get_media_attachment`. The last one base64-encodes the blob, costing about 33%
more bytes on the wire than the file itself; a miss is `{"media":null}` rather
than an empty string, so an absent attachment and a zero-byte one stay
distinguishable.

Support is negotiated: the info frame is now
`{"type":"info","host":{"callbacks":1}}`. A plugin that sees no `host` object
is talking to an older dmart and must not send callbacks — the frame would be
read as its final response. Existing plugins that never send one are
unaffected, and any line that is not a `"type":"callback"` object is still
treated as the response exactly as before.

Two limits bound a misbehaving plugin: 256 callbacks per exchange, and the 30s
timeout now applies per line rather than per exchange — a long honest chain of
callbacks is fine, going silent for 30s is not. A callback that re-enters its
own plugin is rejected rather than allowed to write a second request onto a
pipe that is midway through an exchange.

A `query` runs as the user that triggered the exchange unless it carries an
explicit `as_actor` override, so plugin queries stay inside that user's
permissions by default — the same rule the in-process callback followed.

**Plugins can serve calls in parallel — `"workers": N` in `config.json`.**
Exchanges are serialized per process because the line protocol has no
correlation ids: a reply is matched to its request by arrival order, so two
exchanges sharing one pipe could each read the other's answer. Rather than add
ids and require every plugin to handle concurrent requests itself, dmart now
runs N copies of the executable and dispatches each call to whichever is free.
The plugin contract is unchanged — each worker still sees one message at a
time, so existing plugins work untouched.

Default is 1, i.e. exactly the previous behaviour. It is opt-in because
concurrency changes what a plugin's own state means: a counter, a cache or a
warm connection becomes per-worker rather than per-plugin, and consecutive
calls need not land on the same process. The range is clamped to 1-32 —
`workers` is operator-edited JSON and a stray digit should not decide how many
processes dmart forks.

Genuine parallelism was the one thing in-process `.so` plugins had that
subprocess plugins did not; this closes that gap without putting third-party
code back inside the host process.

### Changed

- **The eight shipped after-hook plugins now run fire-and-forget.** They ship
  `"concurrent": true`, so `audit`, `local_notification`,
  `admin_notification_sender`, `system_notification_sender`,
  `realtime_updates_notifier`, `mcp_sse_bridge`, `semantic_indexer` and
  `resource_folders_creation` no longer add their work to the latency of the
  action that triggered them.

  This is a real behaviour change, not a restoration. The documented default
  has always been `true`, but a source-generated-deserializer defect fixed in
  the previous release meant the field never survived JSON, so every after-hook
  had in fact always been awaited; that release pinned `false` to hold
  behaviour still while the mechanism was fixed. This is the deliberate flip
  that pin was there to make reviewable.

  For the notifiers, the audit log and the indexer, not blocking the response
  is the entire point. **`resource_folders_creation` is the one to know about:**
  it materializes `/schema` on Space create and `people/{shortname}` plus five
  sub-folders on User create, so a create response can now return before those
  folders exist. A client that creates a Space and immediately uploads a schema
  can lose that race. It does not fail — a missing parent folder is an explicit
  *allow* for both folder-level gates — but the write lands without
  folder-level validation, which in the shipped configuration is a no-op only
  because the auto-created folder declares no restrictions. `curl.sh` check 49
  already polls for the folder rather than assuming it. Set `"concurrent":
  false` in that one plugin's `config.json` to keep it awaited.

**Every plugin process is told what the host supports, including after a
crash.** The `{"type":"info","host":{…}}` frame was sent once, by the loader,
to the process running at startup. A plugin that cached the answer — as the
SDK sample does — silently stopped making callbacks after its first crash,
because the replacement process had never been told, and nothing in the log
said so. The frame is now replayed by whichever code starts a process, so a
respawned worker and a brand-new one are indistinguishable.

**A plugin that dies mid-exchange is no longer retried once it has made a
callback.** The retry exists for a plugin that dies before doing anything;
after a callback has been serviced a `save_entry` may already have landed, and
replaying the request would double it.

**API plugins can return binary responses.** The
`{"binary":true,"content_type":…,"body_b64":…,"filename":…}` envelope was only
honoured on the in-process path; it now works for every plugin. Without this,
removing in-process plugins would have silently taken binary responses with it.

### Fixed

- **The last blocker to running after-hooks concurrently was in the test
  harness, not the plugins.** `TestUserCleanup` deletes the rows a hook wrote
  before deleting the user that owns them — `resource_folders_creation`
  materializes `personal/people/{shortname}/*` entries whose
  `owner_shortname` points at the new user. Pinned `"concurrent": false` that
  hook finishes inside the request, so its rows are always there to purge. Set
  `"concurrent": true` it is dispatched with `Task.Run`, so its inserts could
  land *after* the purge, and the user delete then tripped the very foreign key
  the helper exists to avoid.

  It surfaced as `FOREIGN KEY constraint failed` attributed to whichever test
  happened to be running, which is why it looked like an unrelated flake that
  moved between test classes between runs — three different classes across the
  runs that caught it. The between-test drain added previously could not cover
  it: that settles hooks *after* the test method, and this cleanup runs inside
  it. The helper now settles in-flight hooks before purging, in one place that
  all eight affected test files already route through.

  Measured: unpinned went from 1 extra failure in 3 runs to **0 in 6**; pinned
  is unchanged at 0. Behaviour is unchanged — the eight plugins stay pinned —
  but the flip is no longer gated on an unexplained flake.

- **The frontend SBOM listed 300+ things that do not ship.** It asked
  `yarn list --production` — what `package.json` calls a runtime dependency —
  and cxb declares `@tailwindcss/vite`, `tailwindcss`, `vite-plugin-static-copy`,
  `vite-plugin-svelte-md` and `mdsvex` as dependencies. All are build-time, and
  between them they dragged in esbuild, lightningcss, `@tailwindcss/oxide`,
  `@parcel/watcher` and some seventy per-platform native binaries for operating
  systems the artifact does not run on. The document asserted every one of them
  ships inside `/usr/bin/dmart`.

  That is what produced the SBOM-driven false positives. `jmespath` (a
  `svelte-jsoneditor` dependency that tree-shakes away entirely —
  `cxb/vite.config.ts` already lists it under `skipChunks`) drew two critical
  JMESPath CVEs that turned out to be against the Ruby and PHP implementations.
  `apexcharts`, pulled in by `flowbite-svelte`, is dual-licensed and free only
  under $2M annual revenue — a real licence question, raised about a package
  none of whose JavaScript reaches the bundle.

  The inventory now comes from the built bundle. The apps are built with
  sourcemaps and every `node_modules/<pkg>` path in them is collected, so
  tree-shaken code is absent by construction rather than by a maintained
  exclusion list. Stylesheet references (`@import "tailwindcss"`,
  `@plugin 'flowbite/plugin'`, `@source ".../node_modules/<pkg>"`) are unioned
  in: that code does not ship but its generated output does and carries its
  licence, and Vite emits no CSS sourcemaps, so leaving it out would
  under-report. `flowbite` is the live example — its plugin is where those
  `apexcharts` CSS classnames in the bundle actually come from.

  **434 components became 75**, and the count is not the interesting part.
  The old set both over-reported build tooling *and* missed **`svelte` itself**
  — the framework runtime, unquestionably in the shipped bundle, absent from
  the inventory because it is declared a devDependency. Every one of the 75 now
  resolves a licence locally; the npm-registry fallback added alongside the
  licence fix is no longer reached, because the packages that needed it were
  the per-platform binaries that never shipped.

  Generation now fails if a build fails or emits no sourcemaps, rather than
  quietly producing a thinner document, and it reports how many lockfile
  entries were excluded so a shrunken inventory is never mistaken for a broken
  one. The SBOM job now builds the frontends (~40s); `release.yml` builds them
  in a separate parallel job whose output it cannot see.

- **Declared defaults across the wire model were silently discarded.** #234
  fixed this for `PluginWrapper`; the same defect ran through most of the
  model. On meeting an init-only property, the source-generated deserializer
  abandons the parameterless constructor for
  `ObjectWithParameterizedConstructorCreator`, which assigns every such
  property from an args array and passes `default(T)` for whatever the payload
  omitted. The initialisers ran and were immediately overwritten.

  It only showed where the declared default differs from `default(T)`, which is
  what kept it hidden — `Response.Status = Status.Success` looked correct
  because `Success` is the enum's zero member. The same coincidence in reverse
  is where it did real damage:

  - `Space`, `Role`, `Group` and `Permission` deserialized with
    `resource_type` **`user`**, because `ResourceType.User` is the zero member.
  - A user parsed without an explicit language came back **Arabic**, not
    English — `Language.Ar` is the zero member and `= Language.En` was dropped.
  - Every `= new()` collection arrived as `null` and every `= ""` string as
    `null`, which is what forced the `?? ""` coercions in
    `SpaceRepository.UpsertAsync`.
  - `Query.Limit` arrived as `0` rather than `10`, and
    `Query.FilterSchemaNames` as `null` rather than `["meta"]`. `Query` is
    deserialized straight from request bodies by `CsvHandler`,
    `ImportExportHandler`, `ExecuteTaskHandler` and `AlterationHandler`.

  The 67 properties that carry a default are now `set` rather than `init`,
  which puts the generator back on the real constructor. Only those properties
  changed: the rest stay init-only, and types never deserialized from JSON
  (`DmartRole`, `DmartPermission`) were left alone. Types with `required`
  members keep the parameterized creator, but it then carries only the required
  members — which a payload must supply anyway — so the rest keep their
  declared defaults.

  `SpaceRepository`'s `?? ""` backstops stay. They no longer cover an omitted
  field, but a payload that spells out `"icon": null` still lands a null in a
  non-nullable string, because System.Text.Json does not enforce nullability at
  runtime. `SeedSpaceMetaTests` previously asserted the broken shape and is now
  inverted to pin the corrected one.

- **The frontend half of the SBOM now carries licences.** `yarn.lock` records
  only name, version and integrity — it has no licence field — so syft reading
  it emitted 434 components with no licence at all, and every per-RID document
  inherited that on merge. A reviewer could not tell an MIT dependency from a
  revenue-gated commercial one, and Dependency-Track reported the whole tree as
  unlicensed rather than flagging the single term that needs a decision.
  `dist/frontend-sbom.sh` now resolves licences from the installed
  `node_modules` tree, falling back to the npm registry for the optional
  per-platform native binaries (`@esbuild/win32-*`, `lightningcss-*-msvc`,
  `@tailwindcss/oxide-*`, `fsevents`) that never install on the build machine
  and so can never be resolved locally. Coverage went 0/434 → 434/434; the
  component set is unchanged.

  Licences are encoded the way CycloneDX requires rather than pasted into one
  field: SPDX identifiers as `license.id`, compound terms as an SPDX
  `expression`, and anything unrecognised as `license.name`. `license.id` is a
  schema enumeration, and an out-of-enum value fails the validation
  `actions/attest-sbom` runs before signing — so an unknown string degrades to
  a plain name rather than breaking a release. Non-SPDX licences are printed at
  generation time as needing review, which is how `apexcharts` (dual-licensed,
  free only under $2M annual revenue) becomes visible instead of silently
  reading as unlicensed.

  Generation fails if fewer than half the components resolve a licence. The two
  sources fail independently, so losing either still clears the floor; losing
  both is the case worth refusing, because an SBOM asserting 434 unlicensed
  dependencies reads as a licence finding rather than the tooling failure it
  is. `FRONTEND_SBOM_OFFLINE=1` skips the registry lookup for airgapped builds.

- **A plugin's `config.json` defaults were silently discarded.** `PluginWrapper`
  declares `ordinal` defaulting to 9999, `concurrent` to `true` and
  `dependencies` to an empty list, and the SDK documents all three. None of them
  survived deserialization: a config that omitted a field got `0`, `false` and
  `null` instead.

  The cause is a sharp edge in the source-generated deserializer. With any
  init-only property, it abandons the parameterless constructor for
  `ObjectWithParameterizedConstructorCreator`, which assigns *every* such
  property from an args array and passes `default(T)` for whatever the JSON left
  out. The constructor still ran, so the initialisers executed and were then
  immediately overwritten. `new PluginWrapper()` was correct throughout, which is
  why this survived: any test that built the object in C# saw the documented
  values, and only a test that went through JSON could have caught it. The
  properties are now `set` rather than `init`, which puts the generator back on
  the real constructor.

  The practical effect was on `concurrent`, which `PluginManager` reads directly
  to choose between fire-and-forget and awaited after-hook dispatch. Every
  shipped plugin omitted the field, so every after-hook had been awaited —
  the opposite of the documented default, and of what the dispatch code was
  written for.

  **Runtime behaviour is deliberately unchanged by this release.** The eight
  shipped after-hook plugins now state `"concurrent": false` explicitly, so they
  keep running awaited exactly as before. Fixing the mechanism and changing when
  every hook in the system runs are two different changes, and only the first
  belongs in a bug fix. Moving them to fire-and-forget is now a one-line,
  reviewable decision per plugin.

  New plugins are unaffected by the pinning and get the documented default:
  omitting `concurrent` means fire-and-forget, as the SDK has always said.

- **The .NET half of the SBOM now carries licences, and one of them needs a
  decision.** Five components had none, and an unlicensed component is
  indistinguishable from a permissively licensed one — so a term that needs
  attention read as uninteresting.

  `Json.More.Net`, `JsonPointer.Net` and `JsonSchema.Net` declare
  `<license type="file">OSMFEULA.txt</license>`, an **Open Source Maintenance
  Fee** agreement: the source is MIT, but the pre-compiled Binary Release — the
  NuGet package we consume — carries a monthly fee for users in
  revenue-generating activities with annual gross revenue at or above
  **US$10,000**. `JsonSchema.Net` is a direct dependency behind
  `SchemaValidator` and `PreflightService`, so it is compiled into every
  artifact we ship. The two runtime packs were simply missed: they declare MIT,
  but `dist/sbom.sh` injects them outside the restore graph the CycloneDX tool
  reads, so nothing ever asked.

  Licences are now read from each package's `.nuspec` for anything the tool
  left blank, and packages shipping their own licence text are printed at
  generation time rather than reduced to a count. 34/34 components carry a
  licence, and the document still validates against CycloneDX 1.6.

- **`GET /db_size_info/` returns per-table sizes on builds that can provide
  them.** `DbSizeInfoPlugin` hardcoded that `dbstat` is unavailable. That was
  true of the SQLitePCLRaw `e_sqlite3` build and is not true of the static musl
  artifact, which links Alpine's SQLite and does compile
  `SQLITE_ENABLE_DBSTAT_VTAB` in — so the one artifact that could answer
  refused to, and the refusal named a build it was not running. It now runs the
  query and falls back only when that fails. The test asserted the failure
  unconditionally, which is how the stale assumption survived; it accepts both
  outcomes and pins what each must contain.

- **The test suite settles fire-and-forget plugin hooks between tests.**
  `TestParallelization.cs` runs the assembly serially because the suite shares
  one database and process-global plugin state, but that serializes *tests*,
  not their side effects: a concurrent after-hook is dispatched with `Task.Run`
  and outlives the request, so one test's hooks could still be writing while
  the next ran. An assembly-level `BeforeAfterTestAttribute` now waits for them.

  `InFlightTracker` gained `WaitForIdleAsync` for this. `DrainAsync` could not
  be reused: it cancels `ShutdownToken` as its last act, which is correct once
  at teardown and wrong repeatedly — every hook dispatched afterwards would
  receive an already-cancelled token and unwind immediately, so the hooks would
  silently stop running.

  This is a prerequisite for moving any plugin to `"concurrent": true`, not a
  green light. With the eight shipped plugins temporarily unpinned, the suite
  still produced an intermittent extra failure (1 run in 3, in a different test
  than before the change), so something beyond hook overlap remains. With them
  pinned as they ship, the suite reproduces its baseline exactly across three
  runs, so this costs nothing today.

  `DmartFactory.SettlePluginHooksAsync()` covers the case the between-test hook
  cannot: a test asserting on state a hook affects, where the assertion happens
  before the attribute runs.

- **A user-supplied JSON Schema could kill the process, and another could
  silently switch validation off.** Both are reachable by anyone able to store
  a `schema` entry.

  `{"$id":"https://x/s","allOf":[{"$ref":"https://x/s"}]}` compiles fine and
  recurses only when something is evaluated against it. On the shipped
  `JsonSchema.Net` 9.1.4 that recursed until the stack gave out — and a
  `StackOverflowException` cannot be caught, so the process died and took every
  in-flight request with it. Reaching it needed nothing exotic: store that
  schema, then write one entry whose payload references it. Upgrading to 9.4.0
  turns it into a catchable exception.

  Separately, `JsonSchema.FromText` registers a schema's `$id` into a
  **process-global** registry that refuses to overwrite. dmart recompiles the
  same document routinely, because `ClearCache()` runs on every schema entry
  write — so the second compile threw, `GetCompiledAsync` caught it and
  returned null, and null reads as "schema not found — pass through". Writing
  any schema entry therefore stopped every `$id`-bearing schema from being
  enforced, leaving one warning log as the only trace. dmart's own seed schemas
  declare no `$id`, which is why nothing caught it. Every compile now gets its
  own registry.

  Schema documents are also checked on the way in rather than waved through, so
  an unusable one is refused with the error attributed to its author instead of
  to whoever later writes the first entry against it. The check evaluates a
  trivial instance rather than only compiling, because compiling is exactly what
  fails to notice a reference cycle. Only exceptions reject: a schema that
  legitimately fails against the empty instance — anything with `required` — is
  fine, and so is one that recurses legally through `$defs`.

  The upgrade keeps `JsonSchema.Net` on its Open Source Maintenance Fee terms
  (see the SBOM entry above); the last MIT release, 8.0.5, still has the crash.

### Removed

**In-process `.so` plugins.** dmart no longer loads shared libraries into its
own process via `NativeLibrary.Load`. The C ABI (`get_info`, `hook`,
`handle_request`, `free_string`, `init`, `dmart_plugin_version`), the
`DmartCallbacks` struct and its capability marker, and the C# SDK header in
`custom_plugins_sdk/shared/` are all gone, along with the two `.so` sample
projects — replaced by Python samples that use the protocol above.

This mode ran third-party code inside the host process, so a segfault in a
plugin took dmart down with it, and it could not work in a static build at all
(`dlopen` is unavailable there). The subprocess protocol now covers everything
it could do, including every callback.

**If you have a `.so` deployed**, dmart reports it at startup and on
`GET /info/plugins` as a load failure naming the removal rather than skipping
the directory in silence — a plugin that stops running should never be
something you have to infer from behaviour that quietly stopped happening. Port
it using `custom_plugins_sdk/README.md`; the event and request envelopes are
unchanged, so the handler logic usually transfers as-is.

Two host-side details went with it: `ProcessEnv` (a libc `setenv`
write-through that existed only because in-process plugins read the real
`environ` — child processes inherit the managed view, so the managed API is
enough now), and the `[ThreadStatic]` actor context's dependency on
synchronous native frames.

- **`validate_schema` is gone from the query body.** It had no consumers
  anywhere in `Services`, `Api` or `DataAdapters`, while `docs/query.md`
  advertised it — so it read as a working switch. Removing it changes no
  output: `Query` is deserialized from request bodies and never serialized into
  a response, so a client that keeps sending it is unaffected, since
  System.Text.Json skips unmapped members. (`cxb` also passes `validate_schema`
  to `/managed/entry`, whose handler never declared it either; those calls were
  already inert and are left alone.)

## v1.4.1 — 2026-09-04

### Fixed

**`POST /user/profile` accepts an unchanged `email`/`msisdn` again.** v1.4.0
refused all six contact keys on presence alone. But `email` and `msisdn` are
part of the profile *representation*: a client that reads its profile, edits a
display name and posts the Record back sends them straight back unchanged, and
that ordinary round-trip started failing with `INVALID_DATA` having changed
nothing. They are now refused only when they name a *different* address than
the row holds; an echo, or a null, is the no-op it always was. `new_email`,
`new_msisdn`, `email_otp` and `msisdn_otp` are still refused by name. (#228)

**`POST /user/verify-contact` no longer returns 500 when `code` is missing.**
The field is non-nullable in the request record but nothing enforced that on
the wire, so an omitted `code` reached the hasher as null — and did so
precisely when a live code existed for the destination, i.e. right after
`/otp-request`. Now a `MISSING_DATA` 400, refused before the store is touched
so it cannot spend a verification attempt either. (#228)

**Contact changes reach the audit history again.** `/user/verify-contact`
wrote directly to the user row, skipping the diff the `/user/profile` path it
replaced used to append — so a changed email was invisible to
`/managed/query?type=history`. (#228)

**Confirming a contact no longer rewrites its stored spelling.** An
admin-provisioned or OAuth-sourced `Alice@Example.com` was silently lowercased
the first time its owner confirmed it. The address is now written only on an
actual change. (#228)

**`GET /user/profile` returns the caller's avatar.** Attachments on the user's
own row now come back under `attachments`, in the same shape `/managed/entry`
returns, so a client that already renders `record.attachments` needs no second
call. Avatar only, matching Python's `filter_shortnames=["avatar"]`. (#227)

**`/managed/entry` honours `retrieve_attachments` for spaces, users, roles and
permissions.** Those four returned a bare row and silently ignored the flag.
(#227)

### Security

**`fast-uri` pinned past four HIGH CVEs** (CVE-2026-75899, CVE-2026-75931,
CVE-2026-75975, CVE-2026-76172 — SSRF, host confusion via IDN, IPv6
normalisation, URI parsing). It reached the frontend bundle transitively via
`svelte-jsoneditor` → `ajv`. The declared range already permitted the fix; the
lockfile was stale. Pinned through the root `resolutions` block, as with
`vite` and `esbuild`. (#229)


## v1.4.0 — 2026-09-02

### Breaking — the OTP issuing endpoints are now one endpoint

Two routes are gone. Any client that calls them gets a 422:

| removed | use instead |
| --- | --- |
| `POST /user/otp-request-login` | `POST /user/otp-request` with `"purpose": "login"` |
| `POST /user/password-reset-request` | `POST /user/otp-request` with `"purpose": "reset"` |

`POST /user/otp-request` is now the single issuing API and **requires** an
explicit `purpose`: `login`, `reset`, `register` or `verify-contact`. A request
without one is refused with `INVALID_DATA "invalid purpose"`. Codes never cross
purposes — one minted for `login` cannot complete a signup, and `/user/create`
accepts only a code minted at `register`.

**`POST /user/otp-confirm` is replaced by `POST /user/verify-contact`**, which
owns every contact-plus-OTP operation:

```http
POST /user/otp-request      {"purpose": "verify-contact", "email": "me@x.com"}
POST /user/verify-contact   {"code": "123456", "email": "me@x.com"}
```

Authenticated. Prove control of an address and it becomes yours, verified —
the *same* call whether it is the address already on your row or a new one.
Which of the two it is comes from state the server already holds, so the caller
does not declare intent. A new address is uniqueness-checked before the code is
spent, and verified flags never regress.

Renamed rather than kept: `otp-confirm` was named for the token it consumes,
and *every* OTP redemption confirms an OTP — logging in, registering and
resetting all do — so the name described the whole category while the handler
served one member of it. Every other endpoint here is named for its outcome
(`/user/login`, `/user/create`, `/user/password-reset-confirm`); this one now
is too.

**`POST /user/profile` no longer accepts contact fields.** `email`,
`new_email`, `email_otp`, `msisdn`, `new_msisdn` and `msisdn_otp` are
**refused by name**, with an error pointing at `/user/verify-contact` — not
ignored, because a client still sending `new_email` would otherwise get a 200
and no change. Everything else on the endpoint is unaffected.

The `new_` prefix is gone with them. It was load-bearing only on
`/user/profile`: `email` is part of the profile representation, so a caller
reading their profile, editing a display name and posting it back sends `email`
unchanged — and had that meant "change my email", an ordinary round-trip would
have demanded an OTP for a field nobody touched. A dedicated endpoint has no
representation to echo, so one unprefixed field is unambiguous.

**`ALLOW_PASSWORD_RESET_RESEND_AFTER` is retired.** `ALLOW_OTP_RESEND_AFTER` now
covers every purpose. A `config.env` still carrying the old key **boots with a
warning** rather than failing — a key that was documented last release is not a
typo, and refusing to start would turn this upgrade into an unannounced outage.

**The in-repo frontends (catalog, cxb) were updated in this release.** Any other
client calling the removed routes needs the same treatment.

### Added

- **Abuse controls on issuing.** A resend cooldown (`ALLOW_OTP_RESEND_AFTER`)
  and a daily cap (`MAX_OTP_REQUESTS_PER_DAY`, default 10), both per
  destination. Each is split into two independent budgets — account recovery in
  one, everything else in the other — so no single flood can close both sign-in
  and password reset. Switching purpose *within* a budget is not a bypass.
- **A verify-attempt cap.** `MAX_OTP_VERIFY_ATTEMPTS` wrong guesses per code,
  after which the code is dead. Verification is single-use: a correct code is
  consumed on success and cannot be replayed.
- **Optional implicit registration.** With `ENABLE_OTP_IMPLICIT_REGISTRATION`,
  a login-purpose request for an unknown msisdn or email creates the account on
  redemption. Off by default.
- **OTP history retention.** Consumed and expired rows are swept hourly by
  `OtpHistorySweeper`, keeping `OTP_HISTORY_RETENTION_DAYS` (default 2).

### Security

- **Every OTP verify path is capped and consuming.** Previously some paths
  verified without consuming, so a code could be replayed, and without an
  attempt cap a 6-digit code could be brute-forced.
- **An anonymous caller could deny a victim both sign-in and account recovery.**
  `register` needs no token and no existing user, so one request a minute
  against a known destination held the resend cooldown permanently open and
  swallowed every login and reset as a silent 200. Both the cooldown and the
  daily cap are now bucketed so one flood cannot close the other. A targeted
  flood can still exhaust one bucket; closing that needs a per-caller identity
  this endpoint does not have.
- **Issuing honours the lockout cool-down.** It gated on the raw active flag,
  which lockout clears only via `IsLockedAsync` — so a locked account stayed
  silently un-OTP-able forever after its cool-down expired. For a password-less
  account, whose only credential is the OTP, nothing would ever have unlocked it.
- **Destinations are no longer logged in clear.** Phone numbers and email
  addresses were written at Information on every silent no-op branch; they are
  now an 8-character fingerprint.

### Fixed

- **`DmartClient.ConfirmOtpAsync` sent the wrong body key** and could therefore
  never succeed: it posted `otp` where the request record binds `code`, so the
  server saw no code at all. Latent since the method was added; found while
  reworking the endpoint it calls. It is now `VerifyContactAsync`, with the
  correct key.

- **Password reset was unrecoverable for a mixed-case stored email, and locked
  the account.** Issuing stored the code under the lowercased address while
  confirming looked it up under the raw stored value, so every *correct* code
  returned `OTP_INVALID` — and each attempt counted toward the failed-attempt
  lockout. Mixed-case rows are ordinary: admin provisioning and OAuth both store
  the address as given.
- **A user could not confirm the contact already on their row** if it carried
  any uppercase — `/user/profile` compared a normalised input against the raw
  column ordinally, so the check could never pass.
- **`DROP TABLE IF EXISTS otp;` ran on every startup.** It sat in the idempotent
  create script rather than a migration, and `otp` is the table python-dmart
  uses — so on a shared database every C# restart destroyed it. The C# store is
  the new `otps` table; the legacy one is left alone.
- **A code was consumed before checks that could still fail**, in implicit
  registration, `/user/create` schema validation, and both contact-change paths.
  Each of those failures is recoverable, and each burned a valid code — after
  which a retry inside the resend cooldown answered a silent 200 with nothing
  sent.
- **`IssueAsync` was not atomic.** Supersede and insert ran as two statements,
  so concurrent issues could leave two redeemable codes despite the documented
  invariant.

### Migration

None required. Live OTPs are invalidated by the store change — anyone
mid-flow requests a new code.

## v1.3.3 — 2026-09-02

### Breaking

- **netstandard2.1 consumers: dictionary keys stop being snake_cased on the
  way out.** See the `DictionaryKeyPolicy` entry below for what changed. What
  it means for you depends on whether the payload has a schema, and the two
  cases are very different:

  **With a `schema_shortname` — you get a loud, precise error.** The server
  stores keys verbatim and the space's JSON Schema is the enforcement point, so
  a body written as `endPoint` against a schema declaring `end_point` is
  rejected on write:

  ```
  430 payload failed schema validation:
      required: Required properties ["end_point"] are not present;
      /endPoint: All values fail against the false schema
  ```

  Nothing is silently stored wrong. The fix is to spell the key the way the
  schema declares it — which for dmart's own schemas is snake_case.

  **Without a schema, and for attribute bags — keys round-trip verbatim.**
  `{"myKey": "v"}` is stored and read back as `myKey`. So a netstandard2.1
  caller who has been writing `Attributes["myKey"]` against v1.3.x has that
  data on the server under `my_key`, and after this upgrade the same code
  writes `myKey`. Here there is no schema to catch it: migrate the stored
  entries (read under the old key, write under the new), or spell the key the
  way you want it stored.

  net8.0+ consumers are unaffected — that leg was fixed in v1.3.2 and this
  brings the other into line.

  **On the convention.** snake_case remains the convention for dmart payload
  fields, and the shipped schemas follow it (`end_point`, `request_body`,
  `schema_shortname`). It is a convention that schemas *declare* and validation
  *enforces* per space — it was never a wire-level rewrite, and the server has
  never performed one. The client used to, which meant it silently rewrote keys
  the caller had chosen deliberately and could not turn off. Removing it makes
  the client agree with the server; the schema keeps enforcing the convention,
  and now says so out loud when a key does not match.

### Fixed

- **The two serializer legs disagreed about dictionary keys.** v1.3.2 dropped
  `DictionaryKeyPolicy` from `DmartClientJsonContext` because a dictionary here
  is DATA — attribute bags and nested `Dictionary<string,string>` values, whose
  keys belong to the caller and the space's schema — so snake-casing them is
  silent corruption. `DmartClient.DefaultJsonOptions` kept the policy, and that
  object is both the netstandard2.1 leg's serializer and public API a caller can
  serialize a `Record` with. So the same client contradicted itself by target:
  `"myKey"` shipped verbatim from net8.0+ and as `"my_key"` from netstandard2.1,
  the second of which the server — which sets no `DictionaryKeyPolicy` either —
  then stored under a name the caller could not read back.

  The policy is gone from `DefaultJsonOptions` too. Property names still
  snake_case: those are a fixed shape both ends agree on. Every key the client
  itself writes was already snake_case, so nothing the SDK sends changes. A
  caller who was relying on the netstandard leg to rewrite *their* keys was
  relying on the corruption, and should snake_case them at the source.

### Added

- **`Dmart.Client` can read the decimal-point spelling of an integer.** dmart
  validates payload bodies with JSON Schema, where `"type": "integer"` means "a
  number with a zero fractional part" — `10240.0` satisfies it, so dmart accepts,
  stores and returns that value for a field its own schema calls an integer.
  System.Text.Json refuses to read it into an `int`, which left the client
  **stricter than the server it talks to**: a caller whose model matched the
  schema still got

  ```
  JsonException: The JSON value could not be converted to System.Int32.
  ```

  `IntegralInt32Converter` and `IntegralInt64Converter` (namespace
  `Dmart.Client.Json`) close exactly that gap and nothing wider — a value with a
  real fraction is still rejected, because the schema would reject it too, and
  the comparison runs through `decimal` so integers past 2<sup>53</sup> keep
  every digit. Use them per property via `[JsonConverter(...)]`, which needs no
  options plumbing and stays trim/AOT-safe, or register them on your own
  `JsonSerializerOptions` for a whole body; System.Text.Json wraps them for the
  `int?`/`long?` forms automatically. `DmartClient.DefaultJsonOptions` and the
  source-gen context both carry them, so the client's own types read the same way
  on every target framework.

  Writing is unchanged, and so is what the client hands you: payload bodies still
  arrive byte-exact as `JsonElement`, `10240.0` included. The tolerance is in the
  read, not a rewrite of the data. A field reading back as `10240.0` was *stored*
  that way — Python renders every float like that, as does .NET for a `decimal`
  carrying scale.

  **This supersedes the consumer guidance published in v1.3.1**, which told
  callers that such a field "gets a hard `JsonException: The JSON value could
  not be converted to System.Int32`" and to "map it to `decimal`/`double` … or
  normalise it at the producer". That is right for a `"type": "number"` schema
  and wrong when the schema says `integer` — there the caller's `int` was
  correct and the reader was not. With these converters registered, `int` and
  `long` read the value directly and the workaround is no longer needed.

  The server-side guarantee from the same release is untouched and still
  holds: dmart does not reformat numbers anywhere in the stack, so a field
  that reads back as `1000.0` was stored that way.

## v1.3.2 — 2026-09-01

### Performance

- **`min`/`max` over a JSON field walked the same jsonb path four times per
  row.** Ordering by the value's real type (v1.3.1) means consulting both forms
  of the path: a `jsonb_typeof` guard and a value, in each of two aggregates.
  Written inline that is four descents through `payload::jsonb->'body'->…` for
  every row in the group, where one would do.

  The extraction is now hoisted into a `CROSS JOIN LATERAL` over a FROM-less
  `SELECT`, which computes it once per row and hands it to the aggregates by
  name. Two reducers over the same path share one lateral, so a `min` and a
  `max` on the same field walk it once between them.

  Row-preserving by construction — a `SELECT` with no `FROM` yields exactly one
  row, NULL included — so nothing about which rows the aggregate sees changes.
  Every other reducer mentions its field once and is left alone rather than
  paying for a join that saves nothing.

  **SQLite emits none of this and needs none of it**: its `->>` carries the
  value's own type, so `min`/`max` there are a single `MIN(field)` already.
  `ISqlDialect` gained a `Reducer` overload returning an optional FROM
  fragment alongside the expression; it has a default implementation that
  hoists nothing, so third-party dialects are unaffected.

## v1.3.1 — 2026-09-01

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

  Every site that builds a subpath prefix now escapes it (`\`, `%`, `_`) and
  matches under `ESCAPE '\'`, via a new `SubpathScope` helper in
  `Dmart.QueryGrammar` — twenty in all, not just the two on the query path:

  | Component | Sites |
  | --- | --- |
  | `EntryRepository` | 10 — list, export, cascade-delete, move, count |
  | `AttachmentRepository` | 4 |
  | `HistoryRepository` | 4 |
  | `QueryHelper` + `Dmart.SqlAdapter` | 2 — the read paths above |
  | `SemanticSearchService` | 1 |

  **Not all of them are reads, and that matters more, not less.**
  `EntryRepository`'s folder cascade uses the predicate to DELETE and to MOVE
  subtrees, and `AttachmentRepository.DeleteUnderSubpathAsync` to delete
  attachments. An over-matching prefix there does not leak a row — it destroys
  or relocates one belonging to a sibling folder. Those paths were never
  masked by the ACL predicate, so unlike the read leak they were reachable
  before v1.3.0 as well.

  The escaping is emitted in SQL rather than applied to the bound value, so one
  parameter still serves both halves of `subpath = $n OR <descendants>` and
  every positional parameter after it stays where it was. The three sites that
  inline the subpath as a SQL literal instead of binding it use a C#
  counterpart with the same substitutions in the same order.
  `SemanticSearchService` keeps its own bare prefix semantics (no `/`
  separator); only its metacharacters are neutralised.

  **Operators:** on v1.3.0, reads against a subpath with a sibling differing by
  a single character at an underscore position should be treated as having been
  unrestricted. Separately, on **any** version, a folder delete or move scoped
  to such a subpath could have reached the sibling's rows. Subpaths without `_`
  or `%` in their names were never affected by either.

### Fixed

- **`Dmart.Client` could not put a `decimal` (or most other CLR scalars) in an
  attribute bag.** `Record.Attributes` / `Request.Attributes` are
  `Dictionary<string, object>`, so System.Text.Json resolves every value by its
  *runtime* type. On net8.0+ the client routes bodies through the
  source-generated `DmartClientJsonContext`, which only ever registered
  `string`, `bool`, `int`, `long` and `double` — so a `decimal` money field, a
  `float`, a `short`/`byte`/`uint`/`ulong`, a `Guid`, a `DateTimeOffset`, an
  `int[]` or a `List<object>` all threw

  ```
  NotSupportedException: JsonTypeInfo metadata for type 'System.Decimal' was not
  provided by TypeInfoResolver of type 'Dmart.Client.Json.DmartClientJsonContext'
  ```

  at serialize time, before the request left the process. The netstandard2.1 leg
  is reflection-based and never had the problem, which is why this only ever bit
  modern consumers. The context now registers the closed set of JSON-representable
  scalars plus the common collection shapes. A consumer POCO still has to be
  handed over as a `JsonElement` — that is inherent to staying trim/AOT-safe.

- **`Dmart.Client` rewrote dictionary keys on the way out.** The
  source-generated context set `DictionaryKeyPolicy = SnakeCaseLower` alongside
  the property policy, so every key the caller chose was snake_cased before it
  left the process: an attribute stored as `myKey` arrived at the server as
  `my_key`, and read back as nothing under the name it was written with. Nested
  `Dictionary<string, string>` values had the same done to them. The server's
  `DmartJsonContext` sets no `DictionaryKeyPolicy`, so the two sides disagreed
  about the caller's own field names. The policy is gone; dictionary keys now go
  on the wire verbatim. `PropertyNamingPolicy` is unchanged — model properties
  are still snake_case, because those are a fixed shape both ends agree on.

- **Aggregation results were rounded on the way out.** `QueryService` narrowed
  every aggregation cell to a type the server's source-gen context knew —
  `long → int` and `decimal → double`. The same defect class as above, and both
  casts lost data. PostgreSQL emits `SUM`/`AVG` over numeric as `numeric`, which
  Npgsql hands back as `decimal`, so every money aggregate went through binary
  floating point: a `SUM` of `12345678901234567.89` returned
  `12345678901234568`, and `0.1 + 0.2` returned `0.30000000000000004`. The
  `long → int` cast was pure loss — `long` was already registered, and the cast
  silently wrapped any count past `int.MaxValue`. `DmartJsonContext` now
  registers `decimal` (and the remaining scalars, matching the client), and the
  casts are gone.

  **Wire shape is unchanged.** `AVG(numeric)` arrives from PostgreSQL at scale 16
  — the average of 10, 20, 30, 30 is literally `22.5000000000000000` — and
  `decimal` carries trailing zeros through System.Text.Json where `double` does
  not. Emitting it raw would have turned `22.5` into `22.5000000000000000`, so
  the scale is normalised away without altering the value. Callers keep seeing
  `22.5` and `90`; what changes is only that the value behind them is now exact.

- **`min` and `max` compared JSON numbers as text on PostgreSQL.** A jsonb path
  resolves through `->>`, which hands back text, so the two ordering reducers ran
  a string comparison: over amounts 9, 10 and 100, `min` answered `"10"` and
  `max` answered `"9"`. Wrong for any field whose values vary in digit count, and
  silent. PostgreSQL's default collation also ignores punctuation, so negatives
  misordered against decimals too.

  Both reducers now split the group by the value's actual JSON type, read off
  the jsonb form of the same path: one aggregate over the rows that really are
  JSON numbers, one over the rest, `COALESCE` picking which half answers.
  Numbers order numerically, text keeps its lexicographic order (ISO-8601
  timestamps depend on it), and the comparison uses `::numeric`, not the
  `::float` of the sort keys, so integers past 2<sup>53</sup> cannot tie. Both
  aggregates stream, so memory stays flat in group size.

  Reading the type rather than sniffing the extracted text is what makes it
  safe on strings that merely look numeric. `->>` erases the difference between
  the number `7` and the string `"007"`, so a regex sniff sends zero-padded
  codes — SKUs, account numbers, ISO 3166 numerics — down the numeric branch,
  where `::numeric::text` canonicalises `"007"` to `"7"`: an answer no row in
  the group holds. `jsonb_typeof` cannot make that mistake.

  On mixed data numbers order below text. That is **SQLite's** type ordering,
  not jsonb's own — jsonb ranks Number *above* String — and matching SQLite is
  the point, so the two backends answer the same instead of each inventing an
  answer. JSON nulls and absent fields stay out of both aggregates, as `->>`
  yielding SQL NULL already did.

  **SQLite was never affected** — its `->>` returns the JSON value's own SQL
  type, so it was already comparing numbers as numbers. `ISqlDialect` gained a
  `Reducer` overload carrying the field's JSON form alongside its text form; it
  has a default implementation delegating to the existing three-argument member,
  so third-party dialects are unaffected. Plain columns are untouched: they are
  already natively typed, and `MIN(updated_at)` still returns a timestamp.

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

### Notes

- **Numbers are not reformatted anywhere in the stack**, and
  `NumberFidelityTests` now pins it: an integer written through
  `/managed/request` comes back out of create, update *and* patch as the same
  integer, exact past 2<sup>53</sup>. A field that reads back as `1000.0` was
  *stored* that way — `decimal` is the only .NET type that keeps trailing-zero
  scale through System.Text.Json (`1000.0m` serializes as `"1000.0"`), so a
  producer modelling an integral field as `decimal` is what puts that form in the
  store. Consumers mapping such a field onto `int` get a hard
  `JsonException: The JSON value could not be converted to System.Int32`;
  map it to `decimal`/`double` (matching a `"type": "number"` schema), or
  normalise it at the producer.

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
