<script lang="ts">
    import { Button, Label, Modal, Select, Spinner } from "flowbite-svelte";
    import { Dmart, QueryType, ResourceType } from "@edraj/tsdmart";
    import { Level, showToast } from "@/utils/toast";
    import { currentListView } from "@/stores/global";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { formatNumber } from "@/utils/format";
    import { _ } from "@/i18n";
    import {
        describeCsvFailureRow,
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

    const uid = $props.id();

    let selectedResourceType = $state(ResourceType.content);
    let selectedSchema = $state<string | null>(null);
    let payloadFiles = $state<File[]>([]);
    let isUploading = $state(false);
    let resourceTypeError = $state(false);
    let schemaError = $state(false);
    let fileError = $state(false);
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
                    return [{ name: ResourceType.user.toString(), value: ResourceType.user }];
                }
                if (subpath === "roles" || subpath === "/roles") {
                    return [{ name: ResourceType.role.toString(), value: ResourceType.role }];
                }
                if (subpath === "permissions" || subpath === "/permissions") {
                    return [{ name: ResourceType.permission.toString(), value: ResourceType.permission }];
                }
            }
            return [
                { name: ResourceType.content.toString(), value: ResourceType.content },
                { name: ResourceType.folder.toString(), value: ResourceType.folder },
                { name: ResourceType.ticket.toString(), value: ResourceType.ticket },
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
                } else if (subpath === "permissions" || subpath === "/permissions") {
                    selectedResourceType = ResourceType.permission;
                } else {
                    selectedResourceType = ResourceType.content;
                }
            } else {
                selectedResourceType = ResourceType.content;
            }
            // Everything the last upload left behind, cleared — as catalog's
            // dialog already did. Without this a reopened dialog still showed
            // the previous upload's failures and a live "Continue from row N"
            // button, which re-sent that file (and its schema) against whatever
            // subpath the dialog is mounted on now.
            selectedSchema = null;
            payloadFiles = [];
            result = null;
            sent = null;
            resourceTypeError = false;
            schemaError = false;
            fileError = false;
        }
    });

    // The dropdown only needs shortnames.
    const schemaOptions = $derived(Dmart.query({
        space_name,
        type: QueryType.search,
        subpath: "/schema",
        search: "",
        retrieve_json_payload: false,
        limit: 100,
    }).then((schemas) =>
        (schemas?.records ?? [])
            .map((e) => e.shortname)
            .filter((e) => !["meta_schema", "folder_rendering"].includes(e))
            .map((e) => ({ name: e, value: e })),
    ));

    function handleFileChange(e: Event) {
        const files = (e.target as HTMLInputElement).files;
        if (files && files.length > 0) {
            payloadFiles = Array.from(files);
            result = null;
            fileError = false;
        }
    }

    async function handleCSVUpload() {
        resourceTypeError = !selectedResourceType;
        schemaError = !selectedSchema;
        fileError = payloadFiles.length === 0;

        if (resourceTypeError || schemaError || fileError) {
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
                    // cxb's axios instance has no request interceptor, so a
                    // call made through it directly carries no Authorization —
                    // the same reason tools/import.svelte spreads these.
                    headers: Dmart.getHeaders(),
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
                upload.isUpdate
                    ? $_("csv_rows_updated", { values: { count: result.imported } })
                    : $_("csv_rows_imported", { values: { count: result.imported } }),
            );
            result = null;
            payloadFiles = [];
            isOpen = false;
        } else {
            showToast(Level.warn, result.message ?? $_("csv_rows_failed", { values: { count: result.failedCount } }));
        }
    }

    const fieldError = "mt-1 text-sm text-danger";
</script>

<Modal bind:open={isOpen} size="md" title={$_("upload_csv")} class="rounded-modal shadow-modal">
    <div class="space-y-4">
        <div>
            <Label for="{uid}-resource-type" class="mb-1.5">{$_("resource_type")}</Label>
            <Select
                id="{uid}-resource-type"
                items={resourceTypeItems}
                bind:value={selectedResourceType}
                onchange={() => (resourceTypeError = false)}
                disabled={resourceTypeItems.length === 1}
                aria-invalid={resourceTypeError}
            />
            {#if resourceTypeError}
                <p class={fieldError} role="alert">{$_("resource_type_required")}</p>
            {/if}
        </div>

        <div>
            <Label for="{uid}-schema" class="mb-1.5">{$_("schema")}</Label>
            {#await schemaOptions}
                <LoadingState variant="skeleton" rows={1} />
            {:then items}
                <Select
                    id="{uid}-schema"
                    {items}
                    placeholder={$_("select_schema")}
                    bind:value={selectedSchema}
                    onchange={() => (schemaError = false)}
                    aria-invalid={schemaError}
                />
            {/await}
            {#if schemaError}
                <p class={fieldError} role="alert">{$_("schema_required")}</p>
            {/if}
        </div>

        <div>
            <Label for="{uid}-file" class="mb-1.5">{$_("csv_file")}</Label>
            <input
                id="{uid}-file"
                type="file"
                accept=".csv"
                onchange={handleFileChange}
                aria-invalid={fileError}
                class="block w-full text-sm text-text border border-border rounded-control cursor-pointer bg-surface-2 file:me-3 file:border-0 file:bg-surface-3 file:text-text file:px-3 file:py-2 focus:outline-none"
            />
            {#if fileError}
                <p class={fieldError} role="alert">{$_("csv_file_required")}</p>
            {/if}
        </div>

        <div class="flex items-start gap-3">
            <input
                id="{uid}-update"
                type="checkbox"
                bind:checked={isUpdate}
                class="mt-1 h-4 w-4 rounded-control border-border-strong bg-surface-2 text-primary focus:ring-primary"
            />
            <div class="flex-1">
                <Label for="{uid}-update" class="mb-0">{$_("update_entries")}</Label>
                <p class="text-xs text-text-muted mt-1">
                    {isUpdate ? $_("csv_update_help") : $_("csv_create_help")}
                </p>
            </div>
        </div>

        {#if result}
            <div class="rounded-card border border-danger/30 bg-danger-soft text-text p-4 max-h-80 overflow-y-auto" role="alert">
                <h4 class="font-semibold text-sm tabular-nums">
                    {sent?.isUpdate
                        ? $_("csv_rows_updated", { values: { count: result.imported } })
                        : $_("csv_rows_imported", { values: { count: result.imported } })},
                    {$_("csv_rows_failed", { values: { count: result.failedCount } })}
                </h4>
                {#if result.message}
                    <p class="mt-2 text-sm break-words">{result.message}</p>
                {/if}
                {#if result.resumeRow}
                    <Button size="xs" color="primary" class="mt-2" onclick={continueUpload} disabled={isUploading}>
                        {$_("continue_from_row", { values: { row: formatNumber(result.resumeRow) } })}
                    </Button>
                {/if}
                {#each failureGroups.slice(0, MAX_GROUPS) as group (group.error)}
                    <div class="mt-3">
                        <p class="text-sm font-medium break-words">
                            {group.error}
                            <span class="font-normal text-text-muted tabular-nums">
                                ({$_("n_rows", { values: { count: group.rows.length } })})
                            </span>
                        </p>
                        <ul class="mt-1 text-xs space-y-0.5 text-text-muted">
                            {#each group.rows.slice(0, MAX_ROWS_PER_GROUP) as failure (failure.row)}
                                <li class="break-all">{describeCsvFailureRow(failure)}</li>
                            {/each}
                            {#if group.rows.length > MAX_ROWS_PER_GROUP}
                                <li>{$_("and_n_more", { values: { count: group.rows.length - MAX_ROWS_PER_GROUP } })}</li>
                            {/if}
                        </ul>
                    </div>
                {/each}
                {#if failureGroups.length > MAX_GROUPS}
                    <p class="mt-3 text-xs text-text-muted">
                        {$_("and_n_other_errors", { values: { count: failureGroups.length - MAX_GROUPS } })}
                    </p>
                {/if}
            </div>
        {/if}
    </div>

    <div class="flex items-center justify-end gap-2 mt-6">
        <Button color="alternative" onclick={() => (isOpen = false)} disabled={isUploading}>{$_("cancel")}</Button>
        <Button color="primary" onclick={handleCSVUpload} disabled={isUploading}>
            {#if isUploading}
                <Spinner size="4" class="me-2" />
                {$_("uploading")}
            {:else}
                {$_("upload")}
            {/if}
        </Button>
    </div>
</Modal>
