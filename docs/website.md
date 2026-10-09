# Publishing a website from dmart

`dmart website build` turns the content of a space (by default `website`) into
a static site, and the server serves it under `WEBSITE_URL` (default
`/website`). Pages are real HTML with the text in the markup, so crawlers and
link previews see the content without running JavaScript.

A ready-made `website` space is the **`website` pack** in
[edraj/website](https://github.com/edraj/website): dmart.cc's 14 documentation
pages, its home page and its navigation, with the schemas below and a
`website_public` role for opening it to anonymous readers. Install it with that
repository's `pack/install.sh`; it is no longer part of `dmart seed`.

dmart.cc itself is built the other way this content model supports: by a
separate frontend (Svelte + Routify) that reads the same space through the
public API at build time. `dmart website build` is the zero-tooling path, with
nothing to install beyond dmart; that repository is the bring-your-own-frontend
path.

## The content model

| Where | What | Shape |
|---|---|---|
| `/pages/<shortname>` | One page each. The URL is the entry's `slug`, or its shortname when it has none. | `content_type` `markdown`, `html` or `text` |
| `/site/config` | Brand, header links, the docs sidebar, the footer, `base_url`. | JSON, schema `site_config` |
| `/site/home` | The landing page's text: hero, stats, sections. | JSON, schema `landing_page` |

Both schemas live in the space's `/schema` folder and describe every field.

- **Docs or standalone.** A page listed in `site/config`'s `nav` renders with
  the sidebar; any other page renders on its own (dmart.cc's Features and Why
  pages).
- **Links.** Content links to other pages the way it always has, with
  root-relative paths like `/data-model` and `/`. The build rewrites links that
  name a page of the site so they point to wherever the site is mounted. Other
  paths are left alone.
- **Shortnames can't contain hyphens**, but URLs often do. Use `slug` to keep a
  URL such as `/data-model`.
- **Drafts.** An inactive entry (`is_active: false`) is a draft. It isn't
  published, and nav items that point at it are dropped with a warning.
- **The landing page's look** (hero layout, icons, the architecture figure)
  comes from the template. The entry holds only text. `icon` and `figure`
  name files the template ships (`WebsiteTemplate/icons/*.svg`,
  `WebsiteTemplate/figures/*.svg`); an unknown name renders nothing.
- **Section kinds:** `cards`, `steps`, `figures` (measured values),
  `explainer`, `prose` and `banner`.
- **The `explainer` section** plays an animated figure one scene at a time,
  with the section's `items` as the scene captions, in order. The built-in
  `explainer` figure tells dmart's story in five scenes. Each scene runs six
  seconds, and the player starts when the figure scrolls into view, pauses
  on request, and jumps to a scene when its caption is clicked. With reduced
  motion it shows each scene's finished frame; without JavaScript it shows
  the last scene and all the captions. A figure of your own marks each scene
  as a `<g class="sc">`, and its animations run from an offset state to the
  element's resting pose, so a scene with animations off still reads.

## Making the space public

Loading the space publishes nothing. The build reads **as an anonymous
visitor**, so it sees exactly what the `world` permission lets anonymous
readers see. Until an operator grants that, the build refuses to run:

```
website: space 'website' has nothing an anonymous visitor can read under /pages or /site — refusing to publish an empty site.
```

This is deliberate. A fresh install shouldn't serve a public website by
default, and one switch should control both whether the API serves this
content to strangers and whether the site contains it.

With the `website` pack, that switch is `pack/install.sh --public`: it adds the
pack's `website_public` role (query and view of active entries in `website`,
nothing else) to the anonymous user, beside `world`.

Without the pack, widen `world` itself. Use the API rather than SQL (see
`docs/permissions.md`):

```bash
curl -X POST localhost:5099/managed/request \
  -H "Authorization: Bearer $ADMIN_TOKEN" -H 'Content-Type: application/json' -d '{
  "space_name":"management","request_type":"update",
  "records":[{"resource_type":"permission","subpath":"/permissions","shortname":"world",
    "attributes":{"subpaths":{"website":["__all_subpaths__"]},
                  "resource_types":["content","folder"],
                  "actions":["query","view"],"conditions":["is_active"]}}]}'
```

`subpaths` replaces the whole map. If other spaces are already public, keep
them in it.

## Building

```bash
dmart website build [--space website] [--base https://example.com] \
                    [--mount /website] [--out DIR] [--template DIR]
```

| Option | Default | |
|---|---|---|
| `--space` | `website` | The space to publish. |
| `--base` | `site/config` `base_url` | Public origin. It's needed for canonical links, `og:url`, `og:image` and `sitemap.xml`. Without one those are left out, with a warning. |
| `--mount` | `WEBSITE_URL` | The URL path links are built for. See "Serving a site at a domain's root" below. |
| `--out` | `WEBSITE_DIR` | The output root. |
| `--template` | built in | A directory with your own `layout.html` and assets. |

Content goes live only when an operator rebuilds. That keeps an edit in
progress from going live by accident, and keeps rendering out of the request
path. The running server picks up a new build on its next request; it doesn't
need a restart.

### Output layout

```
WEBSITE_DIR/
  current              the name of the live build (one line)
  builds/
    20261008T101500123Z/
      index.html       the landing page
      <slug>/index.html
      404.html
      robots.txt
      sitemap.xml      when there is a base URL
      _site/           site.css, site.js, favicon.svg, og-card.png
```

`current` is replaced with a rename, so the server always serves either the
old build or the new one, never a mix. The two newest builds are kept. To roll
back, write the previous build's name into `current`.

When the CLI and the server run as different OS users, set `WEBSITE_DIR`
explicitly. Otherwise each resolves its own `~/.dmart/website`.

## Serving

`WebsiteMiddleware` serves the live build under `WEBSITE_URL`:

- `/website/data-model` and `/website/data-model/` serve
  `data-model/index.html`. `/website` serves the landing page.
- A miss returns the build's `404.html` with status 404. Before the first
  build, the response is a plain 404 rather than the API's JSON envelope.
- Pages are sent with `Cache-Control: no-cache` and an ETag, so browsers
  revalidate and a rebuild shows up straight away. Assets are versioned with
  `?v=<hash>`, so they can be cached.
- `WEBSITE_URL=/` (or empty) turns serving off. The site is served from disk
  before routing, so at the root it would shadow the API.

### Serving a site at a domain's root

To make a site such as dmart.cc answer at its root, keep `WEBSITE_URL` at
`/website`, build with `--mount /`, and map the root onto the prefix in the
reverse proxy:

```caddy
dmart.cc {
    rewrite * /website{uri}
    reverse_proxy 127.0.0.1:5099
}
```

`--mount /` makes the build emit `/data-model` and `/_site/site.css`, and the
proxy turns those into `/website/data-model` and `/website/_site/site.css`.
Canonical URLs come from `--base` plus the mount, so pass
`--base https://dmart.cc`.

## What a page can and cannot run

- **Markdown** is rendered with raw HTML disabled, and the "generic
  attributes" extension is off. Link targets are restricted to `http(s)`,
  `mailto`, `tel` and relative URLs; anything else becomes `#`. A markdown
  author can't put script on the page.
- **`html` pages** are published as written. Their author has write access to
  the space, and re-sanitizing would strip markup they wrote on purpose.
  That's a trust boundary. What contains it is the site's
  Content-Security-Policy: `script-src 'self' https://cdn.jsdelivr.net` with
  no `'unsafe-inline'`, so inline `<script>`, `on*=` handlers and
  `javascript:` URLs don't run. The site shares an origin with the admin UI,
  which is why that policy matters.
- **Diagrams.** A ```` ```mermaid ```` fence becomes `<pre class="mermaid">`.
  The page then loads mermaid 12.0.0 from jsdelivr, pinned with a
  subresource-integrity hash, and only pages that contain a diagram load it.
  At 5.6 MB, the engine is too large to embed in the binary.
- **Fonts** come from Google Fonts. The policy allows `fonts.googleapis.com`
  and `fonts.gstatic.com`.

## Your own template

`--template DIR` replaces the built-in look. The directory needs
`layout.html`. These are optional: `site.css`, `site.js`, `favicon.svg`,
`og-card.png` (copied to `_site/`), plus `icons/*.svg` and `figures/*.svg`
(inlined where the landing page names them). `layout.html` is filled in by
placeholder:

| Placeholder | Value |
|---|---|
| `{{head}}` | charset, viewport, title, description, canonical, Open Graph and Twitter tags |
| `{{assets}}`, `{{version}}` | the `_site` URL, and a content hash for `?v=` |
| `{{scripts}}` | the mermaid tag on pages with diagrams, otherwise empty |
| `{{body_class}}` | `page-home`, `page-doc` or `page-standalone` |
| `{{home}}`, `{{brand}}` | home URL and brand text |
| `{{header_links}}` | `<a class="header-link">…`, the current one with `aria-current="page"` |
| `{{sidebar}}` | doc pages only: `<aside class="sidebar" id="sidebar">…` |
| `{{main}}` | the page itself |
| `{{footer}}` | `site/config` `footer`, rendered |
| `{{lang}}` | `en` |

The HTML each placeholder carries is pinned by
`dmart.Tests/Unit/Services/WebsiteRendererTests.cs`.
