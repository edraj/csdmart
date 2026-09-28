<script lang="ts">
    import { Button, Label, Modal, Select, Spinner } from "flowbite-svelte";
    import { Dmart, QueryType, ResourceType } from "@edraj/tsdmart";
    import { Level, showToast } from "@/utils/toast";
    import { currentListView } from "@/stores/global";
    import {
        describeCsvFailureRow,
        formatCount,
        groupCsvFailures,
        mergeCsvImportResults,
        uploadCsv,
        type CsvImportResult,
    } from "@shared/csv-import";

    let {
        space_name,
        subpath,
        isOpen = $bindable(false),
    }: {
        space_name: string;
        subpath: string;
        isOpen: boolean;
    } = $props();

    let selectedResourceType = $state(ResourceType.content);
    let selectedSchema = $state<string | null>(null);
    let payloadFiles = $state([]);
    let isUploading = $state(false);
    let resourceTypeError = $state(false);
    let schemaError = $state(false);
    let isUpdate = $state(false);

    // What the last upload came to, and the choices it was made with — a
    // "continue from row N" must re-send exactly those, not whatever the form
    // has been changed to since.
    let result = $state<CsvImportResult | null>(null);
    let sent = $state<{ resourceType: ResourceType; schema: string; file: File; isUpdate: boolean } | null>(null);
    let failureGroups = $derived(result ? groupCsvFailures(result.failed) : []);
    const MAX_GROUPS = 20;
    const MAX_ROWS_PER_GROUP = 10;

    let resourceTypeItems = $derived(
        (() => {
            if (space_name === "management") {
                if (subpath === "users" || subpath === "/users") {
                    return [
                        {
                            name: ResourceType.user.toString(),
                            value: ResourceType.user,
                        },
                    ];
                }
                if (subpath === "roles" || subpath === "/roles") {
                    return [
                        {
                            name: ResourceType.role.toString(),
                            value: ResourceType.role,
                        },
                    ];
                }
                if (subpath === "permissions" || subpath === "/permissions") {
                    return [
                        {
                            name: ResourceType.permission.toString(),
                            value: ResourceType.permission,
                        },
                    ];
                }
            }
            return [
                {
                    name: ResourceType.content.toString(),
                    value: ResourceType.content,
                },
                {
                    name: ResourceType.folder.toString(),
                    value: ResourceType.folder,
                },
                {
                    name: ResourceType.ticket.toString(),
                    value: ResourceType.ticket,
                },
            ];
        })(),
    );

    $effect(() => {
        if (isOpen) {
            if (space_name === "management") {
                if (subpath === "users" || subpath === "/users") {
                    selectedResourceType = ResourceType.user;
                } else if (subpath === "roles" || subpath === "/roles") {
                    selectedResourceType = ResourceType.role;
                } else if (
                    subpath === "permissions" ||
                    subpath === "/permissions"
                ) {
                    selectedResourceType = ResourceType.permission;
                } else {
                    selectedResourceType = ResourceType.content;
                }
            } else {
                selectedResourceType = ResourceType.content;
            }
        }
    });

    function parseQuerySchemaResponse(schemas) {
        const records = schemas?.records ?? [];

        let result = [];
        const _schemas = records.map((e) => e.shortname);
        result = _schemas.filter(
            (e: any) => !["meta_schema", "folder_rendering"].includes(e),
        );

        let r = result.map((e: any) => ({
            name: e,
            value: e,
        }));
        return r;
    }

    function handleFileChange(e) {
        const files = e.target.files;
        if (files.length > 0) {
            payloadFiles = Array.from(files);
            result = null;
        }
    }

    async function handleCSVUpload() {
        resourceTypeError = false;
        schemaError = false;

        let hasError = false;

        if (!selectedResourceType) {
            showToast(Level.warn, "Please select a resource type");
            resourceTypeError = true;
            hasError = true;
        }

        if (!selectedSchema) {
            showToast(Level.warn, "Please select a schema");
            schemaError = true;
            hasError = true;
        }

        if (!payloadFiles.length) {
            showToast(Level.warn, "Please select a CSV file");
            hasError = true;
        }

        if (hasError) {
            return;
        }

        sent = {
            resourceType: selectedResourceType,
            schema: selectedSchema!,
            file: payloadFiles[0],
            isUpdate,
        };
        result = null;
        await send(1);
    }

    // After the server's time limit stopped an import part-way: re-sends the
    // same file from the first row it did not reach.
    async function continueUpload() {
        if (sent && result?.resumeRow) await send(result.resumeRow);
    }

    async function send(startRow: number) {
        const upload = sent!;
        isUploading = true;
        let outcome: CsvImportResult;
        try {
            outcome = await uploadCsv(
                (url, body, config) => Dmart.axiosDmartInstance.post(url, body, config),
                {
                    resourceType: upload.resourceType,
                    spaceName: space_name,
                    subpath,
                    schema: upload.schema,
                    file: upload.file,
                    isUpdate: upload.isUpdate,
                    startRow,
                },
            );
        } finally {
            isUploading = false;
        }
        result = startRow > 1 && result ? mergeCsvImportResults(result, outcome) : outcome;

        if (result.imported > 0) {
            // A failed refresh leaves the list stale; it must not hide the
            // outcome of the upload itself.
            try {
                await $currentListView?.fetchPageRecords();
            } catch {}
        }
        if (result.ok && result.failedCount === 0) {
            showToast(
                Level.info,
                `CSV uploaded: ${formatCount(result.imported)} ${upload.isUpdate ? "updated" : "imported"}`,
            );
            result = null;
            isOpen = false;
        } else {
            showToast(Level.warn, result.message ?? `${formatCount(result.failedCount)} rows failed`);
        }
    }
</script>

<Modal bodyClass="h-auto justify-center" bind:open={isOpen} size="md">
    {#snippet header()}
        <h3>Upload CSV</h3>
    {/snippet}
    <div>
        <Label>
            Resource Type
            <Select
                class="my-2 {resourceTypeError ? 'border-red-500' : ''}"
                items={resourceTypeItems}
                bind:value={selectedResourceType}
                onchange={() => (resourceTypeError = false)}
                disabled={resourceTypeItems.length === 1}
            />
            {#if resourceTypeError}
                <p class="text-red-500 text-xs mt-1">
                    Resource type is required
                </p>
            {/if}
        </Label>

        <Label class="mt-3">
            Schema
            {#await Dmart.query( { space_name, type: QueryType.search, subpath: "/schema", search: "", retrieve_json_payload: true, limit: 100 }, )}
                <div role="status" class="max-w-sm animate-pulse">
                    <div
                        class="h-3 bg-gray-200 rounded-full dark:bg-gray-700 mx-2 my-2.5"
                    ></div>
                </div>
            {:then schemas}
                <Select
                    class="mt-2 {schemaError ? 'border-red-500' : ''}"
                    items={parseQuerySchemaResponse(schemas)}
                    bind:value={selectedSchema}
                    onchange={() => (schemaError = false)}
                />
                {#if schemaError}
                    <p class="text-red-500 text-xs mt-1">Schema is required</p>
                {/if}
            {/await}
        </Label>

        <Label class="mt-3">
            CSV File
            <input
                type="file"
                accept=".csv"
                onchange={handleFileChange}
                class="mt-2 block w-full text-sm text-gray-900 border border-gray-300 rounded-lg cursor-pointer bg-gray-50 dark:text-gray-400 focus:outline-none dark:bg-gray-700 dark:border-gray-600 dark:placeholder-gray-400"
            />
        </Label>

        <Label class="mt-3 flex items-start">
            <input
                type="checkbox"
                bind:checked={isUpdate}
                class="mt-1 mr-3 w-4 h-4 text-blue-600 bg-gray-100 border-gray-300 rounded focus:ring-blue-500 dark:focus:ring-blue-600 dark:ring-offset-gray-800 focus:ring-2 dark:bg-gray-700 dark:border-gray-600"
            />
            <div class="flex-1">
                <span
                    class="text-sm font-medium text-gray-900 dark:text-gray-300"
                    >Update entries</span
                >
                <p class="text-xs text-gray-500 dark:text-gray-400 mt-1">
                    {#if isUpdate}
                        • Will update existing entries with matching shortname
                    {:else}
                        • Will create new entries from CSV data
                    {/if}
                </p>
            </div>
        </Label>

        {#if result}
            <div
                class="mt-4 p-4 border border-red-300 bg-red-50 text-red-800 rounded max-h-80 overflow-y-auto dark:bg-gray-800 dark:border-red-800 dark:text-red-300"
            >
                <h4 class="font-semibold">
                    {formatCount(result.imported)}
                    {sent?.isUpdate ? "updated" : "imported"}, {formatCount(result.failedCount)} failed
                </h4>
                {#if result.message}
                    <p class="mt-2 text-sm break-words">{result.message}</p>
                {/if}
                {#if result.resumeRow}
                    <Button size="xs" class="mt-2 bg-primary" onclick={continueUpload} disabled={isUploading}>
                        Continue from row {formatCount(result.resumeRow)}
                    </Button>
                {/if}
                {#each failureGroups.slice(0, MAX_GROUPS) as group}
                    <div class="mt-3">
                        <p class="text-sm font-medium break-words">
                            {group.error}
                            <span class="font-normal">
                                ({formatCount(group.rows.length)} {group.rows.length === 1 ? "row" : "rows"})
                            </span>
                        </p>
                        <ul class="mt-1 text-xs space-y-0.5">
                            {#each group.rows.slice(0, MAX_ROWS_PER_GROUP) as failure}
                                <li class="break-all">{describeCsvFailureRow(failure)}</li>
                            {/each}
                            {#if group.rows.length > MAX_ROWS_PER_GROUP}
                                <li>… and {formatCount(group.rows.length - MAX_ROWS_PER_GROUP)} more</li>
                            {/if}
                        </ul>
                    </div>
                {/each}
                {#if failureGroups.length > MAX_GROUPS}
                    <p class="mt-3 text-xs">
                        … and {formatCount(failureGroups.length - MAX_GROUPS)} other errors
                    </p>
                {/if}
            </div>
        {/if}
    </div>

    {#snippet footer()}
        <Button color="alternative" onclick={() => (isOpen = false)}
            >Cancel</Button
        >
        <Button
            class="bg-primary"
            onclick={handleCSVUpload}
            disabled={isUploading}
        >
            {#if isUploading}
                <Spinner size="4" class="mr-2" />
                Uploading...
            {:else}
                Upload
            {/if}
        </Button>
    {/snippet}
</Modal>
