<script lang="ts">
    import { Accordion, AccordionItem, Input, Label, Select, Textarea } from "flowbite-svelte";
    import { CloseOutline, PlusOutline } from "flowbite-svelte-icons";
    import { RequestType, ResourceType } from "@edraj/tsdmart";
    import { onMount } from "svelte";
    import { getChildren, getChildrenAndSubChildren, getSpaces } from "@/lib/dmart_services";
    import { spaces as spacesStore } from "@/stores/management/spaces";
    import IconButton from "@/components/ui/IconButton.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import type { PermissionRules } from "@/utils/entryShapes";
    import { _ } from "@/i18n";

    let {
        formData = $bindable(),
        // eslint-disable-next-line @typescript-eslint/no-unused-vars, no-useless-assignment -- $bindable() written back to the parent, never read here
        validateFn = $bindable(),
        readOnly = false,
    }: {
        /** The permission entry: its rules, with the rest of the record riding along. */
        formData: PermissionRules & Record<string, unknown>;
        validateFn: () => boolean;
        readOnly: boolean;
    } = $props();

    const uid = $props.id();
    let form: HTMLFormElement;

    formData = {
        ...formData,
        subpaths: formData.subpaths || {},
        resource_types: formData.resource_types || [],
        actions: formData.actions || [],
        conditions: formData.conditions || [],
        restricted_fields: formData.restricted_fields || [],
        allowed_fields_values: formData.allowed_fields_values || {},
        filter_fields_values: formData.filter_fields_values || "",
    };

    const resourceTypeOptions = Object.keys(ResourceType).map((key) => ({
        name: key,
        value: ResourceType[key as keyof typeof ResourceType],
    }));

    const requestTypeOptions = Object.keys(RequestType).map((key) => ({
        name: key,
        value: RequestType[key as keyof typeof RequestType] as string,
    }));
    requestTypeOptions.unshift({ name: "view", value: "view" });
    requestTypeOptions.unshift({ name: "query", value: "query" });

    let selectedResourceType = $state("");
    let selectedAction = $state("");
    let newCondition = $state("");
    let newRestrictedField = $state("");

    let subpaths: { name: string; value: string }[] = $state([]);
    let selectedSpace = $state("");
    let selectedSubpath = $state("");
    let loadingSpaces = $state(false);
    let loadingSubpaths = $state(false);

    // The spaces store is filled once at boot; request only when it is empty.
    const spaces = $derived([
        { name: "__all_spaces__", value: "__all_spaces__" },
        ...($spacesStore ?? []).map((space) => ({ name: space.shortname, value: space.shortname })),
    ]);

    onMount(async () => {
        if (readOnly || $spacesStore !== null) return;
        loadingSpaces = true;
        try {
            await getSpaces();
        } catch (error) {
            console.error("Failed to load spaces:", error);
        } finally {
            loadingSpaces = false;
        }
    });

    // The lists are seeded above, so the `?? []` fallbacks only satisfy the
    // optional types; the server strips an empty list from what it sends.
    function addResourceType() {
        const current = formData.resource_types ?? [];
        if (selectedResourceType && !current.includes(selectedResourceType)) {
            formData.resource_types = [...current, selectedResourceType];
            selectedResourceType = "";
        }
    }

    function removeResourceType(item: string) {
        formData.resource_types = (formData.resource_types ?? []).filter((i) => i !== item);
    }

    function addAction() {
        const current = formData.actions ?? [];
        if (selectedAction && !current.includes(selectedAction)) {
            formData.actions = [...current, selectedAction];
            selectedAction = "";
        }
    }

    function removeAction(item: string) {
        formData.actions = (formData.actions ?? []).filter((i) => i !== item);
    }

    function addCondition() {
        const current = formData.conditions ?? [];
        if (newCondition && !current.includes(newCondition)) {
            formData.conditions = [...current, newCondition];
            newCondition = "";
        }
    }

    function removeCondition(item: string) {
        formData.conditions = (formData.conditions ?? []).filter((i) => i !== item);
    }

    function addRestrictedField() {
        const current = formData.restricted_fields ?? [];
        if (newRestrictedField && !current.includes(newRestrictedField)) {
            formData.restricted_fields = [...current, newRestrictedField];
            newRestrictedField = "";
        }
    }

    function removeRestrictedField(item: string) {
        formData.restricted_fields = (formData.restricted_fields ?? []).filter((i) => i !== item);
    }

    async function loadSubpaths(spaceName: string) {
        if (!spaceName) return;

        loadingSubpaths = true;
        try {
            const subpathsResponse: string[] = [];
            const childSubpaths = await getChildren(spaceName, "/");
            await getChildrenAndSubChildren(subpathsResponse, spaceName, "", childSubpaths);
            subpaths = subpathsResponse.map((record) => ({ name: record, value: record }));
        } catch (error) {
            console.error("Failed to load subpaths:", error);
            subpaths = [];
        } finally {
            subpaths.unshift({ name: "__all_subpaths__", value: "__all_subpaths__" });
            subpaths.unshift({ name: "/", value: "/" });
            loadingSubpaths = false;
        }
    }

    function addSubpathToSpace() {
        if (!selectedSpace || !selectedSubpath) return;

        if (!formData.subpaths) {
            formData.subpaths = {};
        }
        const current = formData.subpaths[selectedSpace] ?? [];
        if (!current.includes(selectedSubpath)) {
            formData.subpaths[selectedSpace] = [...current, selectedSubpath];
        }

        selectedSubpath = "";
    }

    function removeSubpath(space: string, subpath: string) {
        const subpaths = formData.subpaths;
        if (!subpaths) return;
        subpaths[space] = (subpaths[space] ?? []).filter((p) => p !== subpath);

        // Remove the space key if no subpaths remain
        if (subpaths[space].length === 0) {
            const { [space]: _removed, ...rest } = subpaths;
            formData.subpaths = rest;
        }
    }

    // ── Allowed fields values: a JSON text box kept in sync while it parses ─
    let jsonEditorContent = $state("");
    try {
        jsonEditorContent = JSON.stringify(formData.allowed_fields_values, null, 2);
    } catch {
        jsonEditorContent = "{}";
    }

    const jsonError = $derived.by(() => {
        try {
            JSON.parse(jsonEditorContent);
            return null;
        } catch {
            return $_("invalid_json");
        }
    });

    function validate() {
        if (jsonError) {
            return false;
        }
        const isValid = form.checkValidity();
        if (!isValid) {
            form.reportValidity();
        }
        return isValid;
    }

    $effect(() => {
        validateFn = validate;
    });

    // Commit the JSON on every edit that parses, so the value is saved without
    // an extra "apply" click. The entry edit page's Save flow snapshots formData
    // directly and cannot safely invoke this sub-form's validate() (the form
    // may be unmounted). Invalid JSON is left uncommitted until corrected.
    $effect(() => {
        if (readOnly || !formData) return;
        try {
            formData.allowed_fields_values = JSON.parse(jsonEditorContent);
        } catch {
            // ignore until the JSON parses
        }
    });

    $effect(() => {
        if (selectedSpace) {
            loadSubpaths(selectedSpace);
        }
    });

    const subpathEntries: [string, string[]][] = $derived(Object.entries(formData.subpaths ?? {}));

    const chip = "inline-flex items-center gap-1 rounded-full ps-3 pe-1 py-0.5 text-sm";
    const emptyBox = "mt-2 p-3 rounded-card border border-dashed border-border text-center text-sm text-text-muted";
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 sm:p-5 my-2">
    <h2 class="text-lg font-semibold text-text mb-4">{$_("permission_settings")}</h2>

    <form bind:this={form} class="space-y-5" onsubmit={(e) => e.preventDefault()}>
        <div>
            <Label for="{uid}-resource-type" class="mb-1.5">{$_("resource_types")}</Label>
            {#if !readOnly}
                <div class="flex gap-2">
                    <Select id="{uid}-resource-type" class="grow" placeholder={$_("select_resource_type")} items={resourceTypeOptions} bind:value={selectedResourceType} />
                    <IconButton label={$_("add")} variant="outline" onclick={addResourceType} disabled={!selectedResourceType}>
                        <PlusOutline size="sm" />
                    </IconButton>
                </div>
            {/if}

            {#if formData.resource_types?.length}
                <ul class="mt-2 flex flex-wrap gap-2" aria-label={$_("resource_types")}>
                    {#each formData.resource_types as item (item)}
                        <li class="{chip} bg-primary-soft text-primary {readOnly ? 'pe-3' : ''}">
                            <span>{item}</span>
                            {#if !readOnly}
                                <IconButton size="sm" label={$_("remove_item", { values: { name: item } })} class="text-primary hover:bg-primary/10" onclick={() => removeResourceType(item)}>
                                    <CloseOutline size="xs" />
                                </IconButton>
                            {/if}
                        </li>
                    {/each}
                </ul>
            {:else}
                <p class={emptyBox}>{$_("no_resource_types_added")}</p>
            {/if}
        </div>

        <div>
            <Label for="{uid}-action" class="mb-1.5">{$_("actions")}</Label>
            {#if !readOnly}
                <div class="flex gap-2">
                    <Select id="{uid}-action" class="grow" placeholder={$_("select_action")} items={requestTypeOptions} bind:value={selectedAction} />
                    <IconButton label={$_("add")} variant="outline" onclick={addAction} disabled={!selectedAction}>
                        <PlusOutline size="sm" />
                    </IconButton>
                </div>
            {/if}

            {#if formData.actions?.length}
                <ul class="mt-2 flex flex-wrap gap-2" aria-label={$_("actions")}>
                    {#each formData.actions as item (item)}
                        <li class="{chip} bg-success-soft text-success {readOnly ? 'pe-3' : ''}">
                            <span>{item}</span>
                            {#if !readOnly}
                                <IconButton size="sm" label={$_("remove_item", { values: { name: item } })} class="text-success hover:bg-success/10" onclick={() => removeAction(item)}>
                                    <CloseOutline size="xs" />
                                </IconButton>
                            {/if}
                        </li>
                    {/each}
                </ul>
            {:else}
                <p class={emptyBox}>{$_("no_actions_added")}</p>
            {/if}
        </div>

        <Accordion flush>
            <AccordionItem>
                {#snippet header()}<span>{$_("subpaths")}</span>{/snippet}
                <div class="py-2 space-y-4">
                    {#if !readOnly}
                        <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                            <div>
                                <Label for="{uid}-space" class="mb-1.5">{$_("space")}</Label>
                                {#if loadingSpaces}
                                    <LoadingState variant="skeleton" rows={1} />
                                {:else}
                                    <Select id="{uid}-space" placeholder={$_("select_space")} items={spaces} bind:value={selectedSpace} />
                                {/if}
                            </div>

                            <div>
                                <Label for="{uid}-subpath" class="mb-1.5">{$_("subpath")}</Label>
                                {#if loadingSubpaths}
                                    <LoadingState variant="skeleton" rows={1} />
                                {:else}
                                    <div class="flex gap-2">
                                        <Select id="{uid}-subpath" class="grow" placeholder={$_("select_subpath")} items={subpaths} bind:value={selectedSubpath} disabled={!selectedSpace} />
                                        <IconButton label={$_("add")} variant="outline" onclick={addSubpathToSpace} disabled={!selectedSpace || !selectedSubpath}>
                                            <PlusOutline size="sm" />
                                        </IconButton>
                                    </div>
                                {/if}
                            </div>
                        </div>
                    {/if}
                    {#if subpathEntries.length > 0}
                        <div class="rounded-card border border-border bg-surface p-4 space-y-4">
                            {#each subpathEntries as [space, paths] (space)}
                                <div>
                                    <p class="font-medium text-text mb-2">{space}</p>
                                    <ul class="flex flex-wrap gap-2" aria-label={space}>
                                        {#each paths as path (path)}
                                            <li class="{chip} bg-info-soft text-info {readOnly ? 'pe-3' : ''}">
                                                <span class="font-mono text-xs">{path}</span>
                                                {#if !readOnly}
                                                    <IconButton size="sm" label={$_("remove_item", { values: { name: path } })} class="text-info hover:bg-info/10" onclick={() => removeSubpath(space, path)}>
                                                        <CloseOutline size="xs" />
                                                    </IconButton>
                                                {/if}
                                            </li>
                                        {/each}
                                    </ul>
                                </div>
                            {/each}
                        </div>
                    {:else}
                        <p class={emptyBox}>{$_("no_subpaths_added")}</p>
                    {/if}
                </div>
            </AccordionItem>

            <AccordionItem>
                {#snippet header()}<span>{$_("conditions")}</span>{/snippet}
                <div class="py-2">
                    {#if !readOnly}
                        <div class="flex gap-2">
                            <Select
                                id="{uid}-condition"
                                class="grow"
                                placeholder={$_("select_condition")}
                                items={[
                                    { name: "own", value: "own" },
                                    { name: "is_active", value: "is_active" },
                                ]}
                                bind:value={newCondition}
                                aria-label={$_("conditions")}
                            />
                            <IconButton label={$_("add")} variant="outline" onclick={addCondition} disabled={!newCondition}>
                                <PlusOutline size="sm" />
                            </IconButton>
                        </div>
                    {/if}

                    {#if formData.conditions?.length}
                        <ul class="mt-2 flex flex-wrap gap-2" aria-label={$_("conditions")}>
                            {#each formData.conditions as item (item)}
                                <li class="{chip} bg-warning-soft text-warning {readOnly ? 'pe-3' : ''}">
                                    <span>{item}</span>
                                    {#if !readOnly}
                                        <IconButton size="sm" label={$_("remove_item", { values: { name: item } })} class="text-warning hover:bg-warning/10" onclick={() => removeCondition(item)}>
                                            <CloseOutline size="xs" />
                                        </IconButton>
                                    {/if}
                                </li>
                            {/each}
                        </ul>
                    {:else}
                        <p class={emptyBox}>{$_("no_conditions_added")}</p>
                    {/if}
                </div>
            </AccordionItem>

            <AccordionItem>
                {#snippet header()}<span>{$_("restricted_fields")}</span>{/snippet}
                <div class="py-2">
                    {#if !readOnly}
                        <div class="flex gap-2">
                            <Input id="{uid}-restricted" class="grow" placeholder={$_("add_restricted_field")} bind:value={newRestrictedField} aria-label={$_("restricted_fields")} />
                            <IconButton label={$_("add")} variant="outline" onclick={addRestrictedField} disabled={!newRestrictedField}>
                                <PlusOutline size="sm" />
                            </IconButton>
                        </div>
                    {/if}

                    {#if formData.restricted_fields?.length}
                        <ul class="mt-2 flex flex-wrap gap-2" aria-label={$_("restricted_fields")}>
                            {#each formData.restricted_fields as item (item)}
                                <li class="{chip} bg-danger-soft text-danger {readOnly ? 'pe-3' : ''}">
                                    <span>{item}</span>
                                    {#if !readOnly}
                                        <IconButton size="sm" label={$_("remove_item", { values: { name: item } })} class="text-danger hover:bg-danger/10" onclick={() => removeRestrictedField(item)}>
                                            <CloseOutline size="xs" />
                                        </IconButton>
                                    {/if}
                                </li>
                            {/each}
                        </ul>
                    {:else}
                        <p class={emptyBox}>{$_("no_restricted_fields_added")}</p>
                    {/if}
                </div>
            </AccordionItem>

            <AccordionItem>
                {#snippet header()}<span>{$_("allowed_fields_values")}</span>{/snippet}
                <div class="py-2">
                    <Label for="{uid}-allowed-json" class="mb-1.5">{$_("allowed_fields_values")}</Label>
                    <p class="mb-2 text-xs text-text-muted">{$_("allowed_fields_values_help")}</p>
                    <Textarea
                        id="{uid}-allowed-json"
                        rows={10}
                        class="font-mono text-sm"
                        dir="ltr"
                        bind:value={jsonEditorContent}
                        disabled={readOnly}
                        aria-invalid={!!jsonError}
                        aria-describedby={jsonError ? `${uid}-allowed-json-error` : undefined}
                    />
                    {#if jsonError}
                        <p id="{uid}-allowed-json-error" class="mt-1 text-sm text-danger" role="alert">{jsonError}</p>
                    {/if}
                </div>
            </AccordionItem>

            <AccordionItem>
                {#snippet header()}<span>{$_("filter_fields_values")}</span>{/snippet}
                <div class="py-2">
                    <Label for="{uid}-filter" class="mb-1.5">{$_("filter_fields_values")}</Label>
                    <p class="mb-2 text-xs text-text-muted">{$_("filter_fields_values_help")}</p>
                    <Input id="{uid}-filter" placeholder={$_("filter_fields_values")} bind:value={formData.filter_fields_values} disabled={readOnly} />
                </div>
            </AccordionItem>
        </Accordion>
    </form>
</div>
