<script lang="ts">
    import { onMount } from "svelte";
    import { Button, Dropdown, DropdownItem, Modal, Spinner } from "flowbite-svelte";
    import { DotsHorizontalOutline, EyeOutline, PenOutline, PlusOutline, TrashBinOutline } from "flowbite-svelte-icons";
    import { Dmart, RequestType, ResourceType, type ApiResponseRecord } from "@edraj/tsdmart";
    import { spaces } from "@/stores/management/spaces";
    import { jsonEditorContentParser } from "@/utils/jsonEditor";
    import { getSpaces } from "@/lib/dmart_services";
    import { Level, showToast } from "@/utils/toast";
    import { removeEmpty } from "@/utils/compare";
    import { _ } from "@/i18n";
    import MetaForm from "@/components/management/forms/MetaForm.svelte";
    import Prism from "@/components/Prism.svelte";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import SpaceGrid from "@/components/ui/SpaceGrid.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
    import IconButton from "@/components/ui/IconButton.svelte";

    type SpaceForm = {
        shortname: string;
        is_active: boolean;
        slug: string;
        displayname: Record<string, string>;
        description: Record<string, string>;
    };

    const emptyForm = (): SpaceForm => ({
        shortname: "",
        is_active: true,
        slug: "",
        displayname: { en: "", ar: "" },
        description: { en: "", ar: "" },
    });

    let viewMetaModal = $state(false);
    let editModal = $state(false);
    let deleteModal = $state(false);
    let addSpaceModal = $state(false);
    let selectedSpace: ApiResponseRecord | null = $state(null);
    let modelError: unknown = $state(null);
    let loadError: unknown = $state(null);
    let isLoadingSpaces = $state(false);

    let spaceFormData: SpaceForm = $state(emptyForm());
    let validateSpaceForm = $state(() => true);
    let isActionLoading = $state(false);

    // svelte-jsoneditor is ~250 kB and only two modals need it: load it the
    // first time one of them opens instead of on the spaces page itself.
    let editorModule: Promise<typeof import("svelte-jsoneditor")> | null = null;
    function loadEditor() {
        editorModule ??= import("svelte-jsoneditor");
        return editorModule;
    }
    let jeContent = $state<{ json: unknown }>({ json: undefined });

    const visibleSpaces = $derived(
        ($spaces ?? []).filter((space) => space?.attributes?.hide_space !== true),
    );

    async function loadSpaces() {
        isLoadingSpaces = true;
        loadError = null;
        try {
            await getSpaces();
        } catch (error) {
            loadError = error;
        } finally {
            isLoadingSpaces = false;
        }
    }

    onMount(() => {
        // The layout fires this once at boot (best-effort); if that failed or
        // has not landed yet, this page owns the retry.
        if ($spaces === null) void loadSpaces();
    });

    function showAddSpaceModal() {
        modelError = null;
        spaceFormData = emptyForm();
        addSpaceModal = true;
    }

    async function createSpace() {
        if (!validateSpaceForm()) return;
        const shortname = spaceFormData.shortname.trim();
        if (!shortname) return;
        try {
            isActionLoading = true;
            modelError = null;
            const attributes = {
                is_active: spaceFormData.is_active,
                slug: spaceFormData.slug,
                displayname: spaceFormData.displayname,
                description: spaceFormData.description,
            };
            await Dmart.request({
                space_name: shortname,
                request_type: RequestType.create,
                records: [
                    {
                        resource_type: ResourceType.space,
                        shortname,
                        subpath: "/",
                        attributes: removeEmpty(attributes),
                    },
                ],
            });
            showToast(Level.info, $_("space_created", { values: { shortname } }));
            await getSpaces();
            addSpaceModal = false;
        } catch (error) {
            modelError = error;
        } finally {
            isActionLoading = false;
        }
    }

    function viewMeta(space: ApiResponseRecord) {
        modelError = null;
        selectedSpace = structuredClone($state.snapshot(space)) as ApiResponseRecord;
        jeContent = { json: selectedSpace ?? undefined };
        viewMetaModal = true;
    }

    function editSpace(space: ApiResponseRecord) {
        modelError = null;
        selectedSpace = structuredClone($state.snapshot(space)) as ApiResponseRecord;
        jeContent = { json: selectedSpace ?? undefined };
        editModal = true;
    }

    async function saveChanges() {
        if (!selectedSpace) return;
        let record: { attributes?: Record<string, unknown>; uuid?: string };
        try {
            record = jsonEditorContentParser($state.snapshot(jeContent));
        } catch {
            modelError = $_("invalid_json");
            return;
        }
        delete record.uuid;
        try {
            isActionLoading = true;
            modelError = null;
            await Dmart.request({
                space_name: selectedSpace.shortname,
                request_type: RequestType.update,
                records: [
                    {
                        resource_type: ResourceType.space,
                        shortname: selectedSpace.shortname,
                        subpath: "/",
                        attributes: record.attributes ?? {},
                    },
                ],
            });
            editModal = false;
            showToast(Level.info, $_("space_updated", { values: { shortname: selectedSpace.shortname } }));
            await getSpaces();
        } catch (error) {
            modelError = error;
        } finally {
            isActionLoading = false;
        }
    }

    function confirmDelete(space: ApiResponseRecord) {
        modelError = null;
        selectedSpace = space;
        deleteModal = true;
    }

    async function deleteSpace() {
        if (!selectedSpace) return;
        const shortname = selectedSpace.shortname;
        try {
            isActionLoading = true;
            modelError = null;
            await Dmart.request({
                space_name: shortname,
                request_type: RequestType.delete,
                records: [
                    {
                        resource_type: ResourceType.space,
                        shortname,
                        subpath: "/",
                        attributes: {},
                    },
                ],
            });
            showToast(Level.info, $_("space_deleted", { values: { shortname } }));
            deleteModal = false;
            selectedSpace = null;
            await getSpaces();
        } catch (error) {
            modelError = error;
        } finally {
            isActionLoading = false;
        }
    }

    /** The server's full error envelope, for the collapsible details block. */
    function errorBody(error: unknown): object | null {
        const data = (error as { response?: { data?: unknown } })?.response?.data;
        return data && typeof data === "object" ? data : null;
    }
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader title={$_("spaces")} description={$_("spaces_description")}>
        {#snippet actions()}
            <Button color="primary" onclick={showAddSpaceModal}>
                <PlusOutline class="me-2 h-5 w-5" aria-hidden="true" />{$_("add_space")}
            </Button>
        {/snippet}
    </PageHeader>

    {#if loadError}
        <ErrorState title={$_("spaces_load_failed")} error={loadError} onRetry={loadSpaces} />
    {:else if $spaces === null || isLoadingSpaces}
        <LoadingState variant="skeleton" rows={6} />
    {:else if visibleSpaces.length === 0}
        <EmptyState title={$_("no_spaces")} hint={$_("no_spaces_hint")}>
            <Button color="primary" size="sm" onclick={showAddSpaceModal}>{$_("add_space")}</Button>
        </EmptyState>
    {:else}
        <SpaceGrid spaces={visibleSpaces} href={(space) => `/management/content/${space.shortname}`}>
            {#snippet actions(space)}
                <IconButton
                    id="space-menu-{space.shortname}"
                    label={$_("space_actions", { values: { shortname: space.shortname } })}
                    variant="outline"
                    size="sm"
                >
                    <DotsHorizontalOutline size="sm" />
                </IconButton>
                <Dropdown simple triggeredBy="#space-menu-{space.shortname}" class="min-w-44">
                    <DropdownItem onclick={() => viewMeta(space)}>
                        <span class="flex items-center gap-2">
                            <EyeOutline size="sm" aria-hidden="true" /> {$_("view_metadata")}
                        </span>
                    </DropdownItem>
                    <DropdownItem onclick={() => editSpace(space)}>
                        <span class="flex items-center gap-2">
                            <PenOutline size="sm" aria-hidden="true" /> {$_("edit")}
                        </span>
                    </DropdownItem>
                    <DropdownItem onclick={() => confirmDelete(space)}>
                        <span class="flex items-center gap-2 text-danger">
                            <TrashBinOutline size="sm" aria-hidden="true" /> {$_("delete")}
                        </span>
                    </DropdownItem>
                </Dropdown>
            {/snippet}
        </SpaceGrid>
    {/if}
</div>

<Modal bind:open={addSpaceModal} size="xl" title={$_("add_space")} class="rounded-modal shadow-modal">
    <div class="space-y-4">
        <MetaForm bind:formData={spaceFormData} bind:validateFn={validateSpaceForm} isCreate={true} />

        {#if modelError}
            <ErrorState compact error={modelError}>
                {#if errorBody(modelError)}
                    <details class="text-xs">
                        <summary class="cursor-pointer text-text-muted">{$_("details")}</summary>
                        <div class="mt-2 max-h-60 overflow-auto"><Prism code={errorBody(modelError) ?? {}} /></div>
                    </details>
                {/if}
            </ErrorState>
        {/if}
    </div>

    <div class="flex items-center justify-end gap-2 mt-6">
        <Button color="alternative" onclick={() => (addSpaceModal = false)} disabled={isActionLoading}>{$_("cancel")}</Button>
        <Button color="primary" onclick={createSpace} disabled={isActionLoading}>
            {#if isActionLoading}
                <Spinner class="me-2" size="4" />
                {$_("creating")}
            {:else}
                {$_("create")}
            {/if}
        </Button>
    </div>
</Modal>

<Modal bind:open={viewMetaModal} size="xl" title={$_("space_metadata")} class="rounded-modal shadow-modal">
    {#if selectedSpace}
        {#await loadEditor()}
            <LoadingState />
        {:then editor}
            {@const JSONEditor = editor.JSONEditor}
            <JSONEditor content={jeContent} readOnly={true} />
        {/await}
    {/if}
</Modal>

<Modal bind:open={editModal} size="xl" title={$_("edit_space")} class="rounded-modal shadow-modal">
    <div class="space-y-4">
        {#if selectedSpace}
            {#await loadEditor()}
                <LoadingState />
            {:then editor}
                {@const JSONEditor = editor.JSONEditor}
                <JSONEditor bind:content={jeContent} readOnly={false} mode={editor.Mode.text} />
            {/await}
        {/if}

        {#if modelError}
            <ErrorState compact error={modelError}>
                {#if errorBody(modelError)}
                    <details class="text-xs">
                        <summary class="cursor-pointer text-text-muted">{$_("details")}</summary>
                        <div class="mt-2 max-h-60 overflow-auto"><Prism code={errorBody(modelError) ?? {}} /></div>
                    </details>
                {/if}
            </ErrorState>
        {/if}
    </div>
    <div class="flex items-center justify-end gap-2 mt-6">
        <Button color="alternative" onclick={() => (editModal = false)} disabled={isActionLoading}>{$_("cancel")}</Button>
        <Button color="primary" onclick={saveChanges} disabled={isActionLoading}>
            {#if isActionLoading}
                <Spinner class="me-2" size="4" />
                {$_("saving")}
            {:else}
                {$_("save_changes")}
            {/if}
        </Button>
    </div>
</Modal>

<ConfirmDialog
    bind:open={deleteModal}
    title={$_("delete_space")}
    body={selectedSpace
        ? `${$_("confirm_delete_space", { values: { shortname: selectedSpace.shortname } })}\n${$_("cannot_be_undone")}`
        : ""}
    variant="danger"
    confirmLabel={$_("delete_space")}
    loading={isActionLoading}
    loadingLabel={$_("deleting")}
    error={modelError}
    onConfirm={deleteSpace}
    onCancel={() => (selectedSpace = null)}
/>
