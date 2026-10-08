<script lang="ts">
    import type { ApiResponseRecord } from "@edraj/tsdmart";
    import type { Snippet } from "svelte";
    import { url } from "@roxi/routify";
    import { _ } from "@/i18n";
    import { formatDate } from "@/utils/format";
    import { localizedText } from "@/utils/localized";

    // The one space-card grid: Spaces, Events and Health check all list the
    // same cards and differ only in where a card leads and whether it has an
    // actions menu. Cards are links (open in a new tab works); the actions
    // menu is a sibling of the link, never inside it.
    let {
        spaces,
        link,
        actions,
    }: {
        spaces: ApiResponseRecord[];
        /** Internal route path for a space's card. */
        // Routify resolves $url() against the route tree: a node path with
        // [params], never a concrete "/management/content/foo" (that throws
        // "could not travel to foo" and the page never renders).
        link: (space: ApiResponseRecord) => { path: string; params?: Record<string, string> };
        /** Optional per-card menu, rendered in the card's top end corner. */
        actions?: Snippet<[ApiResponseRecord]>;
    } = $props();
</script>

<ul class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4 w-full items-stretch" role="list">
    {#each spaces as space (space.shortname)}
        <li class="relative flex">
            <a
                href={$url(link(space).path, link(space).params ?? {})}
                class="flex flex-col w-full h-full p-4 sm:p-5 rounded-card border border-border bg-surface-2 text-text shadow-card
                    transition-shadow hover:shadow-modal hover:border-border-strong
                    focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
            >
                <span class="flex items-start justify-between gap-2 {actions ? 'pe-9' : ''}">
                    <span class="inline-block px-2 py-0.5 rounded-control border border-border bg-surface text-xs font-medium text-text-muted font-mono truncate">
                        {space.shortname}
                    </span>
                </span>
                <span class="mt-3 font-semibold text-base leading-snug break-words">
                    {localizedText(space.attributes?.displayname, space.shortname)}
                </span>
                {#if space.attributes?.description}
                    <span class="mt-1.5 text-sm text-text-muted line-clamp-3">
                        {localizedText(space.attributes?.description, "")}
                    </span>
                {/if}
                <span class="mt-auto pt-4 text-xs text-text-faint tabular-nums">
                    {$_("updated")}: {formatDate(space.attributes?.updated_at, "date") || $_("not_applicable")}
                </span>
            </a>
            {#if actions}
                <div class="absolute top-3 end-3">
                    {@render actions(space)}
                </div>
            {/if}
        </li>
    {/each}
</ul>
