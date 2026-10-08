<script lang="ts">
    import type { Content, Mode, OnRenderMenu } from "svelte-jsoneditor";
    import LoadingState from "./LoadingState.svelte";
    import ErrorState from "./ErrorState.svelte";

    // svelte-jsoneditor plus CodeMirror is ~830 kB of JS and 104 kB of CSS, and
    // it is only ever wanted inside a modal or the Entry tab. The module is
    // fetched the first time one of those mounts, so no list route carries it.
    // `mode` is a plain string: importing the `Mode` enum as a value would pull
    // the whole package back into the caller's chunk.
    let {
        content = $bindable({ json: {} }),
        mode = "text",
        readOnly = false,
        onRenderMenu,
        class: className = "",
    }: {
        content?: Content;
        mode?: "text" | "tree" | "table";
        readOnly?: boolean;
        onRenderMenu?: OnRenderMenu;
        class?: string;
    } = $props();

    let editorModule: Promise<typeof import("svelte-jsoneditor")> | null = null;
    function loadEditor() {
        editorModule ??= import("svelte-jsoneditor");
        return editorModule;
    }
</script>

{#await loadEditor()}
    <LoadingState variant="skeleton" rows={4} class={className} />
{:then editor}
    {@const JSONEditor = editor.JSONEditor}
    <div class="rounded-control overflow-hidden border border-border {className}">
        <JSONEditor bind:content mode={mode as unknown as Mode} {readOnly} {onRenderMenu} />
    </div>
{:catch error}
    <ErrorState compact {error} />
{/await}
