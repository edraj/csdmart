<script lang="ts">
    import { SidebarItem } from "flowbite-svelte";
    import {
        ChevronDownOutline,
        ChevronRightOutline,
        FolderOutline,
    } from "flowbite-svelte-icons";
    import SpacesSubpathItemsSidebar from "./SpacesSubpathItemsSidebar.svelte";
    import { activeRoute } from "@roxi/routify";
    import { untrack } from "svelte";
    import { joinSubpath, normalizeSubpath, toRouteSubpath } from "@/utils/subpath";
    import { _ } from "@/i18n";

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
    }: {
        spaceName: string;
        parentPath: string;
        item: { shortname: string; attributes?: { displayname?: { en?: string } } };
        depth?: number;
        expandedSpaces: Set<string>;
        loadChildren: (spaceName: string, subpath?: string, invalidate?: boolean) => Promise<unknown[]>;
        toggleExpanded: (spaceName: string, subpath?: string, forceExpand?: boolean | null) => Promise<void>;
        isExpanded: (spaceName: string, subpath?: string) => boolean;
        getChildrenForSpace: (spaceName: string, subpath?: string) => any[];
        hasMoreChildren: (spaceName: string, subpath?: string) => boolean;
        loadMore: (spaceName: string, subpath?: string) => Promise<void>;
    } = $props();

    // This folder's canonical subpath (`/a/b`); the route spelling is derived
    // from it where a URL is needed.
    const currentPath = $derived(joinSubpath(parentPath, item.shortname));
    const activePath = $derived(normalizeSubpath($activeRoute?.params?.subpath));
    const isCurrent = $derived(activePath === currentPath);

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

    function handleToggle(event: Event) {
        event.preventDefault();
        event.stopPropagation();
        void toggleExpanded(spaceName, currentPath);
    }
</script>

<SidebarItem
    label={item.attributes?.displayname?.en || item.shortname}
    href={`/management/content/${spaceName}/${toRouteSubpath(currentPath)}`}
    class="flex-1 whitespace-nowrap {isCurrent ? 'bg-gray-300 text-white' : ''}"
    style="margin-inline-start: {depth * 20}px;"
    aria-current={isCurrent ? "page" : undefined}
>
    {#snippet icon()}
        <div class="flex items-center gap-2">
            <button
                type="button"
                class="p-1 rounded"
                aria-expanded={isExpanded(spaceName, currentPath)}
                aria-label={item.shortname}
                onclick={handleToggle}
            >
                {#if isExpanded(spaceName, currentPath)}
                    <ChevronDownOutline size="sm" />
                {:else}
                    <ChevronRightOutline size="sm" class="rtl:rotate-180" />
                {/if}
            </button>

            <div>
                <FolderOutline
                    size="md"
                    class="text-gray-500"
                    style="transform: rotate(180deg); position: relative; z-index: 5;"
                />
            </div>
        </div>
    {/snippet}
</SidebarItem>

{#if isExpanded(spaceName, currentPath)}
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
        />
    {/each}
    {#if hasMoreChildren(spaceName, currentPath)}
        <li>
            <button
                type="button"
                class="w-full text-start text-sm text-primary px-3 py-1.5 hover:underline cursor-pointer"
                style="margin-inline-start: {(depth + 1) * 20}px;"
                onclick={() => loadMore(spaceName, currentPath)}
            >
                {$_("load_more")}
            </button>
        </li>
    {/if}
{/if}
