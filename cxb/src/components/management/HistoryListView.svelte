<script lang="ts">
    import { resolveTotal } from "@shared/query-total";
    import {Dmart, QueryType, SortyType, type ApiResponseRecord} from "@edraj/tsdmart";
    import {Button, ListPlaceholder, Modal, Table} from "flowbite-svelte";
    import {onMount} from "svelte";
    import {_} from "@/i18n";
    import Prism from "../Prism.svelte";
    import Pagination from "@/components/ui/Pagination.svelte";
    import {pageCount} from "@/utils/paging";
    import {rowKey} from "@/utils/rowKey";

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

        {#each records as record (rowKey(record))}
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
                        {#each Object.entries(record.attributes.diff) as [key, change] (key)}
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
            <Pagination
                class="my-6"
                page={currentPage}
                pageSize={limit}
                total={totalItems}
                pageSizes={[5, 10, 25, 50]}
                onPageChange={goToPage}
                onPageSizeChange={changeLimit}
            />
        {/if}
    </div>

    <Modal bind:open={showModal} class="pt-8">
        <Prism language="json" code={modalData} />
    </Modal>
{/if}
