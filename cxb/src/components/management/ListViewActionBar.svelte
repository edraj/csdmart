<script lang="ts">
    import {
        ClockArrowOutline,
        DownloadOutline,
        FileCirclePlusOutline,
        FileCopyOutline,
        FileExportOutline,
        TrashBinOutline,
        UploadOutline,
    } from "flowbite-svelte-icons";
    import { Button } from "flowbite-svelte";
    import Toolbar from "@/components/ui/Toolbar.svelte";
    import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
    import Lazy from "@/components/ui/Lazy.svelte";
    import { onMount } from "svelte";
    import { checkAccess } from "@/utils/checkAccess";
    import { currentEntry, currentListView, subpathInManagementNoAction } from "@/stores/global";
    import { bulkBucket } from "@/stores/management/bulk_bucket";
    import { Dmart, RequestType, ResourceType, type ActionRequest } from "@edraj/tsdmart";
    import { _ } from "@/i18n";
    import { Level, showToast } from "@/utils/toast";
    import { searchListView } from "@/stores/management/triggers";
    import { user } from "@/stores/user";
    import { goto, params } from "@roxi/routify";
    import { errorMessage } from "@/utils/errorMessage";
    import { bulkMoveEntryToTrash } from "@/utils/entryManagement";
    import { normalizeSubpath, recordSubpath, trashRestoreTarget, trashRoot } from "@/utils/subpath";

    let { space_name, subpath }: { space_name: string; subpath: string } = $props();

    let canCreate = $state(false);
    let canUploadCSV = $state(false);
    let canDownloadCSV = $state(false);

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
                canCreate = checkAccess("create", space_name, subpath, subpath.slice(0, -1));
            } else {
                canCreate = checkAccess("create", space_name, subpath, "content");
            }
        } else {
            canCreate =
                checkAccess("create", space_name, subpath, "content") ||
                checkAccess("create", space_name, subpath, "folder");
        }
    });

    // ── Modals: each loads its component (and the editors it pulls in) the
    //    first time it opens, so none of them sits in the list's chunk. ─────
    let isCreateOpen = $state(false);
    let isCSVUploadModalOpen = $state(false);
    let isCSVDownloadModalOpen = $state(false);
    let isBulkMoveCopyOpen = $state(false);
    let bulkActionType = $state<"move" | "copy">("move");

    function openBulkMove() {
        bulkActionType = "move";
        isBulkMoveCopyOpen = true;
    }

    function openBulkCopy() {
        bulkActionType = "copy";
        isBulkMoveCopyOpen = true;
    }

    // ── Destructive actions: one dialog, two verbs ──────────────────────────
    // `confirmAction` is the verb the dialog speaks; `confirmOpen` is bound to
    // the dialog so Escape and the overlay close it the same way Cancel does.
    let confirmAction = $state<"delete" | "trash">("delete");
    let confirmOpen = $state(false);
    let isActionLoading = $state(false);
    let actionError = $state<unknown>(null);
    let forceDelete = $state(false);
    const showForce = $derived(
        $bulkBucket.some(
            (b) => b.resource_type === ResourceType.folder || b.resource_type === ResourceType.user,
        ),
    );
    const selectedCount = $derived($bulkBucket.length);
    const selectedNames = $derived($bulkBucket.map((e) => e.shortname).join(", "));

    function askDelete() {
        actionError = null;
        forceDelete = false;
        confirmAction = "delete";
        confirmOpen = true;
    }

    function askTrash() {
        actionError = null;
        confirmAction = "trash";
        confirmOpen = true;
    }

    async function handleBulkDelete() {
        if (!$bulkBucket.length) return;
        try {
            isActionLoading = true;
            actionError = null;
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
                showToast(Level.info, $_("entries_deleted", { values: { count: records.length } }));
                await $currentListView?.fetchPageRecords();
                bulkBucket.set([]);
                confirmOpen = false;
            } else {
                showToast(Level.warn);
                actionError = response;
            }
        } catch (error: unknown) {
            actionError = error;
            showToast(Level.warn, errorMessage(error, $_("entries_delete_failed")));
        } finally {
            isActionLoading = false;
        }
    }

    async function handleBulkTrash() {
        if (!$bulkBucket.length) return;
        try {
            isActionLoading = true;
            actionError = null;
            const result = await bulkMoveEntryToTrash(
                $state.snapshot($bulkBucket),
                space_name,
                $user.shortname ?? "",
            );

            if (result.success) {
                await $currentListView?.fetchPageRecords();
                $bulkBucket = [];
                confirmOpen = false;
            } else {
                actionError = result.errorMessage;
            }
        } catch (error: unknown) {
            actionError = error;
            showToast(Level.warn, errorMessage(error, $_("entries_trash_failed")));
        } finally {
            isActionLoading = false;
        }
    }

    // ── Search ──────────────────────────────────────────────────────────────
    // The list clears the shared query when the folder changes; the box
    // follows, and typing overrides it until the next change.
    let searchInput = $derived($searchListView);

    function handleSearch(queryText: string) {
        searchListView.set(queryText);
        // A new search starts from page 1; keeping the old page offset
        // would land past the end of a smaller result set.
        const { page: _page, search: _search, ...rest } = $params;
        $goto("$leaf", queryText ? { ...rest, search: queryText } : rest);
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
        } catch (error: unknown) {
            showToast(Level.warn, errorMessage(error, $_("entries_restore_failed")));
        } finally {
            isActionLoading = false;
        }
    }

</script>

<Toolbar
    class="my-2 mx-3"
    bind:search={searchInput}
    placeholder={$_("search")}
    onSearch={handleSearch}
>
    {#if selectedCount === 0}
        {#if canCreate && !isEntryTrash}
            <Button size="sm" color="primary" onclick={() => (isCreateOpen = true)}>
                <FileCirclePlusOutline size="sm" class="me-1.5" aria-hidden="true" />
                {$_("create")}
            </Button>
        {/if}
        {#if canUploadCSV}
            <Button size="sm" color="alternative" onclick={() => (isCSVUploadModalOpen = true)}>
                <UploadOutline size="sm" class="me-1.5" aria-hidden="true" />
                {$_("upload")}
            </Button>
        {/if}
        {#if canDownloadCSV}
            <Button size="sm" color="alternative" onclick={() => (isCSVDownloadModalOpen = true)}>
                <DownloadOutline size="sm" class="me-1.5" aria-hidden="true" />
                {$_("download")}
            </Button>
        {/if}
    {:else}
        <span class="text-sm text-text-muted tabular-nums me-1" aria-live="polite">
            {$_("n_selected", { values: { count: selectedCount } })}
        </span>
        {#if isEntryTrash}
            <Button size="sm" color="alternative" onclick={restoreEntries} disabled={isActionLoading}>
                <ClockArrowOutline size="sm" class="me-1.5" aria-hidden="true" />
                {$_("restore")}
            </Button>
            <Button size="sm" color="red" outline onclick={askDelete} disabled={isActionLoading}>
                <TrashBinOutline size="sm" class="me-1.5" aria-hidden="true" />
                {$_("bulk_delete")}
            </Button>
        {:else}
            <Button size="sm" color="alternative" onclick={openBulkMove} disabled={isActionLoading}>
                <FileExportOutline size="sm" class="me-1.5 rtl:rotate-180" aria-hidden="true" />
                {$_("bulk_move")}
            </Button>
            <Button size="sm" color="alternative" onclick={openBulkCopy} disabled={isActionLoading}>
                <FileCopyOutline size="sm" class="me-1.5" aria-hidden="true" />
                {$_("bulk_copy")}
            </Button>
            <Button size="sm" color="red" outline onclick={askTrash} disabled={isActionLoading}>
                <TrashBinOutline size="sm" class="me-1.5" aria-hidden="true" />
                {$_("bulk_trash")}
            </Button>
            <Button size="sm" color="red" outline onclick={askDelete} disabled={isActionLoading}>
                <TrashBinOutline size="sm" class="me-1.5" aria-hidden="true" />
                {$_("bulk_delete")}
            </Button>
        {/if}
    {/if}
</Toolbar>

{#if canCreate && isCreateOpen}
    <Lazy load={() => import("@/components/management/Modals/ModalCreateEntry.svelte")} pending="none">
        {#snippet children(ModalCreateEntry)}
            <ModalCreateEntry {space_name} {subpath} bind:isOpen={isCreateOpen} />
        {/snippet}
    </Lazy>
{/if}

{#if canUploadCSV && isCSVUploadModalOpen}
    <Lazy load={() => import("@/components/management/Modals/ModalCSVUpload.svelte")} pending="none">
        {#snippet children(ModalCSVUpload)}
            <ModalCSVUpload {space_name} {subpath} bind:isOpen={isCSVUploadModalOpen} />
        {/snippet}
    </Lazy>
{/if}

{#if canDownloadCSV && isCSVDownloadModalOpen}
    <Lazy load={() => import("@/components/management/Modals/ModalCSVDownload.svelte")} pending="none">
        {#snippet children(ModalCSVDownload)}
            <ModalCSVDownload {space_name} {subpath} bind:isOpen={isCSVDownloadModalOpen} />
        {/snippet}
    </Lazy>
{/if}

{#if isBulkMoveCopyOpen}
    <Lazy load={() => import("@/components/management/Modals/ModalBulkMoveCopy.svelte")} pending="none">
        {#snippet children(ModalBulkMoveCopy)}
            <ModalBulkMoveCopy
                {space_name}
                {subpath}
                bind:isOpen={isBulkMoveCopyOpen}
                actionType={bulkActionType}
            />
        {/snippet}
    </Lazy>
{/if}

<ConfirmDialog
    bind:open={confirmOpen}
    variant="danger"
    title={confirmAction === "trash"
        ? $_("trash_n_entries", { values: { count: selectedCount } })
        : $_("delete_n_entries", { values: { count: selectedCount } })}
    body={confirmAction === "trash"
        ? $_("confirm_trash_entries", { values: { names: selectedNames } })
        : `${$_("confirm_delete_entries", { values: { names: selectedNames } })}\n${$_("cannot_be_undone")}`}
    confirmLabel={confirmAction === "trash" ? $_("move_to_trash") : $_("delete")}
    loading={isActionLoading}
    loadingLabel={confirmAction === "trash" ? $_("moving") : $_("deleting")}
    error={actionError}
    onConfirm={confirmAction === "trash" ? handleBulkTrash : handleBulkDelete}
>
    {#if confirmAction === "delete" && showForce}
        <label class="flex items-start gap-2 text-sm cursor-pointer">
            <input type="checkbox" bind:checked={forceDelete} class="mt-0.5 h-4 w-4 rounded-control border-border-strong text-primary focus:ring-primary" />
            <span>
                <span class="font-semibold text-text">{$_("force_delete")}</span>
                <span class="block text-text-muted">{$_("force_delete_help")}</span>
            </span>
        </label>
    {/if}
</ConfirmDialog>
