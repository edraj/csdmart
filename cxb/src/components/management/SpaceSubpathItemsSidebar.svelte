<script lang="ts">
    import { Sidebar, SidebarGroup, SidebarItem } from "flowbite-svelte";
    import { CodeForkSolid } from "flowbite-svelte-icons";
    import { SvelteSet } from "svelte/reactivity";
    import { ResourceType } from "@edraj/tsdmart";
    import { Level, showToast } from "@/utils/toast";
    import { getChildren } from "@/lib/dmart_services";
    import SpacesSubpathItemsSidebar from "./SpacesSubpathItemsSidebar.svelte";
    import { params } from "@roxi/routify";
    import { spaces } from "@/stores/management/spaces";
    import { spaceChildren } from "@/stores/global";
    import { sidebarCacheKey } from "@/utils/subpath";
    import { hasMoreRecords } from "@/utils/paging";
    import { _ } from "@/i18n";

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
            } catch (error: any) {
                showToast(
                    Level.warn,
                    error?.response?.data?.error?.message ?? error?.message ?? $_("subpaths_load_failed"),
                );
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
        } catch (error: any) {
            showToast(
                Level.warn,
                error?.response?.data?.error?.message ?? error?.message ?? $_("subpaths_load_failed"),
            );
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

    let currentSpaceNameLabel = $state($params.space_name);
    async function getCurrentSpaceNameLabel() {
        if (!$spaces) return;
        const currentSpace = $spaces.filter(
            (space) => space.shortname === $params.space_name,
        );
        currentSpaceNameLabel =
            currentSpace.length === 1
                ? currentSpace[0].attributes?.displayname?.en ||
                  $params.space_name
                : $params.space_name;
    }
    $effect(() => {
        if ($spaces) {
            getCurrentSpaceNameLabel();
        }
    });

    loadChildren($params.space_name);
</script>

<Sidebar
    position="static"
    class="h-full w-full [&_aside]:w-full [&_aside]:bg-transparent [&_aside]:border-0 [&_aside]:shadow-none"
>
    <SidebarGroup>
        <SidebarItem
            label={currentSpaceNameLabel}
            href={"/management/content/" + $params.space_name}
        >
            {#snippet icon()}
                <div class="flex items-center gap-2">
                    <CodeForkSolid
                        size="md"
                        class="text-gray-500"
                        style="transform: rotate(180deg); position: relative; z-index: 5;"
                    />
                </div>
            {/snippet}
        </SidebarItem>
        {#each getChildrenForSpace($params.space_name, "/") as child (child.shortname)}
            <SpacesSubpathItemsSidebar
                spaceName={$params.space_name}
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
            />
        {/each}
        {#if hasMoreChildren($params.space_name, "/")}
            <li>
                <button
                    type="button"
                    class="w-full text-start text-sm text-primary px-3 py-1.5 hover:underline cursor-pointer"
                    style="margin-inline-start: 20px;"
                    onclick={() => loadMoreChildren($params.space_name, "/")}
                >
                    {$_("load_more")}
                </button>
            </li>
        {/if}
    </SidebarGroup>
</Sidebar>
