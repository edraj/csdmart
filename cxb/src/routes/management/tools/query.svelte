<script lang="ts">
    import { onMount, untrack } from "svelte";
    import { Dmart, QueryType, ResourceType, type ApiResponseRecord, type QueryRequest } from "@edraj/tsdmart";
    import { Button, Checkbox, Input, Label, Select, Spinner } from "flowbite-svelte";
    import { FilterOutline, FilterSolid, SearchOutline } from "flowbite-svelte-icons";
    import downloadFile from "@/utils/downloadFile";
    import { Level, showToast } from "@/utils/toast";
    import { errorMessage } from "@/utils/errorMessage";
    import { fetchCsv, getChildren, getChildrenAndSubChildren } from "@/lib/dmart_services";
    import { addDateFilters, createBaseQuery } from "@/utils/routes/queryHelpers";
    import { csvFileName, type CsvQuery } from "@/utils/csvExport";
    import { _ } from "@/i18n";
    import Aggregation from "@/components/management/tools/Aggregation.svelte";
    import Prism from "@/components/Prism.svelte";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";

    // Constants
    const DEFAULT_QUERY_LIMIT = 10;
    const SUBPATHS_PAGE_SIZE = 100;

    let spaces: ApiResponseRecord[] = $state([]);
    let spacesError: unknown = $state(null);
    let space_name: string = $state("");
    let queryType: QueryType | null = $state(null);
    let subpath: string = $state("/");
    let resource_type: ResourceType | null = $state(null);
    let resource_shortnames: string = $state("");
    let search: string = $state("");
    let from_date: string = $state("");
    let to_date: string = $state("");
    let offset: number = $state(0);
    let limit: number = $state(DEFAULT_QUERY_LIMIT);
    let retrieve_attachments: boolean = $state(false);
    let retrieve_json_payload: boolean = $state(false);

    let aggregation_data = $state({
        load: [],
        group_by: [],
        reducers: [],
    });

    let response: unknown = $state.raw(null);
    let queryError: unknown = $state(null);
    let isQuerying = $state(false);
    let isDownloading = $state(false);
    let isDisplayFilter = $state(false);

    let subpaths: string[] = $state([]);

    async function loadSpaces() {
        spacesError = null;
        try {
            spaces = (await Dmart.getSpaces())?.records ?? [];
        } catch (e: unknown) {
            spacesError = e;
            showToast(Level.warn, errorMessage(e, $_("spaces_load_failed")));
        }
    }

    onMount(loadSpaces);

    // Monotonic id so a slow walk of the previous space cannot land after a
    // fast switch to the next one and populate the dropdown with its folders.
    let subpathsSeq = 0;
    async function loadSubpaths(target: string) {
        const seq = ++subpathsSeq;
        subpaths = [];
        try {
            const roots = await getChildren(target, "/", SUBPATHS_PAGE_SIZE);
            const collected: string[] = [];
            // Walks with the FULL path of each folder — the old copy here passed
            // only the shortname, so anything two levels deep resolved wrongly.
            await getChildrenAndSubChildren(collected, target, "", roots);
            if (seq !== subpathsSeq) return;
            subpaths = collected.sort();
        } catch (e: unknown) {
            if (seq !== subpathsSeq) return;
            showToast(Level.warn, errorMessage(e, $_("subpaths_load_failed")));
        }
    }

    async function handleResponse() {
        if (!space_name || !subpath || !queryType) return;

        const query_request: QueryRequest = {
            type: queryType,
            exact_subpath: true,
            ...createBaseQuery({
                space_name,
                subpath,
                resource_type,
                resource_shortnames,
                search,
                offset,
                limit,
                retrieve_attachments,
                retrieve_json_payload,
            }),
        };
        addDateFilters(query_request, from_date, to_date);

        isQuerying = true;
        queryError = null;
        try {
            response = await Dmart.query(query_request);
        } catch (e: unknown) {
            // Show the server's envelope in the result pane so the failure is
            // visible where the answer would have been.
            queryError = e;
            response = (e as { response?: { data?: unknown } })?.response?.data ?? null;
        } finally {
            isQuerying = false;
        }
    }

    async function handleDownload() {
        if (!space_name || !subpath) return;
        const body = {
            type: "search",
            ...createBaseQuery({
                space_name,
                subpath,
                resource_type,
                resource_shortnames,
                search,
                retrieve_json_payload: true,
            }),
        } as Record<string, unknown>;
        // An export is not a page: no offset, and an explicit numeric limit
        // (the bound number input may hand back a string).
        delete body.offset;
        const requestedLimit = Number(limit);
        body.limit = Number.isFinite(requestedLimit) && requestedLimit > 0 ? requestedLimit : DEFAULT_QUERY_LIMIT;

        addDateFilters(body as QueryRequest, from_date, to_date);

        isDownloading = true;
        try {
            const csv = await fetchCsv(body as unknown as CsvQuery);
            downloadFile(csv, csvFileName(space_name, subpath), "text/csv");
        } catch (e: unknown) {
            showToast(Level.warn, errorMessage(e, $_("csv_download_failed")));
        } finally {
            isDownloading = false;
        }
    }

    $effect(() => {
        const target = space_name;
        if (!target) return;
        untrack(() => {
            void loadSubpaths(target);
        });
    });
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader
        title={$_("query")}
        description={$_("query_description")}
        icon={SearchOutline}
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    />

    <div class="space-y-4">
        {#if spacesError}
            <ErrorState compact title={$_("spaces_load_failed")} error={spacesError} onRetry={loadSpaces} />
        {/if}

        <Card>
            <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
                <div>
                    <Label for="type" class="mb-2">{$_("query_type")}</Label>
                    <Select id="type" required bind:value={queryType}>
                        <option value={null} disabled>{$_("select_query_type")}</option>
                        {#each Object.keys(QueryType) as _queryType (_queryType)}
                            <option value={_queryType}>{_queryType}</option>
                        {/each}
                    </Select>
                </div>
                <div>
                    <Label for="space_name" class="mb-2">{$_("space_name")}</Label>
                    <Select id="space_name" bind:value={space_name}>
                        <option value="" disabled>{$_("select_space")}</option>
                        {#each spaces as space (space.shortname)}
                            <option value={space.shortname}>{space.shortname}</option>
                        {/each}
                    </Select>
                </div>
                <div>
                    <Label for="subpath" class="mb-2">{$_("subpath")}</Label>
                    <Select id="subpath" bind:value={subpath}>
                        <option value="/">/</option>
                        {#each subpaths as path (path)}
                            <option value={path}>{path}</option>
                        {/each}
                    </Select>
                </div>
            </div>

            <div class="mt-4">
                <button
                    type="button"
                    class="inline-flex items-center gap-2 text-sm text-primary hover:underline rounded-control cursor-pointer"
                    aria-expanded={isDisplayFilter}
                    aria-controls="query-filters"
                    onclick={() => (isDisplayFilter = !isDisplayFilter)}
                >
                    {#if isDisplayFilter}<FilterSolid size="sm" aria-hidden="true" />{:else}<FilterOutline size="sm" aria-hidden="true" />{/if}
                    {isDisplayFilter ? $_("hide_filters") : $_("show_filters")}
                </button>
            </div>

            {#if isDisplayFilter}
                <div id="query-filters" class="mt-4 p-4 rounded-card border border-border bg-surface space-y-4">
                    <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
                        <div>
                            <Label for="resource_type" class="mb-2">{$_("resource_types")}</Label>
                            <Select id="resource_type" bind:value={resource_type}>
                                <option value={null}>{$_("any")}</option>
                                {#each Object.keys(ResourceType) as type (type)}
                                    <option value={type}>{type}</option>
                                {/each}
                            </Select>
                        </div>
                        <div>
                            <Label for="search" class="mb-2">{$_("search")}</Label>
                            <Input id="search" type="text" bind:value={search} />
                        </div>
                        <div>
                            <Label for="resource_shortnames" class="mb-2">{$_("shortnames")}</Label>
                            <Input id="resource_shortnames" type="text" bind:value={resource_shortnames} />
                        </div>
                    </div>

                    <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
                        <div>
                            <Label for="from_date" class="mb-2">{$_("from")}</Label>
                            <Input id="from_date" type="date" bind:value={from_date} />
                        </div>
                        <div>
                            <Label for="to_date" class="mb-2">{$_("to")}</Label>
                            <Input id="to_date" type="date" bind:value={to_date} />
                        </div>
                        <div class="flex items-end">
                            <Checkbox id="retrieve_attachments" bind:checked={retrieve_attachments}>
                                {$_("retrieve_attachments")}
                            </Checkbox>
                        </div>
                    </div>

                    <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
                        <div>
                            <Label for="limit" class="mb-2">{$_("limit")}</Label>
                            <Input id="limit" type="number" min="1" bind:value={limit} />
                        </div>
                        <div>
                            <Label for="offset" class="mb-2">{$_("offset")}</Label>
                            <Input id="offset" type="number" min="0" bind:value={offset} />
                        </div>
                        <div class="flex items-end">
                            <Checkbox id="retrieve_json_payload" bind:checked={retrieve_json_payload}>
                                {$_("retrieve_json_payload")}
                            </Checkbox>
                        </div>
                    </div>
                </div>
            {/if}

            {#if queryType === QueryType.aggregation}
                <div class="mt-4">
                    <Aggregation bind:aggregation_data />
                </div>
            {/if}

            <div class="flex flex-wrap items-center justify-end gap-2 mt-5">
                <Button color="alternative" onclick={handleDownload} disabled={isDownloading || !space_name}>
                    {#if isDownloading}<Spinner class="me-2" size="4" />{/if}
                    {$_("download_csv")}
                </Button>
                <Button color="primary" onclick={handleResponse} disabled={isQuerying || !space_name || !queryType}>
                    {#if isQuerying}<Spinner class="me-2" size="4" />{/if}
                    {$_("run_query")}
                </Button>
            </div>
        </Card>

        {#if queryError}
            <ErrorState compact title={$_("query_failed")} error={queryError} />
        {/if}
        {#if response === null}
            <p class="text-sm text-text-muted text-center py-6">{$_("no_response_yet")}</p>
        {:else}
            <Prism code={response as object} />
        {/if}
    </div>
</div>
