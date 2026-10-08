<script lang="ts">
    import {
        ClockArrowOutline,
        CloseOutline,
        DownloadOutline,
        FileCirclePlusOutline,
        FileCopyOutline,
        FileExportOutline,
        SearchOutline,
        TrashBinOutline,
        UploadOutline,
    } from "flowbite-svelte-icons";
    import {
        Button,
        ButtonGroup,
        Input,
        InputAddon,
        Modal,
    } from "flowbite-svelte";
    import ModalCreateEntry from "@/components/management/Modals/ModalCreateEntry.svelte";
    import ModalCSVUpload from "@/components/management/Modals/ModalCSVUpload.svelte";
    import ModalCSVDownload from "@/components/management/Modals/ModalCSVDownload.svelte";
    import ModalBulkMoveCopy from "@/components/management/Modals/ModalBulkMoveCopy.svelte";
    import Prism from "@/components/Prism.svelte";
    import { onMount } from "svelte";
    import { checkAccess } from "@/utils/checkAccess";
    import {
        currentEntry,
        currentListView,
        subpathInManagementNoAction,
    } from "@/stores/global";
    import { bulkBucket } from "@/stores/management/bulk_bucket";
    import { Dmart, RequestType, ResourceType, type ActionRequest } from "@edraj/tsdmart";
    import { _ } from "svelte-i18n";
    import { Level, showToast } from "@/utils/toast";
    import { searchListView } from "@/stores/management/triggers";
    import { user } from "@/stores/user";
    import { goto, params } from "@roxi/routify";

    import {
        bulkMoveEntryToTrash,
    } from "@/utils/entryManagement";
    import {
        normalizeSubpath,
        recordSubpath,
        trashRestoreTarget,
        trashRoot,
    } from "@/utils/subpath";

    let { space_name, subpath }: { space_name: string; subpath: string } =
        $props();

    let canCreate = $state(false);
    let canUploadCSV = $state(false);
    let canDownloadCSV = $state(false);
    let isCSVDownloadModalOpen = $state(false);

    const isEntryTrash = $derived(
        space_name === "personal" &&
        normalizeSubpath(subpath).startsWith(trashRoot($user.shortname ?? "")),
    );

    onMount(() => {
        if ($currentEntry?.entry?.payload?.body?.allow_csv) {
            canDownloadCSV = true;
        }
        if ($currentEntry?.entry?.payload?.body?.allow_upload_csv) {
            canUploadCSV = true;
        }

        if (space_name === "management" && subpath === "/") {
            canCreate = false;
            canUploadCSV = false;
            return;
        } else if (space_name === "management" && subpath === "health_check") {
            canCreate = false;
            canUploadCSV = false;
            return;
        }
        if (space_name === "management") {
            if (subpathInManagementNoAction.includes(subpath)) {
                canCreate = checkAccess(
                    "create",
                    space_name,
                    subpath,
                    subpath.slice(0, -1),
                );
            } else {
                canCreate = checkAccess(
                    "create",
                    space_name,
                    subpath,
                    "content",
                );
            }
        } else {
            canCreate =
                checkAccess("create", space_name, subpath, "content") ||
                checkAccess("create", space_name, subpath, "folder");
        }
    });

    let isOpen = $state(false);

    let isActionLoading = $state(false);
    let openDeleteModal = $state(false);
    let modelError: any = $state(null);
    let forceDelete = $state(false);
    const showForce = $derived(
        $bulkBucket.some(
            (b) => b.resource_type === ResourceType.folder || b.resource_type === ResourceType.user,
        ),
    );
    function deleteCurrentEntry() {
        modelError = null;
        forceDelete = false;
        openDeleteModal = true;
    }
    async function handleBulkDelete() {
        if ($bulkBucket.length) {
            try {
                isActionLoading = true;
                modelError = null;
                // Each record names its own subpath: on a non-exact list (the
                // Trash page, folders with expand_children) rows come from
                // several subpaths, and the list's own would be wrong for them.
                const records = $bulkBucket.map((b) => ({
                    resource_type: b.resource_type as ResourceType,
                    shortname: b.shortname,
                    subpath: recordSubpath(b, subpath),
                    attributes: {},
                }));

                const request_body: ActionRequest & { force?: boolean } = {
                    space_name,
                    request_type: RequestType.delete,
                    force: showForce && forceDelete,
                    records: records,
                };
                const response = await Dmart.request(request_body);

                if (response?.status === "success") {
                    showToast(Level.info);
                    await $currentListView?.fetchPageRecords();
                    bulkBucket.set([]);
                    openDeleteModal = false;
                } else {
                    showToast(Level.warn);
                    modelError = response;
                }
            } catch (error: any) {
                modelError = error.response?.data?.error;
                showToast(
                    Level.warn,
                    "Failed to delete entries. Please try again later.",
                );
            } finally {
                isActionLoading = false;
            }
        }
    }

    async function handleBulkTrash() {
        if ($bulkBucket.length) {
            try {
                isActionLoading = true;
                const result = await bulkMoveEntryToTrash(
                    $state.snapshot($bulkBucket),
                    space_name,
                    $user.shortname ?? "",
                );

                if (result.success) {
                    await $currentListView?.fetchPageRecords();
                    $bulkBucket = [];
                }
            } catch {
                showToast(
                    Level.warn,
                    "Failed to move entries to trash. Please try again later.",
                );
            } finally {
                isActionLoading = false;
            }
        }
    }


    let searchInput = $state($searchListView);
    async function handleSearch(e?: Event) {
        e?.preventDefault();
        searchListView.set(searchInput);
        // A new search starts from page 1; keeping the old page offset
        // would land past the end of a smaller result set.
        const { page: _page, search: _search, ...rest } = $params;
        $goto("$leaf", searchInput ? { ...rest, search: searchInput } : rest);
    }

    let isCSVUploadModalOpen = $state(false);
    function handleCSVUploadModal() {
        isCSVUploadModalOpen = true;
    }

    async function restoreEntries() {
        isActionLoading = true;

        try {
            const records = $bulkBucket.map((b) => {
                // `/people/<user>/trash/<space>/<subpath>` → where it came from.
                // The helper handles the leading slash that used to shift the
                // split indices and make the destination space "trash".
                const target = trashRestoreTarget(b.subpath);
                if (!target) {
                    throw new Error($_("not_in_trash"));
                }
                const srcSubpath = recordSubpath(b, subpath);

                return {
                    resource_type: b.resource_type as ResourceType,
                    shortname: b.shortname,
                    subpath: srcSubpath,
                    attributes: {
                        src_space_name: "personal",
                        src_subpath: srcSubpath,
                        src_shortname: b.shortname,

                        dest_space_name: target.space_name,
                        dest_subpath: target.subpath,
                        dest_shortname: b.shortname,
                    },
                };
            });

            await Dmart.request({
                space_name: "personal",
                request_type: RequestType.move,
                records: records,
            });
            bulkBucket.set([]);
            await $currentListView?.fetchPageRecords();
            showToast(Level.info, $_("entries_restored"));
        } catch (error: any) {
            showToast(
                Level.warn,
                error?.response?.data?.error?.message ?? error?.message ?? $_("entries_restore_failed"),
            );
        } finally {
            isActionLoading = false;
        }
    }

    let isBulkMoveCopyOpen = $state(false);
    let bulkActionType = $state("move");

    function openBulkMove() {
        bulkActionType = "move";
        isBulkMoveCopyOpen = true;
    }

    function openBulkCopy() {
        bulkActionType = "copy";
        isBulkMoveCopyOpen = true;
    }
</script>

<div class="flex flex-col md:flex-row justify-between items-center my-2 mx-3">
    <div class="w-1/2">
        <form onsubmit={handleSearch}>
            <ButtonGroup class="w-full">
                <Input
                    id="website-admin"
                    placeholder="Search..."
                    bind:value={searchInput}
                    type="search"
                />
                {#if searchInput.length > 0}
                    <InputAddon
                        class="cursor-pointer"
                        onclick={(e) => {
                            searchInput = "";
                            handleSearch(e);
                        }}
                    >
                        <CloseOutline
                            class="h-4 w-4 text-gray-500 dark:text-gray-400"
                        />
                    </InputAddon>
                {/if}
                <InputAddon class="cursor-pointer" onclick={handleSearch}>
                    <SearchOutline
                        class="h-4 w-4 text-gray-500 dark:text-gray-400"
                    />
                </InputAddon>
            </ButtonGroup>
        </form>
    </div>
    <div>
        {#if $bulkBucket.length === 0}
            {#if canCreate && !isEntryTrash}
                <Button
                    class="bg-primary cursor-pointer"
                    size="xs"
                    onclick={() => (isOpen = true)}
                >
                    <FileCirclePlusOutline size="md" /> Create
                </Button>
            {/if}
            {#if canUploadCSV}
                <Button
                    class="text-primary cursor-pointer"
                    size="xs"
                    outline
                    onclick={handleCSVUploadModal}
                >
                    <UploadOutline size="md" /> Upload
                </Button>
            {/if}
            {#if canDownloadCSV}
                <Button
                    class="text-primary cursor-pointer"
                    size="xs"
                    outline
                    onclick={() => (isCSVDownloadModalOpen = true)}
                >
                    <DownloadOutline size="md" /> Download
                </Button>
            {/if}
        {/if}
        {#if $bulkBucket.length}
            {#if isEntryTrash}
                <Button
                    class="text-primary cursor-pointer"
                    size="xs"
                    outline
                    onclick={restoreEntries}
                >
                    <ClockArrowOutline size="md" /> Restore
                </Button>
                <Button
                    class="text-red-500 cursor-pointer"
                    size="xs"
                    outline
                    onclick={deleteCurrentEntry}
                    disabled={isActionLoading}
                >
                    <TrashBinOutline size="md" /> Bulk delete
                </Button>
            {:else}
                <Button
                    class="text-primary cursor-pointer"
                    size="xs"
                    outline
                    onclick={openBulkMove}
                >
                    <FileExportOutline size="md" /> Bulk Move
                </Button>
                <Button
                    class="text-primary cursor-pointer"
                    size="xs"
                    outline
                    onclick={openBulkCopy}
                >
                    <FileCopyOutline size="md" /> Bulk Copy
                </Button>
                <Button
                    class="text-red-500 cursor-pointer"
                    size="xs"
                    outline
                    onclick={handleBulkTrash}
                    disabled={isActionLoading}
                >
                    <TrashBinOutline size="md" /> Bulk Trash
                </Button>
                <Button
                    class="text-red-600 cursor-pointer"
                    size="xs"
                    outline
                    onclick={deleteCurrentEntry}
                >
                    <TrashBinOutline size="md" /> Bulk delete
                </Button>
            {/if}
        {/if}
    </div>
</div>

{#if canCreate}
    <ModalCreateEntry {space_name} {subpath} bind:isOpen />
{/if}

{#if canUploadCSV}
    <ModalCSVUpload {space_name} {subpath} bind:isOpen={isCSVUploadModalOpen} />
{/if}

{#if canDownloadCSV}
    <ModalCSVDownload
        {space_name}
        {subpath}
        bind:isOpen={isCSVDownloadModalOpen}
    />
{/if}

<ModalBulkMoveCopy
    {space_name}
    {subpath}
    bind:isOpen={isBulkMoveCopyOpen}
    actionType={bulkActionType}
/>

<Modal bind:open={openDeleteModal} size="md" title="Confirm Deletion">
    <p class="text-center mb-6">
        Are you sure you want to delete <span class="font-bold"
            >{$bulkBucket.map((e) => e.shortname).join(", ")}</span
        >
        {$bulkBucket.length === 1 ? "entry" : "entries"}?<br />
        This action cannot be undone.
    </p>

    {#if showForce}
        <label class="flex items-start gap-2 mb-4 text-sm cursor-pointer">
            <input type="checkbox" bind:checked={forceDelete} class="mt-0.5" />
            <span>
                <span class="font-semibold">{$_("force_delete")}</span>
                <span class="block text-gray-600">{$_("force_delete_help")}</span>
            </span>
        </label>
    {/if}

    {#if modelError}
        <div class="mt-4">
            <p class="text-red-600 font-medium mb-2">Error:</p>
            <div class="max-h-60 overflow-auto">
                <Prism code={modelError} />
            </div>
        </div>
    {/if}

    <div class="flex justify-between w-full">
        <Button color="alternative" onclick={() => (openDeleteModal = false)}
            >Cancel</Button
        >
        <Button
            color="red"
            onclick={handleBulkDelete}
            disabled={isActionLoading}
            >{isActionLoading ? "Deleting..." : "Delete"}</Button
        >
    </div>
</Modal>
