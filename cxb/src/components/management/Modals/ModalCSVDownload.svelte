<script lang="ts">
    import {Button, Checkbox, Input, Label, Modal} from "flowbite-svelte";
    import {QueryType} from "@edraj/tsdmart";
    import downloadFile from "@/utils/downloadFile";
    import {Level, showToast} from "@/utils/toast";
    import {currentListView} from "@/stores/global";
    import {fetchCsv} from "@/lib/dmart_services";
    import {buildCsvQuery, csvFileName} from "@/utils/csvExport";
    import {_} from "@/i18n";

    let { isOpen = $bindable(false), space_name, subpath }: {
        isOpen: boolean;
        space_name: string;
        subpath: string;
    } = $props();

    let downloadAll = $state(false);
    let limit = $state("");
    let startDate = $state("");
    let endDate = $state("");

    let isCSVDownloadInProgress = $state(false);
    async function handleDownloadCSV() {
        try {
            isCSVDownloadInProgress = true;

            // Start from the list's live query (search, sort, filters) but
            // never its paging: offset is dropped and limit set explicitly.
            const query = buildCsvQuery(
                $currentListView?.query ?? {},
                {
                    downloadAll,
                    limit,
                    startDate,
                    endDate,
                    total: $currentListView?.total ?? null,
                },
                { space_name, subpath: subpath || "/", type: QueryType.search },
            );

            // text/csv straight through — no JSON round trip.
            const csv = await fetchCsv(query);
            downloadFile(csv, csvFileName(space_name, subpath), "text/csv");
            isOpen = false;
        } catch (e: any) {
            showToast(Level.warn, e?.response?.data?.error?.message ?? $_("csv_download_failed"));
        } finally {
            isCSVDownloadInProgress = false;
        }
    }
</script>

<Modal bind:open={isOpen} size="xs" autoclose={false} class="w-full">
    <div class="">
        <h3 class="mb-5 text-lg font-normal text-gray-500 dark:text-gray-400">
            CSV Download Options
        </h3>

        <div class="mb-4">
            <Label for="limit" class="mb-2">{$_("limit")}</Label>
            <Input id="limit" type="number" placeholder="Enter limit" bind:value={limit} min="1" disabled={downloadAll} />
        </div>

        <div class="mb-4">
            <Label for="startDate" class="mb-2">Start Date</Label>
            <Input id="startDate" type="date" bind:value={startDate} disabled={downloadAll} />
        </div>

        <div class="mb-4">
            <Label for="endDate" class="mb-2">End Date</Label>
            <Input id="endDate" type="date" bind:value={endDate} disabled={downloadAll} />
        </div>

        <div class="mb-4">
            <Checkbox id="downloadAll" bind:checked={downloadAll}>Download all</Checkbox>
        </div>

        <div class="flex justify-center gap-4">
            <Button color="alternative" onclick={() => isOpen = false}>{$_("cancel")}</Button>
            <Button class="bg-primary" disabled={isCSVDownloadInProgress} onclick={handleDownloadCSV}>{$_("download_csv")}</Button>
        </div>
    </div>
</Modal>
