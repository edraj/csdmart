<script lang="ts">
    import { Label, Select } from "flowbite-svelte";
    import { ContentType, Dmart, QueryType, ResourceType } from "@edraj/tsdmart";
    import FolderForm from "@/components/management/forms/FolderForm.svelte";
    import LazyJsonEditor from "@/components/ui/LazyJsonEditor.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { currentEntry, InputMode, resourcesWithFormAndJson, resourceTypeWithNoPayload } from "@/stores/global";
    import SchemaForm from "@/components/management/forms/SchemaForm.svelte";
    import WorkflowForm from "@/components/management/forms/WorkflowForm.svelte";
    import DynamicSchemaBasedForms from "@/components/management/forms/DynamicSchemaBasedForms.svelte";
    import TranslationForm from "@/components/management/forms/TranslationForm.svelte";
    import HtmlEditor from "@/components/management/editors/HtmlEditor.svelte";
    import MarkdownEditor from "@/components/management/editors/MarkdownEditor.svelte";
    import { fetchWorkflows } from "@/lib/dmart_services";
    import { getPayloadSchema } from "@/utils/entryManagement";
    import { params } from "@roxi/routify";
    import { untrack } from "svelte";
    import { generateObjectFromSchema } from "@/utils/renderer/rendererUtils";
    import { jsonEditorContentParser } from "@/utils/jsonEditor";
    import { _ } from "@/i18n";

    let {
        isCreate = true,
        selectedResourceType = $bindable(),
        selectedSchema = $bindable(),
        contentType = $bindable(),
        selectedWorkflow = $bindable(),
        selectedInputMode = $bindable(),
        content = $bindable(),
        // eslint-disable-next-line @typescript-eslint/no-unused-vars, no-useless-assignment -- $bindable() written back to the parent, never read here
        errorContent = $bindable(),
    } = $props();

    const uid = $props.id();
    const subpath = $params.subpath;
    const spaceName: string = $params.space_name;

    const contentTypeOptions = [
        { name: "JSON", value: "json" },
        { name: "HTML", value: "html" },
        { name: "Markdown", value: "markdown" },
        { name: $_("text"), value: "text" },
    ];

    function handleRenderMenu(items: any[]) {
        items = items.filter((item) => !["tree", "text", "table"].includes(item.text));
        const itemsWithoutSpace = items.slice(0, items.length - 2);
        return itemsWithoutSpace.concat([{ separator: true }, { space: true }]);
    }

    const folderPreference = $currentEntry?.entry?.payload?.body;

    // ── The selected schema's body: one request for the one schema in use.
    //    The dropdown itself only ever needed shortnames. ─────────────────
    let selectedSchemaContent: any = $state(null);
    let schemaSeq = 0;

    async function applySchema(shortname: string | null | undefined) {
        const seq = ++schemaSeq;
        if (!shortname) {
            selectedSchemaContent = null;
            if (isCreate && selectedResourceType !== ResourceType.folder) {
                content = { json: {} };
            }
            return;
        }
        try {
            const result = await getPayloadSchema(shortname, spaceName);
            if (seq !== schemaSeq) return;
            const body = result?.payload?.body ?? null;
            selectedSchemaContent = body;
            if (isCreate) {
                if (selectedResourceType === ResourceType.content && shortname === "translation") {
                    content = { json: [] };
                } else {
                    content = { json: body ? generateObjectFromSchema(body) : {} };
                }
            }
        } catch (e: unknown) {
            if (seq !== schemaSeq) return;
            selectedSchemaContent = null;
            errorContent = (e as { response?: { data?: unknown }; message?: string })?.response?.data ?? (e as Error)?.message;
        }
    }

    const isFolderFormReady = $derived(selectedResourceType === ResourceType.folder && selectedSchemaContent !== null);

    // A folder's payload is always described by folder_rendering.
    if (selectedResourceType === ResourceType.folder) {
        selectedSchema = "folder_rendering";
    }

    // Create mode: picking a resource type resets the schema and the content.
    $effect(() => {
        if (isCreate && selectedResourceType) {
            untrack(() => {
                if (selectedResourceType === ResourceType.folder) {
                    selectedSchema = "folder_rendering";
                } else {
                    selectedSchema = null;
                    content = { json: {} };
                }
            });
        }
    });

    // Whatever schema is selected (by the dropdown, the resource type or the
    // entry being edited), load it once; nothing else fetches schema bodies.
    $effect(() => {
        if (contentType !== ContentType.json && selectedResourceType !== ResourceType.folder) return;
        const shortname = selectedSchema;
        untrack(() => {
            void applySchema(shortname);
        });
    });

    let mismatchedProperties = $derived.by(() => {
        if (!selectedSchemaContent?.properties || !content || typeof content !== "object" || Array.isArray(content)) {
            return [];
        }
        const schemaKeys = new Set(Object.keys(selectedSchemaContent.properties));
        const payloadKeys = Object.keys(content);
        return payloadKeys.filter((key) => !schemaKeys.has(key));
    });

    // Dropdown options: shortnames only, no payload.
    const schemaOptions = Dmart.query({
        space_name: spaceName,
        type: QueryType.search,
        subpath: "/schema",
        search: "",
        retrieve_json_payload: false,
        limit: 100,
    }).then((schemas) => parseQuerySchemaResponse(schemas));

    function parseQuerySchemaResponse(schemas: { records?: Array<{ shortname: string }> } | null) {
        const records = schemas?.records ?? [];

        let result: string[];
        const _schemas = records.map((e) => e.shortname);
        if (selectedResourceType === ResourceType.folder) {
            result = ["folder_rendering", ..._schemas];
        } else {
            result = _schemas.filter((e) => !["meta_schema", "folder_rendering"].includes(e));
        }
        let r: { name: string; value: string | null }[] = result.map((e) => ({ name: e, value: e }));

        if (folderPreference && folderPreference?.content_schema_shortnames?.length) {
            r = r.filter((s) => folderPreference.content_schema_shortnames.includes(s.value));
        }

        r.unshift({ name: $_("none"), value: null });
        return r;
    }

    $effect(() => {
        if (isCreate && contentType === ContentType.json) {
            if (selectedInputMode === InputMode.json) {
                untrack(() => {
                    try {
                        content = {
                            text: JSON.stringify(jsonEditorContentParser($state.snapshot(content)), null, 2),
                        };
                    } catch {}
                });
            } else if (selectedInputMode === InputMode.form) {
                untrack(() => {
                    try {
                        content = {
                            json: jsonEditorContentParser($state.snapshot(content)),
                        };
                    } catch {}
                });
            }
        }
    });
</script>

<div class="w-full max-w-4xl mx-auto space-y-4">
    {#if !resourceTypeWithNoPayload.includes(selectedResourceType)}
        {#if isCreate && !["workflows", "schema"].includes(subpath) && ![ResourceType.folder, ResourceType.role, ResourceType.permission].includes(selectedResourceType)}
            {#if selectedResourceType === ResourceType.content}
                <div>
                    <Label for="{uid}-content-type" class="mb-1.5">{$_("content_type")}</Label>
                    <Select
                        id="{uid}-content-type"
                        items={contentTypeOptions}
                        value={contentType}
                        onchange={(e: Event) => {
                            const value = (e.target as HTMLSelectElement).value;
                            if (value !== "json") {
                                content = "";
                            } else {
                                content = { json: {} };
                            }
                            contentType = value;
                        }}
                    />
                </div>
            {/if}

            {#if contentType === "json" || selectedResourceType !== ResourceType.content}
                <div>
                    <Label for="{uid}-schema" class="mb-1.5">{$_("schema")}</Label>
                    {#await schemaOptions}
                        <LoadingState variant="skeleton" rows={1} />
                    {:then items}
                        <Select id="{uid}-schema" {items} bind:value={selectedSchema} />
                    {/await}
                </div>
            {/if}
        {/if}

        {#if selectedResourceType === ResourceType.folder && isFolderFormReady}
            {#if selectedInputMode === InputMode.form}
                {#if isCreate}
                    {#if content.json}
                        <FolderForm bind:content={content.json} />
                    {/if}
                {:else}
                    <FolderForm bind:content />
                {/if}
            {:else if isCreate && selectedInputMode === InputMode.json}
                <LazyJsonEditor onRenderMenu={handleRenderMenu} mode="text" bind:content />
            {/if}
        {/if}

        {#if isCreate && selectedResourceType === ResourceType.ticket}
            <div>
                <Label for="{uid}-workflow" class="mb-1.5">{$_("workflow_shortname")}</Label>
                {#await fetchWorkflows(spaceName)}
                    <LoadingState variant="skeleton" rows={1} />
                {:then workflows}
                    <Select
                        id="{uid}-workflow"
                        items={workflows.map((w) => ({ name: w.shortname, value: w.shortname }))}
                        bind:value={selectedWorkflow}
                        placeholder={$_("select_workflow")}
                    />
                {/await}
            </div>
        {/if}

        {#if selectedResourceType === ResourceType.schema}
            {#if selectedInputMode === InputMode.form}
                {#if isCreate}
                    {#if content.json}
                        <SchemaForm bind:content={content.json} />
                    {/if}
                {:else}
                    <SchemaForm bind:content />
                {/if}
            {:else if isCreate && selectedInputMode === InputMode.json}
                <LazyJsonEditor onRenderMenu={handleRenderMenu} mode="text" bind:content />
            {/if}
        {/if}

        {#if subpath === "workflows"}
            {#if selectedInputMode === InputMode.form}
                {#if isCreate}
                    {#if content.json}
                        <WorkflowForm bind:content={content.json} />
                    {/if}
                {:else}
                    <WorkflowForm bind:content />
                {/if}
            {/if}
        {/if}

        {#if selectedResourceType === ResourceType.content && selectedSchema === "translation"}
            {#if selectedSchemaContent}
                {#if isCreate}
                    <TranslationForm bind:entries={content.json} columns={Object.keys(selectedSchemaContent.properties.items.items.properties)} />
                {:else}
                    <TranslationForm bind:entries={content} columns={Object.keys(selectedSchemaContent.properties.items.items.properties)} />
                {/if}
            {/if}
        {:else if selectedResourceType === ResourceType.content && contentType === "html"}
            <HtmlEditor bind:content />
        {:else if selectedResourceType === ResourceType.content && contentType === "markdown"}
            <MarkdownEditor bind:content />
        {:else if selectedResourceType === ResourceType.content && contentType === "text"}
            <div>
                <Label for="{uid}-text" class="mb-1.5">{$_("content")}</Label>
                <textarea
                    id="{uid}-text"
                    class="w-full min-h-48 rounded-control border border-border bg-surface-2 text-text p-2.5 text-sm focus:ring-primary focus:border-primary"
                    dir="auto"
                    bind:value={content}
                ></textarea>
            </div>
        {:else}
            {#if !isCreate && mismatchedProperties.length > 0}
                <div class="rounded-card border border-info/30 bg-info-soft p-4" role="note">
                    <p class="text-sm font-medium text-text">
                        {$_("undeclared_properties", { values: { count: mismatchedProperties.length } })}
                    </p>
                    <p class="text-sm text-text-muted mt-1">{$_("undeclared_properties_hint")}</p>
                    <ul class="list-disc list-inside text-sm text-text mt-1">
                        {#each mismatchedProperties as prop (prop)}
                            <li><code class="font-mono text-xs bg-surface-2 border border-border px-1 rounded-control">{prop}</code></li>
                        {/each}
                    </ul>
                </div>
            {/if}
            <div>
                {#if resourcesWithFormAndJson.includes(selectedResourceType)}
                    {#if selectedInputMode === InputMode.form}
                        {#if isCreate}
                            {#if content.json}
                                <DynamicSchemaBasedForms schema={selectedSchemaContent} bind:content={content.json} />
                            {/if}
                        {:else}
                            <DynamicSchemaBasedForms schema={selectedSchemaContent} bind:content />
                        {/if}
                    {:else if isCreate && selectedInputMode === InputMode.json}
                        <LazyJsonEditor onRenderMenu={handleRenderMenu} mode="text" bind:content />
                    {/if}
                {/if}
            </div>
        {/if}
    {/if}
</div>
