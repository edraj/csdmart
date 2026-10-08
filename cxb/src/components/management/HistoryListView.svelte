<script lang="ts">
    import { resolveTotal } from "@shared/query-total";
    import {Dmart, QueryType, SortyType, type ApiResponseRecord} from "@edraj/tsdmart";
    import {Button, ListPlaceholder, Modal, Table} from "flowbite-svelte";
    import {onMount} from "svelte";
    import {_} from "@/i18n";
    import Prism from "../Prism.svelte";

    let { space_name, subpath, shortname }: { space_name:string, subpath:string, shortname:string } = $props();

    let records: ApiResponseRecord[] = $state([]);
    let loading = $state(true);
    let error: string | null = $state(null);
    let limit = $state(10);
    let offset = $state(0);
    let totalItems = $state(0);
    let showModal = $state(false);
    let modalData: any = $state(null);

    const currentPage = $derived(Math.floor(offset / limit) + 1);
    const totalPages = $derived(Math.max(1, Math.ceil(totalItems / limit)));
    const hasPrev = $derived(offset > 0);
    const hasNext = $derived(offset + limit < totalItems);
    // Up to five page buttons centred on the current page.
    const pageNumbers = $derived.by(() => {
        const count = Math.min(5, totalPages);
        const start = Math.max(1, Math.min(currentPage - 2, totalPages - count + 1));
        return Array.from({ length: count }, (_, i) => start + i);
    });

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
                search: '',
                limit,
                offset,
                sort_by: 'timestamp',
                sort_type: SortyType.descending
            });
            if (seq !== requestSeq) return;
            records = response?.records || [];
            totalItems = resolveTotal(response?.attributes?.total, offset + records.length);
        } catch (e: any) {
            if (seq !== requestSeq) return;
            error = e?.response?.data?.error?.message ?? e?.message ?? $_("history_load_failed");
        } finally {
            if (seq === requestSeq) loading = false;
        }
    }

    function goToPage(page: number) {
        const target = Math.min(Math.max(1, page), totalPages);
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
        return key.split('.').map(part =>
            part.charAt(0).toUpperCase() + part.slice(1)
        ).join(' > ');
    }

    function handleModalDetails(value: unknown) {
        modalData = value;
        showModal = true;
    }
</script>

{#if loading && records.length === 0}
    <ListPlaceholder class="m-5" size="lg" style="width: 100%"/>
{:else}
    <div class="p-6 bg-white border border-gray-200 rounded-lg shadow-sm dark:bg-gray-800 dark:border-gray-700 w-full" aria-busy={loading}>
        {#if error}
            <div role="alert" class="flex items-center justify-between gap-3 p-3 mb-4 rounded border border-red-300 bg-red-50 text-red-800 dark:bg-red-900/20 dark:border-red-700 dark:text-red-300">
                <span>{$_("history_load_failed")} {error}</span>
                <Button size="xs" color="light" onclick={fetchHistory}>{$_("retry")}</Button>
            </div>
        {/if}

        {#each records as record}
            <div class="flex justify-between mt-4 mb-2">
                <p class="text-lg">
                    <strong>By: </strong> {record.attributes?.owner_shortname || $_("unknown")}
                </p>
                <p class="text-lg">
                    <strong>At: </strong> {new Date(record.attributes?.timestamp).toLocaleString()}
                </p>
            </div>

            {#if record.attributes?.diff && Object.keys(record.attributes.diff).length > 0}
                <div class="border rounded-lg overflow-hidden">
                    <Table class="w-full table-fixed">
                        <thead>
                        <tr>
                            <th class="px-4 py-2 w-[20%]">Property</th>
                            <th class="px-4 py-2 w-[40%]">Previous Value</th>
                            <th class="px-4 py-2 w-[40%]">New Value</th>
                        </tr>
                        </thead>
                        <tbody>
                        {#each Object.entries(record.attributes.diff) as [key, change]}
                            {@const typedChange = change as {old?: any, new?: any}}
                            <tr class="border-b hover:bg-gray-50">
                                <td class="px-4 py-2 font-medium">{formatKey(key)}</td>
                                <td class="px-4 py-2 bg-red-50 whitespace-normal">
                                    <button type="button" class="text-red-600 font-bold block break-words text-start w-full cursor-pointer" onclick={() => handleModalDetails(typedChange?.old)}>{JSON.stringify(typedChange?.old) || ''}</button>
                                </td>
                                <td class="px-4 py-2 bg-green-50 whitespace-normal">
                                    <button type="button" class="text-green-600 font-bold block break-words text-start w-full cursor-pointer" onclick={() => handleModalDetails(typedChange?.new)}>{JSON.stringify(typedChange?.new) || ''}</button>
                                </td>
                            </tr>
                        {/each}
                        </tbody>
                    </Table>
                </div>
            {:else}
                <div class="text-center py-4 text-gray-500">No changes recorded</div>
            {/if}
        {/each}

        {#if records.length === 0 && !error}
            <div class="text-center py-8 text-gray-500">{$_("no_history_records")}</div>
        {/if}

        {#if totalItems > 0}
            <div class="flex flex-col sm:flex-row justify-between my-6">
                <div class="mb-2 sm:mb-0">
                    <span class="me-2">{$_("items_per_page")}:</span>
                    <div class="inline-flex gap-1">
                        {#each [5, 10, 25, 50] as pageSize}
                            <button
                                type="button"
                                class="px-3 py-1 text-sm rounded {limit === pageSize ? 'bg-blue-600 text-white' : 'bg-gray-200 text-gray-800 dark:bg-gray-700 dark:text-gray-200'}"
                                aria-pressed={limit === pageSize}
                                onclick={() => changeLimit(pageSize)}
                            >
                                {pageSize}
                            </button>
                        {/each}
                    </div>
                </div>

                <div class="flex items-center gap-2">
                    <span class="text-sm text-gray-700 dark:text-gray-300">
                        {$_("page_of_pages", { values: { page: currentPage, pages: totalPages } })}
                    </span>

                    <nav class="flex gap-1" aria-label="History pages">
                        <button
                            type="button"
                            class="px-2 py-1 rounded {!hasPrev ? 'bg-gray-100 text-gray-400 cursor-not-allowed' : 'bg-gray-200 text-gray-800 hover:bg-gray-300 dark:bg-gray-700 dark:text-gray-200'}"
                            disabled={!hasPrev}
                            aria-label={$_("previous")}
                            onclick={() => goToPage(currentPage - 1)}
                        >
                            <span class="rtl:hidden">&lt;</span><span class="hidden rtl:inline">&gt;</span> {$_("previous")}
                        </button>

                        {#each pageNumbers as page (page)}
                            <button
                                type="button"
                                class="px-3 py-1 rounded {page === currentPage ? 'bg-blue-600 text-white' : 'bg-gray-200 text-gray-800 hover:bg-gray-300 dark:bg-gray-700 dark:text-gray-200'}"
                                aria-current={page === currentPage ? "page" : undefined}
                                onclick={() => goToPage(page)}
                            >
                                {page}
                            </button>
                        {/each}

                        <button
                            type="button"
                            class="px-2 py-1 rounded {!hasNext ? 'bg-gray-100 text-gray-400 cursor-not-allowed' : 'bg-gray-200 text-gray-800 hover:bg-gray-300 dark:bg-gray-700 dark:text-gray-200'}"
                            disabled={!hasNext}
                            aria-label={$_("next")}
                            onclick={() => goToPage(currentPage + 1)}
                        >
                            {$_("next")} <span class="rtl:hidden">&gt;</span><span class="hidden rtl:inline">&lt;</span>
                        </button>
                    </nav>
                </div>
            </div>
        {/if}
    </div>

    <Modal bind:open={showModal} class="pt-8">
        <Prism language="json" code={modalData} />
    </Modal>
{/if}
