<script lang="ts">
    import { _ } from "@/i18n";
    import { Button, Input, Label, Modal, Select } from "flowbite-svelte";
    import { Dmart, RequestType, ResourceType } from "@edraj/tsdmart";
    import { Level, showToast } from "@/utils/toast";
    import Prism from "@/components/Prism.svelte";
    import LazyJsonEditor from "@/components/ui/LazyJsonEditor.svelte";
    import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import IconButton from "@/components/ui/IconButton.svelte";
    import { getChildren, getChildrenAndSubChildren, getSpaces } from "@/lib/dmart_services";
    import { spaces as spacesStore } from "@/stores/management/spaces";
    import { errorMessage } from "@/utils/errorMessage";
    import { limitJsonForDisplay } from "@/utils/displayJson";
    import { EyeOutline, PenOutline, PlusOutline, TrashBinOutline } from "flowbite-svelte-icons";

    let {
        relationships = $bindable([]),
        space_name,
        subpath,
        resource_type,
        parent_shortname,
    }: {
        relationships: any[];
        space_name: string;
        subpath: string;
        resource_type: ResourceType;
        parent_shortname: string;
    } = $props();

    let isSaving = $state(false);

    let isEditing = $state(false);
    let editIndex = $state(-1);
    let showForm = $state(false);

    let relSpaceName = $state("");
    let relSubpath = $state("/");
    let relShortname = $state("");
    let relType = $state(ResourceType.content);
    let relSchemaShortname = $state("");

    let relAttributes: any = $state({ json: {} });

    let subpaths: string[] = $state([]);
    let shortnames: any[] = $state([]);
    let isLoadingSubpaths = $state(false);
    let isLoadingShortnames = $state(false);

    // The spaces store is filled once at boot; only fall back to a request
    // when nothing has loaded it yet.
    const spaces = $derived($spacesStore ?? []);
    $effect(() => {
        if (showForm && $spacesStore === null) {
            getSpaces().catch(() => {
                /* the select stays empty; the toast on save explains */
            });
        }
    });

    async function loadSubpaths(spaceName: string) {
        if (!spaceName) {
            subpaths = [];
            return;
        }
        isLoadingSubpaths = true;
        try {
            const tempSubpaths: string[] = [];
            const rootChildren = await getChildren(spaceName, "/", 100);
            await getChildrenAndSubChildren(tempSubpaths, spaceName, "", rootChildren);
            subpaths = tempSubpaths.reverse();
        } catch {
            subpaths = [];
        } finally {
            isLoadingSubpaths = false;
        }
    }

    async function loadShortnames(spaceName: string, subpathVal: string) {
        if (!spaceName || !subpathVal) {
            shortnames = [];
            return;
        }
        isLoadingShortnames = true;
        try {
            const result = await getChildren(spaceName, subpathVal, 100);
            shortnames = (result.records || []).filter((r: any) => r.resource_type !== "folder");
        } catch {
            shortnames = [];
        } finally {
            isLoadingShortnames = false;
        }
    }

    $effect(() => {
        if (relSpaceName) {
            loadSubpaths(relSpaceName);
        }
    });

    $effect(() => {
        if (relSpaceName && relSubpath) {
            loadShortnames(relSpaceName, relSubpath);
        }
    });

    function onSpaceChange() {
        relSubpath = "/";
        relShortname = "";
        shortnames = [];
    }

    function onSubpathChange() {
        relShortname = "";
    }

    function resetForm() {
        relSpaceName = "";
        relSubpath = "/";
        relShortname = "";
        relType = ResourceType.content;
        relSchemaShortname = "";
        relAttributes = { json: {} };
        isEditing = false;
        editIndex = -1;
        showForm = false;
    }

    function handleRenderMenu(items: any[]) {
        return items.filter((item: any) => !["tree", "table"].includes(item.text));
    }

    function populateFormForEdit(index: number) {
        const rel = relationships[index];
        const locator = rel.related_to || {};
        relSpaceName = locator.space_name || "";
        relSubpath = locator.subpath || "/";
        relShortname = locator.shortname || "";
        relType = locator.type || ResourceType.content;
        relSchemaShortname = locator.schema_shortname || "";
        relAttributes = { json: rel.attributes || {} };
        isEditing = true;
        editIndex = index;
        showForm = true;
    }

    function buildRelationship() {
        const locator: any = {
            type: relType,
            space_name: relSpaceName,
            subpath: relSubpath,
            shortname: relShortname,
        };
        if (relSchemaShortname) {
            locator.schema_shortname = relSchemaShortname;
        }

        let attrs = {};
        try {
            if (relAttributes?.json) {
                attrs = relAttributes.json;
            } else if (relAttributes?.text) {
                attrs = JSON.parse(relAttributes.text);
            }
        } catch {
            attrs = {};
        }

        return {
            related_to: locator,
            attributes: attrs,
        };
    }

    async function saveRelationships(updatedRelationships: any[]): Promise<boolean> {
        isSaving = true;
        try {
            await Dmart.request({
                space_name,
                request_type: RequestType.update,
                records: [
                    {
                        resource_type,
                        shortname: parent_shortname,
                        subpath,
                        attributes: {
                            relationships: updatedRelationships,
                        },
                    },
                ],
            });
            showToast(Level.info, $_("relationships_saved"));
            return true;
        } catch (e: unknown) {
            showToast(Level.warn, errorMessage(e, $_("relationships_save_failed")));
            return false;
        } finally {
            isSaving = false;
        }
    }

    async function addRelationship() {
        const rel = buildRelationship();
        if (!rel.related_to.space_name || !rel.related_to.shortname) return;

        let updated: any[];
        if (isEditing && editIndex >= 0) {
            updated = [...relationships];
            updated[editIndex] = rel;
        } else {
            updated = [...relationships, rel];
        }

        if (await saveRelationships(updated)) {
            relationships = updated;
            resetForm();
        }
    }

    // ── Remove: confirmed first ────────────────────────────────────────────
    let removeIndex = $state(-1);
    let removeOpen = $state(false);
    const removeTarget = $derived(removeIndex >= 0 ? relationships[removeIndex] : null);

    function askRemove(index: number) {
        removeIndex = index;
        removeOpen = true;
    }

    async function removeRelationship() {
        const updated = relationships.filter((_: any, i: number) => i !== removeIndex);
        if (await saveRelationships(updated)) {
            relationships = updated;
            removeOpen = false;
        }
    }

    let isDetailsOpen = $state(false);
    let detailsRel: any = $state(null);
    const detailsAttributes = $derived(limitJsonForDisplay(detailsRel?.attributes ?? {}));

    function openDetails(rel: any) {
        detailsRel = rel;
        isDetailsOpen = true;
    }

    function locatorLabel(rel: any): string {
        return `${rel?.related_to?.space_name ?? ""}${rel?.related_to?.subpath ?? "/"}/${rel?.related_to?.shortname ?? ""}`.replace(/\/+/g, "/");
    }
</script>

<div class="space-y-4 w-full">
    {#if relationships && relationships.length > 0}
        <div class="rounded-card border border-border bg-surface-2 shadow-card overflow-x-auto">
            <table class="w-full text-sm text-start border-collapse">
                <thead class="bg-surface text-text-muted text-xs font-semibold">
                    <tr>
                        <th scope="col" class="p-2.5 text-start">{$_("type")}</th>
                        <th scope="col" class="p-2.5 text-start">{$_("space_name")}</th>
                        <th scope="col" class="p-2.5 text-start">{$_("subpath")}</th>
                        <th scope="col" class="p-2.5 text-start">{$_("shortname")}</th>
                        <th scope="col" class="p-2.5 text-end">{$_("actions")}</th>
                    </tr>
                </thead>
                <tbody>
                    {#each relationships as rel, index (index)}
                        <tr class="border-t border-border hover:bg-surface-3 transition-colors">
                            <td class="p-2.5 text-text">{rel.related_to?.type || "content"}</td>
                            <td class="p-2.5 text-text">{rel.related_to?.space_name || "-"}</td>
                            <td class="p-2.5 text-text font-mono text-xs">{rel.related_to?.subpath || "/"}</td>
                            <td class="p-2.5 text-text">{rel.related_to?.shortname || "-"}</td>
                            <td class="p-2.5">
                                <div class="inline-flex items-center justify-end gap-1 w-full">
                                    <IconButton label={$_("view_details")} size="sm" onclick={() => openDetails(rel)}>
                                        <EyeOutline size="sm" />
                                    </IconButton>
                                    <IconButton label={$_("edit")} size="sm" onclick={() => populateFormForEdit(index)}>
                                        <PenOutline size="sm" />
                                    </IconButton>
                                    <IconButton label={$_("remove")} size="sm" variant="danger" disabled={isSaving} onclick={() => askRemove(index)}>
                                        <TrashBinOutline size="sm" />
                                    </IconButton>
                                </div>
                            </td>
                        </tr>
                    {/each}
                </tbody>
            </table>
        </div>
    {:else}
        <EmptyState title={$_("no_relationships")} hint={$_("no_relationships_hint")} />
    {/if}

    <div class="flex justify-end">
        <Button
            size="sm"
            color="primary"
            onclick={() => {
                resetForm();
                showForm = true;
            }}
        >
            <PlusOutline size="sm" class="me-1.5" aria-hidden="true" />
            {$_("add_relationship")}
        </Button>
    </div>
</div>

<Modal
    bind:open={showForm}
    size="lg"
    title={isEditing ? $_("edit_relationship") : $_("new_relationship")}
    onclose={resetForm}
    class="rounded-modal shadow-modal"
>
    <div class="space-y-4 w-full">
        <div>
            <Label for="rel-space" class="mb-1.5">{$_("space_name")}</Label>
            <Select id="rel-space" bind:value={relSpaceName} onchange={onSpaceChange}>
                <option value="">{$_("select_space")}</option>
                {#each spaces as space (space.shortname)}
                    <option value={space.shortname}>{space.shortname}</option>
                {/each}
            </Select>
        </div>

        <div>
            <Label for="rel-subpath" class="mb-1.5">{$_("subpath")}</Label>
            <Select id="rel-subpath" bind:value={relSubpath} onchange={onSubpathChange} disabled={!relSpaceName || isLoadingSubpaths}>
                <option value="/">/</option>
                {#each subpaths as path (path)}
                    <option value={path}>{path}</option>
                {/each}
            </Select>
            {#if isLoadingSubpaths}
                <p class="text-xs text-text-muted mt-1">{$_("loading_subpaths")}</p>
            {/if}
        </div>

        <div>
            <Label for="rel-shortname" class="mb-1.5">{$_("shortname")}</Label>
            <Select id="rel-shortname" bind:value={relShortname} disabled={!relSpaceName || isLoadingShortnames}>
                <option value="">{$_("select_entry_option")}</option>
                {#each shortnames as item (item.shortname)}
                    <option value={item.shortname}>{item.shortname}</option>
                {/each}
            </Select>
            {#if isLoadingShortnames}
                <p class="text-xs text-text-muted mt-1">{$_("loading_entries")}</p>
            {/if}
        </div>

        <div>
            <Label for="rel-type" class="mb-1.5">{$_("resource_type")}</Label>
            <Select id="rel-type" bind:value={relType}>
                {#each Object.values(ResourceType) as rt (rt)}
                    <option value={rt}>{rt}</option>
                {/each}
            </Select>
        </div>

        <div>
            <Label for="rel-schema" class="mb-1.5">{$_("schema_shortname")} <span class="text-text-faint font-normal">({$_("optional")})</span></Label>
            <Input id="rel-schema" type="text" bind:value={relSchemaShortname} placeholder={$_("schema_shortname")} />
        </div>

        <div>
            <p class="text-sm font-medium text-text mb-1.5" id="rel-attributes-label">{$_("attributes")}</p>
            <div aria-labelledby="rel-attributes-label" style="min-height: 120px;">
                <LazyJsonEditor onRenderMenu={handleRenderMenu} mode="text" bind:content={relAttributes} />
            </div>
        </div>
    </div>

    <div class="flex items-center justify-end gap-2 w-full pt-4 border-t border-border mt-4">
        <Button color="alternative" onclick={resetForm}>{$_("cancel")}</Button>
        <Button color="primary" onclick={addRelationship} disabled={!relSpaceName || !relShortname || isSaving}>
            {#if isSaving}
                {$_("saving")}
            {:else}
                {isEditing ? $_("save_changes") : $_("add")}
            {/if}
        </Button>
    </div>
</Modal>

<Modal bind:open={isDetailsOpen} size="lg" title={$_("relationship_details")} class="rounded-modal shadow-modal">
    {#if detailsRel}
        <dl class="grid grid-cols-3 gap-x-4 gap-y-2 text-sm w-full">
            <dt class="font-medium text-text-muted">{$_("type")}</dt>
            <dd class="col-span-2 text-text">{detailsRel.related_to?.type || "content"}</dd>
            <dt class="font-medium text-text-muted">{$_("space_name")}</dt>
            <dd class="col-span-2 text-text">{detailsRel.related_to?.space_name || "-"}</dd>
            <dt class="font-medium text-text-muted">{$_("subpath")}</dt>
            <dd class="col-span-2 text-text font-mono text-xs">{detailsRel.related_to?.subpath || "/"}</dd>
            <dt class="font-medium text-text-muted">{$_("shortname")}</dt>
            <dd class="col-span-2 text-text">{detailsRel.related_to?.shortname || "-"}</dd>
            {#if detailsRel.related_to?.schema_shortname}
                <dt class="font-medium text-text-muted">{$_("schema_shortname")}</dt>
                <dd class="col-span-2 text-text">{detailsRel.related_to.schema_shortname}</dd>
            {/if}
        </dl>
        <div class="mt-4">
            <p class="text-sm font-medium text-text-muted mb-2">{$_("attributes")}</p>
            {#if detailsAttributes.truncated}
                <p class="text-xs text-text-muted mb-1">{$_("preview_truncated")}</p>
            {/if}
            <div class="max-h-80 overflow-auto">
                <Prism code={detailsAttributes.value as object | string} />
            </div>
        </div>
    {/if}

    <div class="flex justify-end w-full pt-4 border-t border-border mt-4">
        <Button color="alternative" onclick={() => (isDetailsOpen = false)}>{$_("close")}</Button>
    </div>
</Modal>

<ConfirmDialog
    bind:open={removeOpen}
    variant="danger"
    title={$_("remove_relationship")}
    body={$_("confirm_remove_relationship", { values: { target: removeTarget ? locatorLabel(removeTarget) : "" } })}
    confirmLabel={$_("remove")}
    loading={isSaving}
    loadingLabel={$_("saving")}
    onConfirm={removeRelationship}
/>
