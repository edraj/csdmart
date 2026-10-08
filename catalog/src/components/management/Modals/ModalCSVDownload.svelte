<script lang="ts">
  import { _ } from "@/i18n";
    import {
        Dmart,
        QueryType,
        type QueryRequest,
        RequestType,
        ResourceType,
    } from "@edraj/tsdmart";
    import { downloadFile } from "@shared/download-file";
    import {
        warningToastMessage,
        successToastMessage,
        errorToastMessage,
    } from "@/lib/toasts_messages";
    import Modal from "@/components/Modal.svelte";

    interface Props {
        isOpen?: boolean;
        space_name: string;
        subpath: string;
        query?: QueryRequest | null;
        availableSpaces?: { shortname: string; displayname?: string }[];
        folderMetadata?: any;
        indexAttributes?: any[];
        onUpdateFolder?: () => void;
    }

    let {
        isOpen = $bindable(false),
        space_name,
        subpath,
        query = null,
        availableSpaces = [],
        folderMetadata = null,
        indexAttributes = [],
        onUpdateFolder = () => {},
    }: Props = $props();

    // Follows the prop until the user picks another space (writable $derived).
    let selectedSpace = $derived(space_name);
    let downloadAll = $state(false);
    let limit = $state("");
    let startDate = $state("");
    let endDate = $state("");

    let isCSVDownloadInProgress = $state(false);

    // column editing state
    let editingCsvColumns = $state<{ key: string; name: string }[]>([]);
    let isSavingColumns = $state(false);

    // Reset selected space when modal opens
    $effect(() => {
        if (isOpen) {
            selectedSpace = space_name;
            const existingColumns =
                folderMetadata?.payload?.body?.csv_columns ||
                folderMetadata?.attributes?.payload?.body?.csv_columns ||
                [];
            editingCsvColumns = JSON.parse(JSON.stringify(existingColumns));
        }
    });

    function getParentPath(path: string) {
        if (!path || path === "/") return "/";
        const parts = path.split("/").filter((p) => p);
        parts.pop();
        return parts.length > 0 ? "/" + parts.join("/") : "/";
    }

    function addCsvColumnSetting() {
        editingCsvColumns = [...editingCsvColumns, { key: "", name: "" }];
    }

    function removeCsvColumnSetting(index: number) {
        editingCsvColumns = editingCsvColumns.filter((_, i) => i !== index);
    }

    function copyIndexAttributes() {
        let indexAttrs =
            indexAttributes && indexAttributes.length > 0
                ? indexAttributes
                : [];
        if (indexAttrs.length === 0) {
            indexAttrs = [
                { key: "displayname", name: "Display Name" },
                { key: "status", name: "Status" },
                { key: "author", name: "Author" },
                { key: "updated_at", name: "Last Modified" },
            ];
        }
        editingCsvColumns = JSON.parse(JSON.stringify(indexAttrs));
    }

    async function handleUpdateCsvColumns() {
        if (!folderMetadata) return;
        isSavingColumns = true;
        try {
            const response = await Dmart.request({
                space_name: selectedSpace,
                request_type: RequestType.update,
                records: [
                    {
                        resource_type: ResourceType.folder,
                        shortname: folderMetadata.shortname,
                        subpath: getParentPath(subpath),
                        attributes: {
                            payload: {
                                ...(folderMetadata?.payload ||
                                    folderMetadata?.attributes?.payload ||
                                    {}),
                                body: {
                                    ...(folderMetadata?.payload?.body ||
                                        folderMetadata?.attributes?.payload
                                            ?.body ||
                                        {}),
                                    csv_columns: editingCsvColumns.filter(
                                        (a) => a.key.trim() && a.name.trim(),
                                    ),
                                },
                            },
                        },
                    },
                ],
            });

            if (response && response.status === "success") {
                successToastMessage("CSV Columns Updated");
                if (onUpdateFolder) onUpdateFolder();
            } else {
                errorToastMessage("Failed to update CSV Columns");
            }
        } catch (err: any) {
            errorToastMessage("Error updating CSV Columns: " + err.message);
        } finally {
            isSavingColumns = false;
        }
    }

    function parseSpacesForSelect(spaces: typeof availableSpaces) {
        return spaces.map((s) => ({
            name: s.displayname || s.shortname,
            value: s.shortname,
        }));
    }

    async function handleDownloadCSV() {
        try {
            isCSVDownloadInProgress = true;

            const csvQuery: QueryRequest = query
                ? { ...query }
                : {
                      space_name: selectedSpace,
                      subpath,
                      type: QueryType.search,
                      search: "",
                      limit: 1000,
                      offset: 0,
                  };

            if (!downloadAll) {
                if (limit) {
                    csvQuery.limit = parseInt(limit);
                }

                if (startDate) {
                    csvQuery.from_date = startDate;
                }

                if (endDate) {
                    csvQuery.to_date = endDate;
                }
            } else {
                csvQuery.limit = 1_000_000;
                delete csvQuery.from_date;
                delete csvQuery.to_date;
            }

            const data = await Dmart.csv(csvQuery) as any;
            downloadFile(
                data,
                `${selectedSpace}_${subpath.replace(/\//g, "_")}.csv`,
                "text/csv",
            );
            isOpen = false;
        } catch {
            warningToastMessage("Failed to download CSV");
        } finally {
            isCSVDownloadInProgress = false;
        }
    }
</script>

{#if isOpen}
    <Modal
        onClose={() => (isOpen = false)}
        title={$_("users_page.download_csv")}
        ariaLabel="Download CSV"
        size="xl"
    >
        {#snippet icon()}
            <svg
                class="w-6 h-6"
                fill="none"
                stroke="currentColor"
                viewBox="0 0 24 24"
                xmlns="http://www.w3.org/2000/svg"
            >
                <path
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    stroke-width="2"
                    d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4"
                ></path>
            </svg>
        {/snippet}

        <div class="space-y-4">
                    {#if availableSpaces.length > 0}
                        <div class="space-y-1.5">
                            <label
                                for="space"
                                class="block text-[10px] uppercase font-bold text-text-faint tracking-wider px-1"
                                >{$_("fields.space")}</label
                            >
                            <select
                                id="space"
                                class="w-full px-4 py-2 bg-surface border border-border rounded-xl text-sm focus:ring-2 focus:ring-primary text-text"
                                bind:value={selectedSpace}
                            >
                                {#each parseSpacesForSelect(availableSpaces) as space (space.value)}
                                    <option value={space.value}
                                        >{space.name}</option
                                    >
                                {/each}
                            </select>
                        </div>
                    {/if}

                    <div class="space-y-1.5">
                        <label
                            for="limit"
                            class="block text-[10px] uppercase font-bold text-text-faint tracking-wider px-1"
                            >{$_("csv_modal.limit")}</label
                        >
                        <input
                            id="limit"
                            type="number"
                            placeholder={$_("labels.enter_limit")}
                            bind:value={limit}
                            min="1"
                            disabled={downloadAll}
                            class="w-full px-4 py-2 bg-surface border border-border rounded-xl text-sm focus:ring-2 focus:ring-primary text-text disabled:opacity-50"
                        />
                    </div>

                    <div class="space-y-1.5">
                        <label
                            for="startDate"
                            class="block text-[10px] uppercase font-bold text-text-faint tracking-wider px-1"
                            >{$_("csv_modal.start_date")}</label
                        >
                        <input
                            id="startDate"
                            type="date"
                            bind:value={startDate}
                            disabled={downloadAll}
                            class="w-full px-4 py-2 bg-surface border border-border rounded-xl text-sm focus:ring-2 focus:ring-primary text-text disabled:opacity-50"
                        />
                    </div>

                    <div class="space-y-1.5">
                        <label
                            for="endDate"
                            class="block text-[10px] uppercase font-bold text-text-faint tracking-wider px-1"
                            >{$_("csv_modal.end_date")}</label
                        >
                        <input
                            id="endDate"
                            type="date"
                            bind:value={endDate}
                            disabled={downloadAll}
                            class="w-full px-4 py-2 bg-surface border border-border rounded-xl text-sm focus:ring-2 focus:ring-primary text-text disabled:opacity-50"
                        />
                    </div>

                    <div class="flex items-center gap-3 pt-2 px-1">
                        <input
                            id="downloadAll"
                            type="checkbox"
                            bind:checked={downloadAll}
                            class="w-4 h-4 text-primary bg-surface border border-border rounded focus:ring-primary focus:ring-2"
                        />
                        <label
                            for="downloadAll"
                            class="text-[10px] uppercase font-bold text-text-faint tracking-wider cursor-pointer"
                            >{$_("csv_modal.download_all")}</label
                        >
                    </div>
                </div>

                <div class="mt-8 pt-6 border-t border-border">
                    <div class="flex items-center justify-between mb-4">
                        <h3 class="text-sm font-bold text-text">
                            CSV Columns Config
                        </h3>
                        <div class="flex items-center gap-2 flex-wrap">
                            <button
                                type="button"
                                class="text-[10px] uppercase font-bold text-primary hover:text-primary bg-primary-soft hover:bg-primary-soft px-3 py-1.5 rounded-lg transition-colors"
                                onclick={copyIndexAttributes}
                            >
                                Copy table columns to CSV headers
                            </button>
                            <button
                                type="button"
                                class="p-1.5 text-primary hover:bg-primary-soft rounded-lg transition-colors border border-transparent hover:border-primary/30"
                                onclick={addCsvColumnSetting}
                                title={$_("users_page.add_column")}
                                aria-label={$_("users_page.add_column")}
                            >
                                <svg
                                    class="w-4 h-4"
                                    fill="none"
                                    stroke="currentColor"
                                    viewBox="0 0 24 24"
                                >
                                    <path
                                        stroke-linecap="round"
                                        stroke-linejoin="round"
                                        stroke-width="2"
                                        d="M12 4v16m8-8H4"
                                    ></path>
                                </svg>
                            </button>
                        </div>
                    </div>

                    {#if editingCsvColumns.length === 0}
                        <div
                            class="text-center py-6 bg-surface border border-border rounded-xl mb-4"
                        >
                            <p
                                class="text-[11px] text-text-faint font-medium uppercase tracking-wider"
                            >
                                No columns configured
                            </p>
                            <button
                                type="button"
                                class="mt-2 text-xs font-semibold text-primary hover:text-primary"
                                onclick={addCsvColumnSetting}
                            >
                                + Add First Column
                            </button>
                        </div>
                    {:else}
                        <div class="space-y-3 mb-4">
                            {#each editingCsvColumns as attr, index (index)}
                                <div
                                    class="flex items-start gap-3 bg-surface-2 p-3 border border-border rounded-xl shadow-sm"
                                >
                                    <div class="flex-1 space-y-3">
                                        <div class="space-y-1.5">
                                            <label
                                                class="block text-[10px] uppercase font-bold text-text-faint tracking-wider px-1"
                                                for="csv-column-label-{index}">{$_("users_page.column_label")}</label
                                            >
                                            <input
                                                id="csv-column-label-{index}"
                                                type="text"
                                                bind:value={attr.name}
                                                placeholder={$_("users_page.column_label_placeholder")}
                                                class="w-full px-4 py-2 bg-surface border border-border rounded-xl text-sm focus:ring-2 focus:ring-primary text-text"
                                            />
                                        </div>
                                        <div class="space-y-1.5">
                                            <label
                                                class="block text-[10px] uppercase font-bold text-text-faint tracking-wider px-1"
                                                for="csv-data-key-{index}">{$_("users_page.column_key")}</label
                                            >
                                            <input
                                                id="csv-data-key-{index}"
                                                type="text"
                                                bind:value={attr.key}
                                                placeholder={$_("labels.column_key_example")}
                                                class="w-full px-4 py-2 bg-surface border border-border rounded-xl text-sm font-mono text-primary focus:ring-2 focus:ring-primary"
                                            />
                                        </div>
                                    </div>
                                    <button
                                        type="button"
                                        class="mt-6 p-2 text-danger hover:text-danger hover:bg-danger-soft rounded-lg transition-colors border border-transparent hover:border-danger/30"
                                        onclick={() =>
                                            removeCsvColumnSetting(index)}
                                        aria-label={$_("users_page.remove_column")}
                                    >
                                        <svg
                                            class="w-4 h-4"
                                            fill="none"
                                            stroke="currentColor"
                                            viewBox="0 0 24 24"
                                        >
                                            <path
                                                stroke-linecap="round"
                                                stroke-linejoin="round"
                                                stroke-width="2"
                                                d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16"
                                            ></path>
                                        </svg>
                                    </button>
                                </div>
                            {/each}
                        </div>
                    {/if}

                    <div class="flex justify-end mt-4">
                        <button
                            onclick={handleUpdateCsvColumns}
                            disabled={isSavingColumns || !folderMetadata}
                            class="px-5 py-2 bg-surface-2 border border-border text-text rounded-xl text-xs font-bold hover:bg-surface hover:border-border-strong disabled:opacity-50 disabled:cursor-not-allowed transition-all flex items-center gap-2 shadow-sm"
                        >
                            {#if isSavingColumns}
                                <div
                                    class="w-3 h-3 border-2 border-border-strong border-t-transparent rounded-full animate-spin"
                                ></div>
                                Saving...
                            {:else}
                                Save Columns
                            {/if}
                        </button>
                    </div>
        </div>

        {#snippet footer()}
            <button
                onclick={() => (isOpen = false)}
                class="px-6 py-2.5 text-sm font-medium text-text-muted hover:text-text hover:bg-surface rounded-xl transition-colors border border-transparent"
            >
                Cancel
            </button>
            <button
                onclick={handleDownloadCSV}
                disabled={isCSVDownloadInProgress}
                class="px-8 py-2.5 bg-primary text-text-on-primary rounded-xl text-sm font-semibold hover:bg-primary-hover shadow-md shadow-card disabled:opacity-50 disabled:cursor-not-allowed transition-all flex items-center gap-2"
            >
                {#if isCSVDownloadInProgress}
                    <div
                        class="w-4 h-4 border-2 border-text-on-primary/30 border-t-white rounded-full animate-spin"
                    ></div>
                    Downloading...
                {:else}
                    Download
                {/if}
            </button>
        {/snippet}
    </Modal>
{/if}
