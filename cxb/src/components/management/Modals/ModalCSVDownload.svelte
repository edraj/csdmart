<script lang="ts">
    import { Button, Checkbox, Input, Label, Modal, Spinner } from "flowbite-svelte";
    import { QueryType } from "@edraj/tsdmart";
    import downloadFile from "@/utils/downloadFile";
    import { Level, showToast } from "@/utils/toast";
    import { currentListView } from "@/stores/global";
    import { fetchCsv } from "@/lib/dmart_services";
    import { buildCsvQuery, csvFileName } from "@/utils/csvExport";
    import { errorMessage } from "@/utils/errorMessage";
    import { _ } from "@/i18n";

    let {
        isOpen = $bindable(false),
        space_name,
        subpath,
    }: {
        isOpen: boolean;
        space_name: string;
        subpath: string;
    } = $props();

    const uid = $props.id();

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
        } catch (e: unknown) {
            showToast(Level.warn, errorMessage(e, $_("csv_download_failed")));
        } finally {
            isCSVDownloadInProgress = false;
        }
    }
</script>

<Modal bind:open={isOpen} size="xs" autoclose={false} title={$_("csv_download_options")} class="rounded-modal shadow-modal">
    <div class="space-y-4">
        <div>
            <Label for="{uid}-limit" class="mb-1.5">{$_("limit")}</Label>
            <Input id="{uid}-limit" type="number" placeholder={$_("limit")} bind:value={limit} min="1" disabled={downloadAll} />
        </div>

        <div>
            <Label for="{uid}-startDate" class="mb-1.5">{$_("start_date")}</Label>
            <Input id="{uid}-startDate" type="date" bind:value={startDate} disabled={downloadAll} />
        </div>

        <div>
            <Label for="{uid}-endDate" class="mb-1.5">{$_("end_date")}</Label>
            <Input id="{uid}-endDate" type="date" bind:value={endDate} disabled={downloadAll} />
        </div>

        <div class="flex items-center gap-2">
            <Checkbox id="{uid}-downloadAll" bind:checked={downloadAll} />
            <Label for="{uid}-downloadAll" class="mb-0 font-normal">{$_("download_all")}</Label>
        </div>
    </div>

    <div class="flex items-center justify-end gap-2 mt-6">
        <Button color="alternative" onclick={() => (isOpen = false)} disabled={isCSVDownloadInProgress}>{$_("cancel")}</Button>
        <Button color="primary" disabled={isCSVDownloadInProgress} onclick={handleDownloadCSV}>
            {#if isCSVDownloadInProgress}
                <Spinner size="4" class="me-2" />
                {$_("downloading")}
            {:else}
                {$_("download_csv")}
            {/if}
        </Button>
    </div>
</Modal>
