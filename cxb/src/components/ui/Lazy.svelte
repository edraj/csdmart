<script lang="ts" generics="P extends Record<string, unknown>">
    import type { Component, Snippet } from "svelte";
    import LoadingState from "./LoadingState.svelte";
    import ErrorState from "./ErrorState.svelte";

    // Code-splits a component: `load` is a `() => import("./X.svelte")`, run
    // the first time this mounts (the browser caches the module afterwards), so
    // a tab's component and the libraries it drags in stay out of the chunk of
    // the page that merely offers the tab. The snippet receives the component
    // and renders it with whatever props and bindings it needs.
    let {
        load,
        pending = "skeleton",
        rows = 4,
        children,
    }: {
        load: () => Promise<{ default: Component<P> }>;
        /** What to show while the module downloads; "none" for modals. */
        pending?: "skeleton" | "spinner" | "none";
        /** Skeleton rows while the module downloads. */
        rows?: number;
        children: Snippet<[Component<P>]>;
    } = $props();

    // The loader is fixed for the life of this instance; a changed `load`
    // prop would mean a different component and a different <Lazy>.
    // svelte-ignore state_referenced_locally
    const module = load();
</script>

{#await module}
    {#if pending === "skeleton"}
        <LoadingState variant="skeleton" {rows} />
    {:else if pending === "spinner"}
        <LoadingState />
    {/if}
{:then mod}
    {@render children(mod.default)}
{:catch error}
    <ErrorState {error} />
{/await}
