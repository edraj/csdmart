<script lang="ts">
    import { Dmart, ResourceType } from "@edraj/tsdmart";
    import { url } from "@roxi/routify";
    import { ArrowRightOutline } from "flowbite-svelte-icons";
    import MetaPermissionForm from "@/components/management/forms/MetaPermissionForm.svelte";
    import SpaceMapView from "@/components/management/renderers/SpaceMapView.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import type { PermissionEntry } from "@/utils/entryShapes";
    import { _ } from "@/i18n";

    const { permissions = [], showTabs = true }: { permissions: string[]; showTabs?: boolean } = $props();

    // ── View mode ──────────────────────────────────────────────────────────────
    let viewMode: "list" | "map" = $state("list");

    function permissionHref(permission: string): string {
        return $url(`/management/content/[space_name]/[subpath]/[shortname]/[resource_type]`, {
            space_name: "management",
            subpath: "permissions",
            shortname: permission,
            resource_type: "permission",
        });
    }

    function loadPermission(permission: string) {
        return Dmart.retrieveEntry({
            resource_type: ResourceType.permission,
            space_name: "management",
            subpath: "permissions",
            shortname: permission,
            retrieve_json_payload: true,
            retrieve_attachments: false,
            validate_schema: true,
        });
    }

    // ── Map view data ──────────────────────────────────────────────────────────
    type SubpathInfo = {
        resource_types: string[];
        actions: string[];
        conditions: string[];
        allowed_fields_values: Record<string, unknown>;
        filter_fields_values: string;
    };
    type SpaceMap = Record<string, Record<string, SubpathInfo>>;

    let spaceMap: SpaceMap = $state({});
    let mapLoading = $state(false);
    let mapBuilt = $state(false);

    function mergePermission(permission: PermissionEntry) {
        const subpaths = permission.subpaths ?? {};
        const resourceTypes = permission.resource_types ?? [];
        const actions = permission.actions ?? [];
        const conditions = permission.conditions ?? [];
        const allowedFieldsValues = permission.allowed_fields_values ?? {};
        const filterFieldsValues = permission.filter_fields_values ?? "";

        for (const [space, paths] of Object.entries(subpaths)) {
            if (!spaceMap[space]) spaceMap[space] = {};
            for (const subpath of paths) {
                if (!spaceMap[space][subpath]) {
                    spaceMap[space][subpath] = {
                        resource_types: [],
                        actions: [],
                        conditions: [],
                        allowed_fields_values: {},
                        filter_fields_values: "",
                    };
                }
                const info = spaceMap[space][subpath];
                for (const rt of resourceTypes) {
                    if (!info.resource_types.includes(rt)) info.resource_types.push(rt);
                }
                for (const act of actions) {
                    if (!info.actions.includes(act)) info.actions.push(act);
                }
                for (const cond of conditions) {
                    if (!info.conditions.includes(cond)) info.conditions.push(cond);
                }
                if (Object.keys(allowedFieldsValues).length > 0) {
                    info.allowed_fields_values = { ...info.allowed_fields_values, ...allowedFieldsValues };
                }
                if (filterFieldsValues) {
                    info.filter_fields_values = info.filter_fields_values
                        ? info.filter_fields_values + " | " + filterFieldsValues
                        : filterFieldsValues;
                }
            }
        }
        spaceMap = { ...spaceMap };
    }

    async function buildMap() {
        if (mapBuilt) return;
        mapLoading = true;
        spaceMap = {};

        const results = await Promise.allSettled(permissions.map((permName) => loadPermission(permName)));
        results.forEach((result, i) => {
            if (result.status === "fulfilled" && result.value) {
                mergePermission(result.value);
            } else if (result.status === "rejected") {
                console.warn(`Could not load permission ${permissions[i]}:`, result.reason);
            }
        });

        mapLoading = false;
        mapBuilt = true;
    }

    $effect(() => {
        if (viewMode === "map") {
            buildMap();
        }
    });

    const noop = () => true;

    const toggleBase = "px-3 py-1.5 rounded-control text-sm font-medium transition-colors cursor-pointer";
    const toggleOn = "bg-primary text-text-on-primary";
    const toggleOff = "text-text-muted hover:text-text hover:bg-surface-3";
</script>

<div class="w-full max-w-4xl mx-auto {showTabs ? 'rounded-card border border-border bg-surface-2 shadow-card p-4 my-2' : 'mt-2'}">
    <!-- View toggle (suppressed when nested inside another explorer) -->
    {#if showTabs}
        <div class="flex gap-1 mb-4 border-b border-border pb-3" role="group" aria-label={$_("view")}>
            <button type="button" class="{toggleBase} {viewMode === 'list' ? toggleOn : toggleOff}" aria-pressed={viewMode === "list"} onclick={() => (viewMode = "list")}>
                {$_("list")}
            </button>
            <button type="button" class="{toggleBase} {viewMode === 'map' ? toggleOn : toggleOff}" aria-pressed={viewMode === "map"} onclick={() => (viewMode = "map")}>
                {$_("map")}
            </button>
        </div>
    {/if}

    {#if viewMode === "list"}
        {#if permissions.length === 0}
            <EmptyState title={$_("no_permissions_added")} />
        {:else}
            <div class="space-y-6">
                {#each permissions as permission (permission)}
                    <section>
                        <h3 class="text-base font-semibold">
                            <a href={permissionHref(permission)} class="inline-flex items-center gap-1.5 text-text hover:text-primary rounded-control">
                                {permission}
                                <ArrowRightOutline size="sm" class="rtl:rotate-180 text-text-faint" aria-hidden="true" />
                            </a>
                        </h3>
                        {#await loadPermission(permission)}
                            <LoadingState variant="skeleton" rows={3} class="mt-2" />
                        {:then permissionEntry}
                            {#if permissionEntry}
                                <MetaPermissionForm formData={permissionEntry} readOnly={true} validateFn={noop} />
                            {/if}
                        {:catch error}
                            <ErrorState compact {error} class="mt-2" />
                        {/await}
                    </section>
                {/each}
            </div>
        {/if}
    {/if}

    {#if viewMode === "map"}
        <SpaceMapView {spaceMap} loading={mapLoading} />
    {/if}
</div>
