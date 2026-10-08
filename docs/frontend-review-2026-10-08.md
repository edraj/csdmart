# Frontend review: cxb and catalog (2026-10-08)

What was done: both SPAs were type-checked, linted, unit-tested and built from
master `0805f27`; the server was run locally with the bundled seed data and
both UIs were walked through in a browser (login, spaces, list/entry/form/
history views, tools, dark mode, Arabic/RTL; catalog home, a space, a post);
and every file under `cxb/src` and `catalog/src` was read for bugs, leftovers,
i18n gaps, accessibility, visual consistency and performance. Spot-checks of
the headline findings against the source are noted inline. Nothing was
changed.

**Status (2026-10-08, branch `fix/frontend-review-2026-10-08`):** every item
below was implemented, grouped into one commit per area (bugs, lint and dead
code, pass A tokens/dark/ui kit/shell/i18n, pass B page rebuilds, pass C
dashboard and forms, performance, tooling, server cache headers); see the
`Unreleased` section of `CHANGELOG.md` for the user-visible summary. Three
items were deliberately left for follow-up PRs: the typing pass for the
remaining explicit `any`s (a warning, not an error, in both lint configs),
the `braces` Dependabot alert (no upstream fix; build-only dependency), and
an end-to-end Playwright suite for both SPAs.

## Start here — the ten that matter most

| # | Where | What | Why first |
|---|---|---|---|
| 1 | catalog `public/config.json` | ships `"backend": "http://localhost:8282"`; the server only auto-fills `backend` when it is empty, so the **embedded catalog is broken on every deployment** that isn't on :8282 (reproduced: "Error Loading Catalogs"). cxb ships `""` and works. | One-line fix; the public UI does not work out of the box today |
| 2 | cxb `ListViewActionBar.svelte:121-126`, `ModalBulkMoveCopy.svelte:74-89`, `:202-207`, `utils/entryManagement.ts:231-233` | bulk delete/move send the *list's* subpath for every record (wrong on Trash and expanded folders); bulk restore slices the path with the leading `/` miscounted (destination space becomes "trash"); bulk trash of a non-root folder sends `-a` for `/a/b` | Data-correctness: operations hit the wrong entry or fail |
| 3 | cxb `ModalCSVDownload.svelte:50`, `tools/query.svelte:136` | `downloadFile(JSON.stringify(data))` on a `text/csv` response → one quoted line with literal `\n`; also copies the list's `offset`/`limit`, so "download all" skips rows | Every CSV export is invalid |
| 4 | catalog FOLDER `:529-534` | "Report" shows `prompt()` then `alert("submitted")` and never calls the API | Users believe they reported content; nothing was sent |
| 5 | catalog SPACE `:642`, FOLDER `:506`, `:132-148`, `DashboardHeader.svelte:155` | share links omit the `[resource_type]` segment (every shared link 404s); breadcrumb links lack `/catalogs`; root-absolute `href="/"` leaves the `/cat/` base | All outbound links from the public UI are broken |
| 6 | cxb `ListView.svelte:254-318` + 6 catch blocks | no try/finally → a 403 or network error leaves the skeleton forever; `error.response.data` without `?.` throws on timeouts and loses the message; no request sequencing (slow old response overwrites new) | One failed query = stuck page |
| 7 | cxb `Login.svelte:74` | `maxlength={24}` on the password field; the server accepts longer passwords | Users with long passwords cannot sign in |
| 8 | cxb: 63 of 77 components hard-code English; catalog: 42 raw keys shown + 134/61 missing ku/ar keys + locale default ignores `default_language` | switching to AR flips direction but **translates nothing** in cxb (reproduced); catalog shows raw keys like `post_detail.login_required.login` | The Arabic UI is not usable as Arabic |
| 9 | catalog SPACE `:117-118` vs `:505-525`, FOLDER `:467-498`, `:1354` | server sorts by shortname; "newest first" re-sorts only the 20 loaded rows; sort change never refetches; Load-more in tag mode checks the wrong flag | The main browse page shows the wrong items |
| 10 | cxb `package.json:10` (`svelte-check --no-tsconfig`), `vite.config.ts:136-148`, catalog: no `eslint.config.*`, 1,093 `any` | type-check skips `.ts` files, every a11y warning is suppressed, lint is broken, CI type-checks nothing | Everything above regresses silently without this |
| 11 | cxb `routes/management/content/[space_name]/[subpath]/index.svelte:13`, `management/_module.svelte:83`, `EntryRenderer.svelte:883-947` | every page/sort/search click re-fetches the folder entry and rebuilds the entire entry view, whose hidden tabs fetch their data too (~7–9 requests per click); the page is built twice on cold load | Performance: 80–90% of cxb's request volume |
| 12 | cxb 7× static `svelte-jsoneditor` imports; catalog `i18n/index.ts:4-6` + `vite.config.ts:91-96` | ~700 kB of editor JS on the cxb Spaces page; ~298 kB of locale JSON in the catalog's main chunk + a 430 kB catch-all vendor chunk | Performance: cold-load size |
| 13 | catalog SPACE `:345-380`, FOLDER `:262-273` | per-item avatar + attachment-count requests, serially, with a re-render after each (20 items ≈ 40–60 requests); counts are already in the main response | Performance: the public listing's load time |
| 14 | `Middleware/CxbMiddleware.cs:141`, `CatalogMiddleware.cs:128` | hashed `/assets/*` served with no `Cache-Control`; every response compressed on the fly | Performance: repeat visits re-validate ~40 files |

The full performance list (35 items) is below.

## Tooling state (measured)

| | cxb | catalog |
|---|---|---|
| `svelte-check` | 0 errors, 5 warnings — but run with `--no-tsconfig`, so `.ts` files are skipped | 0 errors, 3 warnings — only because of 1,093 explicit `any` |
| eslint | no config at all | ESLint 10 installed, no `eslint.config.*` → `yarn lint` crashes |
| vitest | 117 pass (10 files) | 201 pass (16 files) |
| `vite build` | OK, 4.1 s; largest chunks: codemirror 410 kB, flowbite-svelte 274 kB, svelte-jsoneditor 245 kB, ListView 150 kB, icons 116 kB | OK, 5.2 s; vendor 430 kB, index 324 kB, vendor-flowbite 265 kB |
| a11y warnings | suppressed wholesale in `vite.config.ts` | suppressed wholesale in `vite.config.ts:52-60` |
| CI | build + vitest only ("type-checks nothing", per its own comment) | `.gitea/workflows/check.yaml` runs `check` only |
| Dependabot (root `yarn.lock`, all build-time) | 11 open: brace-expansion ×7 (fix 5.0.12), `braces` (no fix), postcss-selector-parser (7.1.6), vitest + @vitest/mocker (4.1.11) | same lockfile |

## Seen in the browser

cxb
- Landing page (`src/routes/index.md`): "servcing", "recieve", "ineract", "presnce", "Design principals", "Usecases"; dead link `/presence_usecases`; `en.json:77` `"interactions": "Ineractions"`.
- Login: label says "Username", placeholder says "Shortname"; the form is `w-1/2` beside an empty blue half with no mobile stacking.
- Spaces grid: cards are not top-aligned (different heights, centred); "Updated: 10/8/2026" is US-ambiguous.
- List view: raw ISO timestamps with microseconds (`2022-11-29T14:05:46.485769`); header casing mixes `shortname`, `Resource type`, `Created At`; **Save** is shown on a folder's list view where nothing is editable.
- Form tab: "Update" inside the Shortname field *and* "Save" in the toolbar — two save actions.
- History tab: "1 of 0 pages" when empty; a second, different pagination widget from the list view's.
- Dark mode: the rows-per-page `<select>` shows no value; the selected sidebar item is dark text on a dark highlight (`SpacesSubpathItemsSidebar.svelte:83` is `bg-gray-300 text-white`, which fails contrast in light mode too).
- Arabic: direction flips, **no string is translated** (Tools, Spaces, Refresh, Delete, Save, tab labels, column headers, "Showing 1 to 3 of 3 entries"…).
- Tools: "HASH: 5d2c40b" as a page heading; "INFORMATION" uppercase next to Title Case cards.

catalog
- Home: "Error Loading Catalogs" until `backend` is corrected (item 1); cards show the owner twice and the entry count under both a "members" and a "comments" icon.
- Post page: the *description* renders raw markdown (`**Role-Based Access Control (RBAC)**` with literal asterisks) while the body renders; a ```mermaid block is shown as a raw code block; every post carries a static "🔥 Hot" badge, "1 min read" computed from fields that don't exist, and an always-on status dot.
- The "En ▾" language control in the header is not in the accessibility tree (not reachable by keyboard or screen reader).
- Not checked (browser session dropped): catalog language switch result, phone-width layouts of either SPA.

## Performance

What is fine: no `setInterval` polling anywhere; the catalog websocket is one shared connection (25 s ping, 3 s reconnect, 5 attempts); every interval and listener is cleaned up; search inputs are debounced (catalog) or submit-only (cxb); no lodash/moment/dayjs, no `import * as` icon imports; Routify routes are lazy. Spot-checked: #1, #2, #4, #21, #28, #32 (confirmed).

**The biggest problem is in cxb: every list interaction (page, sort, search) re-fetches the folder entry and rebuilds the whole entry view, whose hidden tabs also start loading their data.**

### Redundant requests (14)

1. **cxb `routes/management/content/[space_name]/[subpath]/index.svelte:13-17`** (also `[space_name]/index.svelte:7-13`, `[shortname]/[resource_type]/index.svelte:9-17`) — `entryPromise` is `$derived` from the whole `$params`, which in Routify 3.6.4 includes the query params. ListView changes page via `$goto("$leaf", …)` (`ListView.svelte:402,:440`) and so does search (`ListViewActionBar.svelte:190`) → every click re-runs `retrieveEntry` (with attachments + schema validation) and destroys/rebuilds `EntryRenderer` and every child; sorting fetches the list twice; a streaming folder's WebSocket (`ListView.svelte:206-250`) is reopened on every click. — Derive from `$params.space_name`/`subpath` only; let ListView own its fetches. **HIGH: ~7–9 requests + full re-render per click → 1 request.**
2. **cxb `routes/management/_module.svelte:83,:92`** — `<slot/>` is rendered hidden while the profile check is pending, then again in `{:then}`: on a cold load / deep-link refresh the page is built twice and every request is sent twice. **HIGH on cold load.**
3. **cxb `EntryRenderer.svelte:883-947, 973, 1007, 1021`** — hidden tabs are built and fetch immediately on every view (and on every click from #1): `HistoryListView.svelte:53` history query; `RelationshipsPanel.svelte:66,114-118` `getSpaces()`; `PayloadForm.svelte:128-148` schema query (limit 100 + payload) and `:151-169` `folder_rendering` with attachments; `FolderForm.svelte:307,338` every management schema and workflow with payload just to fill dropdowns; `MetaUserForm.svelte:131` 100 roles + 100 groups; `MetaRoleForm.svelte:44` 100 permissions; `MetaPermissionForm.svelte:73` getSpaces. — Build tabs on first open; `retrieve_json_payload:false` for dropdowns. **HIGH: 4–7 requests per view.**
4. **cxb `Attachments.svelte:57-107, 300`** — `{#await fetchDataAssetsForAttachments()}` runs `SELECT * FROM '<file>'` with no LIMIT for every CSV/parquet/jsonl attachment; the result (`attachment.dataAsset`) is never read; it re-runs whenever `filteredAttachments` is reassigned (effects at 217/268/275, every filter click). — Delete, or fetch on preview with a LIMIT. **HIGH for entries with data assets.**
5. **catalog FOLDER `:262-273`, `:1026`** — avatars fetched twice per item: once inside `Promise.all` (the list waits for all lookups), again via `{#await getAvatar()}` per card; `avatarUrl` from `:266` unused; `{#each}` at `:1012` unkeyed; `getAvatar` (`lib/dmart_services/profile.ts:46`) uncached. **HIGH: 2N requests → one per unique owner, list appears a round trip sooner.**
6. **catalog SPACE `:345-380`** (from `:183`) — `enhanceItemsAsync` processes items one at a time: `getAvatar` + `getEntityAttachmentsCount` (`entries.ts:160-183`, an aggregation query that also asks for payload + attachments), re-sorting and re-rendering the whole list after each item; the main query (`:110-121`) already used `retrieve_attachments:true`. **HIGH: 20 items ≈ 40 serial requests (~2–4 s) + 20 re-renders.**
7. **Over-fetching in lists** — cxb `ListView.svelte:290` always `retrieve_json_payload:true` though default columns use metadata only; catalog `spaces.ts:125-126` (`getSpaceContents`) and `:179-180` (`getSpaceFolders`, also `exact_subpath:false`) request attachments used only for counts; `dashboard/admin/[space_name]/[subpath]/index.svelte:429-430` requests attachments it never uses. **MED-HIGH on response size.**
8. **catalog CAT `:61-90`** — 2 serial queries per space on the landing page (counters, then tags; `getSpaceTags` `spaces.ts:240-258` also sets payload + attachments). — Parallelise/aggregate, or load stats on scroll-into-view. **MED-HIGH: 20 spaces = 40 requests.** (Observed during the walk as bursts of 5 identical queries.)
9. **catalog `lib/dmart_services/messaging.ts:222-238`** — `getConversationPartners` fetches 1,000 messages with payload + attachments to build a set of sender names, again on every debounced user search (`routes/messaging/index.svelte:239-247, 1474-1478`). **MED-HIGH.**
10. **catalog `dashboard/admin/[space_name]/[subpath]/index.svelte:396-437`** — every `loadContents` re-fetches the folder entity (payload + attachments) and the space tags along with the list. **MED: 3 requests per page change → 1.**
11. **catalog `spaces.ts:62-76`** — `getSpaceHideFolders` calls `getSpaces()` on every navigation and the page waits for it (`[subpath]/index.svelte:161`, `admin/[space_name]/[subpath]/index.svelte:241`, `admin/[space_name]/index.svelte:277`); catalog never caches spaces; cxb has a `spaces` store ignored by `RelationshipsPanel.svelte:66`, `MetaPermissionForm.svelte:75`, `tools/query.svelte:68`, `tools/export.svelte:70`. **MED.**
12. **catalog `routes/notifications/index.svelte:68, :216-221`** — one WebSocket event reloads notifications twice (handler + `$newNotificationType` effect); `{#await getAvatar()}` per row (`:490`, up to 100, uncached, unkeyed each at `:472`). **MED.**
13. **catalog ENTRY `:125-139`** — `getRelatedContents` runs on every post view and is discarded (`relatedContent = []`, TODO); `checkUserReaction` (`comments_reactions.ts:167-190`) re-asks for reactions already in `postData.attachments`. **LOW-MED: 2 requests per post.**
14. **catalog `lib/dmart_services/entries.ts:125-155`** — `getMyEntities` sends one unbounded query per space (payload + attachments, `exact_subpath:false`, no limit, no concurrency cap); `streamEntitiesAcrossSpaces` in the same file already has a 6-wide pool. **MED with many spaces.**

### Rendering (6)

15. **cxb `tools/export.svelte:91-111`, `tools/query.svelte:112`** — "Preview" requests `limit: 1_000_000` with payload + attachments into a deeply proxied `$state`, then `Prism.svelte:11-14` `JSON.stringify`s and highlights on the main thread. — Limit to ~100 rows, `$state.raw`, render collapsed. **HIGH: can freeze/OOM the tab.**
16. **cxb `EntryRenderer.svelte:501-512`** — every keystroke in the Form tab deep-clones the whole entry (`$state.snapshot`), parses it and `isDeepEqual`s against the original; `:978` snapshots the whole entry just to pass `.attachments`. — Debounce the dirty check ~250 ms / set a flag. **MED: typing lag on large entries.**
17. **catalog FOLDER `:1081-1101`** — `JSON.stringify(body, null, 2)` twice per card per render to show 150 characters; `filteredContentsDerived` (`:436-501`) copies and sorts everything on any change; unkeyed each at `:1012`. **MED with large bodies.**
18. **cxb `EntryRenderer.svelte:863-865` → `Table2Cols.svelte`** — default tab renders the whole entry recursively with `<svelte:self>` (thousands of nodes for large payloads). — Collapse below depth 2. **MED.**
19. **cxb `ListView.svelte:566-569, 606`** — `isFetching` replaces the whole table with a placeholder on every fetch (rows torn down, rebuilt, faded in); unkeyed each; `cellText` → `getAttributeValue` calls `get(_)`/`get(locale)` per cell (`utils/listViewUtils.ts:24,42,46,50,70`), subscribing/unsubscribing a store each time. **LOW-MED.**
20. **Client-side sorting of one server page** — `catalog dashboard/admin/[space_name]/[subpath]/index.svelte:489-520`, FOLDER `:467-498`, SPACE `applyFiltersAndSort`; `components/DataTable.svelte:484` loops `Array(totalPages)` per render. — Send `sort_by`/`sort_type`. **LOW CPU, fixes a correctness bug (catalog #3).**

### Bundle (7)

21. **catalog `src/i18n/index.ts:4-6`** — `import ar/en/ku from "./*.json"` statically: ~298 kB of the 324 kB `index-*.js` (en 70.5, ar 91.6, ku 135.9 kB minified). — `register("ar", () => import("./ar.json"))` + `waitLocale()`. **HIGH: ~200–230 kB off every cold load.**
22. **cxb `svelte-jsoneditor` imported statically** in `content/index.svelte:18`, `SpaceSubpathItemsSidebar.svelte:12`, `EntryRenderer.svelte:27`, `Attachments.svelte:18`, `RelationshipsPanel.svelte:18`, `PayloadForm.svelte:10`, `ModalCreateAttachments.svelte:4` — pulls `svelte-jsoneditor` (245 kB) + `@codemirror` (410 kB) + lodash-es + fortawesome + 104 kB CSS onto the first Spaces page although the editor only appears in modals/the Entry tab. — One `LazyJsonEditor.svelte` with `{#await import("svelte-jsoneditor")}`; `"text"` instead of `Mode.text`. **HIGH: ~700 kB JS + 104 kB CSS off content pages.**
23. **catalog `vite.config.ts:91-96`** — `manualChunks` sends every node_modules package except flowbite/routify into one 430 kB `vendor` chunk preloaded on every page; it contains `typewriter-editor`/`@typewriter/delta` (defeating the lazy `await import("typewriter-editor")` at `HtmlEditor.svelte:42`), marked and DOMPurify. — Only svelte, tsdmart, axios, svelte-i18n in vendor. **HIGH: ~100–200 kB off first load.**
24. **cxb `ListViewActionBar.svelte:20-23`** — modals imported statically; `ModalCreateEntry` → `PayloadForm` → `HtmlEditor` (typewriter 50 kB + 28 kB) and `MarkdownEditor` (marked 41 kB + dompurify 28 kB + plugins), all linked from `ListView-*.js` for every list. — `{#await import(...)}` inside `{#if open}`. **MED-HIGH: ~250 kB off every list view.**
25. **cxb `vite.config.ts:148-163`** — per-package chunking makes `flowbite-svelte` (274 kB) and `flowbite-svelte-icons` (116 kB) one chunk each containing every component/icon used anywhere; both `_module` chunks import them. — Let rolldown split per route. **MED: ~150–250 kB off first paint.**
26. **cxb `EntryRenderer.svelte:40-50, 58-59`** — `WorkflowDiagram` (pulls `plantuml-encoder` 53 kB), `SchemaDiagram`, every Meta*Form, `RolesExplorer`, `PermissionsExplorer`, `HistoryListView` imported statically. — `import()` per tab when opened (also fixes #3). **MED.**
27. **`catalog/src/app.css:7-10`, `cxb/src/app.css:15-18`** — `@source` scans all of `flowbite-svelte-icons/dist` and `flowbite-svelte/dist`; resulting CSS 262 kB (catalog) / 298 kB (cxb), render-blocking. — Drop the icons `@source`. **LOW-MED, needs measuring.**

### Caching (4)

28. **`Middleware/CxbMiddleware.cs:141-145`, `Middleware/CatalogMiddleware.cs:128-132`** — `UseStaticFiles` without `OnPrepareResponse`: hashed `assets/*` are sent with no `Cache-Control` (only `config.json` gets `no-cache`), so browsers heuristically revalidate; `Program.cs:3084` compresses every response on the fly at the fastest level, 400 kB chunks included; catalog lists `vite-plugin-compression2` but never uses it. — `Cache-Control: public, max-age=31536000, immutable` for `/assets/`, `no-cache` for `index.html`, build-time `.br`/`.gz`. **HIGH on repeat visits (~40 conditional requests → 0) and saves server CPU.**
29. **catalog `routes/_module.svelte:14-19, 115-120`** — `redirectTo` sets `window.location.href` (full reload) for `/` and `/login` after a successful session check and on 401: re-fetches `index.html`, `config.json`, `getProfile`, re-parses ~1 MB of JS. — `$goto`. **MED.**
30. **Startup waterfall** — catalog `main.ts:2,8` imports App statically so `config.json` is requested only after ~1 MB of JS ran; `:16` calls `hydrate()` on a never-SSR'd body (mismatch + fallback mount); cxb `main.ts:5-7` fetches config, then loads the App chunk serially. — `<link rel="preload" href="config.json" as="fetch">`, `Promise.all`, `mount`. **LOW-MED: one round trip on cold start.**
31. **catalog `lib/utils/messagingUtils.ts:179-185`** — full conversations saved to localStorage per partner (11 call sites in `routes/messaging/index.svelte`), synchronous `JSON.stringify` per message, never evicted, not cleared on signout (`stores/user.ts:254-263`). **LOW-MED, also a privacy issue.**

### Leaks (2)

32. **`marked.use(mangle()); marked.use(gfmHeadingId(...))` in component instance scripts** (12 sites: catalog `PostContent.svelte:15-20`, `editors/MarkdownEditor.svelte:10`, `routes/entries/create.svelte:53`, `routes/entries/.../index.svelte:47`, `SchemaTemplateManager.svelte:25`; cxb `editors/MarkdownEditor.svelte:10`, …) — `marked` is one global instance; each component creation adds another layer of extension hooks, so the chain grows for the whole session. — `new Marked(mangle(), gfmHeadingId())` once in a module. **MED: memory and parse time grow steadily.**
33. **cxb `ListView.svelte:73`, `EntryRenderer.svelte:102`** — global stores `currentListView`/`currentEntry` keep closures of destroyed components alive (records, editor state); `ListViewActionBar` can call a stale `fetchPageRecords`. — Clear in `onDestroy`. **LOW.**

### Media (2)

34. **catalog `components/Media.svelte:60-82`** (grid at `components/Attachments.svelte:346-355`) — every image attachment is downloaded at full size as a blob as soon as its card appears; no lazy loading, no thumbnail; blob URLs skip the browser cache; preview downloads the same file again (`Attachments.svelte:168`). — `<img src loading="lazy" decoding="async">` (cookie auth already works per the comment at `:34-37`). **HIGH for photo-heavy entries.**
35. **Images without lazy loading** — 20 of 21 `<img>` in catalog; list avatars render the original at 40 px (`[subpath]/index.svelte:1027`, `notifications/index.svelte:490`, `entries/.../[resource_type]/index.svelte:684`); cxb `renderers/Media.svelte:36` (modal-only). **LOW-MED.**

### Performance — do these first

1. **Stop the cxb rebuild on every list click** (#1, #2, #3): derive the entry fetch from `space_name`/`subpath` only, don't render `<slot/>` during the profile check, build EntryRenderer tabs on open. Cuts cxb request volume per interaction by ~80–90% and removes the cold-load double fetch and the data-asset scans (#4).
2. **Lazy-load the cxb JSON editor and the four modals** (#22, #24): ~950 kB less JS on content pages.
3. **Shrink the catalog first load** (#21, #23): lazy locales + a narrow `vendor` rule, ~300–400 kB off every cold load, and the typewriter lazy import starts working.
4. **Kill the catalog per-item requests** (#5, #6): cached avatars, counts from the attachments already fetched, one state update — a 20-item page goes from ~40–60 requests to ~1–5.
5. **Cache headers + pre-compression on the server** (#28): `immutable` for `/assets/*`, `.br` at build time — repeat visits make no asset requests.

## cxb — full list (40)

Paths under `cxb/`. Spot-checked against the source: #2, #3, #4, #5, #6, #8 (confirmed).

### Bugs (18)

1. `src/components/management/ListView.svelte:419-434` (+ `datatable/RowsPerPage.svelte:19-21`) — changing rows-per-page keeps the current page: on page 10, switching to 100 rows sends offset 900 and the EmptyState (`:576-584`) says "This folder is empty" and hides the pager; the `page` URL param keeps it stuck after reload. Emptying the last page with bulk delete/trash (`ListViewActionBar.svelte:138,168`) does the same. — Reset the page on size change, clamp to `ceil(total/limit)` after deletes, keep the pager when `total>0`.
2. `ListViewActionBar.svelte:121-126` and `Modals/ModalBulkMoveCopy.svelte:74-89` — bulk delete and bulk move send the list's `subpath` for every record; wrong whenever the list is non-exact (Trash page uses `exact_subpath={false}`, folders with `expand_children`). — Use each record's `b.subpath`.
3. `ListViewActionBar.svelte:202-207` — bulk restore does `b.subpath.split("/").slice(3)`; subpaths carry a leading slash, so the destination space comes out as "trash". — Strip the leading `/` and share one helper with `EntryRenderer.svelte:354`.
4. `src/utils/entryManagement.ts:231-233` — bulk trash of a folder sends `entry.subpath.split("/").slice(0,-1).join("-")`: `/a/b` → `-a`, `/a` → `/`. — Send `entry.subpath` unchanged.
5. `Modals/ModalCSVDownload.svelte:50`, `tools/query.svelte:136` — `downloadFile(JSON.stringify(data))` on a `text/csv` response; also copies the list's `offset`/`limit`. — Pass `data` as is, `delete query.offset`, set `limit` explicitly.
6. `content/[space_name]/health_check/[space_name_health]/index.svelte:91` — fetches with `shortname: $params.space_name` ("management") instead of `space_name_health`; `:48-49` read params that don't exist on this route, so the modal always says "Entry does not exist".
7. `ListView.svelte:254-318` — `fetchPageRecords` has no try/finally: any 403/network error leaves `isFetching=true` forever; calls at `:179,:409,:523` are not awaited; no request id, so a slow old response overwrites a newer one; with `delay_total_count` the `total=-1` at `:305` can overwrite the real total ("Showing 1 to -1 of -1").
8. `src/components/Login.svelte:74` — `maxlength={24}` on the password; the server accepts longer. — Remove it.
9. `tools/query.svelte:73-87`, `tools/export.svelte:75-89` — duplicated `buildSubpaths` calls `getChildren(space, shortname)` instead of the full path, so folders two+ levels deep resolve wrongly; fast space switches mix results. — Reuse `getChildrenAndSubChildren` (`src/lib/dmart_services.ts:96-105`).
10. `renderers/EntryRenderer.svelte:150-151,170-189` — `validateMetaForm`/`validateRTForm` are bound but never called on save, so edits skip the validation create enforces; `ticketData` (`:121,:919`) is collected but never sent.
11. `BreadCrumbLite.svelte:27-40,50` — crumbs built once in `onMount` (stale on prop change); each-block keyed by `item.text`, so `/docs/docs` throws `each_key_duplicate`.
12. `src/utils/jsons/list_cols.json:14-17` — column key `owner_shortname` shows `payload.schema_shortname` titled "Schema shortname" (sorting that column sorts by owner); date paths never reach `formatDate` (`listViewUtils.ts:46`), hence raw ISO timestamps in the default list.
13. Sidebar cache keys don't match: `ModalCreateEntry.svelte:341`/`EntryRenderer.svelte:423-429` write `space:/a/b`, `SpacesSubpathItemsSidebar.svelte:37-46` reads `space:/a-b`; new nested folders don't appear until reload. Children capped at 50 with no "load more" (`SpaceSubpathItemsSidebar.svelte:36`).
14. `error.response.data` without `?.` — `ModalCreateEntry.svelte:347`, `content/index.svelte:114`, `forms/PayloadForm.svelte:85,167`, `forms/MetaForm.svelte:162`, `SpaceSubpathItemsSidebar.svelte:144`. — `error?.response?.data ?? error.message`.
15. Unhandled fetches on mount: `tools/index.svelte:24-29`, `tools/info.svelte:45-52`, `query.svelte:66-71,112,132`, `export.svelte:68-73`; `profile.svelte:11-17` `{#await}` with no `{:catch}` and unchecked `records[0]` → blank page on failure.
16. `HistoryListView.svelte:168` — "Next" sets `offset=min(total-limit, offset+limit)` (23 items @10 → offset 13); "1 of 0 pages" when empty (`:134`); errors only logged (`:35-36`).
17. `EntryRenderer.svelte:483-535` — unsaved-changes guard covers only Refresh/`beforeunload`; in-app navigation drops edits silently; `:538` uses deprecated `on:beforeunload`. — Add a Routify `beforeUrlChange` guard.
18. Security-ish: `diagram/SchemaDiagram.svelte:8-13`, `diagram/WorkflowDiagram.svelte:26-29` send schema/workflow content to `www.plantuml.com`; the token is kept in localStorage (`stores/user.ts:59`, `stores/auth.ts:3`) although cookies are sent. `{@html}` uses are DOMPurify-sanitized and `target=_blank` links have `rel=noopener` (fine).

### Leftovers / dead code (5)

19. `SpaceSubpathItemsSidebar.svelte:82-238,278-385` — ~250 lines of space create/edit/delete modals copied from `content/index.svelte`; nothing opens them.
20. Unused: `forms/ConfigForm.svelte`, `utils/metaFormUtils.ts` (7 exports), `utils/timeago.ts`; `listViewUtils.ts` `buildQueryObject`/`applyFolderHiding`/`normalizeSubpath`/`calculateNumberOfPages`/`storeRowsPerPageSetting` (ListView re-implements them inline); `lib/helpers.ts:36-48`; `stores/user.ts:104 switchLocale`; `stores/management/refresh_spaces.ts` (toggled at `App.svelte:138`, no subscriber).
21. User-visible debug text: `routes/index.svelte` ("Welcome to the Svelte App"); `[space_name]/index.svelte:33-35`, `[subpath]/index.svelte:75-78` ("For some reason … params doesn't have the needed info" + JSON dump); `[resource_type]/index.svelte:32-34` ("We shouldn't be here"); `tools/index.svelte:33` "HASH: …" as a heading.
22. Commented-out code (`ListView.svelte:527`, `PayloadForm.svelte:393`, `MetaTicketForm.svelte:207-209`, `export.svelte:41-51`); stale TODO `HomeHeader.svelte:85`; `ListViewActionBar.svelte:51,322-330` `canDelete` never true and its Delete button has no onclick; `EntryRenderer.svelte:481` `isRefreshLoading` never set; `HistoryListView.svelte:42-51,65` unused handlers; `ModalCreateEntry.svelte:313-317` impossible condition.
23. 27 bare `$goto;` statements; the space-card grid copied three times (`content/index.svelte:232-300`, `tools/events/index.svelte:40-74`, `health_check/index.svelte:22+`); three impact modals in `EntryRenderer.svelte:1095-1212`; three pager implementations. — Extract `SpaceGrid`, `ImpactModal`, `Pagination`.

### i18n (5)

24. Only 14 of 77 `.svelte` files use `$_`; 89 of 263 keys are used. Hard-coded: `ListViewActionBar.svelte:266,299-383,413-449`, `ListView.svelte:579-582,674-675`, `EntryRenderer.svelte:569-832` (all tab labels), `ManagementHeader.svelte:35-36,174,180`, `Login.svelte:46-47,90`, every modal/form, most tools pages.
25. en/ar parity is clean (263/263) but `"upload"` is defined twice; keys misspelled `cofrirm_deleting_attachment` (Arabic value still English, `ar.json:175`), `verfication`; 174 keys never referenced (`maqola`, `newsfeed`, `tag_cloud`, `forum`, `wwwimx`, `captcha_chars` — carried over from another app).
26. Display names always `.en` (12 places, e.g. `content/index.svelte:285,289`, `Attachments.svelte:446,454`) although `listViewUtils.ts:21-35` has `localizedDisplayName`; `i18n/index.ts:44` inverted check means new visitors get "en" regardless of `website.default_language`.
27. RTL: 113 physical direction classes vs 19 logical (`Pagination.svelte:44,61` arrows not mirrored, `SpacesSubpathItemsSidebar.svelte:85` `margin-left`, `content/index.svelte:237` `left-2`, `_module.svelte:18` `border-r`); `App.svelte:138` sets `document.dir` but never `<html lang>`; `ManagementHeader.svelte:39` offers "ku" with no ku locale.
28. Dates formatted three ways (`toLocale*String()`, fixed `YYYY-MM-DD HH:mm` in `lib/helpers.ts:1`, raw ISO in lists); `i18n/index.ts:93` date/time formatters never used.

### Accessibility & visual polish (9)

29. Nested interactive elements: Dropdown inside `<Button>` (`ManagementHeader.svelte:162-184`, `HomeHeader.svelte:54-83`, `content/index.svelte:238-268`, `Attachments.svelte:373`); `<button>` inside the SidebarItem `<a>` (`SpacesSubpathItemsSidebar.svelte:89`); `<div>` directly inside `<tr>` (`ListView.svelte:612-645`).
30. Icon-only controls without a name: `ListViewActionBar.svelte:271-287` (clickable spans, not focusable), `Pagination.svelte:42-63` (no aria-label, no `aria-current`), "…"/eye buttons (`content/index.svelte:238`, `Attachments.svelte:373,474`), hamburger (`_module.svelte:57`), eye toggle (`Login.svelte:77`), "×" (`MetaUserForm.svelte:364`, `RoleSelector.svelte:143`).
31. Mouse-only targets with warnings silenced: list rows (`ListView.svelte:608-611`), space/event cards, tools cards (`tools/index.svelte:35-201`), breadcrumb items, health-check entries. — `<a href>`/`<button>`.
32. Dark mode: 24 `bg-white` and 55 `text-gray-600` without `dark:` (`Pagination.svelte:44-61`, `_module.svelte:55`, `profile.svelte:26`, `RoleSelector.svelte:115`, `MetaUserForm.svelte:337,409`, `HistoryListView.svelte:101-104`); `Prism.svelte:22-23` loads two light themes and no dark one.
33. Delete looks different everywhere: `EntryRenderer.svelte:1031-1078` (sm, `bg-red-600!`), `ListViewActionBar.svelte:413-452` (md, `color="red"`), `content/index.svelte:401-403` (blue spinner on red); `text-red-500` vs `-600`; second "Bulk delete" lacks `disabled`; Trash button title says "Delete this entry". — One `ConfirmDeleteModal`.
34. Labels/typos/dead links: "Bulk delete" vs "Bulk Move/Copy/Trash"; "UPLOAD" (`Attachments.svelte:362`), "INFORMATION" (`tools/index.svelte:43`); `FolderForm.svelte` "Remove" vs "×" and invalid `text-gray hover:text-gray`; "entires" (`health_check/[space_name_health]/index.svelte:100,136`); `index.md` typos; dead links `index.md:52` `/presence_usecases`, `HomeHeader.svelte:31` `href="/"` (leaves `/cxb/`).
35. Loading/error/empty states: errors as un-styled bootstrap `alert alert-danger` (`[space_name]/index.svelte:29-31`), Flowbite Alert (`[subpath]/index.svelte:142-146`), `<p style="color:red">` (entry route `:32`, health check `:146`); raw "AxiosError…"; spaces grid has no spinner/empty state; placeholders `style="width: 100vw"` inside the sidebar layout (`ListView.svelte:568,573`) → horizontal scroll.
36. Leftover bootstrap: `ListView.svelte:529-541` (`modal-header`/`btn-close` → empty invisible button), `:666` `form-select`, `Media.svelte:33,36` `mw-100`, `Login.svelte:43` `row`; `BreadCrumbLite.svelte:43` `aria-label="Solid background breadcrumb example"`; selected sidebar item `bg-gray-300 text-white` (`SpacesSubpathItemsSidebar.svelte:83`); folder icons rotated 180° (`:101`).
37. Svelte 4 leftovers: `Media.svelte:7-12` (`export let`, non-reactive props), `SchemaDiagram.svelte:4-5`; Login page `w-1/2` with no mobile stacking.

### Tooling (3)

38. `package.json:10` `svelte-check --no-tsconfig` skips `.ts` files; CI (`.github/workflows/ci.yml:301-323`) runs only build + vitest. — `svelte-check --tsconfig ./tsconfig.json --fail-on-warnings` as a CI step.
39. `tsconfig.json:37` `noImplicitAny: false`; `vite.config.ts:136-148` drops every `a11y*` warning plus `state_referenced_locally`, `non_reactive_update`, `event_directive_deprecated`; no eslint/prettier (mixed indentation).
40. Dead tooling: `cxb/.github/workflows/build.yaml` never runs and copies a non-existent `config.sample.json`; `babel.config.json` references uninstalled presets; `App.svelte:38-134` is a ~100-line route rewriter for `.en/.ar/.ku` pages that don't exist.

## catalog — full list (40)

Paths under `catalog/src/`. CAT = `routes/catalogs/index.svelte`, SPACE = `routes/catalogs/[space_name]/index.svelte`, FOLDER = `routes/catalogs/[space_name]/[subpath]/index.svelte`, ENTRY = `…/[shortname]/[resource_type]/index.svelte`. Spot-checked: #1, #5, #6, #9, #14, #17 (confirmed).

### Bugs (16)

1. FOLDER `:529-534` (used at `:1229`) — "Report" shows `prompt()` then `alert("submitted")` and never calls the API. — Reuse `ReportModal` as SPACE does.
2. SPACE `:45, :114, :666` — `openReportModal` overwrites the `subpath` state that `loadContents` uses; after reporting from a sub-folder, "Load more" pages the wrong folder. — Separate `reportSubpath`.
3. SPACE `:117-118` vs `:505-525`; FOLDER `:467-498`, `:120` — server sorts by shortname asc; "newest first" re-sorts only the 20 loaded; sort change never refetches; FOLDER's "updated" option has no switch case. — Send `sort_by`/`sort_type`, reset offset on change.
4. Search: SPACE `:222` calls the global `searchInCatalog` (every space) but result clicks (`:549-558`) assume the current space; CAT `:284`/SPACE `:275` a failed search sets the page-level `error` that is never cleared; no request token (stale overwrite).
5. SPACE `:642`, FOLDER `:506` — share URLs omit `[resource_type]` (`/catalogs/s/sub/short?resource_type=x` matches no route → 404). — Build `/catalogs/{s}/{sub}/{short}/{rt}`.
6. FOLDER `:132-148` (`:565-572`) — breadcrumb paths lack `/catalogs` and repeat the space crumb; every breadcrumb link is broken.
7. FOLDER `:259` + `:755` — `resolveTotal` falls back to 0; with `RETRIEVE_TOTAL_DEFAULT=false` (server returns −1) every folder shows "empty" and hides the search box. — Branch on `allContents.length`.
8. SPACE `:1354` — tag-filtered Load-more checks `hasMoreItems` instead of `tagFilteredHasMore`; `loadMoreItems` returns early at `:535`.
9. `i18n/index.ts:54` — `JSON.parse(stored || '"en"')` → first-time visitors always get English; `default_language: "ar"` and locale detection (`:62-76`) never apply.
10. ENTRY `:59-61` — `isOwner = $user.shortname === itemShortname` compares with the entry's shortname, not `owner_shortname`; owners never see the attachment delete button (`Attachments.svelte:333`).
11. CAT `:62-103` — per-space stats in one `Promise.all`; one failing space zeroes all stats/tags (`tags.records` null at `:77`). — `Promise.allSettled`.
12. FOLDER `:652`, `:1332` — "Try again" and `onUploadSuccess={loadContents}` run in append mode; after CSV import the next page is appended. — `() => loadContents(true)`.
13. ENTRY — error state (`:323-336`) never shows the message, no retry/back; `:112` shows the raw axios message; after a comment/reaction (`:187,:219,:232`) the whole page reloads with a spinner and loses scroll.
14. Dead controls: SPACE `:746` "New post" has no `onclick`; CAT `:562-585` Category/Tags filters contain only "All" and are never applied; CAT `:525` "Popular" sorts by `updated_at`.
15. Root-absolute links ignore `<base href="/cat/">`: `DashboardHeader.svelte:155`, `routes/home/index.svelte:163-170`, `routes/[...404].svelte:22`; `/community` missing from `PUBLIC_VIEW_ROUTES` (`lib/constants.ts:46`) so anonymous visitors are bounced to login.
16. CAT `:125` uses `encodeURIComponent(record.subpath)` while every other page dash-encodes and ENTRY `:70` decodes dashes; global-search results open the wrong subpath. — One shared `encodeSubpath`/`decodeSubpath`.

### Leftovers / dead code (8)

17. Never imported (~1,650 lines): `components/CreateTemplateModal.svelte` (705), `components/management/SchemaTemplateManager.svelte` (761), `components/forms/primitives/{Field,FieldGroup,Label}.svelte` (173), `lib/ui/modal-sizes.ts`.
18. ENTRY `:137-141` `//TODO fix` forces `relatedContent = []` while `getRelatedContents` still fires every load; `:150-154` commented-out; `:521` passes `$locale` as the fallback argument.
19. SPACE unused: `enhanceItem` (`:294-343`), `getItemIcon` (`:561-576`), `handleCardTagClick` (`:670`); `extractContentTags` (`:465`) refetches tags on every load-more; `class="z"` (`:708`). App-wide: 30 stray `$goto;`, 148 commented-out lines (37 in `routes/dashboard/admin/[space_name]/[subpath]/index.svelte`), commented share block `PostInteractions.svelte:63-77`.
20. 48 `console.log`: `routes/messaging/index.svelte:669,:850` log full chat content; `lib/services/websocket.ts:146` logs the WebSocket URL including `?token=` (dev builds); `routes/surveys/create.svelte:200,:225` "Preview"/"Save Draft" only `console.log("not implemented")` yet are clickable.
21. Helpers copied from `../cxb` that drifted: `lib/downloadFile.ts` never revokes its object URL (cxb fixed it); `lib/fileUtils.ts:10` lacks cxb's basename fix; `lib/uuid.ts` duplicates `generateUuidV4` in `lib/helpers.ts`; `lib/schemaEditorUtils.ts` diverged. — Move to `ui-shared/` like `password-reset` and `query-total`.
22. Duplicates: 13 local `formatDate`/`formatRelativeTime`; 25 files define their own `isRTL` store (i18n already exports `dir`); 24 copies of `@keyframes spin`; toolbar CSS pasted across CAT/SPACE/FOLDER; display-name fallback order differs (`lib/utils/postUtils.ts:3` ar>en, CAT `:135` en first, FOLDER `:368` ignores locale). — `lib/format.ts`, `CatalogToolbar`, `localized()`.
23. Dead files/config: `public/sw.js` never registered (would serve `index.html` cache-first); `static/robots.txt`/`sitemap.xml` point at `localhost:4173` and aren't served; `babel.config.json` references uninstalled presets; `tsconfig.app.json` unused; `delay_total_count` never read; `.gitignore:27` two entries merged; `main.ts:10-16` unused `isHydrating`, `hydrate()` on a body with no SSR markup.
24. Literal space names bypass `lib/constants.ts`: `"management"` ×13 in `routes/dashboard/admin/users.svelte`; `contact-messages.svelte:140` hard-codes `"applications"`/`"contacts"`; ENTRY `:278` hard-codes `"authors"`.

### i18n (6)

25. 42 call sites render a raw key (missing from all three locales): ENTRY `:592,:595` (`post_detail.login_required.login/.cancel`), SPACE `:1405`, `routes/login.svelte:48` (`ThisFieldIsRequired`), `routes/register.svelte:136,:146,:439`, `DashboardHeader.svelte:283` (`menu`, the `|| "Menu"` fallback never triggers). 9 more use `{default: "English"}` with no key (`BreadcrumbNavigation.svelte:35-56`, ENTRY `:376,:482`).
26. Key drift: en 1,637 / ar 1,577 / ku 1,972 keys; 61 en keys missing in ar (`admin_content.bulk_actions.*`), 134 missing in ku (`actions.*`, `delete_confirmation.*`, `catalog_contents.share.*`), 469 only in ku; ar `catalog_contents.results.with_tags` drops `{count}`; fallback locale is ar, so Kurdish users see Arabic.
27. Hard-coded English: FOLDER `:294`, `:381-397` ("N/A", "Just now", "m ago"), `:600-615` ("Import CSV"/"Export CSV"), aria-labels from English template literals (`:682,:1103,:1118,:1201,:1225`), `:1130`; SPACE `:649`, `:1197`; `InteractiveForm.svelte:21,27,35,37` (`:27` uses an English sentence as the key); `PostContent.svelte:246,:268`; `Media.svelte:140`; the 404 page; 190 literal English `aria-label/title/placeholder` + 65 template literals app-wide.
28. Labels built once at script init don't update on `switchLocale`: SPACE `:65-70`, FOLDER `:117-122`, `ReportModal.svelte:26-38`, `routes/home/index.svelte:16-47` (~50 sites). — `$derived`.
29. Dates: SPACE `:593-600` glues number to suffix with no space/plurals ("5ساعات مضت"); four formatting styles; CAT `:262` `toLocaleDateString("")` throws if `$locale` unset. — `Intl.DateTimeFormat`/`RelativeTimeFormat`.
30. RTL: 107 physical `ml/mr/pl/pr` vs 5 logical; 18 `space-x-*` (2 reversed); 245 physical CSS props vs 11 logical patched by 119 `.rtl` overrides; back arrow in `BreadcrumbNavigation.svelte:21-33` doesn't flip.

### Accessibility & visual polish (7)

31. CAT cards: entry count shown twice (members icon `:851-857`, comments icon `:896-910`); owner twice (`:841-844`, `:894`); SPACE `:1303-1323` shows `mediaCount || shareCount` under a bookmark icon.
32. Fake data: `PostHeader.svelte:105` static "🔥 Hot" badge; read time (`:14-24,:98`) from non-existent `content_en/ar/ku` fields → always "1 min read"; status dot (`:33`) always on; `InteractiveForm.svelte:21` hard-coded "YO" avatar.
33. `PostInteractions.svelte:41` like button has no `aria-label`/`aria-pressed`; `:23` `.clickable` counter with no handler; unused `_` import.
34. Dialogs: ENTRY `:557-601` custom overlay with no `role="dialog"`, Escape or focus handling (use `components/Modal.svelte`); 21 native `alert/confirm/prompt` (share copy, `NestedComments.svelte:114`).
35. Clickable cards are `<div role="button">` (CAT `:815`, SPACE `:1123`, FOLDER `:1013`): Space key scrolls, nested buttons, no middle-click/open-in-new-tab; SPACE `:1228-1236` clickable `<span>` tag pill; 51 `svelte-ignore` a11y comments. — `<a href>` cards.
36. ENTRY styles: 55 hex colours, 0 design tokens (`#fafafa` vs `--surface-page`), redefines `.mb-6` (`:680`); SPACE `:1239,:1359` and `PostContent` use Tailwind grays — blocks the dark-theme stub in `app.css:143`.
37. Page context: only 4 routes set `<title>` (`index.html:13` ignores `website.title`); SPACE hero (`:740`) shows the raw shortname; `BreadcrumbNavigation` never renders a trail, only a back button, and `copyLink` (`:8-10`) gives no feedback; the 404 page is unstyled.

### Tooling (3)

38. `yarn lint` broken: ESLint 10 with no `eslint.config.*`, no `eslint-plugin-svelte`/`typescript-eslint`; CI runs `check` only.
39. Strictness: 1,093 explicit `any`; `vite.config.ts:52-60` suppresses every a11y warning; Svelte 4/5 syntax mixed (10 `export let`, 5 `createEventDispatcher`, 15 `on:`, 11 `$:`); `noUnusedLocals`/`noUnusedParameters` off; no i18n parity test.
40. `package.json`: unused `tailwindcss-rtl`, `vite-plugin-compression2`, `tslib`; two markdown pipelines (`mdsvex`, `vite-plugin-svelte-md`) with no `.md` sources; `spank` in runtime deps; an app with `peerDependencies`; `public/config.json` `backend: "http://localhost:8282"` contradicts the vite proxy comment that expects `""` (and breaks the embedded build — item 1 of the top ten).

## Suggested packaging

1. **PR "frontend correctness"** — items 1–7 and 9 above plus catalog #7, #10–#12, cxb #6, #9–#12, #16. Mostly one-to-five-line fixes; each verifiable by hand.
2. **PR "tooling"** — strict `svelte-check --tsconfig` in both, re-enable a11y warnings, add `eslint.config.js` (svelte plugin, `no-console`), an i18n key-parity vitest, bump the four fixable Dependabot packages. This is what stops the rest from coming back.
3. **PR "i18n"** — cxb: route the shell, list view and entry tabs through `$_`; catalog: add the 42 missing keys, fix the locale default, `$derived` labels, `ms/me` logical classes.
4. **PR "polish"** — shared `Pagination`, `ConfirmDeleteModal`, `Loading/Error/Empty`, one `formatDate`; typos; dark-mode tokens; dead code removal (~1,650 lines in catalog, ~250 in cxb).
5. **PR "performance, cxb"** — perf #1–#4 (no rebuild per click, no double slot, lazy tabs, drop the data-asset scan), #22, #24 (lazy editor and modals), #15 (preview limit).
6. **PR "performance, catalog + server"** — perf #5, #6, #8 (avatar cache, counts from attachments, landing-page stats), #21, #23 (lazy locales, narrow vendor), #28 (cache headers + pre-compression), #32 (one `Marked` instance), #34 (lazy images).
