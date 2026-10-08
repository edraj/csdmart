<script lang="ts">
    import { ChevronDownOutline, ChevronRightOutline, FolderOutline } from "flowbite-svelte-icons";
    import SpacesSubpathItemsSidebar from "./SpacesSubpathItemsSidebar.svelte";
    import { activeRoute, url } from "@roxi/routify";
    import { untrack } from "svelte";
    import { joinSubpath, normalizeSubpath, toRouteSubpath } from "@/utils/subpath";
    import { localizedText } from "@/utils/localized";
    import { _ } from "@/i18n";

    // One folder node in the tree: an expander <button> and a link <a> side
    // by side (never nested), indented with margin-inline-start so the tree
    // reads correctly in RTL.
    let {
        spaceName,
        parentPath,
        item,
        depth = 0,
        expandedSpaces,
        loadChildren,
        toggleExpanded,
        isExpanded,
        getChildrenForSpace,
        hasMoreChildren,
        loadMore,
        onNavigate,
    }: {
        spaceName: string;
        parentPath: string;
        item: { shortname: string; attributes?: { displayname?: Record<string, string | null | undefined> } };
        depth?: number;
        expandedSpaces: Set<string>;
        loadChildren: (spaceName: string, subpath?: string, invalidate?: boolean) => Promise<unknown[]>;
        toggleExpanded: (spaceName: string, subpath?: string, forceExpand?: boolean | null) => Promise<void>;
        isExpanded: (spaceName: string, subpath?: string) => boolean;
        getChildrenForSpace: (spaceName: string, subpath?: string) => Array<{ shortname: string; attributes?: Record<string, unknown> }>;
        hasMoreChildren: (spaceName: string, subpath?: string) => boolean;
        loadMore: (spaceName: string, subpath?: string) => Promise<void>;
        onNavigate?: () => void;
    } = $props();

    // This folder's canonical subpath (`/a/b`); the route spelling is derived
    // from it where a URL is needed.
    const currentPath = $derived(joinSubpath(parentPath, item.shortname));
    const activePath = $derived(normalizeSubpath($activeRoute?.params?.subpath));
    const isCurrent = $derived(activePath === currentPath);
    const expanded = $derived(isExpanded(spaceName, currentPath));
    const label = $derived(localizedText(item.attributes?.displayname, item.shortname));
    const indent = $derived(`${depth * 1}rem`);

    // Open the chain of folders that leads to the one being viewed, so a deep
    // link shows where it sits in the tree.
    $effect(() => {
        const path = currentPath;
        const active = activePath;
        if (active === path || active.startsWith(`${path}/`)) {
            untrack(() => {
                void toggleExpanded(spaceName, path, true);
            });
        }
    });
</script>

<li>
    <div
        class="flex items-center gap-1 h-9 rounded-control pe-2 transition-colors
            {isCurrent ? 'bg-primary-soft text-primary' : 'text-text hover:bg-surface-3'}"
        style="margin-inline-start: {indent}"
    >
        <button
            type="button"
            class="shrink-0 w-7 h-7 inline-flex items-center justify-center rounded-control hover:bg-surface-3 text-text-muted cursor-pointer"
            aria-expanded={expanded}
            aria-label={expanded ? $_("collapse_folder", { values: { name: label } }) : $_("expand_folder", { values: { name: label } })}
            onclick={() => void toggleExpanded(spaceName, currentPath)}
        >
            {#if expanded}
                <ChevronDownOutline size="sm" aria-hidden="true" />
            {:else}
                <ChevronRightOutline size="sm" class="rtl:rotate-180" aria-hidden="true" />
            {/if}
        </button>
        <a
            href={$url(`/management/content/${spaceName}/${toRouteSubpath(currentPath)}`)}
            onclick={onNavigate}
            aria-current={isCurrent ? "page" : undefined}
            class="flex items-center gap-2 min-w-0 flex-1 h-full rounded-control"
        >
            <FolderOutline size="md" class="shrink-0 {isCurrent ? 'text-primary' : 'text-text-faint'}" aria-hidden="true" />
            <span class="truncate">{label}</span>
        </a>
    </div>
</li>

{#if expanded}
    {#each getChildrenForSpace(spaceName, currentPath) as child (child.shortname)}
        <SpacesSubpathItemsSidebar
            {spaceName}
            parentPath={currentPath}
            item={child}
            depth={depth + 1}
            {expandedSpaces}
            {loadChildren}
            {toggleExpanded}
            {isExpanded}
            {getChildrenForSpace}
            {hasMoreChildren}
            {loadMore}
            {onNavigate}
        />
    {/each}
    {#if hasMoreChildren(spaceName, currentPath)}
        <li>
            <button
                type="button"
                class="w-full text-start text-sm text-primary h-8 px-2 rounded-control hover:bg-surface-3 cursor-pointer"
                style="padding-inline-start: calc({(depth + 1) * 1}rem + 2.25rem)"
                onclick={() => loadMore(spaceName, currentPath)}
            >
                {$_("load_more")}
            </button>
        </li>
    {/if}
{/if}
