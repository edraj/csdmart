<script lang="ts">
    import { _ } from "svelte-i18n";
    import { onMount } from "svelte";
    import downloadFile from "@/utils/downloadFile";
    import { Level, showToast } from "@/utils/toast";
    import {
        Dmart,
        type QueryRequest,
        QueryType,
        ResourceType,
    } from "@edraj/tsdmart";
    import { fetchCsv, getChildren, getChildrenAndSubChildren } from "@/lib/dmart_services";
    import {
        Button,
        Card,
        Checkbox,
        Input,
        Label,
        Select,
    } from "flowbite-svelte";
    import {
        FilterOutline,
        FilterSolid,
        SearchOutline,
        ArrowLeftOutline,
    } from "flowbite-svelte-icons";
    import { goto } from "@roxi/routify";
    import Aggregation from "@/components/management/tools/Aggregation.svelte";
    import Prism from "@/components/Prism.svelte";
    import {
        addDateFilters,
        createBaseQuery,
    } from "@/utils/routes/queryHelpers";
    import { csvFileName } from "@/utils/csvExport";
    import { untrack } from "svelte";

    // Constants
    const DEFAULT_QUERY_LIMIT = 10;
    const SUBPATHS_PAGE_SIZE = 100;

    let spaces: any[] = $state([]);
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

    let response: any = $state(null);
    let isDisplayFilter = $state(false);

    let subpaths: string[] = $state([]);

    onMount(async () => {
        try {
            spaces = (await Dmart.getSpaces())?.records ?? [];
        } catch (e: any) {
            showToast(Level.warn, e?.response?.data?.error?.message ?? $_("spaces_load_failed"));
        }
    });

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
        } catch (e: any) {
            if (seq !== subpathsSeq) return;
            showToast(Level.warn, e?.response?.data?.error?.message ?? $_("subpaths_load_failed"));
        }
    }

    async function handleResponse() {
        if (!space_name || !subpath || !queryType) {
            return;
        }

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

        try {
            response = await Dmart.query(query_request);
        } catch (e: any) {
            // Show the server's envelope in the result pane so the failure is
            // visible where the answer would have been.
            response = e?.response?.data ?? { error: e?.message ?? $_("query_failed") };
            showToast(Level.warn, e?.response?.data?.error?.message ?? $_("query_failed"));
        }
    }

    async function handleDownload() {
        if (!space_name || !subpath) {
            return;
        }
        const body: any = {
            type: "search",
            ...createBaseQuery({
                space_name,
                subpath,
                resource_type,
                resource_shortnames,
                search,
                retrieve_json_payload: true,
            }),
        };
        // An export is not a page: no offset, and an explicit numeric limit
        // (the bound number input may hand back a string).
        delete body.offset;
        const requestedLimit = Number(limit);
        body.limit = Number.isFinite(requestedLimit) && requestedLimit > 0 ? requestedLimit : DEFAULT_QUERY_LIMIT;

        addDateFilters(body, from_date, to_date);

        try {
            const csv = await fetchCsv(body);
            downloadFile(csv, csvFileName(space_name, subpath), "text/csv");
        } catch (e: any) {
            showToast(Level.warn, e?.response?.data?.error?.message ?? $_("csv_download_failed"));
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

<div class="container mx-auto p-8">
    <button
        class="flex items-center gap-2 text-gray-600 hover:text-primary-600 mb-6 transition-colors"
        onclick={() => $goto("/management/tools")}
    >
        <ArrowLeftOutline size="sm" />
        <span>Back to Tools</span>
    </button>

    <div class="flex items-center gap-3 mb-8">
        <div class="p-3 bg-primary-100 rounded-full">
            <SearchOutline class="w-8 h-8 text-primary-600" />
        </div>
        <div>
            <h1 class="text-2xl font-bold">Query</h1>
            <p class="text-gray-500">
                Perform queries against connected Dmart instance.
            </p>
        </div>
    </div>

    <div class="min-w-11/12">
        <Card class="min-w-full p-4">
            <div class="space-y-4">
                <div class="grid grid-cols-1 md:grid-cols-12 gap-4 mb-4">
                    <div class="md:col-span-1 flex items-center justify-center">
                        <button
                            type="button"
                            class="text-blue-600 hover:text-blue-800 cursor-pointer"
                            onclick={() => (isDisplayFilter = !isDisplayFilter)}
                            aria-label="Toggle filter options"
                        >
                            {#if isDisplayFilter}
                                <FilterSolid size="xl" />
                            {:else}
                                <FilterOutline size="xl" />
                            {/if}
                        </button>
                    </div>
                    <div class="md:col-span-3">
                        <Label for="type" class="mb-2">{$_("query_type")}</Label
                        >
                        <Select id="type" required bind:value={queryType}>
                            {#each Object.keys(QueryType) as _queryType (_queryType)}
                                <option value={_queryType}>{_queryType}</option>
                            {/each}
                        </Select>
                    </div>
                    <div class="md:col-span-4">
                        <Label for="space_name" class="mb-2"
                            >{$_("space_name")}</Label
                        >
                        <Select id="space_name" bind:value={space_name}>
                            {#each spaces as space (space.shortname)}
                                <option value={space.shortname}
                                    >{space.shortname}</option
                                >
                            {/each}
                        </Select>
                    </div>
                    <div class="md:col-span-4">
                        <Label for="subpath" class="mb-2">{$_("subpath")}</Label
                        >
                        <Select id="subpath" bind:value={subpath}>
                            <option value="/">/</option>
                            {#each subpaths as path (path)}
                                <option value={path}>{path}</option>
                            {/each}
                        </Select>
                    </div>
                </div>

                {#if isDisplayFilter}
                    <div
                        class="bg-gray-50 p-4 rounded-lg border border-gray-200 mb-4"
                    >
                        <div class="grid grid-cols-1 md:grid-cols-3 gap-4 mb-4">
                            <div>
                                <Label for="resource_type" class="mb-2"
                                    >{$_("resource_types")}</Label
                                >
                                <Select
                                    id="resource_type"
                                    bind:value={resource_type}
                                >
                                    <option value={null}></option>
                                    {#each Object.keys(ResourceType) as type (type)}
                                        <option value={type}>{type}</option>
                                    {/each}
                                </Select>
                            </div>
                            <div>
                                <Label for="search" class="mb-2"
                                    >{$_("search")}</Label
                                >
                                <Input
                                    id="search"
                                    type="text"
                                    bind:value={search}
                                />
                            </div>
                            <div>
                                <Label for="resource_shortnames" class="mb-2"
                                    >{$_("shortnames")}</Label
                                >
                                <Input
                                    id="resource_shortnames"
                                    type="text"
                                    bind:value={resource_shortnames}
                                />
                            </div>
                        </div>

                        <div class="grid grid-cols-1 md:grid-cols-3 gap-4 mb-4">
                            <div>
                                <Label for="from_date" class="mb-2"
                                    >{$_("from")}</Label
                                >
                                <Input
                                    id="from_date"
                                    type="date"
                                    bind:value={from_date}
                                />
                            </div>
                            <div>
                                <Label for="to_date" class="mb-2"
                                    >{$_("to")}</Label
                                >
                                <Input
                                    id="to_date"
                                    type="date"
                                    bind:value={to_date}
                                />
                            </div>
                            <div class="flex items-end">
                                <div class="flex items-center gap-2">
                                    <Checkbox
                                        id="retrieve_attachments"
                                        bind:checked={retrieve_attachments}
                                    />
                                    <Label
                                        for="retrieve_attachments"
                                        class="mb-0"
                                        >{$_("retrieve_attachments")}</Label
                                    >
                                </div>
                            </div>
                        </div>

                        <div class="grid grid-cols-1 md:grid-cols-3 gap-4">
                            <div>
                                <Label for="limit" class="mb-2"
                                    >{$_("limit")}</Label
                                >
                                <Input
                                    id="limit"
                                    type="number"
                                    bind:value={limit}
                                />
                            </div>
                            <div>
                                <Label for="offset" class="mb-2"
                                    >{$_("offset")}</Label
                                >
                                <Input
                                    id="offset"
                                    type="number"
                                    bind:value={offset}
                                />
                            </div>
                            <div class="flex items-end">
                                <div class="flex items-center gap-2">
                                    <Checkbox
                                        id="retrieve_json_payload"
                                        bind:checked={retrieve_json_payload}
                                    />
                                    <Label
                                        for="retrieve_json_payload"
                                        class="mb-0"
                                        >{$_("retrieve_json_payload")}</Label
                                    >
                                </div>
                            </div>
                        </div>
                    </div>
                {/if}

                {#if queryType === QueryType.aggregation}
                    <div class="mb-4">
                        <Aggregation bind:aggregation_data />
                    </div>
                {/if}

                <div class="flex justify-between">
                    <Button onclick={handleResponse} color="blue"
                        >{$_("submit")}</Button
                    >
                    <Button color="blue" outline onclick={handleDownload}
                        >{$_("download_csv")}</Button
                    >
                </div>
            </div>
        </Card>
    </div>

    {#if response === null}
        <p class="text-gray-500 text-center">No response yet.</p>
    {:else}
        <Prism bind:code={response} />
    {/if}
</div>
