import { describe, expect, it } from "vitest";
import {
  configureMarked, hasLeadingHeading, pathFor, renderPage, renderSitemap,
} from "./generate.mjs";

const cfg = { base: "https://dmart.cc", outDir: "out" };
// The real options, including the mermaid renderer override — passing a bare
// { gfm: true } here would test a configuration the generator never uses.
const opts = configureMarked();

function rec(attrs = {}, shortname = "data_model") {
  return { shortname, attributes: attrs };
}

describe("pathFor", () => {
  it("prefers the slug, because shortnames cannot contain hyphens", () => {
    expect(pathFor(rec({ slug: "data-model" }))).toBe("data-model");
  });

  it("falls back to the shortname when there is no slug", () => {
    expect(pathFor(rec({}))).toBe("data_model");
    expect(pathFor(rec({ slug: "   " }))).toBe("data_model");
  });
});

describe("hasLeadingHeading", () => {
  it("detects an ATX heading", () => {
    expect(hasLeadingHeading("# Title\n\nbody", "markdown")).toBe(true);
  });

  it("detects a setext heading", () => {
    expect(hasLeadingHeading("Title\n=====\n\nbody", "markdown")).toBe(true);
  });

  it("is not fooled by a hash that is not a heading", () => {
    expect(hasLeadingHeading("#hashtag not a heading", "markdown")).toBe(false);
  });

  it("treats a document opening at h2 as having no h1, so the layout supplies one", () => {
    // `## x` is an h2. The page still needs a top-level heading, so the layout
    // must not stand down just because *some* heading is present.
    expect(hasLeadingHeading("## Subheading only", "markdown")).toBe(false);
  });

  it("detects a leading <h1> in html", () => {
    expect(hasLeadingHeading("<h1>Title</h1><p>x</p>", "html")).toBe(true);
    expect(hasLeadingHeading("<p>no heading</p>", "html")).toBe(false);
  });

  it("returns false for empty or unknown content types", () => {
    expect(hasLeadingHeading("", "markdown")).toBe(false);
    expect(hasLeadingHeading("# x", "text")).toBe(false);
  });
});

describe("renderPage", () => {
  const base = {
    slug: "data-model",
    displayname: { en: "Data model" },
    description: { en: "Entries and payloads." },
    payload: { content_type: "markdown", body: "# Data model\n\nThe slash dance." },
  };

  it("bakes the content into the markup", () => {
    const html = renderPage(rec(base), cfg, opts);
    expect(html).toContain("The slash dance.");
    expect(html).toContain("<title>Data model</title>");
  });

  it("does not repeat the title when the body already has an h1", () => {
    const html = renderPage(rec(base), cfg, opts);
    // One from <title>, one from the markdown's own heading. Not three.
    expect(html.match(/Data model/g)).toHaveLength(3); // title, og:title, h1
    expect(html.match(/<h1[^>]*>/g)).toHaveLength(1);
  });

  it("supplies an h1 when the body has none", () => {
    const html = renderPage(
      rec({ ...base, payload: { content_type: "markdown", body: "Just prose." } }),
      cfg, opts,
    );
    expect(html).toContain("<h1>Data model</h1>");
    expect(html).toContain("Just prose.");
  });

  it("emits canonical and Open Graph when a base URL is given", () => {
    const html = renderPage(rec(base), cfg, opts);
    expect(html).toContain('<link rel="canonical" href="https://dmart.cc/data-model">');
    expect(html).toContain('<meta property="og:url" content="https://dmart.cc/data-model">');
  });

  it("omits canonical and og:url without a base URL, rather than emitting a relative one", () => {
    const html = renderPage(rec(base), { ...cfg, base: "" }, opts);
    expect(html).not.toContain("canonical");
    expect(html).not.toContain("og:url");
  });

  it("renders mermaid fences as <pre class=mermaid>, not as a code block", () => {
    const html = renderPage(
      rec({ ...base, payload: { content_type: "markdown", body: "```mermaid\ngraph TD\nA-->B\n```" } }),
      cfg, opts,
    );
    expect(html).toContain('<pre class="mermaid">');
    expect(html).toContain("graph TD");
  });

  it("renders GFM tables", () => {
    const html = renderPage(
      rec({ ...base, payload: { content_type: "markdown", body: "| a | b |\n|---|---|\n| 1 | 2 |" } }),
      cfg, opts,
    );
    expect(html).toContain("<table>");
  });

  it("escapes a title containing markup", () => {
    const html = renderPage(
      rec({ ...base, displayname: { en: '<script>x</script>' } }), cfg, opts,
    );
    expect(html).not.toContain("<script>x</script>");
    expect(html).toContain("&lt;script&gt;");
  });

  it("wraps plain text in <pre> rather than interpreting it", () => {
    const html = renderPage(
      rec({ ...base, payload: { content_type: "text", body: "a < b" } }), cfg, opts,
    );
    expect(html).toContain("<pre>a &lt; b</pre>");
  });

  it("falls back to the shortname when there is no displayname", () => {
    const html = renderPage(rec({ ...base, displayname: undefined }), cfg, opts);
    expect(html).toContain("<title>data_model</title>");
  });
});

describe("renderSitemap", () => {
  it("lists one absolute url per entry, with lastmod when known", () => {
    const xml = renderSitemap(
      [rec({ slug: "data-model", updated_at: "2026-10-01T12:00:00" })], cfg,
    );
    expect(xml).toContain("<loc>https://dmart.cc/data-model</loc>");
    expect(xml).toContain("<lastmod>2026-10-01</lastmod>");
  });

  it("omits lastmod rather than inventing one", () => {
    const xml = renderSitemap([rec({ slug: "x" })], cfg);
    expect(xml).not.toContain("<lastmod>");
  });
});
