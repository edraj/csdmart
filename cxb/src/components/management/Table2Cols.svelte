<script lang="ts">
    import { ChevronRightOutline } from "flowbite-svelte-icons";
    import { _ } from "@/i18n";
    import Table2Cols from "./Table2Cols.svelte";

    // Values come straight from server-supplied entry records (displayname,
    // payload body, /info/* responses), so they are never injected as HTML.
    // Scalars go through ordinary `{...}` interpolation (auto-escaped) and
    // objects recurse through this same component in its `nested` form.
    //
    // A large payload used to render thousands of nodes at once; below
    // `collapseBelow` levels a nested object is drawn folded and only expands
    // when the reader asks.
    let {
        entry = {},
        // `nested` swaps the table markup for the <ul> used to show object
        // values, so the recursion does not nest whole tables.
        nested = false,
        depth = 0,
        collapseBelow = 2,
    }: {
        entry?: Record<string, unknown> | unknown[];
        nested?: boolean;
        depth?: number;
        collapseBelow?: number;
    } = $props();

    let expanded = $state(false);

    const entries = $derived(Object.entries(entry ?? {}));
    const folded = $derived(nested && depth >= collapseBelow && !expanded);

    function scalar(value: unknown): string {
        if (value === null || value === undefined) return $_("not_applicable");
        if (typeof value === "boolean") return value ? $_("yes") : $_("no");
        return String(value);
    }
</script>

{#if nested}
    {#if folded}
        <button
            type="button"
            class="inline-flex items-center gap-1 text-sm text-primary hover:underline cursor-pointer rounded-control"
            aria-expanded="false"
            onclick={() => (expanded = true)}
        >
            <ChevronRightOutline size="xs" class="rtl:rotate-180" aria-hidden="true" />
            {$_("show_n_fields", { values: { count: entries.length } })}
        </button>
    {:else if entries.length === 0}
        <span class="text-text-faint">{Array.isArray(entry) ? "[]" : "{}"}</span>
    {:else}
        <ul class="ms-4 border-s border-border ps-3 space-y-0.5">
            {#each entries as [key, value] (key)}
                <li class="text-sm break-words">
                    <span class="font-medium text-text-muted">{key}: </span>{#if value !== null && typeof value === "object"}<Table2Cols
                            entry={value as Record<string, unknown>}
                            nested={true}
                            depth={depth + 1}
                            {collapseBelow}
                        />{:else}<span class="text-text">{scalar(value)}</span>{/if}
                </li>
            {/each}
        </ul>
    {/if}
{:else}
    <div class="rounded-card border border-border bg-surface-2 shadow-card overflow-x-auto">
        <table class="w-full text-sm text-start border-collapse tabular-nums">
            <thead class="bg-surface text-text-muted text-xs font-semibold">
                <tr>
                    <th scope="col" class="p-2.5 text-start border-b border-border w-1/4">{$_("key")}</th>
                    <th scope="col" class="p-2.5 text-start border-b border-border">{$_("value")}</th>
                </tr>
            </thead>
            <tbody>
                {#each entries as [key, value] (key)}
                    <tr class="border-b border-border last:border-b-0 even:bg-surface/60 align-top">
                        <th scope="row" class="p-2.5 text-start font-medium text-text whitespace-nowrap">{key}</th>
                        <td class="p-2.5 text-text break-words">
                            {#if value !== null && typeof value === "object"}
                                <Table2Cols
                                    entry={value as Record<string, unknown>}
                                    nested={true}
                                    depth={depth + 1}
                                    {collapseBelow}
                                />
                            {:else}
                                {scalar(value)}
                            {/if}
                        </td>
                    </tr>
                {/each}
            </tbody>
        </table>
    </div>
{/if}
