<script lang="ts">
    import ListView from "@/components/management/ListView.svelte";
    import { Dmart, QueryType, ResourceType, RequestType, type ResponseEntry } from "@edraj/tsdmart";
    import { checkAccess } from "@/utils/checkAccess";
    import {
        ClockArrowOutline,
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
    } from "flowbite-svelte-icons";
    import { jsonEditorContentParser } from "@/utils/jsonEditor";
    import Prism from "@/components/Prism.svelte";
    import Table2Cols from "@/components/management/Table2Cols.svelte";
    import BreadCrumbLite from "@/components/management/BreadCrumbLite.svelte";
    import LazyJsonEditor from "@/components/ui/LazyJsonEditor.svelte";
    import Lazy from "@/components/ui/Lazy.svelte";
    import ConfirmDialog from "@/components/ui/ConfirmDialog.svelte";
    import ImpactModal from "@/components/ui/ImpactModal.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import { currentEntry, currentListView, InputMode, spaceChildren } from "@/stores/global";
    import { untrack, onDestroy, tick } from "svelte";
    import { get } from "svelte/store";
    import { activeRoute, beforeUrlChange, goto } from "@roxi/routify";
    import { sidebarCacheKey, trashRestoreTarget, normalizeSubpath } from "@/utils/subpath";
    import { Button, Card, TabItem, Tabs } from "flowbite-svelte";
    import { searchListView } from "@/stores/management/triggers";
    import { isDeepEqual } from "@/utils/compare";
    import { user } from "@/stores/user";
    import { getParentSubpath as getParentPath } from "@/utils/entryManagement";
    import { getChildren } from "@/lib/dmart_services";
    import { deleteEntry, moveEntryToTrash, saveEntry } from "@/utils/entryManagement";
    import { bulkBucket } from "@/stores/management/bulk_bucket";
    import { showToast, Level } from "@/utils/toast";
    import { errorMessage as describeError } from "@/utils/errorMessage";
    import { limitJsonForDisplay } from "@/utils/displayJson";
    import { _ } from "@/i18n";

    const EDITOR_INIT_DELAY = 512;
    // The dirty check deep-compares the whole entry; it runs once per pause in
    // typing rather than once per keystroke.
    const DIRTY_CHECK_DELAY = 250;
    const DEFAULT_RECORDS_LIMIT = 50;

    type TabKey =
        | "list"
        | "entry"
        | "form"
        | "attachments"
        | "history"
        | "diagram"
        | "roles_explorer"
        | "permissions_explorer"
        | "relationships";

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

    let errorMessage: unknown = $state(null);
    const errorPreview = $derived(errorMessage ? limitJsonForDisplay(errorMessage) : null);

    // svelte-ignore state_referenced_locally
    const isEntryTrash =
        space_name === "personal" &&
        subpath.startsWith(`people/${$user.shortname}/trash`);

    // svelte-ignore state_referenced_locally
    const canUpdate = checkAccess("update", space_name, subpath, resource_type);
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

    const isContainer = $derived(resource_type === ResourceType.folder || resource_type === ResourceType.space);
    const hasDiagram = $derived(resource_type === ResourceType.schema || subpath === "workflows");

    let activeTab = $state<TabKey>("list");
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
        if (activeTab !== "form") return true;
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

    // ── Impact warning before saving a schema, permission or role ──────────
    type Impact =
        | { kind: "schema"; count: number }
        | { kind: "permission"; roles: string[] }
        | { kind: "role"; count: number };
    let impact = $state<Impact | null>(null);
    let impactOpen = $state(false);

    const impactTitle = $derived(
        impact?.kind === "permission"
            ? $_("permission_update_warning")
            : impact?.kind === "role"
              ? $_("role_update_warning")
              : $_("schema_update_warning"),
    );
    const impactMessage = $derived(
        impact?.kind === "permission"
            ? $_("permission_impact_message", { values: { count: impact.roles.length } })
            : impact?.kind === "role"
              ? $_("role_impact_message", { values: { count: impact.count } })
              : impact?.kind === "schema"
                ? $_("schema_impact_message", { values: { count: impact.count } })
                : "",
    );
    const impactQuestion = $derived(
        impact?.kind === "permission"
            ? $_("confirm_update_permission")
            : impact?.kind === "role"
              ? $_("confirm_update_role")
              : $_("confirm_update_schema"),
    );
    const impactDetails = $derived(impact?.kind === "permission" ? impact.roles : []);

    async function handleSave() {
        errorMessage = null;
        // The same validation create enforces; the browser highlights the
        // offending field through reportValidity().
        if (!formsAreValid()) {
            errorMessage = $_("fill_required_meta");
            return;
        }
        try {
            if (resource_type === ResourceType.schema) {
                isActionLoading = true;
                const countersResult = await Dmart.query({
                    type: QueryType.counters,
                    space_name: space_name,
                    subpath: "/",
                    exact_subpath: false,
                    retrieve_json_payload: false,
                    search: `@payload.schema_shortname:${entry.shortname}`,
                });
                const count = countersResult?.attributes?.returned ?? 0;
                isActionLoading = false;
                if (count > 0) {
                    impact = { kind: "schema", count };
                    impactOpen = true;
                    return;
                }
            } else if (resource_type === ResourceType.permission) {
                isActionLoading = true;
                const rolesResult = await Dmart.query({
                    type: QueryType.search,
                    space_name: "management",
                    subpath: "/roles",
                    exact_subpath: true,
                    retrieve_json_payload: false,
                    search: `@permissions:${entry.shortname}`,
                    limit: 100,
                    offset: 0,
                });
                const roles = (rolesResult?.records ?? []).map((r) => r.shortname);
                isActionLoading = false;
                if (roles.length > 0) {
                    impact = { kind: "permission", roles };
                    impactOpen = true;
                    return;
                }
            } else if (resource_type === ResourceType.role) {
                isActionLoading = true;
                const usersResult = await Dmart.query({
                    type: QueryType.counters,
                    space_name: "management",
                    subpath: "/users",
                    exact_subpath: true,
                    retrieve_json_payload: false,
                    search: `@roles:${entry.shortname}`,
                });
                const count = usersResult?.attributes?.returned ?? 0;
                isActionLoading = false;
                if (count > 0) {
                    impact = { kind: "role", count };
                    impactOpen = true;
                    return;
                }
            }
        } catch {
            // The impact check is advisory; saving proceeds without it.
            isActionLoading = false;
        }
        await performSave();
    }

    async function confirmImpactedSave() {
        impactOpen = false;
        await performSave();
    }

    // ── Delete / trash: one dialog, two verbs ──────────────────────────────
    let confirmAction = $state<"delete" | "trash">("delete");
    let confirmOpen = $state(false);
    let forceDelete = $state(false);
    const showForce = $derived(
        resource_type === ResourceType.folder || resource_type === ResourceType.user,
    );
    const canTrash = $derived(canDelete && !isEntryTrash && !isContainer);

    function askDelete() {
        errorMessage = null;
        forceDelete = false;
        confirmAction = "delete";
        confirmOpen = true;
    }

    function askTrash() {
        errorMessage = null;
        confirmAction = "trash";
        confirmOpen = true;
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
            confirmOpen = false;
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
        errorMessage = null;

        const result = await moveEntryToTrash(
            entry,
            space_name,
            subpath,
            resource_type,
            $user.shortname ?? "",
        );

        if (result.success) {
            confirmOpen = false;
            isActionLoading = false;
            await tick();
            navigateAfterEntryAction();
            return;
        }
        errorMessage = result.errorMessage;
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
        } catch (e: unknown) {
            showToast(Level.warn, describeError(e, $_("entry_restore_failed")));
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
        if (activeTab === "entry") {
            untrack(() => {
                try {
                    const _jeContent = jsonEditorContentParser(
                        $state.snapshot(jeContent),
                    );
                    jeContent = { text: JSON.stringify(_jeContent, null, 2) };
                } catch {}
            });
        } else if (activeTab === "form") {
            untrack(() => {
                try {
                    const _jeContent = jsonEditorContentParser(
                        $state.snapshot(jeContent),
                    );
                    jeContent = { json: _jeContent };
                } catch {}
            });
        }
    });

    let hasStreamChanges = $state(false);
    async function handleRefresh() {
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

    // ── Dirty check, debounced ────────────────────────────────────────────
    // Walk the (proxied) content so every nested property is a dependency of
    // this effect — a form field two levels down must still mark the entry
    // dirty — but leave the clone + deep compare to a timer, so a burst of
    // keystrokes costs one comparison instead of one per key.
    function touch(value: unknown): void {
        if (value === null || typeof value !== "object") return;
        if (Array.isArray(value)) {
            for (const item of value) touch(item);
            return;
        }
        for (const key of Object.keys(value as Record<string, unknown>)) {
            touch((value as Record<string, unknown>)[key]);
        }
    }

    let dirtyTimer: ReturnType<typeof setTimeout> | null = null;
    function computeDirty() {
        dirtyTimer = null;
        try {
            isJEDirty = !isDeepEqual(
                jsonEditorContentParser($state.snapshot(jeContent)),
                $state.snapshot(originalJeContent),
            );
        } catch {
            isJEDirty = true;
        }
    }

    $effect(() => {
        touch(jeContent);
        touch(originalJeContent);
        if (dirtyTimer) clearTimeout(dirtyTimer);
        dirtyTimer = setTimeout(computeDirty, DIRTY_CHECK_DELAY);
        return () => {
            if (dirtyTimer) clearTimeout(dirtyTimer);
        };
    });

    onDestroy(() => {
        clearTimeout(_initTimer);
        if (dirtyTimer) clearTimeout(dirtyTimer);
        // Do not keep this instance alive through the global store once the
        // entry view is gone; a newer instance has already replaced it.
        if (get(currentEntry)?.refreshEntry === refreshEntry) {
            currentEntry.set(null);
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

    const attachmentCount = $derived(Object.values(entry.attachments ?? {}).flat(1).length);

    // One look for every tab: underline, tokens, sentence case.
    const tabActive =
        "inline-flex items-center gap-2 px-3 py-2.5 text-sm font-medium border-b-2 border-primary text-primary bg-transparent rounded-none whitespace-nowrap";
    const tabInactive =
        "inline-flex items-center gap-2 px-3 py-2.5 text-sm font-medium border-b-2 border-transparent text-text-muted hover:text-text hover:border-border-strong bg-transparent rounded-none whitespace-nowrap";
</script>

<svelte:window onbeforeunload={beforeUnload} />

<div class="flex flex-col w-full gap-3 px-3 pt-3 sm:px-4">
    <div class="flex flex-wrap items-center justify-between gap-3">
        <BreadCrumbLite
            {space_name}
            {subpath}
            {resource_type}
            schema_name={schemaShortname ?? undefined}
            shortname={entry.shortname}
            payloadContentType={entry?.payload?.content_type}
        />

        <!-- Actions only where they apply: Save on the two editable tabs,
             Trash for things that are not containers, Restore in the trash,
             Refresh everywhere. -->
        <div class="flex flex-wrap items-center gap-2 ms-auto" role="toolbar" aria-label={$_("actions")}>
            {#if canUpdate && (activeTab === "entry" || activeTab === "form")}
                <Button size="sm" color="primary" onclick={handleSave} disabled={isActionLoading || !isJEDirty}>
                    <FloppyDiskOutline size="sm" class="me-1.5" aria-hidden="true" />
                    {$_("save")}
                </Button>
            {/if}
            {#if isEntryTrash}
                <Button size="sm" color="alternative" onclick={restoreTrashEntry} disabled={isActionLoading}>
                    <ClockArrowOutline size="sm" class="me-1.5" aria-hidden="true" />
                    {$_("restore")}
                </Button>
            {/if}
            {#if canDelete && !isEntryTrash && $bulkBucket.length === 0}
                {#if canTrash}
                    <Button size="sm" color="alternative" onclick={askTrash} disabled={isActionLoading} title={$_("move_to_trash")}>
                        <TrashBinOutline size="sm" class="me-1.5" aria-hidden="true" />
                        {$_("trash")}
                    </Button>
                {/if}
                <Button size="sm" color="red" outline onclick={askDelete} disabled={isActionLoading} title={$_("delete_entry")}>
                    <TrashBinSolid size="sm" class="me-1.5" aria-hidden="true" />
                    {$_("delete")}
                </Button>
            {/if}
            <Button
                size="sm"
                color={hasStreamChanges ? "yellow" : "alternative"}
                onclick={handleRefresh}
                title={hasStreamChanges ? $_("changes_available") : $_("refresh")}
            >
                <RefreshOutline size="sm" class="me-1.5" aria-hidden="true" />
                {hasStreamChanges ? $_("changes_available") : $_("refresh")}
            </Button>
        </div>
    </div>

    <Tabs
        tabStyle="underline"
        bind:selected={activeTab}
        divider={false}
        class="flex-wrap gap-1 border-b border-border space-x-0 rtl:space-x-reverse"
        classes={{ content: "mt-3 p-0 bg-transparent dark:bg-transparent rounded-none" }}
    >
        <TabItem key="list" activeClass={tabActive} inactiveClass={tabInactive}>
            {#snippet titleSlot()}
                {#if isContainer}
                    <ListOutline size="sm" aria-hidden="true" />
                    <span>{$_("list_view")}</span>
                {:else}
                    <EyeSolid size="sm" aria-hidden="true" />
                    <span>{$_("content")}</span>
                {/if}
            {/snippet}
            {#if isContainer}
                <!-- Re-mount on refresh (coinTriggerRefresh toggles in refreshEntry) so
                     editing the folder's index_attributes and hitting Refresh rebuilds
                     the columns from the new attributes AND reloads the data, instead of
                     keeping the columns computed at first mount. -->
                {#key coinTriggerRefresh}
                    <ListView
                        {space_name}
                        {subpath}
                        folderColumns={entry?.payload?.body?.index_attributes ?? null}
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
                <Table2Cols entry={{ [$_("resource_type")]: resource_type, ...entry }} />
            {/if}
        </TabItem>

        <TabItem key="entry" activeClass={tabActive} inactiveClass={tabInactive}>
            {#snippet titleSlot()}
                <EditOutline size="sm" aria-hidden="true" />
                <span>{$_("entry")}</span>
            {/snippet}
            {#if jeContent.text || jeContent.json}
                <LazyJsonEditor bind:content={jeContent} mode="text" />
            {/if}
            {#if errorPreview}
                <div class="mt-3">
                    <ErrorState compact title={$_("save_failed")} message={typeof errorMessage === "string" ? errorMessage : undefined}>
                        {#if typeof errorMessage !== "string"}
                            <div class="max-h-60 overflow-auto"><Prism code={errorPreview.value as object | string} /></div>
                        {/if}
                    </ErrorState>
                </div>
            {/if}
        </TabItem>

        <TabItem key="form" activeClass={tabActive} inactiveClass={tabInactive}>
            {#snippet titleSlot()}
                <RectangleListOutline size="sm" aria-hidden="true" />
                <span>{$_("form")}</span>
            {/snippet}
            {#key coinTriggerRefresh}
                {#if jeContent.json}
                    <Lazy load={() => import("@/components/management/forms/MetaForm.svelte")}>
                        {#snippet children(MetaForm)}
                            <MetaForm bind:formData={jeContent.json} bind:validateFn={validateMetaForm} isCreate={false} />
                        {/snippet}
                    </Lazy>
                    {#if resource_type === ResourceType.user}
                        <Lazy load={() => import("@/components/management/forms/MetaUserForm.svelte")}>
                            {#snippet children(MetaUserForm)}
                                <MetaUserForm bind:formData={jeContent.json} bind:validateFn={validateRTForm} isCreate={false} />
                            {/snippet}
                        </Lazy>
                    {:else if resource_type === ResourceType.space}
                        <Lazy load={() => import("@/components/management/forms/SpaceForm.svelte")}>
                            {#snippet children(SpaceForm)}
                                <SpaceForm bind:formData={jeContent.json} spaceName={space_name} />
                            {/snippet}
                        </Lazy>
                    {:else if resource_type === ResourceType.role}
                        <Lazy load={() => import("@/components/management/forms/MetaRoleForm.svelte")}>
                            {#snippet children(MetaRoleForm)}
                                <MetaRoleForm bind:formData={jeContent.json} bind:validateFn={validateRTForm} />
                            {/snippet}
                        </Lazy>
                    {:else if resource_type === ResourceType.permission}
                        <Lazy load={() => import("@/components/management/forms/MetaPermissionForm.svelte")}>
                            {#snippet children(MetaPermissionForm)}
                                <MetaPermissionForm bind:formData={jeContent.json} bind:validateFn={validateRTForm} readOnly={false} />
                            {/snippet}
                        </Lazy>
                    {:else if resource_type === ResourceType.ticket}
                        <Lazy load={() => import("@/components/management/forms/MetaTicketForm.svelte")}>
                            {#snippet children(MetaTicketForm)}
                                <MetaTicketForm {space_name} {subpath} shortname={entry.shortname} meta={jeContent.json} />
                            {/snippet}
                        </Lazy>
                    {/if}
                    {#if jeContent?.json?.payload?.body}
                        <Card class="p-4 max-w-4xl mx-auto my-2 rounded-card border-border bg-surface-2 shadow-card">
                            <h2 class="text-lg font-semibold text-text mb-4">{$_("payload")}</h2>
                            <Lazy load={() => import("@/components/management/forms/PayloadForm.svelte")}>
                                {#snippet children(PayloadForm)}
                                    <PayloadForm
                                        isCreate={false}
                                        bind:selectedResourceType={resource_type}
                                        selectedSchema={schemaShortname}
                                        bind:selectedWorkflow={jeContent.json.workflow_shortname}
                                        bind:selectedInputMode
                                        bind:contentType={jeContent.json.payload.content_type}
                                        bind:content={jeContent.json.payload.body}
                                    />
                                {/snippet}
                            </Lazy>
                        </Card>
                    {/if}
                {/if}
                {#if errorPreview}
                    <div class="mt-3">
                        <ErrorState compact title={$_("save_failed")} message={typeof errorMessage === "string" ? errorMessage : undefined}>
                            {#if typeof errorMessage !== "string"}
                                <div class="max-h-60 overflow-auto"><Prism code={errorPreview.value as object | string} /></div>
                            {/if}
                        </ErrorState>
                    </div>
                {/if}
            {/key}
        </TabItem>

        {#if hasDiagram}
            <TabItem key="diagram" activeClass={tabActive} inactiveClass={tabInactive}>
                {#snippet titleSlot()}
                    <DrawSquareSolid size="sm" aria-hidden="true" />
                    <span>{$_("diagram")}</span>
                {/snippet}
                {#if resource_type === ResourceType.schema}
                    <Lazy load={() => import("@/components/management/diagram/SchemaDiagram.svelte")}>
                        {#snippet children(SchemaDiagram)}
                            <SchemaDiagram shortname={entry.shortname} properties={entry.payload?.body?.properties} />
                        {/snippet}
                    </Lazy>
                {:else if entry?.payload?.body}
                    <Lazy load={() => import("@/components/management/diagram/WorkflowDiagram.svelte")}>
                        {#snippet children(WorkflowDiagram)}
                            <WorkflowDiagram shortname={entry.shortname} workflowContent={entry?.payload?.body} />
                        {/snippet}
                    </Lazy>
                {/if}
            </TabItem>
        {/if}

        <TabItem key="attachments" activeClass={tabActive} inactiveClass={tabInactive}>
            {#snippet titleSlot()}
                <PaperClipOutline size="sm" aria-hidden="true" />
                <span>{$_("attachments")}</span>
                {#if attachmentCount}
                    <span class="tabular-nums text-xs text-text-faint">({attachmentCount})</span>
                {/if}
            {/snippet}
            <Lazy load={() => import("@/components/management/renderers/Attachments.svelte")}>
                {#snippet children(Attachments)}
                    <Attachments
                        {resource_type}
                        {space_name}
                        {subpath}
                        parent_shortname={entry.shortname}
                        attachments={entry.attachments ?? {}}
                        {refreshEntry}
                    />
                {/snippet}
            </Lazy>
        </TabItem>

        {#if resource_type === ResourceType.user}
            <TabItem key="roles_explorer" activeClass={tabActive} inactiveClass={tabInactive}>
                {#snippet titleSlot()}
                    <ShareNodesSolid size="sm" aria-hidden="true" />
                    <span>{$_("role_explorer")}</span>
                {/snippet}
                <Lazy load={() => import("@/components/management/renderers/RolesExplorer.svelte")}>
                    {#snippet children(RolesExplorer)}
                        <RolesExplorer roles={jeContent.json?.roles ?? []} />
                    {/snippet}
                </Lazy>
            </TabItem>
        {/if}

        {#if resource_type === ResourceType.role}
            <TabItem key="permissions_explorer" activeClass={tabActive} inactiveClass={tabInactive}>
                {#snippet titleSlot()}
                    <ShareNodesSolid size="sm" aria-hidden="true" />
                    <span>{$_("permission_explorer")}</span>
                {/snippet}
                <Lazy load={() => import("@/components/management/renderers/PermissionsExplorer.svelte")}>
                    {#snippet children(PermissionsExplorer)}
                        <PermissionsExplorer permissions={jeContent.json?.permissions ?? []} />
                    {/snippet}
                </Lazy>
            </TabItem>
        {/if}

        <TabItem key="relationships" activeClass={tabActive} inactiveClass={tabInactive}>
            {#snippet titleSlot()}
                <LinkOutline size="sm" aria-hidden="true" />
                <span>{$_("relationships")}</span>
                {#if entryRelationships.length}
                    <span class="tabular-nums text-xs text-text-faint">({entryRelationships.length})</span>
                {/if}
            {/snippet}
            <Lazy load={() => import("@/components/management/renderers/RelationshipsPanel.svelte")}>
                {#snippet children(RelationshipsPanel)}
                    <RelationshipsPanel
                        {resource_type}
                        {space_name}
                        {subpath}
                        parent_shortname={entry.shortname}
                        bind:relationships={entryRelationships}
                    />
                {/snippet}
            </Lazy>
        </TabItem>

        <TabItem key="history" activeClass={tabActive} inactiveClass={tabInactive}>
            {#snippet titleSlot()}
                <ClockOutline size="sm" aria-hidden="true" />
                <span>{$_("history")}</span>
            {/snippet}
            {#key coinTriggerRefresh}
                <Lazy load={() => import("@/components/management/HistoryListView.svelte")}>
                    {#snippet children(HistoryListView)}
                        <HistoryListView {space_name} {subpath} shortname={entry.shortname} />
                    {/snippet}
                </Lazy>
            {/key}
        </TabItem>
    </Tabs>
</div>

<ConfirmDialog
    bind:open={confirmOpen}
    variant="danger"
    title={confirmAction === "trash"
        ? $_("trash_entry_title", { values: { shortname: entry.shortname } })
        : $_("delete_entry_title", { values: { shortname: entry.shortname } })}
    body={confirmAction === "trash"
        ? $_("confirm_trash_entry", { values: { shortname: entry.shortname, resource_type } })
        : `${$_("confirm_delete_entry", { values: { shortname: entry.shortname, resource_type } })}\n${$_("cannot_be_undone")}`}
    confirmLabel={confirmAction === "trash" ? $_("move_to_trash") : $_("delete")}
    loading={isActionLoading}
    loadingLabel={confirmAction === "trash" ? $_("moving") : $_("deleting")}
    error={confirmOpen ? errorMessage : null}
    onConfirm={confirmAction === "trash" ? moveToTrash : deleteCurrentEntry}
>
    {#if confirmAction === "delete" && showForce}
        <label class="flex items-start gap-2 text-sm cursor-pointer">
            <input type="checkbox" bind:checked={forceDelete} class="mt-0.5 h-4 w-4 rounded-control border-border-strong text-primary focus:ring-primary" />
            <span>
                <span class="font-semibold text-text">{$_("force_delete")}</span>
                <span class="block text-text-muted">{$_("force_delete_help")}</span>
            </span>
        </label>
    {/if}
</ConfirmDialog>

<ConfirmDialog
    bind:open={showUnsavedChangesModal}
    variant="danger"
    title={$_("unsaved_changes")}
    body={unsavedPrompt === "leave" ? $_("unsaved_changes_leave_prompt") : $_("unsaved_changes_refresh_prompt")}
    confirmLabel={unsavedPrompt === "leave" ? $_("leave_page") : $_("discard_changes")}
    cancelLabel={unsavedPrompt === "leave" ? $_("stay_on_page") : $_("cancel")}
    onConfirm={confirmDiscardChanges}
    onCancel={cancelDiscardChanges}
/>

<ImpactModal
    bind:open={impactOpen}
    title={impactTitle}
    message={impactMessage}
    details={impactDetails}
    question={impactQuestion}
    subject={entry.shortname}
    loading={isActionLoading}
    onConfirm={confirmImpactedSave}
    onCancel={() => (impactOpen = false)}
/>
