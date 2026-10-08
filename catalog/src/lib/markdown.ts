// The one Markdown renderer.
//
// `marked` exports a single global instance, and five components used to call
// `marked.use(mangle()); marked.use(gfmHeadingId(...))` from their instance
// scripts: every mount stacked another layer of extension hooks onto that
// global for the rest of the session (review perf #32). Everything renders
// through this module-level `Marked` instance instead, and every result goes
// through DOMPurify before it reaches an `{@html}` sink.
//
//   import { renderMarkdown } from "@/lib/markdown";
//   {@html renderMarkdown(body, { diagramLabel: $_("post_detail.markdown.diagram_source") })}
//
// Fenced ```mermaid blocks render as a labelled code block — the source in a
// <pre>, a <figcaption> underneath — rather than pulling a diagram library
// into the bundle for a block most posts never contain.

import { Marked, type Tokens } from "marked";
import { gfmHeadingId } from "marked-gfm-heading-id";
import { mangle } from "marked-mangle";
import { sanitizeHtml } from "@/lib/utils/sanitize";

export interface RenderMarkdownOptions {
  /** Caption under a fenced diagram block (translated by the caller). */
  diagramLabel?: string;
}

/** Fence languages that describe a diagram rather than code to run. */
export const DIAGRAM_LANGUAGES: ReadonlySet<string> = new Set(["mermaid"]);

const DEFAULT_DIAGRAM_LABEL = "Diagram source";

// Read by the code renderer during a synchronous parse() and reset after it,
// so one shared instance can still carry the caller's translated caption.
let diagramLabel = DEFAULT_DIAGRAM_LABEL;

export function escapeHtml(text: string): string {
  return text
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

/** The fence's language word, lower-cased ("```Mermaid title" → "mermaid"). */
export function fenceLanguage(lang: string | undefined): string {
  return (lang ?? "").trim().split(/\s+/)[0]?.toLowerCase() ?? "";
}

const instance = new Marked(mangle(), gfmHeadingId({ prefix: "md-" }), {
  renderer: {
    code(token: Tokens.Code) {
      const lang = fenceLanguage(token.lang);
      if (!DIAGRAM_LANGUAGES.has(lang)) return false; // default renderer
      const source = (token.escaped ? token.text : escapeHtml(token.text)).replace(/\n$/, "");
      return (
        `<figure class="md-diagram" data-lang="${lang}">` +
        `<pre><code class="language-${lang}">${source}\n</code></pre>` +
        `<figcaption>${escapeHtml(diagramLabel)}</figcaption>` +
        `</figure>\n`
      );
    },
  },
});

/**
 * Markdown → sanitized HTML. Returns "" for a missing or non-string source,
 * so callers can `{#if html}` without a second check.
 */
export function renderMarkdown(
  source: string | null | undefined,
  options: RenderMarkdownOptions = {},
): string {
  if (typeof source !== "string" || source.length === 0) return "";
  diagramLabel = options.diagramLabel ?? DEFAULT_DIAGRAM_LABEL;
  try {
    return sanitizeHtml(instance.parse(source, { async: false }));
  } finally {
    diagramLabel = DEFAULT_DIAGRAM_LABEL;
  }
}

/** Inline Markdown (no wrapping <p>) → sanitized HTML, for one-line fields. */
export function renderMarkdownInline(source: string | null | undefined): string {
  if (typeof source !== "string" || source.length === 0) return "";
  return sanitizeHtml(instance.parseInline(source, { async: false }));
}
