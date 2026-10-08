<script lang="ts">
    import {
        Button,
        Modal,
        Sidebar,
        SidebarGroup,
        SidebarItem,
        Spinner,
    } from "flowbite-svelte";
    import { CodeForkSolid } from "flowbite-svelte-icons";
    import { JSONEditor, Mode } from "svelte-jsoneditor";
    import Prism from "@/components/Prism.svelte";
    import { Dmart, RequestType, ResourceType, type ApiResponseRecord } from "@edraj/tsdmart";
    import { Level, showToast } from "@/utils/toast";
    import { getChildren, getSpaces } from "@/lib/dmart_services";
    import { jsonEditorContentParser } from "@/utils/jsonEditor";
    import SpacesSubpathItemsSidebar from "./SpacesSubpathItemsSidebar.svelte";
    import { params } from "@roxi/routify";
    import MetaForm from "./forms/MetaForm.svelte";
    import { spaces } from "@/stores/management/spaces";
    import { spaceChildren } from "@/stores/global";
    import { removeEmpty } from "@/utils/compare";
    import { sidebarCacheKey } from "@/utils/subpath";
    import { hasMoreRecords } from "@/utils/paging";
    import { _ } from "@/i18n";

    // One page of folder children per tree node; "Load more" appends the next.
    const CHILDREN_PAGE_SIZE = 50;

    let expandedSpaces = $state(new Set<string>());
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
        expandedSpaces = new Set(expandedSpaces);
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

    let viewMetaModal = $state(false);
    let editModal = $state(false);
    let deleteModal = $state(false);
    let addSpaceModal = $state(false);
    let selectedSpace: ApiResponseRecord | null = $state(null);
    let modelError: string | null = $state(null);

    let spaceFormData = $state({
        shortname: "",
        is_active: true,
        slug: "",
        displayname: {
            en: "",
            ar: "",
            ku: "",
        },
        description: {
            en: "",
            ar: "",
            ku: "",
        },
    });
    let validateSpaceForm = $state(() => true);

    let isActionLoading = $state(false);

    let jeContent = $state({ json: undefined });

    async function createSpace() {
        if (!validateSpaceForm()) {
            return;
        }

        if (spaceFormData.shortname.trim()) {
            try {
                isActionLoading = true;
                modelError = null;
                const attributes = {
                    is_active: spaceFormData.is_active,
                    slug: spaceFormData.slug,
                    displayname: spaceFormData.displayname,
                    description: spaceFormData.description,
                };
                await Dmart.request({
                    space_name: spaceFormData.shortname.trim(),
                    request_type: RequestType.create,
                    records: [
                        {
                            resource_type: ResourceType.space,
                            shortname: spaceFormData.shortname.trim(),
                            subpath: "/",
                            attributes: removeEmpty(attributes),
                        },
                    ],
                });
                showToast(
                    Level.info,
                    `Space "${spaceFormData.shortname.trim()}" created successfully!`,
                );
                await getSpaces();
                addSpaceModal = false;
            } catch (error: any) {
                modelError = error?.response?.data ?? error?.message;
            } finally {
                isActionLoading = false;
            }
        }
    }

    async function saveChanges() {
        if (selectedSpace) {
            let record;
            try {
                record = jsonEditorContentParser(jeContent);
            } catch (e) {
                modelError = "Invalid JSON format";
                return;
            }
            delete record.uuid;
            try {
                isActionLoading = true;
                modelError = null;
                await Dmart.request({
                    space_name: selectedSpace.shortname,
                    request_type: RequestType.update,
                    records: [
                        {
                            resource_type: ResourceType.space,
                            shortname: selectedSpace.shortname,
                            subpath: "/",
                            attributes: record.attributes,
                        },
                    ],
                });
                editModal = false;
                showToast(
                    Level.info,
                    `Space "${selectedSpace.shortname}" updated successfully!`,
                );
                await getSpaces();
            } catch (error: any) {
                modelError = error?.response?.data ?? error?.message;
            } finally {
                isActionLoading = false;
            }
        }
    }

    async function deleteSpace() {
        if (selectedSpace) {
            try {
                isActionLoading = true;
                modelError = null;
                await Dmart.request({
                    space_name: selectedSpace.shortname,
                    request_type: RequestType.delete,
                    records: [
                        {
                            resource_type: ResourceType.space,
                            shortname: selectedSpace.shortname,
                            subpath: "/",
                            attributes: {},
                        },
                    ],
                });
                showToast(
                    Level.info,
                    `Space "${selectedSpace.shortname}" has been deleted successfully!`,
                );
                deleteModal = false;
                selectedSpace = null;
                await getSpaces();
            } catch (error: any) {
                modelError = error?.response?.data ?? error?.message;
            } finally {
                isActionLoading = false;
            }
        }
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

<Modal bind:open={addSpaceModal} size="xl" title="Add New Space">
    <div class="space-y-4">
        <MetaForm
            bind:formData={spaceFormData}
            bind:validateFn={validateSpaceForm}
            isCreate={true}
        />

        {#if modelError}
            <div class="mt-4">
                <p class="text-red-600 font-medium mb-2">{$_("error")}:</p>
                <div class="max-h-60 overflow-auto">
                    <Prism code={modelError} />
                </div>
            </div>
        {/if}
    </div>

    <div class="flex justify-between w-full mt-4">
        <Button color="alternative" onclick={() => (addSpaceModal = false)}
            >{$_("cancel")}</Button
        >
        <Button class="bg-primary" onclick={createSpace}>
            {#if isActionLoading}
                <Spinner class="me-3" size="4" color="blue" />
                Creating ...
            {:else}
                Create
            {/if}
        </Button>
    </div>
</Modal>

<Modal bind:open={viewMetaModal} size="xl" title="Space Metadata" autoclose>
    <div>
        {#if selectedSpace}
            <JSONEditor content={jeContent} readOnly={true} />
        {/if}
    </div>
</Modal>

<Modal bind:open={editModal} size="xl" title="Edit Space">
    <div>
        {#if selectedSpace}
            <JSONEditor
                bind:content={jeContent}
                readOnly={false}
                mode={Mode.text}
            />
        {/if}

        {#if modelError}
            <div class="mt-4">
                <p class="text-red-600 font-medium mb-2">{$_("error")}:</p>
                <div class="max-h-60 overflow-auto">
                    <Prism code={modelError} />
                </div>
            </div>
        {/if}
    </div>
    <div class="flex justify-between w-full">
        <Button color="alternative" onclick={() => (editModal = false)}
            >{$_("cancel")}</Button
        >
        <Button class="bg-primary" onclick={saveChanges}>
            {#if isActionLoading}
                <Spinner class="me-3" size="4" color="blue" />
                Saving Changes ...
            {:else}
                Save Changes
            {/if}
        </Button>
    </div>
</Modal>

<Modal bind:open={deleteModal} size="md" title="Confirm Deletion">
    {#if selectedSpace}
        <p class="text-center mb-6">
            Are you sure you want to delete the space <span class="font-bold"
                >{selectedSpace.shortname}</span
            >?<br />
            This action cannot be undone.
        </p>
    {/if}

    {#if modelError}
        <div class="mt-4">
            <p class="text-red-600 font-medium mb-2">{$_("error")}:</p>
            <div class="max-h-60 overflow-auto">
                <Prism code={modelError} />
            </div>
        </div>
    {/if}

    <div class="flex justify-between w-full">
        <Button color="alternative" onclick={() => (deleteModal = false)}
            >{$_("cancel")}</Button
        >
        <Button color="red" onclick={deleteSpace}>
            {#if isActionLoading}
                <Spinner class="me-3" size="4" color="blue" />
                Deleting ...
            {:else}
                {$_("delete")}
            {/if}
        </Button>
    </div>
</Modal>
