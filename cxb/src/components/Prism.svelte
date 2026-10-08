<script lang="ts">
    import { _ } from "svelte-i18n";
    import pkg from "@/lib/prism-init";
    import "prismjs/components/prism-json";
    import "prismjs/components/prism-bash";

    const { highlight, languages } = pkg;

    // Highlighting runs on the main thread over the full text: a response
    // with thousands of payload-bearing records (the tools' Preview) used to
    // freeze the tab. Anything past maxChars is cut, and the cut is said.
    let {
        language = "json",
        code = $bindable(),
        maxChars = 200_000,
    }: { language?: string | null; code: object | string; maxChars?: number } = $props();

    const source = $derived.by(() => {
        const lang = language ?? "json";
        return lang === "json" ? (JSON.stringify(code, undefined, 1) ?? "") : String(code ?? "");
    });
    const truncated = $derived(source.length > maxChars);
    const formatted = $derived.by(() => {
        const lang = language ?? "json";
        const grammar = languages[lang] ?? languages.json;
        return highlight(truncated ? source.slice(0, maxChars) : source, grammar, lang);
    });
</script>

<!-- eslint-disable-next-line svelte/no-at-html-tags -- Prism's own escaped markup -->
<pre class="cxb-code language-{language}"><code class="language-{language}">{@html formatted}</code></pre>
{#if truncated}
    <p class="text-xs text-text-muted mt-1">{$_("output_truncated", { values: { shown: maxChars, total: source.length } })}</p>
{/if}

<style>
    /* One theme, both modes: the token colours come from the app palette and
       flip with the `.dark` class, so no vendor theme is imported. */
    .cxb-code {
        display: grid;
        margin: 0;
        padding: 0.75rem 1rem;
        font-size: 0.8rem;
        line-height: 1.5;
        overflow: auto;
        border-radius: var(--radius-control);
        border: 1px solid var(--color-border);
        background: var(--color-surface);
        color: var(--color-text);
        text-align: start;
        direction: ltr;
        white-space: pre;
        tab-size: 2;
    }

    .cxb-code :global(.token.comment),
    .cxb-code :global(.token.prolog),
    .cxb-code :global(.token.doctype),
    .cxb-code :global(.token.cdata) {
        color: var(--color-text-faint);
        font-style: italic;
    }
    .cxb-code :global(.token.punctuation),
    .cxb-code :global(.token.operator) {
        color: var(--color-text-muted);
    }
    .cxb-code :global(.token.property),
    .cxb-code :global(.token.tag),
    .cxb-code :global(.token.constant),
    .cxb-code :global(.token.symbol),
    .cxb-code :global(.token.keyword),
    .cxb-code :global(.token.atrule) {
        color: var(--color-primary);
    }
    .cxb-code :global(.token.boolean),
    .cxb-code :global(.token.number) {
        color: var(--color-warning);
    }
    .cxb-code :global(.token.string),
    .cxb-code :global(.token.char),
    .cxb-code :global(.token.attr-value),
    .cxb-code :global(.token.selector),
    .cxb-code :global(.token.builtin),
    .cxb-code :global(.token.inserted) {
        color: var(--color-success);
    }
    .cxb-code :global(.token.function),
    .cxb-code :global(.token.class-name),
    .cxb-code :global(.token.attr-name),
    .cxb-code :global(.token.variable),
    .cxb-code :global(.token.regex),
    .cxb-code :global(.token.important),
    .cxb-code :global(.token.deleted) {
        color: var(--color-danger);
    }
    .cxb-code :global(.token.url),
    .cxb-code :global(.token.entity) {
        color: var(--color-info);
    }
    .cxb-code :global(.token.important),
    .cxb-code :global(.token.bold) {
        font-weight: 600;
    }
</style>
