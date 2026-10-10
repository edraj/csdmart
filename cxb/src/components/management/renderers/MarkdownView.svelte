<script lang="ts">
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { renderMarkdown } from "@/utils/markdown";
    import { renderDiagrams } from "@shared/diagrams";
    import { theme } from "@/stores/theme.svelte";
    import { _ } from "@/i18n";

    // Rendered markdown, with its ```mermaid fences drawn as diagrams. The
    // editor's preview tab and the content view both show markdown through
    // this, so a diagram looks the same in either.
    let {
        source,
        rows = 5,
    }: {
        source: string;
        /** Skeleton bars while the renderer loads. */
        rows?: number;
    } = $props();

    let html: string | null = $state(null);
    let article: HTMLElement | undefined = $state();

    $effect(() => {
        let current = true;
        renderMarkdown(source).then((rendered) => {
            if (current) html = rendered;
        });
        return () => {
            current = false;
        };
    });

    // A user effect, so it runs after {@html} has put the new markup in the
    // article; it runs again when the markdown or the theme changes.
    $effect(() => {
        if (!article || html === null) return;
        renderDiagrams(article, { dark: theme.resolved === "dark", errorLabel: $_("diagram_error") });
    });
</script>

{#if html === null}
    <LoadingState variant="skeleton" {rows} />
{:else}
    <article bind:this={article} class="markdown-view prose dark:prose-invert max-w-none">
        <!-- eslint-disable-next-line svelte/no-at-html-tags -- sanitised by renderMarkdown -->
        {@html html}
    </article>
{/if}

<style>
    .markdown-view :global(figure.md-diagram) {
        margin: 1.5em 0;
        overflow-x: auto;
    }

    /* Tailwind's preflight makes an svg a block, so text-align cannot centre it. */
    .markdown-view :global(figure.md-diagram svg) {
        display: block;
        margin-inline: auto;
        max-width: 100%;
        height: auto;
    }

    .markdown-view :global(figure.md-diagram > pre) {
        margin: 0;
    }

    .markdown-view :global(.md-diagram-error) {
        margin: 0.5em 0 0;
        white-space: pre-wrap;
        font-size: 0.875em;
        color: var(--color-danger);
    }
</style>
