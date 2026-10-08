<script lang="ts">
    import ListView from "@/components/management/ListView.svelte";
    import {
        Dmart,
        QueryType,
        ResourceType,
        RequestType,
        type ResponseEntry,
    } from "@edraj/tsdmart";
    import { checkAccess } from "@/utils/checkAccess";
    import {
        ClockOutline,
        DrawSquareSolid,
        EditOutline,
        EyeSolid,
        FloppyDiskOutline,
        LinkOutline,
        ListOutline,
        PaperClipOutline,
        RectangleListOutline,
        RefreshOutline,
        ShareNodesSolid,
        TrashBinOutline,
        TrashBinSolid,
        ClockArrowOutline,
    } from "flowbite-svelte-icons";
    import { JSONEditor, Mode } from "svelte-jsoneditor";
    import { jsonEditorContentParser } from "@/utils/jsonEditor";
    import Prism from "@/components/Prism.svelte";
    import Table2Cols from "@/components/management/Table2Cols.svelte";
    import Attachments from "@/components/management/renderers/Attachments.svelte";
    import RelationshipsPanel from "@/components/management/renderers/RelationshipsPanel.svelte";
    import BreadCrumbLite from "@/components/management/BreadCrumbLite.svelte";
    import {
        currentEntry,
        currentListView,
        InputMode,
        spaceChildren,
    } from "@/stores/global";
    import MetaForm from "@/components/management/forms/MetaForm.svelte";
    import MetaUserForm from "@/components/management/forms/MetaUserForm.svelte";
    import MetaRoleForm from "@/components/management/forms/MetaRoleForm.svelte";
    import MetaPermissionForm from "@/components/management/forms/MetaPermissionForm.svelte";
    import SpaceForm from "@/components/management/forms/SpaceForm.svelte";
    import { untrack, onDestroy, tick } from "svelte";
    import { activeRoute, beforeUrlChange, goto } from "@roxi/routify";
    import { sidebarCacheKey, trashRestoreTarget, normalizeSubpath } from "@/utils/subpath";
    import HistoryListView from "@/components/management/HistoryListView.svelte";
    import MetaTicketForm from "@/components/management/forms/MetaTicketForm.svelte";
    import WorkflowDiagram from "@/components/management/diagram/WorkflowDiagram.svelte";
    import SchemaDiagram from "@/components/management/diagram/SchemaDiagram.svelte";
    import { Button, Card, Modal } from "flowbite-svelte";
    import { searchListView } from "@/stores/management/triggers";
    import { isDeepEqual } from "@/utils/compare";
    import { user } from "@/stores/user";
    import PayloadForm from "@/components/management/forms/PayloadForm.svelte";
    import { getParentSubpath as getParentPath } from "@/utils/entryManagement";
    import { getChildren } from "@/lib/dmart_services";
    import RolesExplorer from "@/components/management/renderers/RolesExplorer.svelte";
    import PermissionsExplorer from "@/components/management/renderers/PermissionsExplorer.svelte";
    import {
        deleteEntry,
        moveEntryToTrash,
        saveEntry,
    } from "@/utils/entryManagement";
    import { bulkBucket } from "@/stores/management/bulk_bucket";
    import { showToast, Level } from "@/utils/toast";
    import { _ } from "svelte-i18n";

    const EDITOR_INIT_DELAY = 512;
    const DEFAULT_RECORDS_LIMIT = 50;

    const TabMode = {
        list: 0,
        entry: 1,
        form: 2,
        attachments: 3,
        history: 4,
        diagram: 5,
        roles_explorer: 6,
        permissions_explorer: 7,
        relationships: 8,
    };

    let {
        entry = $bindable(),
        space_name,
        subpath,
        resource_type,
    }: {
        entry: ResponseEntry;
        space_name: string;
        subpath: string;
        resource_type: ResourceType;
    } = $props();

    $goto;

    $searchListView = "";

    const schemaShortname = entry?.payload?.schema_shortname || null;
    let selectedInputMode = $state(InputMode.form);
    currentEntry.set({
        entry,
        refreshEntry,
    });

    let coinTriggerRefresh = $state(false);

    let jeContent: any = $state({ json: structuredClone(entry) });
    let entryRelationships: any[] = $state(entry.relationships || []);
    let originalJeContent: any = $state({});
    let _initTimer = setTimeout(() => {
        originalJeContent = jsonEditorContentParser($state.snapshot(jeContent));
    }, EDITOR_INIT_DELAY);
    let isJEDirty = $state(false);

    onDestroy(() => {
        clearTimeout(_initTimer);
    });

    let errorMessage: string | null | undefined = $state(null);

    // svelte-ignore state_referenced_locally
    const isEntryTrash =
        space_name === "personal" &&
        subpath.startsWith(`people/${$user.shortname}/trash`);

    // svelte-ignore state_referenced_locally
    const canUpdate = checkAccess("update", space_name, subpath, resource_type);
    // svelte-ignore state_referenced_locally
    const canDelete = (() => {
        if (space_name === "management" && subpath === "/") {
            if (
                resource_type === ResourceType.space ||
                resource_type === ResourceType.folder
            ) {
                return false;
            }
        }
        return checkAccess("delete", space_name, subpath, resource_type);
    })();

    let activeTab = $state(TabMode.list);
    let isActionLoading = $state(false);
    // Replaced by the Form tab's components through bind:validateFn; until
    // then (and for resource types without a second form) nothing to check.
    let validateMetaForm: () => boolean = $state(() => true);
    let validateRTForm: () => boolean = $state(() => true);

    /**
     * Run the constraint validators the Form tab's components bound. They are
     * only meaningful while that tab is showing: on the Entry (JSON) tab the
     * forms are unmounted and the JSON text is the source of truth.
     */
    function formsAreValid(): boolean {
        if (activeTab !== TabMode.form) return true;
        for (const validate of [validateMetaForm, validateRTForm]) {
            if (typeof validate === "function" && !validate()) return false;
        }
        return true;
    }

    function navigateAfterEntryAction() {
        if (resource_type === ResourceType.space) {
            $goto(`/management/content`);
        } else if (resource_type === ResourceType.folder) {
            const _subpath = subpath.split("/").slice(0, -1).join("-") || "";
            $goto(`/management/content/[space_name]/[subpath]`, {
                space_name: space_name,
                subpath: _subpath,
            });
        } else {
            $goto(`/management/content/[space_name]/[subpath]`, {
                space_name: space_name,
                subpath: subpath,
            });
        }
    }

    async function performSave() {
        isActionLoading = true;
        errorMessage = null;

        const result = await saveEntry(
            $state.snapshot(jeContent),
            space_name,
            subpath,
            resource_type,
            $state.snapshot(originalJeContent),
        );

        if (result.success) {
            await refreshEntry();
        } else {
            errorMessage = result.errorMessage;
        }

        isActionLoading = false;
    }

    let showSchemaImpactModal = $state(false);
    let schemaAffectedCount = $state(0);

    let showPermissionImpactModal = $state(false);
    let permissionAffectedRoles = $state<string[]>([]);

    let showRoleImpactModal = $state(false);
    let roleAffectedUsersCount = $state(0);

    async function handleSave() {
        errorMessage = null;
        // The same validation create enforces; the browser highlights the
        // offending field through reportValidity().
        if (!formsAreValid()) {
            errorMessage = $_("fill_required_meta");
            return;
        }
        if (resource_type === ResourceType.schema) {
            try {
                isActionLoading = true;
                const countersResult = await Dmart.query({
                    type: QueryType.counters,
                    space_name: space_name,
                    subpath: "/",
                    exact_subpath: false,
                    retrieve_json_payload: true,
                    search: `@payload.schema_shortname:${entry.shortname}`,
                });
                schemaAffectedCount = countersResult?.attributes?.returned ?? 0;
                isActionLoading = false;
                if (schemaAffectedCount > 0) {
                    showSchemaImpactModal = true;
                    return;
                }
            } catch (e) {
                isActionLoading = false;
            }
        } else if (resource_type === ResourceType.permission) {
            try {
                isActionLoading = true;
                const rolesResult = await Dmart.query({
                    type: QueryType.search,
                    space_name: "management",
                    subpath: "/roles",
                    exact_subpath: true,
                    retrieve_json_payload: true,
                    search: `@permissions:${entry.shortname}`,
                    limit: 100,
                    offset: 0,
                });
                permissionAffectedRoles = (rolesResult?.records ?? []).map(
                    (r) => r.shortname,
                );
                isActionLoading = false;
                if (permissionAffectedRoles.length > 0) {
                    showPermissionImpactModal = true;
                    return;
                }
            } catch (e) {
                isActionLoading = false;
            }
        } else if (resource_type === ResourceType.role) {
            try {
                isActionLoading = true;
                const usersResult = await Dmart.query({
                    type: QueryType.counters,
                    space_name: "management",
                    subpath: "/users",
                    exact_subpath: true,
                    retrieve_json_payload: true,
                    search: `@roles:${entry.shortname}`,
                });
                roleAffectedUsersCount =
                    usersResult?.attributes?.returned ?? 0;
                isActionLoading = false;
                if (roleAffectedUsersCount > 0) {
                    showRoleImpactModal = true;
                    return;
                }
            } catch (e) {
                isActionLoading = false;
            }
        }
        await performSave();
    }

    async function confirmSchemaUpdate() {
        showSchemaImpactModal = false;
        await performSave();
    }

    function cancelSchemaUpdate() {
        showSchemaImpactModal = false;
    }

    async function confirmPermissionUpdate() {
        showPermissionImpactModal = false;
        await performSave();
    }

    function cancelPermissionUpdate() {
        showPermissionImpactModal = false;
    }

    async function confirmRoleUpdate() {
        showRoleImpactModal = false;
        await performSave();
    }

    function cancelRoleUpdate() {
        showRoleImpactModal = false;
    }

    let openDeleteModal = $state(false);
    let forceDelete = $state(false);
    const showForce = $derived(
        resource_type === ResourceType.folder || resource_type === ResourceType.user,
    );
    function deleteCurrentEntryModal() {
        errorMessage = null;
        forceDelete = false;
        openDeleteModal = true;
    }
    async function deleteCurrentEntry() {
        isActionLoading = true;
        errorMessage = null;
        const result = await deleteEntry(
            entry,
            space_name,
            subpath,
            resource_type,
            showForce && forceDelete,
        );

        if (result.success) {
            openDeleteModal = false;
            isActionLoading = false;
            await tick();
            navigateAfterEntryAction();
            return;
        } else {
            errorMessage = result.errorMessage;
        }

        isActionLoading = false;
    }

    async function moveToTrash() {
        isActionLoading = true;

        const result = await moveEntryToTrash(
            entry,
            space_name,
            subpath,
            resource_type,
            $user.shortname ?? "",
        );

        if (result.success) {
            navigateAfterEntryAction();
        } else {
            errorMessage = result.errorMessage;
        }

        isActionLoading = false;
    }

    async function restoreTrashEntry() {
        isActionLoading = true;
        try {
            // `/people/<user>/trash/<space>/<subpath>` → where it came from;
            // the helper copes with either slash spelling.
            const target = trashRestoreTarget(subpath);
            if (!target) {
                showToast(Level.warn, $_("not_in_trash"));
                return;
            }
            const srcSubpath = normalizeSubpath(subpath);

            const result = await Dmart.request({
                space_name: "personal",
                request_type: RequestType.move,
                records: [
                    {
                        resource_type: resource_type,
                        shortname: entry.shortname,
                        subpath: srcSubpath,
                        attributes: {
                            src_space_name: "personal",
                            src_subpath: srcSubpath,
                            src_shortname: entry.shortname,

                            dest_space_name: target.space_name,
                            dest_subpath: target.subpath,
                            dest_shortname: entry.shortname,
                        },
                    },
                ],
            });

            if (result.status === "success") {
                showToast(Level.info, $_("entry_restored"));
                $goto("/management/tools/trash");
            } else {
                showToast(Level.warn, $_("entry_restore_failed"));
            }
        } catch (e: any) {
            showToast(Level.warn, e?.response?.data?.error?.message ?? $_("entry_restore_failed"));
        } finally {
            isActionLoading = false;
        }
    }

    async function refreshEntry() {
        if (resource_type === ResourceType.folder) {
            if (isJEDirty) {
                entry = (await Dmart.retrieveEntry({
                    resource_type,
                    space_name,
                    subpath: getParentPath(subpath),
                    shortname: entry.shortname,
                    retrieve_json_payload: true,
                    retrieve_attachments: true,
                    validate_schema: true,
                }))!;
                // The sidebar owns its cache (keys, paging); ask it to reload
                // this folder's parent. Fall back to a direct write with the
                // same key helper when no sidebar is mounted.
                if ($spaceChildren.refresh) {
                    await $spaceChildren.refresh(space_name, getParentPath(subpath), true);
                } else {
                    const children = await getChildren(
                        space_name,
                        getParentPath(subpath),
                        DEFAULT_RECORDS_LIMIT,
                        0,
                        [ResourceType.folder],
                    );
                    $spaceChildren.data.set(
                        sidebarCacheKey(space_name, getParentPath(subpath)),
                        children.records || [],
                    );
                    $spaceChildren = {
                        ...$spaceChildren,
                        data: new Map($spaceChildren.data),
                    };
                }
            }
            await $currentListView?.fetchPageRecords();
        } else {
            entry = (await Dmart.retrieveEntry({
                resource_type,
                space_name,
                subpath,
                shortname: entry.shortname,
                retrieve_json_payload: true,
                retrieve_attachments: true,
                validate_schema: true,
            }))!;
        }
        jeContent = { json: $state.snapshot(entry) };
        entryRelationships = entry.relationships || [];
        clearTimeout(_initTimer);
        _initTimer = setTimeout(() => {
            originalJeContent = jsonEditorContentParser(
                $state.snapshot(jeContent),
            );
        }, EDITOR_INIT_DELAY);
        currentEntry.set({
            entry,
            refreshEntry,
        });
        coinTriggerRefresh = !coinTriggerRefresh;
    }

    $effect(() => {
        if (activeTab === TabMode.entry) {
            untrack(() => {
                try {
                    const _jeContent = jsonEditorContentParser(
                        $state.snapshot(jeContent),
                    );
                    jeContent = { text: JSON.stringify(_jeContent, null, 2) };
                } catch (e) {}
            });
        } else if (activeTab === TabMode.form) {
            untrack(() => {
                try {
                    const _jeContent = jsonEditorContentParser(
                        $state.snapshot(jeContent),
                    );
                    jeContent = { json: _jeContent };
                } catch (e) {}
            });
        }
    });

    let isRefreshLoading = $state(false);
    let hasStreamChanges = $state(false);
    async function handleRefresh(e) {
        if (e) {
            e.preventDefault();
        }

        if (isJEDirty) {
            pendingRefreshAction = async () => {
                await refreshEntry();
                hasStreamChanges = false;
            };
            unsavedPrompt = "refresh";
            showUnsavedChangesModal = true;
            return;
        }

        await refreshEntry();
        hasStreamChanges = false;
    }

    $effect(() => {
        if (jeContent) {
            try {
                isJEDirty = !isDeepEqual(
                    jsonEditorContentParser($state.snapshot(jeContent)),
                    $state.snapshot(originalJeContent),
                );
            } catch (e) {
                isJEDirty = true;
            }
        }
    });

    let showUnsavedChangesModal = $state(false);
    // What the modal is protecting: a Refresh click or an in-app navigation.
    let unsavedPrompt: "refresh" | "leave" = $state("refresh");
    let pendingRefreshAction: (() => void) | null = $state(null);
    let pendingNavigation: ((proceed: boolean) => void) | null = null;

    function confirmDiscardChanges() {
        showUnsavedChangesModal = false;
        if (pendingRefreshAction) {
            pendingRefreshAction();
            pendingRefreshAction = null;
        }
        if (pendingNavigation) {
            // Mark clean so the guard does not fire again for the same leave.
            isJEDirty = false;
            pendingNavigation(true);
            pendingNavigation = null;
        }
    }

    function cancelDiscardChanges() {
        showUnsavedChangesModal = false;
        pendingRefreshAction = null;
        if (pendingNavigation) {
            pendingNavigation(false);
            pendingNavigation = null;
        }
    }

    // The modal can also close through Escape or the overlay, bypassing both
    // buttons; a navigation left waiting on it would block every later one.
    // Any close with a decision still pending means "stay".
    $effect(() => {
        if (!showUnsavedChangesModal && pendingNavigation) {
            const resolve = pendingNavigation;
            pendingNavigation = null;
            resolve(false);
        }
    });

    // Closing or reloading the tab: the browser's own prompt.
    function beforeUnload(event: BeforeUnloadEvent) {
        if (isJEDirty) {
            event.preventDefault();
            event.returnValue = true;
        }
    }

    // In-app navigation: Routify runs these guards before the URL changes and
    // waits for a promise, so the modal can decide. Query-only changes on the
    // same page (the list below rewrites page/sort/search params) are not a
    // leave and pass straight through.
    const pathOf = (route: { url?: string } | null | undefined) =>
        (route?.url ?? "").split("?")[0];
    $beforeUrlChange(({ route }) => {
        if (!isJEDirty) return true;
        if (pathOf(route) === pathOf($activeRoute)) return true;
        if (pendingNavigation) return false;
        return new Promise<boolean>((resolve) => {
            pendingNavigation = resolve;
            pendingRefreshAction = null;
            unsavedPrompt = "leave";
            showUnsavedChangesModal = true;
        });
    });
</script>

<svelte:window onbeforeunload={beforeUnload} />

<div class="flex flex-col w-full">
    <BreadCrumbLite
            {space_name}
            {subpath}
            {resource_type}
            schema_name={schemaShortname ?? undefined}
            shortname={entry.shortname}
            payloadContentType={entry?.payload?.content_type}
    />

    <div class="border-b border-gray-200">
        <ul
                class="flex flex-wrap -mb-px text-sm font-medium text-center"
                role="tablist"
        >
            <li class="mr-2" role="presentation">
                <button
                        class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                    TabMode.list
                        ? 'text-blue-600 border-blue-600'
                        : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                        type="button"
                        role="tab"
                        aria-selected={activeTab === TabMode.list}
                        onclick={() => (activeTab = TabMode.list)}
                >
                    <div class="flex items-center gap-2">
                        {#if [ResourceType.folder, ResourceType.space].includes(resource_type)}
                            <ListOutline size="md" />
                            <p>List view</p>
                        {:else}
                            <EyeSolid size="md" />
                            <p>Content</p>
                        {/if}
                    </div>
                </button>
            </li>
            <li class="mr-2" role="presentation">
                <button
                        class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                    TabMode.entry
                        ? 'text-blue-600 border-blue-600'
                        : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                        type="button"
                        role="tab"
                        aria-selected={activeTab === TabMode.entry}
                        onclick={() => (activeTab = TabMode.entry)}
                >
                    <div class="flex items-center gap-2">
                        <EditOutline size="md" />
                        <p>Entry</p>
                    </div>
                </button>
            </li>
            <li class="mr-2" role="presentation">
                <button
                        class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                    TabMode.form
                        ? 'text-blue-600 border-blue-600'
                        : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                        type="button"
                        role="tab"
                        aria-selected={activeTab === TabMode.form}
                        onclick={() => (activeTab = TabMode.form)}
                >
                    <div class="flex items-center gap-2">
                        <RectangleListOutline size="md" />
                        <p>Form</p>
                    </div>
                </button>
            </li>
            {#if resource_type === ResourceType.schema || subpath === "workflows"}
                <li class="mr-2" role="presentation">
                    <button
                            class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                        TabMode.diagram
                            ? 'text-blue-600 border-blue-600'
                            : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                            type="button"
                            role="tab"
                            aria-selected={activeTab === TabMode.diagram}
                            onclick={() => (activeTab = TabMode.diagram)}
                    >
                        <div class="flex items-center gap-2">
                            <DrawSquareSolid size="md" />
                            <p>Diagram</p>
                        </div>
                    </button>
                </li>
            {/if}
            <li class="mr-2" role="presentation">
                <button
                        class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                    TabMode.attachments
                        ? 'text-blue-600 border-blue-600'
                        : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                        type="button"
                        role="tab"
                        aria-selected={activeTab === TabMode.attachments}
                        onclick={() => (activeTab = TabMode.attachments)}
                >
                    <div class="flex items-center gap-2">
                        <PaperClipOutline size="md" />
                        <p>
                            Attachments {Object.values(entry.attachments ?? {}).flat(
                            1,
                        ).length
                            ? `(${Object.values(entry.attachments ?? {}).flat(1).length})`
                            : ""}
                        </p>
                    </div>
                </button>
            </li>
            {#if resource_type === ResourceType.user}
                <li role="presentation">
                    <button
                            class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                        TabMode.roles_explorer
                            ? 'text-blue-600 border-blue-600'
                            : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                            type="button"
                            role="tab"
                            aria-selected={activeTab === TabMode.roles_explorer}
                            onclick={() => (activeTab = TabMode.roles_explorer)}
                    >
                        <div class="flex items-center gap-2">
                            <ShareNodesSolid size="md" />
                            <p>Role explorer</p>
                        </div>
                    </button>
                </li>
            {/if}
            {#if resource_type === ResourceType.role}
                <li role="presentation">
                    <button
                            class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                        TabMode.permissions_explorer
                            ? 'text-blue-600 border-blue-600'
                            : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                            type="button"
                            role="tab"
                            aria-selected={activeTab ===
                            TabMode.permissions_explorer}
                            onclick={() =>
                            (activeTab = TabMode.permissions_explorer)}
                    >
                        <div class="flex items-center gap-2">
                            <ShareNodesSolid size="md" />
                            <p>Permission explorer</p>
                        </div>
                    </button>
                </li>
            {/if}
            <li role="presentation">
                <button
                        class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                    TabMode.relationships
                        ? 'text-blue-600 border-blue-600'
                        : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                        type="button"
                        role="tab"
                        aria-selected={activeTab === TabMode.relationships}
                        onclick={() => (activeTab = TabMode.relationships)}
                >
                    <div class="flex items-center gap-2">
                        <LinkOutline size="md" />
                        <p>
                            Relationships {entryRelationships.length
                                ? `(${entryRelationships.length})`
                                : ""}
                        </p>
                    </div>
                </button>
            </li>
            <li role="presentation">
                <button
                        class="inline-flex items-center p-4 border-b-2 rounded-t-lg {activeTab ===
                    TabMode.history
                        ? 'text-blue-600 border-blue-600'
                        : 'border-transparent hover:text-gray-600 hover:border-gray-300'}"
                        type="button"
                        role="tab"
                        aria-selected={activeTab === TabMode.history}
                        onclick={() => (activeTab = TabMode.history)}
                >
                    <div class="flex items-center gap-2">
                        <ClockOutline size="md" />
                        <p>History</p>
                    </div>
                </button>
            </li>
            <!-- Save only where something is editable: the Entry (JSON) and
                 Form tabs. A folder's List view has nothing to save. -->
            {#if canUpdate && (activeTab === TabMode.entry || activeTab === TabMode.form)}
                <li class="ms-auto" role="presentation">
                    <button
                            class="inline-flex items-center p-4 border-b-2 rounded-t-lg border-transparent hover:text-primary hover:border-primary"
                            type="button"
                            onclick={handleSave}
                            disabled={isActionLoading || !isJEDirty}
                            style={isActionLoading || !isJEDirty
                            ? "cursor: not-allowed"
                            : "cursor: pointer"}
                            title={$_("save")}
                    >
                        <div class="flex items-center gap-2">
                            <FloppyDiskOutline size="md" class="text-primary" />
                            <p class="text-primary">{$_("save")}</p>
                        </div>
                    </button>
                </li>
            {/if}
            {#if canDelete && !isEntryTrash && $bulkBucket.length === 0}
                <li role="presentation">
                    <button
                            class="inline-flex items-center p-4 border-b-2 rounded-t-lg border-transparent hover:text-red-600 hover:border-red-600"
                            type="button"
                            disabled={isActionLoading}
                            style={isActionLoading
                            ? "cursor: not-allowed"
                            : "cursor: pointer"}
                            onclick={deleteCurrentEntryModal}
                            title="Delete this entry"
                    >
                        <div class="flex items-center gap-2">
                            <TrashBinSolid size="md" class="text-red-500" />
                            <p class="text-red-500">Delete</p>
                        </div>
                    </button>
                </li>
                {#if ![ResourceType.space, ResourceType.folder].includes(resource_type)}
                    <li role="presentation">
                        <button
                                class="inline-flex items-center p-4 border-b-2 rounded-t-lg border-transparent hover:text-red-600 hover:border-red-600"
                                type="button"
                                disabled={isActionLoading}
                                style={isActionLoading
                                ? "cursor: not-allowed"
                                : "cursor: pointer"}
                                onclick={moveToTrash}
                                title="Delete this entry"
                        >
                            <div class="flex items-center gap-2">
                                <TrashBinOutline
                                        size="md"
                                        class="text-red-500"
                                />
                                <p class="text-red-500">Trash</p>
                            </div>
                        </button>
                    </li>
                {/if}
            {/if}
            {#if isEntryTrash}
                <li role="presentation">
                    <button
                            class="inline-flex items-center p-4 border-b-2 rounded-t-lg border-transparent hover:text-green-600 hover:border-green-600"
                            type="button"
                            disabled={isActionLoading}
                            style={isActionLoading
                            ? "cursor: not-allowed"
                            : "cursor: pointer"}
                            onclick={restoreTrashEntry}
                            title="Restore this entry"
                    >
                        <div class="flex items-center gap-2">
                            <ClockArrowOutline
                                    size="md"
                                    class="text-green-500"
                            />
                            <p class="text-green-500">Restore</p>
                        </div>
                    </button>
                </li>
            {/if}
            <li role="presentation">
                <button
                        class={hasStreamChanges
                        ? "inline-flex items-center p-4 border-b-2 rounded-t-lg bg-orange-500 border-orange-500 text-white hover:bg-orange-600 hover:border-orange-600"
                        : "inline-flex items-center p-4 border-b-2 rounded-t-lg border-transparent hover:text-primary hover:border-primary"}
                        type="button"
                        onclick={handleRefresh}
                        disabled={isRefreshLoading}
                        style={isRefreshLoading
                        ? "cursor: not-allowed"
                        : "cursor: pointer"}
                        title={hasStreamChanges ? "Changes available — click to refresh" : "Refresh"}
                >
                    <div class="flex items-center gap-2">
                        <RefreshOutline
                                size="md"
                                class={hasStreamChanges ? "text-white" : "text-primary"}
                        />
                        <p class={hasStreamChanges ? "text-white" : "text-primary"}>
                            Refresh
                        </p>
                    </div>
                </button>
            </li>
        </ul>
    </div>

    <div class="mt-2">
        <div class={activeTab === TabMode.list ? "" : "hidden"} role="tabpanel">
            {#if [ResourceType.folder, ResourceType.space].includes(resource_type)}
<!-- Re-mount on refresh (coinTriggerRefresh toggles in refreshEntry) so
                     editing the folder's index_attributes and hitting Refresh rebuilds
                     the columns from the new attributes AND reloads the data, instead of
                     keeping the columns computed at first mount. -->
                {#key coinTriggerRefresh}
                    <ListView
                            {space_name}
                            {subpath}
                            folderColumns={entry?.payload?.body?.index_attributes ??
                            null}
                            sort_by={entry?.payload?.body?.sort_by ?? null}
                            sort_order={entry?.payload?.body?.sort_type ?? null}
                            query={entry?.payload?.body?.query ?? null}
                            stream={entry?.payload?.body?.stream === true}
                            onStreamUpdate={() => (hasStreamChanges = true)}
                            {canDelete}
                            exact_subpath={entry?.payload?.body?.expand_children !== true}
                    />
                {/key}
            {:else}
                <Table2Cols
                        entry={{ "Resource type": resource_type, ...entry }}
                />
            {/if}
        </div>

        <div
                class={activeTab === TabMode.entry ? "" : "hidden"}
                role="tabpanel"
        >
            {#if activeTab === TabMode.entry && (jeContent.text || jeContent.json)}
                <JSONEditor bind:content={jeContent} mode={Mode.text} />
            {/if}
            {#if errorMessage}
                <div class="overflow-auto">
                    <Prism code={errorMessage} />
                </div>
            {/if}
        </div>

        <div class={activeTab === TabMode.form ? "" : "hidden"} role="tabpanel">
            {#key coinTriggerRefresh}
                {#if jeContent.json}
                    <MetaForm
                            bind:formData={jeContent.json}
                            bind:validateFn={validateMetaForm}
                            isCreate={false}
                    />
                    {#if resource_type === ResourceType.user}
                        <MetaUserForm
                                bind:formData={jeContent.json}
                                bind:validateFn={validateRTForm}
                                isCreate={false}
                        />
                    {:else if resource_type === ResourceType.space}
                        <SpaceForm
                                bind:formData={jeContent.json}
                                spaceName={space_name}
                        />
                    {:else if resource_type === ResourceType.role}
                        <MetaRoleForm
                                bind:formData={jeContent.json}
                                bind:validateFn={validateRTForm}
                        />
                    {:else if resource_type === ResourceType.permission}
                        <MetaPermissionForm
                                bind:formData={jeContent.json}
                                bind:validateFn={validateRTForm}
                                readOnly={false}
                        />
                    {:else if resource_type === ResourceType.ticket}
                        <MetaTicketForm
                                {space_name}
                                {subpath}
                                shortname={entry.shortname}
                                meta={jeContent.json}
                        />
                    {/if}
                    {#if jeContent?.json?.payload?.body}
                        <Card class="p-4 max-w-4xl mx-auto my-2">
                            <h1 class="text-2xl font-bold mb-4">Payload</h1>
                            <PayloadForm
                                    isCreate={false}
                                    bind:selectedResourceType={resource_type}
                                    selectedSchema={schemaShortname}
                                    bind:selectedWorkflow={
                                    jeContent.json.workflow_shortname
                                }
                                    bind:selectedInputMode
                                    bind:contentType={
                                    jeContent.json.payload.content_type
                                }
                                    bind:content={jeContent.json.payload.body}
                            />
                        </Card>
                    {/if}
                {/if}
                {#if errorMessage}
                    <div class="overflow-auto">
                        <Prism code={errorMessage} />
                    </div>
                {/if}
            {/key}
        </div>

        {#if resource_type === ResourceType.schema || subpath === "workflows"}
            <div
                    class={activeTab === TabMode.diagram ? "" : "hidden"}
                    role="tabpanel"
            >
                {#if resource_type === ResourceType.schema}
                    <SchemaDiagram
                            shortname={entry.shortname}
                            properties={entry.payload?.body?.properties}
                    />
                {/if}
                {#if subpath === "workflows" && entry?.payload?.body}
                    <WorkflowDiagram
                            shortname={entry.shortname}
                            workflowContent={entry?.payload?.body}
                    />
                {/if}
            </div>
        {/if}

        <div
                class={activeTab === TabMode.attachments ? "" : "hidden"}
                role="tabpanel"
        >
            <Attachments
                    {resource_type}
                    {space_name}
                    {subpath}
                    parent_shortname={entry.shortname}
                    attachments={$state.snapshot(entry).attachments}
                    {refreshEntry}
            />
        </div>

        {#if activeTab === TabMode.roles_explorer}
            <div
                    class={activeTab === TabMode.roles_explorer ? "" : "hidden"}
                    role="tabpanel"
            >
                <RolesExplorer roles={jeContent.json.roles} />
            </div>
        {/if}

        {#if activeTab === TabMode.permissions_explorer}
            <div
                    class={activeTab === TabMode.permissions_explorer
                    ? ""
                    : "hidden"}
                    role="tabpanel"
            >
                <PermissionsExplorer permissions={jeContent.json.permissions} />
            </div>
        {/if}

        <div
                class={activeTab === TabMode.relationships ? "" : "hidden"}
                role="tabpanel"
        >
            <RelationshipsPanel
                    {resource_type}
                    {space_name}
                    {subpath}
                    parent_shortname={entry.shortname}
                    bind:relationships={entryRelationships}
            />
        </div>

        <div
                class={activeTab === TabMode.history ? "" : "hidden"}
                role="tabpanel"
        >
            {#key coinTriggerRefresh}
                <HistoryListView
                        {space_name}
                        {subpath}
                        shortname={entry.shortname}
                />
            {/key}
        </div>
    </div>
</div>

<Modal
    bind:open={openDeleteModal}
    size="sm"
    placement="center"
    title="Confirm Deletion"
    class="max-w-lg! w-full! my-auto! mx-auto!"
>
    <p class="text-center">
        Are you sure you want to delete <span class="font-bold"
            >{entry.shortname}</span
        >
        ({resource_type})?<br />
        This action cannot be undone.
    </p>

    {#if showForce}
        <label class="flex items-start gap-2 mt-4 text-sm cursor-pointer">
            <input type="checkbox" bind:checked={forceDelete} class="mt-0.5" />
            <span>
                <span class="font-semibold">{$_("force_delete")}</span>
                <span class="block text-gray-600">{$_("force_delete_help")}</span>
            </span>
        </label>
    {/if}

    {#if errorMessage}
        <div class="mt-4">
            <p class="text-red-600 font-medium mb-2">Error:</p>
            <div class="max-h-60 overflow-auto">
                <Prism code={errorMessage} />
            </div>
        </div>
    {/if}

    <div class="flex justify-center gap-3 w-full">
        <Button
            color="alternative"
            class="py-3! px-5!"
            onclick={() => (openDeleteModal = false)}>Cancel</Button
        >
        <Button
            class="py-3! px-5! bg-red-600! hover:bg-red-700! text-white! font-semibold!"
            onclick={deleteCurrentEntry}
            disabled={isActionLoading}
            >{isActionLoading ? "Deleting..." : "Delete"}</Button
        >
    </div>
</Modal>

<Modal
    bind:open={showUnsavedChangesModal}
    size="md"
    title={$_("unsaved_changes")}
>
    <p class="text-center mb-6">
        {unsavedPrompt === "leave"
            ? $_("unsaved_changes_leave_prompt")
            : $_("unsaved_changes_refresh_prompt")}
    </p>

    <div class="flex justify-between w-full">
        <Button color="alternative" onclick={cancelDiscardChanges}
        >{unsavedPrompt === "leave" ? $_("stay_on_page") : $_("cancel")}</Button
        >
        <Button color="red" onclick={confirmDiscardChanges}
        >{unsavedPrompt === "leave" ? $_("leave_page") : $_("discard_changes")}</Button
        >
    </div>
</Modal>

<Modal
        bind:open={showPermissionImpactModal}
        size="md"
        title="Permission Update Warning"
>
    <div class="text-center mb-6">
        <div
                class="bg-yellow-50 border-l-4 border-yellow-400 p-4 mb-4 text-left dark:bg-yellow-900/20 dark:border-yellow-500"
        >
            <p class="text-sm text-yellow-700 font-medium dark:text-yellow-400">
                ⚠ <strong>{permissionAffectedRoles.length}</strong>
                role{permissionAffectedRoles.length === 1 ? "" : "s"} will be affected by this
                permission change.
            </p>
            <ul class="mt-2 list-disc list-inside text-sm text-yellow-700 dark:text-yellow-400">
                {#each permissionAffectedRoles as role}
                    <li>{role}</li>
                {/each}
            </ul>
        </div>
        <p>
            Are you sure you want to update the permission <span class="font-bold"
        >{entry.shortname}</span
        >?
        </p>
    </div>

    <div class="flex justify-between w-full">
        <Button
                class="cursor-pointer"
                color="alternative"
                onclick={cancelPermissionUpdate}>Cancel</Button
        >
        <Button
                class="bg-primary cursor-pointer"
                onclick={confirmPermissionUpdate}
                disabled={isActionLoading}
        >
            {isActionLoading ? "Saving..." : "Confirm Update"}
        </Button>
    </div>
</Modal>

<Modal
        bind:open={showRoleImpactModal}
        size="md"
        title="Role Update Warning"
>
    <div class="text-center mb-6">
        <div
                class="bg-yellow-50 border-l-4 border-yellow-400 p-4 mb-4 text-left dark:bg-yellow-900/20 dark:border-yellow-500"
        >
            <p class="text-sm text-yellow-700 font-medium dark:text-yellow-400">
                ⚠ <strong>{roleAffectedUsersCount}</strong>
                user{roleAffectedUsersCount === 1 ? "" : "s"} will be affected by this
                role change.
            </p>
        </div>
        <p>
            Are you sure you want to update the role <span class="font-bold"
        >{entry.shortname}</span
        >?
        </p>
    </div>

    <div class="flex justify-between w-full">
        <Button
                class="cursor-pointer"
                color="alternative"
                onclick={cancelRoleUpdate}>Cancel</Button
        >
        <Button
                class="bg-primary cursor-pointer"
                onclick={confirmRoleUpdate}
                disabled={isActionLoading}
        >
            {isActionLoading ? "Saving..." : "Confirm Update"}
        </Button>
    </div>
</Modal>

<Modal
        bind:open={showSchemaImpactModal}
        size="md"
        title="Schema Update Warning"
>
    <div class="text-center mb-6">
        <div
                class="bg-yellow-50 border-l-4 border-yellow-400 p-4 mb-4 text-left dark:bg-yellow-900/20 dark:border-yellow-500"
        >
            <p class="text-sm text-yellow-700 font-medium dark:text-yellow-400">
                ⚠ <strong>{schemaAffectedCount}</strong>
                record{schemaAffectedCount === 1 ? "" : "s"} may be affected by this
                schema change.
            </p>
        </div>
        <p>
            Are you sure you want to update the schema <span class="font-bold"
        >{entry.shortname}</span
        >?
        </p>
    </div>

    <div class="flex justify-between w-full">
        <Button
                class="cursor-pointer"
                color="alternative"
                onclick={cancelSchemaUpdate}>Cancel</Button
        >
        <Button
                class="bg-primary cursor-pointer"
                onclick={confirmSchemaUpdate}
                disabled={isActionLoading}
        >
            {isActionLoading ? "Saving..." : "Confirm Update"}
        </Button>
    </div>
</Modal>