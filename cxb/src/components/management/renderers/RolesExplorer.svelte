<script lang="ts">
    import PermissionsExplorer from "@/components/management/renderers/PermissionsExplorer.svelte";
    import SpaceMapView from "@/components/management/renderers/SpaceMapView.svelte";
    import { Dmart, ResourceType } from "@edraj/tsdmart";
    import { url } from "@roxi/routify";
    import { ArrowRightOutline } from "flowbite-svelte-icons";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import { _ } from "@/i18n";

    const { roles = [] }: { roles: string[] } = $props();

    // ── View mode ──────────────────────────────────────────────────────────────
    let viewMode: "list" | "map" = $state("list");

    // ── Cached role data (avoids duplicate API calls in template) ─────────────
    type RoleData = { permissions: string[] };
    let roleDataCache: Record<string, RoleData> = $state({});
    let roleDataLoading = $state(true);

    async function loadRoleData() {
        roleDataLoading = true;
        const results = await Promise.allSettled(
            roles.map((role) =>
                Dmart.retrieveEntry({
                    resource_type: ResourceType.role,
                    space_name: "management",
                    subpath: "roles",
                    shortname: role,
                    retrieve_json_payload: true,
                    retrieve_attachments: false,
                    validate_schema: true,
                }),
            ),
        );
        const cache: Record<string, RoleData> = {};
        results.forEach((result, i) => {
            if (result.status === "fulfilled") {
                cache[roles[i]] = result.value as unknown as RoleData;
            }
        });
        roleDataCache = cache;
        roleDataLoading = false;
    }

    loadRoleData();

    function roleHref(role: string): string {
        return $url(`/management/content/[space_name]/[subpath]/[shortname]/[resource_type]`, {
            space_name: "management",
            subpath: "roles",
            shortname: role,
            resource_type: "role",
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

    /** Merge a single permission's subpaths/resource_types/actions into spaceMap */
    function mergePermission(permission: any) {
        const subpaths: Record<string, string[]> = permission.subpaths ?? {};
        const resourceTypes: string[] = permission.resource_types ?? [];
        const actions: string[] = permission.actions ?? [];
        const conditions: string[] = permission.conditions ?? [];
        const allowedFieldsValues: Record<string, unknown> = permission.allowed_fields_values ?? {};
        const filterFieldsValues: string = permission.filter_fields_values ?? "";

        for (const [space, paths] of Object.entries(subpaths)) {
            if (!spaceMap[space]) spaceMap[space] = {};
            for (const subpath of paths as string[]) {
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

        // Fetch all roles in parallel instead of sequentially
        const roleEntries = await Promise.allSettled(
            roles.map((role) =>
                Dmart.retrieveEntry({
                    resource_type: ResourceType.role,
                    space_name: "management",
                    subpath: "roles",
                    shortname: role,
                    retrieve_json_payload: true,
                    retrieve_attachments: false,
                    validate_schema: true,
                }),
            ),
        );

        // Collect all unique permission names from successful role fetches
        // eslint-disable-next-line svelte/prefer-svelte-reactivity -- plain local accumulator, never rendered
        const permissionNamesSet = new Set<string>();
        for (const result of roleEntries) {
            if (result.status === "fulfilled") {
                const permissionNames: string[] = (result.value as any)?.permissions ?? [];
                permissionNames.forEach((p) => permissionNamesSet.add(p));
            }
        }

        // Fetch all permissions in parallel instead of N+1 sequential calls
        const permResults = await Promise.allSettled(
            Array.from(permissionNamesSet).map((permName) =>
                Dmart.retrieveEntry({
                    resource_type: ResourceType.permission,
                    space_name: "management",
                    subpath: "permissions",
                    shortname: permName,
                    retrieve_json_payload: true,
                    retrieve_attachments: false,
                    validate_schema: true,
                }),
            ),
        );

        for (const result of permResults) {
            if (result.status === "fulfilled") {
                mergePermission(result.value);
            }
        }

        mapLoading = false;
        mapBuilt = true;
    }

    $effect(() => {
        if (viewMode === "map") {
            buildMap();
        }
    });

    const toggleBase = "px-3 py-1.5 rounded-control text-sm font-medium transition-colors cursor-pointer";
    const toggleOn = "bg-primary text-text-on-primary";
    const toggleOff = "text-text-muted hover:text-text hover:bg-surface-3";
</script>

<div class="w-full max-w-4xl mx-auto rounded-card border border-border bg-surface-2 shadow-card p-4 my-2">
    <div class="flex gap-1 mb-4 border-b border-border pb-3" role="group" aria-label={$_("view")}>
        <button type="button" class="{toggleBase} {viewMode === 'list' ? toggleOn : toggleOff}" aria-pressed={viewMode === "list"} onclick={() => (viewMode = "list")}>
            {$_("list")}
        </button>
        <button type="button" class="{toggleBase} {viewMode === 'map' ? toggleOn : toggleOff}" aria-pressed={viewMode === "map"} onclick={() => (viewMode = "map")}>
            {$_("map")}
        </button>
    </div>

    {#if viewMode === "list"}
        {#if roleDataLoading}
            <LoadingState label={$_("loading_roles")} />
        {:else if roles.length === 0}
            <EmptyState title={$_("no_roles_added")} />
        {:else}
            <div class="space-y-6">
                {#each roles as role (role)}
                    <section>
                        <h3 class="text-lg font-semibold">
                            <a href={roleHref(role)} class="inline-flex items-center gap-1.5 text-text hover:text-primary rounded-control">
                                {role}
                                <ArrowRightOutline size="sm" class="rtl:rotate-180 text-text-faint" aria-hidden="true" />
                            </a>
                        </h3>
                        {#if roleDataCache[role]}
                            <PermissionsExplorer permissions={roleDataCache[role].permissions} showTabs={false} />
                        {/if}
                    </section>
                {/each}
            </div>
        {/if}
    {/if}

    {#if viewMode === "map"}
        <SpaceMapView {spaceMap} loading={mapLoading} />
    {/if}
</div>
