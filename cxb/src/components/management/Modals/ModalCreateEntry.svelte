<script lang="ts">
    import { Button, Label, Modal, Select, Spinner } from "flowbite-svelte";
    import { CodeOutline, FileCodeOutline } from "flowbite-svelte-icons";
    import { Dmart, RequestType, ResourceType, type ActionRequestRecord, type ActionResponse } from "@edraj/tsdmart";
    import type { Content } from "svelte-jsoneditor";
    import type { EntryFields, MetaFormData } from "@/utils/entryShapes";
    import { tick, untrack } from "svelte";
    import { scrollToElById } from "@/utils/renderer/rendererUtils";
    import Prism from "@/components/Prism.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import MetaForm from "@/components/management/forms/MetaForm.svelte";
    import MetaUserForm from "@/components/management/forms/MetaUserForm.svelte";
    import { jsonEditorContentParser } from "@/utils/jsonEditor";
    import { currentEntry, currentListView, InputMode, resourcesWithFormAndJson, spaceChildren } from "@/stores/global";
    import MetaRoleForm from "@/components/management/forms/MetaRoleForm.svelte";
    import MetaPermissionForm from "@/components/management/forms/MetaPermissionForm.svelte";
    import { Level, showToast } from "@/utils/toast";
    import { checkAccess } from "@/utils/checkAccess";
    import PayloadForm from "@/components/management/forms/PayloadForm.svelte";
    import { removeEmpty } from "@/utils/compare";
    import { limitJsonForDisplay } from "@/utils/displayJson";
    import { _ } from "@/i18n";

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
    const folderPreference = $currentEntry?.entry?.payload?.body;

    let selectedResourceType = $state(ResourceType.content);
    let allowedResourceTypes = $state<{ name: string; value: ResourceType }[]>([]);

    let selectedInputMode = $state(InputMode.form);

    function setAllowedResourceTypes() {
        if (space_name === "management") {
            if (subpath === "users") {
                if (checkAccess("create", "management", "users", ResourceType.user)) {
                    allowedResourceTypes = [{ name: ResourceType.user.toString(), value: ResourceType.user }];
                    return;
                }
            } else if (subpath === "roles") {
                if (checkAccess("create", "management", "roles", ResourceType.role)) {
                    allowedResourceTypes = [{ name: ResourceType.role.toString(), value: ResourceType.role }];
                    return;
                }
            } else if (subpath === "permissions") {
                if (checkAccess("create", "management", "permissions", ResourceType.permission)) {
                    allowedResourceTypes = [{ name: ResourceType.permission.toString(), value: ResourceType.permission }];
                    return;
                }
            } else {
                allowedResourceTypes = [{ name: ResourceType.content.toString(), value: ResourceType.content }];
            }
        }
        if (subpath === "schema") {
            if (checkAccess("create", space_name, "schema", ResourceType.schema)) {
                allowedResourceTypes = [{ name: ResourceType.schema.toString(), value: ResourceType.schema }];
                return;
            }
        } else if (subpath === "workflows") {
            if (checkAccess("create", space_name, "workflows", ResourceType.content)) {
                allowedResourceTypes = [{ name: ResourceType.content.toString(), value: ResourceType.content }];
                return;
            }
        } else {
            allowedResourceTypes = [];
            for (const rt of [ResourceType.content, ResourceType.ticket, ResourceType.folder]) {
                if (checkAccess("create", space_name, subpath, rt)) {
                    allowedResourceTypes = [...allowedResourceTypes, { name: rt.toString(), value: rt }];
                }
            }
        }
    }
    function prepareResourceTypes() {
        setAllowedResourceTypes();

        if (folderPreference && folderPreference?.content_resource_types?.length) {
            allowedResourceTypes = allowedResourceTypes.filter((rt) =>
                folderPreference.content_resource_types.includes(rt.value),
            );
        }
        selectedResourceType = allowedResourceTypes[0]?.value ?? ResourceType.content;
    }
    prepareResourceTypes();

    let selectedSchema = $state<string | null>(null);

    /** Everything the meta forms write; `shortname` is lifted out into the record. */
    type MetaContent = MetaFormData & EntryFields;

    // JSON-editor content for a JSON payload, the text itself for html/markdown/text.
    let content = $state<string | Content>({
        json: {},
    });
    let metaContent = $state<MetaContent>({});
    let contentType = $state("json");

    let errorContent: unknown = $state(null);
    const errorPreview = $derived(errorContent ? limitJsonForDisplay(errorContent) : null);
    let validateMetaForm = $state<() => boolean>(() => true);
    let validateRTForm = $state<() => boolean>(() => true);

    let isHandleCreateEntryLoading = $state(false);
    let errorModalMessage = $state<string | null>(null);
    async function handleCreateEntry() {
        errorModalMessage = null;
        if (!validateMetaForm()) {
            errorModalMessage = $_("fill_required_meta");
            return;
        }

        if ([ResourceType.user, ResourceType.role, ResourceType.permission].includes(selectedResourceType)) {
            if (!validateRTForm()) {
                errorModalMessage = $_("fill_required_rt");
                return;
            }
        }

        try {
            isHandleCreateEntryLoading = true;
            let response: ActionResponse | null = null;
            const _metaContent = $state.snapshot(metaContent);
            const shortname = _metaContent.shortname;
            delete _metaContent.shortname;

            const requestCreate: ActionRequestRecord = {
                resource_type: selectedResourceType,
                shortname: shortname ?? "",
                subpath: subpath,
                attributes: {
                    ..._metaContent,
                },
            };
            let parsedContent;
            try {
                parsedContent = jsonEditorContentParser($state.snapshot(content));
            } catch {
                errorModalMessage = $_("invalid_json_payload");
                isHandleCreateEntryLoading = false;
                return;
            }

            if (selectedResourceType === ResourceType.ticket) {
                requestCreate.attributes = {
                    ...requestCreate.attributes,
                    workflow_shortname: selectedWorkflow,
                    payload: {
                        body: parsedContent,
                        schema_shortname: selectedSchema,
                        content_type: "json",
                    },
                };
            } else if (selectedResourceType === ResourceType.schema) {
                requestCreate.attributes = {
                    ...requestCreate.attributes,
                    payload: {
                        body: parsedContent,
                        schema_shortname: "meta_schema",
                        content_type: "json",
                    },
                };
            } else if (selectedResourceType === ResourceType.content && subpath === "workflows") {
                requestCreate.attributes = {
                    ...requestCreate.attributes,
                    payload: {
                        body: parsedContent,
                        schema_shortname: "workflow",
                        content_type: "json",
                    },
                };
            } else if (selectedResourceType === ResourceType.content) {
                requestCreate.attributes = {
                    ...requestCreate.attributes,
                    payload: {
                        body: parsedContent,
                        schema_shortname: contentType === "json" ? selectedSchema : null,
                        content_type: contentType,
                    },
                };
            } else if (selectedSchema) {
                requestCreate.attributes = {
                    ...requestCreate.attributes,
                    payload: {
                        body: parsedContent,
                        schema_shortname: selectedSchema,
                        content_type: "json",
                    },
                };
            } else {
                requestCreate.attributes = {
                    ...requestCreate.attributes,
                    payload: {
                        body: parsedContent,
                        schema_shortname: null,
                        content_type: "json",
                    },
                };
            }

            const request = {
                space_name,
                request_type: RequestType.create,
                records: [
                    {
                        resource_type: selectedResourceType,
                        subpath: subpath,
                        // The meta form's `required` check above guarantees a name here.
                        shortname: metaContent.shortname ?? "",
                        attributes: removeEmpty(requestCreate.attributes),
                    },
                ],
            };
            response = await Dmart.request(request);

            // A 200 that still reports a failure carries it under `attributes.error`.
            const responseAttributes = (response as ActionResponse & { attributes?: { error?: unknown } })?.attributes;
            if (responseAttributes && responseAttributes.error) {
                isHandleCreateEntryLoading = false;
                errorContent = responseAttributes.error;
                return;
            }
            await $currentListView?.fetchPageRecords();
            if (selectedResourceType === ResourceType.folder) {
                $spaceChildren.refresh?.(space_name, subpath, true);
            }
            isOpen = false;
            showToast(Level.info, $_("entry_created"));
        } catch (e: unknown) {
            errorContent = (e as { response?: { data?: unknown }; message?: string })?.response?.data ?? (e as Error)?.message;
            tick().then(() => {
                scrollToElById(`${uid}-error-content`);
            });
        } finally {
            isHandleCreateEntryLoading = false;
        }
    }

    let selectedWorkflow = $state<string | null>(null);

    $effect(() => {
        if (isOpen === false) {
            content = {
                json: {},
            };
            metaContent = {};
            contentType = "json";
            errorContent = null;
            selectedSchema = null;
            selectedWorkflow = null;
            selectedInputMode = InputMode.form;
        }
    });

    $effect(() => {
        if (allowedResourceTypes.length === 1) {
            untrack(() => {
                selectedResourceType = allowedResourceTypes[0].value;
            });
        }
    });

    const canToggleMode = $derived(
        ([ResourceType.schema, ...resourcesWithFormAndJson].includes(selectedResourceType) || subpath === "workflows") &&
            (selectedResourceType !== ResourceType.content || contentType === "json"),
    );
</script>

<Modal bind:open={isOpen} size="lg" title={$_("create_entry")} class="rounded-modal shadow-modal" classes={{ body: "min-h-[50vh]" }}>
    <div class="space-y-4">
        {#if errorModalMessage}
            <ErrorState compact message={errorModalMessage} />
        {/if}
        <div>
            <Label for="{uid}-resource-type" class="mb-1.5">{$_("resource_type")}</Label>
            <Select
                id="{uid}-resource-type"
                items={allowedResourceTypes}
                bind:value={selectedResourceType}
                disabled={allowedResourceTypes.length === 1}
            />
        </div>

        <MetaForm bind:formData={metaContent} bind:validateFn={validateMetaForm} isCreate={true} />

        {#if selectedResourceType === ResourceType.user}
            <MetaUserForm bind:formData={metaContent} bind:validateFn={validateRTForm} isCreate={true} />
        {:else if selectedResourceType === ResourceType.role}
            <MetaRoleForm bind:formData={metaContent} bind:validateFn={validateRTForm} />
        {:else if selectedResourceType === ResourceType.permission}
            <MetaPermissionForm bind:formData={metaContent} bind:validateFn={validateRTForm} readOnly={false} />
        {/if}

        <PayloadForm
            bind:selectedResourceType
            bind:selectedSchema
            bind:selectedWorkflow
            bind:selectedInputMode
            bind:contentType
            bind:content
            bind:errorContent
        />

        {#if errorPreview}
            <div id="{uid}-error-content">
                <ErrorState compact title={$_("entry_create_failed")} message={typeof errorContent === "string" ? errorContent : undefined}>
                    {#if typeof errorContent !== "string"}
                        <div class="max-h-60 overflow-auto"><Prism code={errorPreview.value as object | string} language="json" /></div>
                    {/if}
                </ErrorState>
            </div>
        {/if}
    </div>

    {#snippet footer()}
        <div class="w-full flex flex-wrap items-center justify-between gap-2">
            {#if canToggleMode}
                <Button
                    color="alternative"
                    size="sm"
                    onclick={() => (selectedInputMode = selectedInputMode === InputMode.form ? InputMode.json : InputMode.form)}
                    aria-pressed={selectedInputMode === InputMode.json}
                >
                    {#if selectedInputMode === InputMode.form}
                        <CodeOutline size="sm" class="me-1.5" aria-hidden="true" />
                        {$_("json_mode")}
                    {:else}
                        <FileCodeOutline size="sm" class="me-1.5" aria-hidden="true" />
                        {$_("form_mode")}
                    {/if}
                </Button>
            {:else}
                <span></span>
            {/if}
            <div class="flex items-center gap-2">
                <Button color="alternative" onclick={() => (isOpen = false)} disabled={isHandleCreateEntryLoading}>
                    {$_("cancel")}
                </Button>
                <Button color="primary" onclick={handleCreateEntry} disabled={isHandleCreateEntryLoading}>
                    {#if isHandleCreateEntryLoading}
                        <Spinner class="me-2" size="4" />
                        {$_("creating")}
                    {:else}
                        {$_("create")}
                    {/if}
                </Button>
            </div>
        </div>
    {/snippet}
</Modal>
