<script lang="ts">
    import { resolveTotal } from "@shared/query-total";
    import { Dmart, QueryType, SortyType, type ApiResponseRecord } from "@edraj/tsdmart";
    import { Modal } from "flowbite-svelte";
    import { onMount } from "svelte";
    import { _ } from "@/i18n";
    import Prism from "../Prism.svelte";
    import Pagination from "@/components/ui/Pagination.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { pageCount } from "@/utils/paging";
    import { rowKey } from "@/utils/rowKey";
    import { formatDate } from "@/utils/format";
    import { errorMessage } from "@/utils/errorMessage";
    import { limitJsonForDisplay } from "@/utils/displayJson";

    let { space_name, subpath, shortname }: { space_name: string; subpath: string; shortname: string } = $props();

    let records: ApiResponseRecord[] = $state([]);
    let loading = $state(true);
    let error: string | null = $state(null);
    let limit = $state(10);
    let offset = $state(0);
    let totalItems = $state(0);
    let showModal = $state(false);
    let modalData: unknown = $state(null);
    const modalPreview = $derived(limitJsonForDisplay(modalData));

    const currentPage = $derived(Math.floor(offset / limit) + 1);

    // Monotonic request id: a slow earlier page must not overwrite a newer one.
    let requestSeq = 0;
    async function fetchHistory() {
        const seq = ++requestSeq;
        loading = true;
        error = null;
        try {
            const response = await Dmart.query({
                type: QueryType.history,
                filter_shortnames: shortname ? [shortname] : [],
                space_name,
                subpath,
                search: "",
                limit,
                offset,
                sort_by: "timestamp",
                sort_type: SortyType.descending,
            });
            if (seq !== requestSeq) return;
            records = response?.records || [];
            totalItems = resolveTotal(response?.attributes?.total, offset + records.length);
        } catch (e: unknown) {
            if (seq !== requestSeq) return;
            error = errorMessage(e, $_("history_load_failed"));
        } finally {
            if (seq === requestSeq) loading = false;
        }
    }

    function goToPage(page: number) {
        const target = Math.min(Math.max(1, page), pageCount(totalItems, limit));
        offset = (target - 1) * limit;
        fetchHistory();
    }

    function changeLimit(newLimit: number) {
        limit = newLimit;
        offset = 0;
        fetchHistory();
    }

    onMount(fetchHistory);

    function formatKey(key: string) {
        return key
            .split(".")
            .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
            .join(" › ");
    }

    function handleModalDetails(value: unknown) {
        modalData = value;
        showModal = true;
    }

    function short(value: unknown): string {
        const text = JSON.stringify(value) ?? "";
        return text.length > 160 ? `${text.slice(0, 160)}…` : text;
    }
</script>

{#if loading && records.length === 0}
    <div class="rounded-card border border-border bg-surface-2 p-4">
        <LoadingState variant="skeleton" rows={6} />
    </div>
{:else}
    <LoadingState variant="overlay" {loading}>
        <div class="space-y-6">
            {#if error}
                <ErrorState compact title={$_("history_load_failed")} message={error} onRetry={fetchHistory} />
            {/if}

            {#each records as record (rowKey(record))}
                <section class="rounded-card border border-border bg-surface-2 shadow-card overflow-hidden">
                    <header class="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-4 py-3 border-b border-border bg-surface">
                        <p class="text-sm text-text">
                            <span class="text-text-muted">{$_("by")}:</span>
                            <span class="font-medium">{record.attributes?.owner_shortname || $_("unknown")}</span>
                        </p>
                        <p class="text-sm text-text-muted tabular-nums">
                            {formatDate(record.attributes?.timestamp, "datetime")}
                        </p>
                    </header>

                    {#if record.attributes?.diff && Object.keys(record.attributes.diff).length > 0}
                        <div class="overflow-x-auto">
                            <table class="w-full table-fixed text-sm text-start border-collapse">
                                <thead class="bg-surface text-text-muted text-xs font-semibold">
                                    <tr>
                                        <th scope="col" class="px-4 py-2 text-start w-[20%]">{$_("property")}</th>
                                        <th scope="col" class="px-4 py-2 text-start w-[40%]">{$_("previous_value")}</th>
                                        <th scope="col" class="px-4 py-2 text-start w-[40%]">{$_("new_value")}</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {#each Object.entries(record.attributes.diff) as [key, change] (key)}
                                        {@const typedChange = change as { old?: unknown; new?: unknown }}
                                        <tr class="border-t border-border align-top">
                                            <th scope="row" class="px-4 py-2 font-medium text-text text-start break-words">{formatKey(key)}</th>
                                            <td class="px-4 py-2 bg-danger-soft/60 whitespace-normal">
                                                <button
                                                    type="button"
                                                    class="block w-full text-start font-mono text-xs text-danger break-all cursor-pointer rounded-control hover:underline"
                                                    title={$_("view_details")}
                                                    onclick={() => handleModalDetails(typedChange?.old)}
                                                >
                                                    {short(typedChange?.old)}
                                                </button>
                                            </td>
                                            <td class="px-4 py-2 bg-success-soft/60 whitespace-normal">
                                                <button
                                                    type="button"
                                                    class="block w-full text-start font-mono text-xs text-success break-all cursor-pointer rounded-control hover:underline"
                                                    title={$_("view_details")}
                                                    onclick={() => handleModalDetails(typedChange?.new)}
                                                >
                                                    {short(typedChange?.new)}
                                                </button>
                                            </td>
                                        </tr>
                                    {/each}
                                </tbody>
                            </table>
                        </div>
                    {:else}
                        <p class="px-4 py-4 text-sm text-text-muted">{$_("no_changes_recorded")}</p>
                    {/if}
                </section>
            {/each}

            {#if records.length === 0 && !error}
                <EmptyState title={$_("no_history_records")} />
            {/if}

            {#if totalItems > 0}
                <Pagination
                    page={currentPage}
                    pageSize={limit}
                    total={totalItems}
                    pageSizes={[5, 10, 25, 50]}
                    onPageChange={goToPage}
                    onPageSizeChange={changeLimit}
                />
            {/if}
        </div>
    </LoadingState>

    <Modal bind:open={showModal} title={$_("details")} size="lg" class="rounded-modal shadow-modal">
        {#if modalPreview.truncated}
            <p class="text-xs text-text-muted mb-2">{$_("preview_truncated")}</p>
        {/if}
        <div class="max-h-[60vh] overflow-auto">
            <Prism language="json" code={modalPreview.value as object | string} />
        </div>
    </Modal>
{/if}
