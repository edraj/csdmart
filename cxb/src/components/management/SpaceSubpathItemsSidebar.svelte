<script lang="ts">
    import { CodeForkSolid } from "flowbite-svelte-icons";
    import { untrack } from "svelte";
    import { SvelteSet } from "svelte/reactivity";
    import { ResourceType } from "@edraj/tsdmart";
    import { activeRoute, params, url } from "@roxi/routify";
    import { Level, showToast } from "@/utils/toast";
    import { getChildren } from "@/lib/dmart_services";
    import SpacesSubpathItemsSidebar from "./SpacesSubpathItemsSidebar.svelte";
    import { spaces } from "@/stores/management/spaces";
    import { spaceChildren } from "@/stores/global";
    import { normalizeSubpath, sidebarCacheKey } from "@/utils/subpath";
    import { hasMoreRecords } from "@/utils/paging";
    import { errorMessage } from "@/utils/errorMessage";
    import { localizedText } from "@/utils/localized";
    import { _ } from "@/i18n";

    // The folder tree for one space. Keyboard and screen-reader friendly:
    // every node is a link, every expander a named button, nothing nested.
    let { onNavigate }: { onNavigate?: () => void } = $props();

    // One page of folder children per tree node; "Load more" appends the next.
    const CHILDREN_PAGE_SIZE = 50;

    // Reactive set: `.add()`/`.delete()` re-render the tree without copying.
    const expandedSpaces = new SvelteSet<string>();
    $spaceChildren.refresh = loadChildren;

    function publishChildren() {
        // New Map instances so every subscriber (this tree, the entry renderer
        // that invalidates after creating a folder) sees the change.
        $spaceChildren = {
            ...$spaceChildren,
            data: new Map($spaceChildren.data),
            hasMore: new Map($spaceChildren.hasMore),
        };
    }

    export async function loadChildren(
        spaceName: string,
        subpath = "/",
        invalidate = false,
    ) {
        const cacheKey = sidebarCacheKey(spaceName, subpath);

        if (invalidate || !$spaceChildren.data.has(cacheKey)) {
            try {
                const children = await getChildren(spaceName, subpath, CHILDREN_PAGE_SIZE, 0, [
                    ResourceType.folder,
                ]);
                const records = children.records ?? [];
                $spaceChildren.data.set(cacheKey, records);
                $spaceChildren.hasMore.set(
                    cacheKey,
                    hasMoreRecords(children.attributes?.total, records.length, records.length, CHILDREN_PAGE_SIZE),
                );
            } catch (error: unknown) {
                showToast(Level.warn, errorMessage(error, $_("subpaths_load_failed")));
                $spaceChildren.data.set(cacheKey, []);
                $spaceChildren.hasMore.set(cacheKey, false);
            }
            publishChildren();
        }
        return $spaceChildren.data.get(cacheKey) || [];
    }

    async function loadMoreChildren(spaceName: string, subpath = "/") {
        const cacheKey = sidebarCacheKey(spaceName, subpath);
        const loaded = $spaceChildren.data.get(cacheKey) ?? [];
        try {
            const children = await getChildren(spaceName, subpath, CHILDREN_PAGE_SIZE, loaded.length, [
                ResourceType.folder,
            ]);
            const page = children.records ?? [];
            const seen = new Set(loaded.map((r) => r.shortname));
            const merged = [...loaded, ...page.filter((r) => !seen.has(r.shortname))];
            $spaceChildren.data.set(cacheKey, merged);
            $spaceChildren.hasMore.set(
                cacheKey,
                hasMoreRecords(children.attributes?.total, merged.length, page.length, CHILDREN_PAGE_SIZE),
            );
        } catch (error: unknown) {
            showToast(Level.warn, errorMessage(error, $_("subpaths_load_failed")));
        }
        publishChildren();
    }

    async function toggleExpanded(
        spaceName: string,
        subpath = "/",
        forceExpand: boolean | null = null,
    ) {
        const key = sidebarCacheKey(spaceName, subpath);
        if (expandedSpaces.has(key)) {
            if (forceExpand === true) {
                return;
            }
            expandedSpaces.delete(key);
        } else {
            if (forceExpand === false) {
                return;
            }
            expandedSpaces.add(key);
            await loadChildren(spaceName, subpath);
        }
    }

    function isExpanded(spaceName: string, subpath = "/") {
        return expandedSpaces.has(sidebarCacheKey(spaceName, subpath));
    }

    function getChildrenForSpace(spaceName: string, subpath = "/") {
        return $spaceChildren.data.get(sidebarCacheKey(spaceName, subpath)) || [];
    }

    function hasMoreChildren(spaceName: string, subpath = "/") {
        return $spaceChildren.hasMore.get(sidebarCacheKey(spaceName, subpath)) === true;
    }

    const spaceName = $derived($params.space_name as string);
    const currentSpace = $derived(($spaces ?? []).find((space) => space.shortname === spaceName));
    const spaceLabel = $derived(localizedText(currentSpace?.attributes?.displayname, spaceName));
    // The space root is "current" only when no folder is open.
    const atRoot = $derived(normalizeSubpath($activeRoute?.params?.subpath) === "/");

    // Load the root level for the current space, and again when the route
    // moves to another space while this tree stays mounted.
    $effect(() => {
        const name = spaceName;
        untrack(() => {
            void loadChildren(name);
        });
    });
</script>

<nav class="py-3 px-2 text-sm" aria-label={$_("folders")}>
    <ul class="flex flex-col gap-0.5">
        <li>
            <a
                href={$url("/management/content/" + spaceName)}
                onclick={onNavigate}
                aria-current={atRoot ? "page" : undefined}
                class="flex items-center gap-2 h-9 px-2 rounded-control font-semibold text-text hover:bg-surface-3
                    aria-[current=page]:bg-primary-soft aria-[current=page]:text-primary transition-colors"
            >
                <CodeForkSolid size="md" class="shrink-0 text-text-faint" aria-hidden="true" />
                <span class="truncate">{spaceLabel}</span>
            </a>
        </li>
        {#each getChildrenForSpace(spaceName, "/") as child (child.shortname)}
            <SpacesSubpathItemsSidebar
                {spaceName}
                parentPath="/"
                item={child}
                depth={1}
                {expandedSpaces}
                {loadChildren}
                {toggleExpanded}
                {isExpanded}
                {getChildrenForSpace}
                {hasMoreChildren}
                loadMore={loadMoreChildren}
                {onNavigate}
            />
        {/each}
        {#if hasMoreChildren(spaceName, "/")}
            <li>
                <button
                    type="button"
                    class="w-full text-start text-sm text-primary h-8 px-2 rounded-control hover:bg-surface-3 cursor-pointer"
                    style="padding-inline-start: 2.25rem"
                    onclick={() => loadMoreChildren(spaceName, "/")}
                >
                    {$_("load_more")}
                </button>
            </li>
        {/if}
    </ul>
</nav>
