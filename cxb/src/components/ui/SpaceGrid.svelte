<script lang="ts">
    import { Card } from "flowbite-svelte";
    import type { ApiResponseRecord } from "@edraj/tsdmart";
    import type { Snippet } from "svelte";
    import { _ } from "@/i18n";

    // The one space-card grid: Spaces, Events and Health check all list the
    // same cards and differ only in where a click goes and whether a card has
    // an actions menu.
    let {
        spaces,
        onSelect,
        actions,
    }: {
        spaces: ApiResponseRecord[];
        onSelect: (shortname: string) => void;
        /** Optional per-card menu, rendered in the card's top start corner. */
        actions?: Snippet<[ApiResponseRecord]>;
    } = $props();

    function updatedAt(space: ApiResponseRecord): string {
        const raw = space.attributes?.updated_at;
        if (!raw) return "";
        const date = new Date(raw);
        return Number.isNaN(date.getTime()) ? "" : date.toLocaleDateString();
    }
</script>

<div
    class="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 xl:grid-cols-4 gap-4 w-full place-items-center"
>
    {#each spaces as space (space.shortname)}
        <Card class="relative w-full">
            {#if actions}
                <div class="absolute top-2 start-2">
                    {@render actions(space)}
                </div>
            {/if}

            <button
                type="button"
                class="flex flex-col items-center text-center p-4 w-full cursor-pointer"
                onclick={() => onSelect(space.shortname)}
            >
                <span
                    class="inline-block px-3 py-1 mb-3 border border-gray-300 rounded-md text-sm font-medium"
                >
                    {space.shortname}
                </span>

                <span class="font-semibold text-lg">
                    {space.attributes?.displayname?.en || space.shortname}
                </span>

                <span class="text-gray-600 mt-2 mb-4 line-clamp-3">
                    {space.attributes?.description?.en || ""}
                </span>

                <span class="text-xs text-gray-500 mt-auto">
                    {$_("updated")}: {updatedAt(space)}
                </span>
            </button>
        </Card>
    {/each}
</div>
