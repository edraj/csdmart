# Catalog SSG — publishing a static site from dmart content

Design note for making `/cat` emit a real static website from entries held in
dmart, with **dmart.cc as the reference migration**.

**Status: built.** The Node prototype (`catalog/ssg/generate.mjs`) proved the
approach and has been replaced by `dmart website build` in the binary; the
open questions at the end were decided on 2026-10-08 (see "Decided" below).
The operator guide is `docs/website.md`. This note keeps the design record.

Context: this closes the one objection that otherwise ends every CMS
comparison. Today dmart is a headless backend — `PayloadHandler.RendersInline`
deliberately refuses to serve `text/html`
(`Api/Managed/PayloadHandler.cs:155-168`), so dmart hands you content and
something else has to render it. With this, "dmart publishes your website" is
a true statement.

---

## What already exists (more than I expected)

| Piece | State |
|---|---|
| Router | Routify 3 (`@roxi/routify ^3.0.0-next.288`) |
| Prerenderer | `spank ^2.2.4` installed, `yarn ssg` → `npx spank` |
| Markdown → component | **`vite-plugin-svelte-md` already a dependency**, and `.md` is already in the svelte plugin's `extensions` (`catalog/vite.config.ts`) |
| Content routes | `routes/catalogs/[space_name]/[subpath]/[shortname]/[resource_type]/index.svelte` |
| Markdown rendering | `marked` + GFM heading IDs + DOMPurify (`MarkdownEditor.svelte`) |
| Mermaid | dynamic import, cached (`website/src/lib/mermaid.ts`) — pattern to reuse |

**What stops it working today:** one line.

```ts
render: { ssg: false, ssr: false },   // catalog/vite.config.ts:34
```

And the consequence is stated outright in the codebase:

> *"those build-time pages carry no user payload, and the browser re-renders and
> sanitizes on hydration"* — `catalog/src/lib/utils/sanitize.ts`

So pre-rendered output is a **shell**. A crawler that doesn't execute JS sees
nothing, which is exactly the SEO hole.

---

## The dmart.cc content, measured

Before assuming it's a good first target, I counted it. 14 pages, 8,046 lines of
markup:

| | |
|---|---|
| Svelte components used **inside content** | **none, on any page** |
| Distinct CSS classes per page | 7–15 |
| Structure | tables (3–5/page), code blocks (up to 152), mermaid (0–3) |

That is the good news, and it decides feasibility: the content is semantic HTML
over a small, consistent class vocabulary with **nothing bespoke to preserve**.
Tables → GFM tables. Mermaid → ` ```mermaid ` fences. Code → fenced blocks.
It converts to markdown without loss.

**The one real cost:** each page currently has its *own* layout. Moving to
stored content means accepting a **uniform page template**, or keeping layout
in code and moving only prose. That is the decision this note needs answered
(see Decision 1).

---

## Two architectures

### (A) Turn Routify SSG/SSR on

`render: { ssg: true, ssr: true }`, make content loading SSR-safe, run `spank`
against a live dmart at build time.

- **For:** least bespoke; uses the tool already installed as intended.
- **Against:** this is a client-only codebase (`ssr: false` everywhere) with
  `window` reachable at import time — `sanitize.ts` already documents hitting
  the no-DOM case. Making it SSR-clean is an unbounded hunt through 1,884 files.

### (B) Build-time content export, then static compile — **recommended**

A small script queries dmart, writes each entry as a markdown/JSON file into a
generated directory, and the existing `vite-plugin-svelte-md` pipeline compiles
them as routes. Content is baked at build; no SSR required.

- **For:** no SSR hazards; deterministic and diffable (you can read the
  generated tree); works offline from a content snapshot; the export step is
  ~150 lines against `/public/query`.
- **Against:** a generated-routes directory to gitignore, and a build that
  needs dmart reachable (or a committed snapshot).

I recommend **(B)**, and I'd be cautious about anyone's estimate for (A) —
"make a client-only Svelte app SSR-clean" is the kind of task that is 80% done
for a long time.

---

## Scope of the feature

Beyond baking content in, a static *site* needs what WordPress gives you free:

1. **Per-entry `<head>`** — `<title>`, description, canonical, Open Graph,
   Twitter card. From `displayname`/`description` (both already per-language)
   and the space's `primary_website` (`Space.PrimaryWebsite`, in the space meta).
2. **`sitemap.xml`** — from the same query that drives the export, so it cannot
   drift from the pages.
3. **`robots.txt`.**
4. **Stable URLs.** `Entry.Slug` exists (`Dmart.Models/Core/Entry.cs:27`) and is
   currently unused by the catalog. Slug-when-present, shortname otherwise.
5. **Mermaid + code highlighting** at build time where possible, so the first
   paint isn't blocked on a 1.4 MB ELK download. The website's cached
   dynamic-import pattern is the fallback.
6. **i18n.** `routesDir` already declares a `lang-ar` variant; `displayname` and
   `description` are per-language dicts. Emit one tree per language and
   `hreflang` them.

Explicitly **out of scope** for a first cut: image variants/`srcset`, menus,
comment moderation, incremental rebuilds.

---

## Decisions needed

**1. Uniform template, or per-page layout? — RESOLVED: uniform.**
The existing template mechanism does **not** answer this. `TemplateEditor.svelte`
loads entries from a space's `/templates` subpath and substitutes
`{{field:type}}` placeholders with values (`TemplateEditor.svelte:38-40`,
`lib/dmart_services/templates.ts:28`) — that is *content boilerplate*, not page
layout, and bending it into layout duty would be inventing a mechanism rather
than reusing one. So the first cut uses one layout, as originally leaned.
Original options, kept for the record:
   - **(a) Uniform template** — all content renders through one layout. Honest,
     simple, and the real test of "a CMS publishes your site". dmart.cc would
     look more uniform than it does now.
   - **(b) Hybrid** — prose moves into dmart, per-page layout stays in code as a
     template chosen per entry (the existing `TemplateEditor` and
     `dashboard/templates` route suggest a template concept already exists and
     is worth reading before deciding).
   - My lean: **(a)** for the first cut, because (b) risks reproducing the
     current site with extra steps and proving nothing.

**2. Does the build require a live dmart,** or a committed content snapshot?
   Snapshot is friendlier to CI and keeps the website repo buildable offline;
   live is less to keep in sync. I lean snapshot, exported by a command.

**3. Where does the generator live?** In `catalog/` (ships with dmart, benefits
   every deployment) or in `website/` (bespoke to dmart.cc)? Option (b) of our
   earlier discussion was "in catalog, so the work compounds" — I'd hold to
   that, with `website/` consuming it.

**4. Is dmart.cc the right first target at all?** It is a *documentation* site,
   so it exercises tables/code/diagrams hard and menus/images/comments not at
   all. That's a narrow first proof. A blog-shaped target would exercise more of
   what WordPress users actually need. dmart.cc's advantage is that we own it
   and the content is already written.

---

## Suggested sequencing

1. Read the existing template mechanism (`TemplateEditor.svelte`,
   `routes/dashboard/templates`) — it may already answer Decision 1b.
2. Spike the export script against one dmart.cc page converted by hand; confirm
   a baked page renders with content in the HTML source (`curl | grep`), not
   after hydration. **This is the go/no-go.**
3. Then: `<head>` generation, sitemap, slugs.
4. Then: the content migration for the remaining 13 pages.
5. Keep `render.ssg` behind a flag until step 2 passes, so `/cat` as an admin
   SPA is never regressed.

Step 2 is deliberately the smallest thing that falsifies the whole approach.

---

## Not requested, but noted

The `role`-vs-`roles` workflow-schema defect and the other items from
`packs/PLAN.md` are unrelated to this and stay parked there.

---

## Spike result — the go/no-go passed

Run against a local dmart on SQLite (v1.5.17) holding one page entry
(`website` space, `/pages`, `content_type: markdown`) carrying a GFM table, a
fenced code block, a mermaid fence and links. Generated with
`catalog/ssg/generate.mjs`, served by a plain static file server, fetched with
`curl` so **no JavaScript ran**:

```
HTTP 200, 2393 bytes
title       : Data model
description : Entries, payloads, attachments and history — and the subpath r…
canonical   : https://dmart.cc/data-model
body words  : 124          ← content is in the markup, not hydrated
<table>     : 1
<pre class="mermaid"> : 1
```

That is the whole hypothesis confirmed: dmart content can be emitted as static
HTML with the prose in the source, which is what the SPA's shell-only
pre-render could never do. Also emitted: `sitemap.xml` with absolute URLs and
`lastmod` from `updated_at`, and `robots.txt`.

Two defects the spike caught, both now fixed and covered by tests:

- **The title rendered three times** — the layout injected an `<h1>` while the
  markdown already opened with `# Data model`. The layout now supplies a
  heading only when the content has none (`hasLeadingHeading`), and an `h2`
  opener correctly still gets one.
- **A bare-dot URL** for attachments with no file extension (found while
  porting the markdown editor's media insertion, same root cause: tsdmart
  appends `.${ext}` unless `ext` is literally `null`).

## Finding: shortnames cannot contain hyphens

`data-model` is rejected: `^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{1,64}$`. Every dmart.cc URL
is hyphenated (`/data-model`, `/query-search`, `/entity-lifecycle`), so URLs
can only keep their current shape via `Entry.Slug` — which **is** accepted with
hyphens and is verified working end to end here.

The generator therefore prefers `slug` and falls back to `shortname`
(`pathFor`), which leaves the choice per page:

| | URL | Cost |
|---|---|---|
| shortname only | `/data_model` | existing inbound links and accumulated SEO break unless redirects are added |
| shortname + slug | `/data-model` | none — the mechanism is built and tested |

Underscored URLs are a legitimate choice, and the owner has said they are
acceptable; the note here is only that keeping hyphens is free, and that
changing live URLs is the one irreversible part of this migration.

## What remains

3. `<head>` generation — **done** (title, description, canonical, OG, Twitter).
   Sitemap and robots.txt — **done**.
4. **The content migration for dmart.cc's 14 pages.** Not started. The pages
   are semantic HTML over a 7–15 class vocabulary with no Svelte components, so
   an HTML→markdown converter is viable; it is the bulk of the remaining work
   and wants its own review.
5. Keeping `render.ssg` behind a flag — **moot**: the generator is a standalone
   Node tool and never touches the SPA's render mode, so `/cat` as an admin SPA
   cannot regress.

Deliberately still out of scope: image variants/`srcset`, menus, comment
moderation, incremental rebuilds, and build-time mermaid rendering (the fence is
emitted as `<pre class="mermaid">` for the website's existing cached
dynamic-import helper to hydrate).

---

## The `website` space (migration landed)

dmart.cc's 14 pages now exist as dmart content, converted by
`tools/website-migrate/convert.py`. **Moved 2026-10-09:** the space is now
the `website` pack in [edraj/website](https://github.com/edraj/website), and
the converter and `seed/spaces/website` left this repository with it
(dmart.cc is built there by a Routify app from the public API).

**Fidelity, measured rather than asserted.** The converted markdown reproduces
the source's document structure exactly:

| | source | converted |
|---|---:|---:|
| tables | 41 | **41** |
| mermaid diagrams | 14 | **14** |
| `h2` | 102 | **102** |
| `h3` | 128 | **128** |

Per-page word counts land within ~1% (api-docs 2357 → 2357, settings 1328 →
1330, query-search 3652 → 3661; the small surplus is markdown's own table
pipes and fence markers). The only deliberate loss is the text inside
copy-to-clipboard `<button>` subtrees — UI affordances, not content.

**What is lost, plainly:** per-page visual treatment. `endpoint`, `method`,
`plugin-card` and friends are presentation, and one template cannot reproduce
them. The api-docs page feels it most — 60 endpoint blocks become headings plus
code. The content survives; the styling does not.

### Verified end to end

```
dmart seed files-only   → website/ laid down from the binary (31 files)
dmart seed db-only      → 15 entries (14 pages + folder), 0 failed
ssg/generate.mjs        → 14 pages, 41 tables, 14 diagrams, canonical on each
```

`dmart seed db-only` orders this correctly on its own: AdminBootstrap runs
first (creating the `dmart` user), then the import. Importing into a database
that has never been bootstrapped fails every row on
`FOREIGN KEY constraint failed` — `owner_shortname` references `users`.

### Public access is NOT enabled by seeding, deliberately

Seeding the content grants nothing. On a fresh install:

```
POST /public/query  {space_name: "website"}   → total 0     (anonymous)
POST /managed/query {space_name: "website"}   → total 14    (admin)
```

`AdminBootstrap` ships the `world` permission with `subpaths: {}` — inert — and
`docs/permissions.md:100-102` states that once it exists, its scope "belongs to
the operator and bootstrap never repairs, widens or resets" it. Pre-scoping it
from seed would fight a deliberate design decision, and would mean every fresh
dmart install serves a public website by default. That is the operator's call,
not a default.

Enabling it is one call, through the API rather than SQL (a raw `UPDATE` leaves
`query_policies` ungenerated and the authz cache stale —
`docs/permissions.md:96-98`):

```bash
curl -X POST localhost:8000/managed/request \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' -d '{
  "space_name":"management","request_type":"update",
  "records":[{"resource_type":"permission","subpath":"/permissions","shortname":"world",
    "attributes":{"subpaths":{"website":["__all_subpaths__"]},
                  "resource_types":["content","folder"],
                  "actions":["query","view"],"conditions":["is_active"]}}]}'
```

Verified: after that call, `/public/query` returns the pages and the generator
runs with **no token at all**.

### Two open questions this does not answer

**1. Serving it from dmart (`/website`).** The generator writes static files;
nothing yet serves them. `/cxb` and `/cat` are precedent — embedded SPAs behind
a configurable URL prefix — so a `/website` prefix fits. The unresolved part is
**regeneration**: content edited in dmart does not change a site that was
generated at build time. Options, none free: regenerate on demand via a CLI
command; a hook plugin that regenerates on write to the space; or render per
request, which re-opens exactly the XSS question `PayloadHandler` closed.
Worth deciding before building it.

**2. Drift.** dmart.cc currently has two sources of truth: the Svelte pages in
`edraj/website` and this seeded markdown. They will diverge. The clean
resolution is to make the seed authoritative and retire the Svelte pages so the
site builds from dmart — but that retires 8,046 lines of working markup and is
a decision for the site's owner, not a side effect of this migration.

---

## Decided (2026-10-08)

1. **Public by default: no.** Seeding still grants nothing; the operator
   widens `world` with the one API call above. The build reads as anonymous,
   so that same switch decides what the site contains.
2. **Serving and regeneration: a CLI command.** `dmart website build` renders
   into `WEBSITE_DIR/builds/<stamp>/` and swaps `WEBSITE_DIR/current`; the
   server serves it under `WEBSITE_URL` (`/website`). Not a hook on write,
   which would publish an edit in progress, and not per-request rendering,
   which would re-open the XSS question `PayloadHandler` closed.
3. **Drift: the seed becomes the only source.** The Svelte pages in
   `edraj/website` are retired after dmart.cc is cut over to the dmart-built
   site and checked. The per-page styling loss is accepted.
4. **The home page** (never part of the 14) is a `landing_page` entry at
   `/site/home`: its text is content, its layout and figure ship with the
   template. Navigation and footer are `/site/config`.
