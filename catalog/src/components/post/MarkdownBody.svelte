<script lang="ts">
  import { diagrams } from "@/lib/diagrams";

  // Rendered Markdown (already sanitized by lib/markdown) with one set of
  // token-based styles, shared by the entry body, its description and
  // template-based entries. Dark mode follows the tokens; direction follows
  // the document (logical properties only).
  let {
    html,
    compact = false,
    class: className = "",
  }: {
    /** Output of renderMarkdown()/renderMarkdownInline(): trusted, sanitized. */
    html: string;
    /** Smaller type and tighter spacing, for a description under a title. */
    compact?: boolean;
    class?: string;
  } = $props();
</script>

{#if html}
  <div class="md-body {compact ? 'md-compact' : ''} {className}" dir="auto" use:diagrams={html}>
    <!-- eslint-disable-next-line svelte/no-at-html-tags -- sanitized in lib/markdown -->
    {@html html}
  </div>
{/if}

<style>
  .md-body {
    color: var(--color-text);
    font-size: var(--font-size-base);
    line-height: var(--line-height-relaxed);
    overflow-wrap: anywhere;
  }

  .md-compact {
    color: var(--color-text-muted);
    font-size: var(--font-size-sm);
    line-height: var(--line-height-normal);
  }

  .md-body :global(> :first-child) {
    margin-top: 0;
  }

  .md-body :global(> :last-child) {
    margin-bottom: 0;
  }

  .md-body :global(h1),
  .md-body :global(h2),
  .md-body :global(h3),
  .md-body :global(h4),
  .md-body :global(h5),
  .md-body :global(h6) {
    color: var(--color-text);
    font-weight: var(--font-weight-semibold);
    line-height: var(--line-height-tight);
    margin: 1.5em 0 0.5em;
  }

  .md-body :global(h1) {
    font-size: var(--font-size-2xl);
    padding-bottom: 0.3em;
    border-bottom: 1px solid var(--color-border);
  }

  .md-body :global(h2) {
    font-size: var(--font-size-xl);
  }

  .md-body :global(h3) {
    font-size: var(--font-size-lg);
  }

  .md-body :global(h4),
  .md-body :global(h5),
  .md-body :global(h6) {
    font-size: var(--font-size-base);
  }

  .md-body :global(p) {
    margin: 0.75em 0;
  }

  .md-compact :global(p) {
    margin: 0.375em 0;
  }

  .md-body :global(ul),
  .md-body :global(ol) {
    margin: 0.75em 0;
    padding-inline-start: 1.5em;
  }

  .md-body :global(ul) {
    list-style-type: disc;
  }

  .md-body :global(ol) {
    list-style-type: decimal;
  }

  .md-body :global(li) {
    margin: 0.25em 0;
  }

  .md-body :global(li > ul),
  .md-body :global(li > ol) {
    margin: 0.25em 0;
  }

  .md-body :global(a) {
    color: var(--color-primary);
    text-decoration: underline;
    text-underline-offset: 2px;
  }

  .md-body :global(a:hover) {
    color: var(--color-primary-hover);
  }

  .md-body :global(strong) {
    font-weight: var(--font-weight-semibold);
  }

  .md-body :global(code) {
    font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace;
    font-size: 0.875em;
    background: var(--color-surface-3);
    border-radius: var(--radius-control);
    padding: 0.125em 0.375em;
    direction: ltr;
    unicode-bidi: isolate;
  }

  .md-body :global(pre) {
    background: var(--color-surface-3);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-card);
    padding: 1rem;
    margin: 1em 0;
    overflow-x: auto;
    direction: ltr;
    text-align: start;
  }

  .md-body :global(pre code) {
    background: transparent;
    padding: 0;
    font-size: var(--font-size-sm);
    line-height: var(--line-height-normal);
    white-space: pre;
  }

  /* A fenced diagram (```mermaid) shows its source with a caption until
     lib/diagrams.ts has drawn it, and again if it cannot be drawn. */
  .md-body :global(figure.md-diagram) {
    margin: 1em 0;
  }

  .md-body :global(figure.md-diagram > pre) {
    margin: 0;
    border-end-start-radius: 0;
    border-end-end-radius: 0;
    border-bottom: 0;
  }

  .md-body :global(figure.md-diagram > figcaption) {
    font-size: var(--font-size-xs);
    color: var(--color-text-muted);
    background: var(--color-surface-2);
    border: 1px solid var(--color-border);
    border-end-start-radius: var(--radius-card);
    border-end-end-radius: var(--radius-card);
    padding: 0.375rem 0.75rem;
  }

  .md-body :global(figure.md-diagram > figcaption.md-diagram-error) {
    color: var(--color-danger);
    white-space: pre-wrap;
  }

  .md-body :global(blockquote) {
    margin: 1em 0;
    padding-inline-start: 1rem;
    border-inline-start: 3px solid var(--color-primary);
    color: var(--color-text-muted);
  }

  .md-body :global(hr) {
    border: 0;
    border-top: 1px solid var(--color-border);
    margin: 1.5em 0;
  }

  .md-body :global(table) {
    width: 100%;
    border-collapse: collapse;
    margin: 1em 0;
    font-size: var(--font-size-sm);
    display: block;
    overflow-x: auto;
  }

  .md-body :global(th),
  .md-body :global(td) {
    padding: 0.5rem 0.75rem;
    border: 1px solid var(--color-border);
    text-align: start;
    vertical-align: top;
  }

  .md-body :global(th) {
    background: var(--color-surface-3);
    font-weight: var(--font-weight-semibold);
  }

  .md-body :global(img) {
    max-width: 100%;
    height: auto;
    border-radius: var(--radius-card);
    margin: 1em 0;
    display: block;
  }

  .md-body :global(del) {
    text-decoration: line-through;
  }
</style>
