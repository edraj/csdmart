<script lang="ts">
  import { _ } from "@/i18n";
    import {Dmart, QueryType, ResourceType} from "@edraj/tsdmart";
    import {warningToastMessage, successToastMessage} from "@/lib/toasts_messages";
    import Modal from "@/components/Modal.svelte";
    import {
        describeCsvFailureRow,
        formatCount,
        groupCsvFailures,
        mergeCsvImportResults,
        uploadCsv,
        type CsvImportResult,
    } from "@shared/csv-import";

    interface Props {
        space_name: string;
        subpath: string;
        isOpen?: boolean;
        onUploadSuccess?: () => void;
        availableSpaces?: {shortname: string; displayname?: string}[];
    }

    let { 
        space_name, 
        subpath, 
        isOpen = $bindable(false), 
        onUploadSuccess = () => {}, 
        availableSpaces = [] 
    }: Props = $props();

    // Follows the prop until the user picks another space (writable $derived).
    let selectedSpace = $derived(space_name);
    let selectedResourceType = $state(ResourceType.content);
    let selectedSchema = $state<string | null>(null);
    let payloadFiles: File[] = $state([]);
    let isUploading = $state(false);
    let resourceTypeError = $state(false);
    let schemaError = $state(false);
    let isUpdate = $state(false);

    // What the last upload came to, and the choices it was made with — a
    // "continue from row N" must re-send exactly those, not whatever the form
    // has been changed to since.
    let result = $state<CsvImportResult | null>(null);
    let sent = $state<{
        spaceName: string; resourceType: ResourceType; schema: string; file: File; isUpdate: boolean;
    } | null>(null);
    let failureGroups = $derived(result ? groupCsvFailures(result.failed) : []);
    const MAX_GROUPS = 20;
    const MAX_ROWS_PER_GROUP = 10;

    // Reset selected space when modal opens
    $effect(() => {
        if (isOpen) {
            selectedSpace = space_name;
            selectedSchema = null;
            payloadFiles = [];
            result = null;
            sent = null;
        }
    });

    function parseQuerySchemaResponse(schemas: any){
        if (schemas === null) {
            return [];
        }
        const result = schemas.records
            .map((e: any) => e.shortname)
            .filter((e: any) => !["meta_schema", "folder_rendering"].includes(e));

        let r = result.map((e: any) => ({
            name: e,
            value: e
        }));
        return r;
    }

    function parseSpacesForSelect(spaces: typeof availableSpaces) {
        return spaces.map(s => ({
            name: s.displayname || s.shortname,
            value: s.shortname
        }));
    }

    function handleFileChange(e: Event) {
        const target = e.target as HTMLInputElement;
        const files = target.files;
        if (files && files.length > 0) {
            payloadFiles = Array.from(files);
            result = null;
        }
    }

    async function handleCSVUpload() {
        resourceTypeError = false;
        schemaError = false;

        let hasError = false;

        if (!selectedResourceType) {
            warningToastMessage("Please select a resource type");
            resourceTypeError = true;
            hasError = true;
        }

        if (!selectedSchema) {
            warningToastMessage("Please select a schema");
            schemaError = true;
            hasError = true;
        }

        if (!payloadFiles.length) {
            warningToastMessage("Please select a CSV file");
            hasError = true;
        }

        if (hasError) {
            return;
        }

        sent = {
            spaceName: selectedSpace,
            resourceType: selectedResourceType,
            schema: selectedSchema as string,
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
                    spaceName: upload.spaceName,
                    subpath,
                    schema: upload.schema,
                    file: upload.file,
                    isUpdate: upload.isUpdate,
                    startRow,
                    // Catalog's axios instance does inject the bearer token in a
                    // request interceptor, which reaffirms the same value; these
                    // are passed so the upload does not depend on which app's
                    // instance it was handed.
                    headers: Dmart.getHeaders(),
                },
            );
        } finally {
            isUploading = false;
        }
        result = startRow > 1 && result ? mergeCsvImportResults(result, outcome) : outcome;

        if (result.imported > 0) onUploadSuccess();
        if (result.ok && result.failedCount === 0) {
            successToastMessage(
                `CSV uploaded: ${formatCount(result.imported)} ${upload.isUpdate ? "updated" : "imported"}`,
            );
            isOpen = false;
            // Reset state
            selectedSchema = null;
            payloadFiles = [];
            result = null;
        } else {
            warningToastMessage(result.message ?? `${formatCount(result.failedCount)} rows failed`);
        }
    }
</script>

{#if isOpen}
  <Modal
    onClose={() => (isOpen = false)}
    title={$_("users_page.upload_csv")}
    ariaLabel="Upload CSV"
    size="xl"
  >
    {#snippet icon()}
      <svg class="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-8l-4-4m0 0L8 8m4-4v12"></path>
      </svg>
    {/snippet}

    <div class="space-y-4">
            {#if availableSpaces.length > 0}
                <div class="space-y-1.5">
                    <label for="space" class="block text-[10px] uppercase font-bold text-text-faint tracking-wider px-1">{$_("fields.space")}</label>
                    <select 
                        id="space"
                        class="w-full px-4 py-2 bg-surface border border-border rounded-xl text-sm focus:ring-2 focus:ring-primary text-text" 
                        bind:value={selectedSpace}
                    >
                        {#each parseSpacesForSelect(availableSpaces) as space (space.value)}
                            <option value={space.value}>{space.name}</option>
                        {/each}
                    </select>
                </div>
            {/if}

            <div class="space-y-1.5">
                <label for="resourceType" class="block text-[10px] uppercase font-bold text-text-faint tracking-wider px-1">{$_("relationship_modal.fields.resource_type")}</label>
                <select 
                    id="resourceType"
                    class="w-full px-4 py-2 bg-surface border border-border rounded-xl text-sm focus:ring-2 focus:ring-primary text-text {resourceTypeError ? 'ring-2 ring-danger' : ''}" 
                    bind:value={selectedResourceType} 
                    onchange={() => resourceTypeError = false}
                >
                    <option value={ResourceType.content}>{ResourceType.content.toString()}</option>
                    <option value={ResourceType.folder}>{ResourceType.folder.toString()}</option>
                    <option value={ResourceType.ticket}>{ResourceType.ticket.toString()}</option>
                </select>
                {#if resourceTypeError}
                    <p class="text-danger text-xs mt-1 px-1">{$_("csv_modal.resource_type_required")}</p>
                {/if}
            </div>

            <div class="space-y-1.5">
                <label for="schema" class="block text-[10px] uppercase font-bold text-text-faint tracking-wider px-1">{$_("templates.form.schema_label")}</label>
                {#await Dmart.query({
                    space_name: selectedSpace,
                    type: QueryType.search,
                    subpath: "/schema",
                    search: "",
                    retrieve_json_payload: true,
                    limit: 100
                })}
                    <div role="status" class="w-full animate-pulse h-10 bg-surface-3 rounded-xl"></div>
                {:then schemas}
                    <select 
                        id="schema"
                        class="w-full px-4 py-2 bg-surface border border-border rounded-xl text-sm focus:ring-2 focus:ring-primary text-text {schemaError ? 'ring-2 ring-danger' : ''}" 
                        bind:value={selectedSchema} 
                        onchange={() => schemaError = false}
                    >
                        {#each parseQuerySchemaResponse(schemas) as schema (schema.value)}
                            <option value={schema.value}>{schema.name}</option>
                        {/each}
                    </select>
                    {#if schemaError}
                        <p class="text-danger text-xs mt-1 px-1">{$_("csv_modal.schema_required")}</p>
                    {/if}
                {:catch}
                    <p class="text-danger text-sm mt-2 px-1">{$_("errors.loading_schemas")}</p>
                {/await}
            </div>

            <div class="space-y-1.5">
                <label for="csvFile" class="block text-[10px] uppercase font-bold text-text-faint tracking-wider px-1">{$_("csv_modal.csv_file")}</label>
                <input 
                    id="csvFile"
                    type="file" 
                    accept=".csv" 
                    onchange={handleFileChange} 
                    class="w-full px-4 py-2 bg-surface border border-border rounded-xl text-sm focus:ring-2 focus:ring-primary text-text file:me-4 file:py-2 file:px-4 file:rounded-xl file:border-0 file:text-sm file:font-semibold file:bg-primary-soft file:text-primary hover:file:bg-primary-soft" 
                />
            </div>

            <div class="flex items-start gap-3 pt-2 px-1">
                <input
                    id="isUpdate"
                    type="checkbox"
                    bind:checked={isUpdate}
                    class="mt-1 w-4 h-4 text-primary bg-surface border border-border rounded focus:ring-primary focus:ring-2"
                />
                <div class="flex-1">
                    <label for="isUpdate" class="text-[10px] uppercase font-bold text-text-faint tracking-wider cursor-pointer block">{$_("csv_modal.update_entries")}</label>
                    <p class="text-xs text-text-muted mt-1">
                        {#if isUpdate}
                            Will update existing entries with matching shortname
                        {:else}
                            Will create new entries from CSV data
                        {/if}
                    </p>
                </div>
            </div>

            {#if result}
                <div class="mt-4 p-4 border border-danger bg-danger-soft text-danger rounded-lg max-h-80 overflow-y-auto">
                    <h4 class="font-semibold">
                        {formatCount(result.imported)}
                        {sent?.isUpdate ? "updated" : "imported"}, {formatCount(result.failedCount)} failed
                    </h4>
                    {#if result.message}
                        <p class="mt-2 text-sm break-words">{result.message}</p>
                    {/if}
                    {#if result.resumeRow}
                        <button
                            onclick={continueUpload}
                            disabled={isUploading}
                            class="mt-2 px-4 py-1.5 bg-primary text-text-on-primary rounded-lg text-xs font-semibold hover:bg-primary-hover disabled:opacity-50 disabled:cursor-not-allowed"
                        >
                            Continue from row {formatCount(result.resumeRow)}
                        </button>
                    {/if}
                    {#each failureGroups.slice(0, MAX_GROUPS) as group (group.error)}
                        <div class="mt-3">
                            <p class="text-sm font-medium break-words">
                                {group.error}
                                <span class="font-normal">
                                    ({formatCount(group.rows.length)} {group.rows.length === 1 ? "row" : "rows"})
                                </span>
                            </p>
                            <ul class="mt-1 text-xs space-y-0.5">
                                {#each group.rows.slice(0, MAX_ROWS_PER_GROUP) as failure (failure.row)}
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
      <button
        onclick={() => (isOpen = false)}
        class="px-6 py-2.5 text-sm font-medium text-text-muted hover:text-text hover:bg-surface rounded-xl transition-colors border border-transparent"
      >
        Cancel
      </button>
      <button
        onclick={handleCSVUpload}
        disabled={isUploading}
        class="px-8 py-2.5 bg-primary text-text-on-primary rounded-xl text-sm font-semibold hover:bg-primary-hover shadow-md shadow-card disabled:opacity-50 disabled:cursor-not-allowed transition-all flex items-center gap-2"
      >
        {#if isUploading}
          <div class="w-4 h-4 border-2 border-text-on-primary/30 border-t-white rounded-full animate-spin"></div>
          Uploading...
        {:else}
          Upload
        {/if}
      </button>
    {/snippet}
  </Modal>
{/if}
