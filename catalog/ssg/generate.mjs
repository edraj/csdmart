#!/usr/bin/env node
/**
 * Static-site generator over dmart content.
 *
 * Queries dmart for the entries under a subpath and writes one real HTML file
 * per entry, with the content IN THE MARKUP. That is the whole point: the
 * catalog SPA pre-renders only a shell today (`render: { ssg: false }` in
 * vite.config.ts, and sanitize.ts notes that "those build-time pages carry no
 * user payload"), so a crawler that does not execute JS sees nothing. A
 * generator that bakes the content is what turns dmart from a headless backend
 * into something that publishes a website.
 *
 * Deliberately NOT routify/spank SSR. This is a client-only Svelte app with
 * `ssr: false` throughout; making ~1,900 files SSR-clean is an open-ended hunt,
 * and nothing about emitting static pages requires it.
 *
 *   node ssg/generate.mjs --space website --subpath /pages --out dist/site \
 *     [--base https://dmart.cc] [--api http://127.0.0.1:5401] [--token-file F]
 *
 * Exit codes: 0 ok, 1 usage/IO, 2 the API refused or returned nothing.
 */
import { mkdir, writeFile, readFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { marked } from "marked";

// ---------------------------------------------------------------- arguments
function parseArgs(argv) {
  const out = {
    space: "website",
    subpath: "/pages",
    outDir: "dist/site",
    api: process.env.DMART_API ?? "http://127.0.0.1:5401",
    base: process.env.SITE_BASE ?? "",
    tokenFile: process.env.DMART_TOKEN_FILE ?? "",
    limit: 500,
  };
  for (let i = 0; i < argv.length; i++) {
    const [k, inline] = argv[i].split("=");
    const take = () => inline ?? argv[++i];
    switch (k) {
      case "--space": out.space = take(); break;
      case "--subpath": out.subpath = take(); break;
      case "--out": out.outDir = take(); break;
      case "--api": out.api = take(); break;
      case "--base": out.base = take(); break;
      case "--token-file": out.tokenFile = take(); break;
      case "--limit": out.limit = Number(take()); break;
      default:
        throw new Error(`unknown argument: ${k}`);
    }
  }
  return out;
}

// ------------------------------------------------------------------ fetching
async function fetchEntries(cfg) {
  const headers = { "Content-Type": "application/json" };
  // A token is optional on purpose: a site whose content is genuinely public
  // should be generatable through /public/query with no credential at all.
  // When one is supplied we use /managed/query, which is what an
  // unpublished-drafts build needs.
  let scope = "public";
  if (cfg.tokenFile) {
    const token = (await readFile(cfg.tokenFile, "utf8")).trim();
    headers.Authorization = `Bearer ${token}`;
    scope = "managed";
  }
  const res = await fetch(`${cfg.api}/${scope}/query`, {
    method: "POST",
    headers,
    body: JSON.stringify({
      type: "search",
      space_name: cfg.space,
      subpath: cfg.subpath,
      search: "",
      limit: cfg.limit,
      retrieve_json_payload: true,
      sort_by: "shortname",
    }),
  });
  if (!res.ok) throw new Error(`${scope}/query returned HTTP ${res.status}`);
  const body = await res.json();
  if (body.status !== "success") {
    throw new Error(`${scope}/query failed: ${body.error?.message ?? "unknown"}`);
  }
  return body.records ?? [];
}

// ------------------------------------------------------------------ rendering
/**
 * Mermaid fences become `<pre class="mermaid">` rather than rendered SVG.
 *
 * Rendering diagrams at build time would need a headless browser; mermaid's
 * engine pulls ELK (1.4 MB) and cytoscape behind it. The website already
 * solved this with a cached dynamic import that loads the engine only on pages
 * that draw something (website/src/lib/mermaid.ts) — so emit the source in the
 * shape that helper expects and let it hydrate. The prose around the diagram is
 * still in the markup, which is what crawlers need.
 */
export function configureMarked() {
  const renderer = new marked.Renderer();
  const fence = renderer.code.bind(renderer);
  renderer.code = (token) => {
    const lang = token.lang ?? "";
    if (lang === "mermaid") {
      return `<pre class="mermaid">${escapeHtml(token.text)}</pre>\n`;
    }
    return fence(token);
  };
  return { renderer, gfm: true };
}

function escapeHtml(s) {
  return String(s)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

/** First language-keyed value, falling back across the usual suspects. */
function pickLang(dict, fallback = "") {
  if (!dict || typeof dict !== "object") return fallback;
  return dict.en ?? dict.ar ?? dict.ku ?? Object.values(dict)[0] ?? fallback;
}

/**
 * True when the body already opens with its own top-level heading.
 *
 * Without this the layout's <h1> and the document's own `# Title` both render
 * and the page says its name twice — which is also what a crawler reads. The
 * content owns its heading structure; the layout only supplies one when the
 * content didn't.
 */
export function hasLeadingHeading(body, contentType) {
  const head = String(body ?? "").trimStart();
  if (!head) return false;
  if (contentType === "markdown") {
    // ATX (`# x`) or setext (`x` underlined with ===) — both are an h1.
    return /^#\s/.test(head) || /^[^\n]+\n=+\s*(\n|$)/.test(head);
  }
  if (contentType === "html") return /^<h1[\s>]/i.test(head);
  return false;
}

/** URL path for an entry: slug when present, shortname otherwise. */
export function pathFor(record) {
  const a = record.attributes ?? {};
  // Shortnames cannot contain hyphens (the API enforces
  // ^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{1,64}$), so a hyphenated URL can only come from
  // slug. That makes slug load-bearing for any site with existing URLs to
  // preserve, not a nicety.
  const slug = (a.slug ?? "").trim();
  return slug || record.shortname;
}

export function renderPage(record, cfg, markedOpts) {
  const a = record.attributes ?? {};
  const title = pickLang(a.displayname, record.shortname);
  const description = pickLang(a.description, "");
  const path = pathFor(record);
  const canonical = cfg.base ? `${cfg.base.replace(/\/+$/, "")}/${path}` : "";
  const payload = a.payload ?? {};
  const body = typeof payload.body === "string" ? payload.body : "";

  let content;
  switch (payload.content_type) {
    case "markdown":
      content = marked.parse(body, markedOpts);
      break;
    case "html":
      // Taken as-is. The author is an authenticated editor, not a visitor, and
      // re-sanitizing here would strip the markup they deliberately wrote.
      // Worth stating because it IS a trust boundary: whoever can write an
      // entry in this space can put script on the published site.
      content = body;
      break;
    case "text":
      content = `<pre>${escapeHtml(body)}</pre>`;
      break;
    default:
      content = "";
  }

  const meta = [
    `<meta charset="utf-8">`,
    `<meta name="viewport" content="width=device-width,initial-scale=1">`,
    `<title>${escapeHtml(title)}</title>`,
    description ? `<meta name="description" content="${escapeHtml(description)}">` : "",
    canonical ? `<link rel="canonical" href="${escapeHtml(canonical)}">` : "",
    `<meta property="og:type" content="article">`,
    `<meta property="og:title" content="${escapeHtml(title)}">`,
    description ? `<meta property="og:description" content="${escapeHtml(description)}">` : "",
    canonical ? `<meta property="og:url" content="${escapeHtml(canonical)}">` : "",
    `<meta name="twitter:card" content="summary">`,
  ].filter(Boolean).join("\n  ");

  const heading = hasLeadingHeading(body, payload.content_type)
    ? ""
    : `    <h1>${escapeHtml(title)}</h1>\n`;

  return `<!doctype html>
<html lang="en">
<head>
  ${meta}
</head>
<body>
  <main>
${heading}${content}
  </main>
</body>
</html>
`;
}

export function renderSitemap(records, cfg) {
  const base = cfg.base.replace(/\/+$/, "");
  const urls = records.map((r) => {
    const loc = `${base}/${pathFor(r)}`;
    const lastmod = (r.attributes?.updated_at ?? "").slice(0, 10);
    return `  <url>\n    <loc>${escapeHtml(loc)}</loc>${
      lastmod ? `\n    <lastmod>${lastmod}</lastmod>` : ""
    }\n  </url>`;
  });
  return `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${urls.join("\n")}\n</urlset>\n`;
}

// ---------------------------------------------------------------------- main
async function main() {
  const cfg = parseArgs(process.argv.slice(2));
  const markedOpts = configureMarked();

  const records = await fetchEntries(cfg);
  if (records.length === 0) {
    // Writing an empty site over a good one is the worst outcome available
    // here, so refuse rather than "succeed" with nothing.
    console.error(
      `ssg: ${cfg.space}${cfg.subpath} returned 0 entries — refusing to emit an empty site`,
    );
    process.exit(2);
  }

  for (const r of records) {
    const file = join(cfg.outDir, pathFor(r), "index.html");
    await mkdir(dirname(file), { recursive: true });
    await writeFile(file, renderPage(r, cfg, markedOpts), "utf8");
    console.log(`  ${file}`);
  }

  if (cfg.base) {
    const sm = join(cfg.outDir, "sitemap.xml");
    await writeFile(sm, renderSitemap(records, cfg), "utf8");
    console.log(`  ${sm}`);
    const rb = join(cfg.outDir, "robots.txt");
    await writeFile(rb, `User-agent: *\nAllow: /\nSitemap: ${cfg.base.replace(/\/+$/, "")}/sitemap.xml\n`, "utf8");
    console.log(`  ${rb}`);
  } else {
    // A sitemap needs absolute URLs, so it cannot be emitted without --base.
    console.warn("ssg: no --base given — skipping sitemap.xml and robots.txt");
  }

  console.log(`ssg: wrote ${records.length} page(s) to ${cfg.outDir}`);
}

// Only run when invoked directly, so the renderers above stay importable by tests.
if (import.meta.url === `file://${process.argv[1]}`) {
  main().catch((e) => {
    console.error(`ssg: ${e.message}`);
    process.exit(e.message.includes("query") ? 2 : 1);
  });
}
