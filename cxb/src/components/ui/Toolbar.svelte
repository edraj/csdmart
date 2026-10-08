<script lang="ts">
    import type { Snippet } from "svelte";
    import { CloseOutline, SearchOutline } from "flowbite-svelte-icons";
    import { _ } from "@/i18n";
    import IconButton from "./IconButton.svelte";

    // Search (clear + submit) at the start, actions at the end; wraps on
    // narrow widths. `search` is bindable so the page keeps the query; the
    // page only hears about it on submit/clear, never per keystroke.
    let {
        search = $bindable(""),
        placeholder,
        onSearch,
        onClear,
        start,
        children,
        class: className = "",
    }: {
        search?: string;
        placeholder?: string;
        /** Called on submit (Enter or the search button). Omit to hide the search box. */
        onSearch?: (query: string) => void;
        onClear?: () => void;
        /** Extra controls rendered next to the search box. */
        start?: Snippet;
        /** Actions, rendered at the end. */
        children?: Snippet;
        class?: string;
    } = $props();

    function submit(event: Event) {
        event.preventDefault();
        onSearch?.(search.trim());
    }

    function clear() {
        search = "";
        onClear?.();
        onSearch?.("");
    }
</script>

<div class="flex flex-wrap items-center gap-3 {className}" role="toolbar">
    <div class="flex flex-wrap items-center gap-2 grow sm:grow-0">
        {#if onSearch}
            <form onsubmit={submit} class="relative flex items-center w-full sm:w-72" role="search">
                <SearchOutline
                    size="sm"
                    class="absolute start-3 text-text-faint pointer-events-none"
                    aria-hidden="true"
                />
                <input
                    type="search"
                    bind:value={search}
                    placeholder={placeholder ?? $_("search")}
                    aria-label={placeholder ?? $_("search")}
                    class="w-full h-9 ps-9 pe-9 text-sm rounded-control border border-border bg-surface-2 text-text placeholder:text-text-faint focus:border-primary focus:ring-1 focus:ring-primary"
                />
                {#if search}
                    <span class="absolute end-1">
                        <IconButton label={$_("clear_search")} size="sm" onclick={clear}>
                            <CloseOutline size="sm" />
                        </IconButton>
                    </span>
                {/if}
            </form>
        {/if}
        {#if start}
            {@render start()}
        {/if}
    </div>
    {#if children}
        <div class="flex flex-wrap items-center gap-2 ms-auto">
            {@render children()}
        </div>
    {/if}
</div>
