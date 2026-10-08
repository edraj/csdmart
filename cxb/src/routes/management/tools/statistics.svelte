<script lang="ts">
    import { resolveTotal } from "@shared/query-total";
    import { onMount } from "svelte";
    import {
        ChartPieOutline,
        DatabaseSolid,
        FileCodeSolid,
        FolderSolid,
        LockSolid,
        ShieldCheckSolid,
        UsersSolid,
    } from "flowbite-svelte-icons";
    import { Dmart, QueryType, type QueryRequest } from "@edraj/tsdmart";
    import { getSpaces } from "@/lib/dmart_services";
    import { _ } from "@/i18n";
    import { formatNumber } from "@/utils/format";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";
    import Badge from "@/components/ui/Badge.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";

    let isLoading = $state(true);
    let error: unknown = $state(null);

    let mgmtStats = $state({
        users: 0,
        roles: 0,
        permissions: 0,
        totalSpaces: 0,
        totalRecords: 0,
    });

    interface SpaceStat {
        shortname: string;
        total: number;
        entriesOrFolders: number;
        schemas: number;
        folders: number;
    }

    let spaceStats: SpaceStat[] = $state([]);

    async function fetchCount(space: string, resource_type: string, by_subpath = false): Promise<number> {
        try {
            const request = {
                type: QueryType.counters,
                retrieve_total: true,
                space_name: space,
                exact_subpath: false,
                subpath: by_subpath ? resource_type : "/",
                search: by_subpath ? "" : `@resource_type:${resource_type}`,
            } as unknown as QueryRequest;
            const resp = await Dmart.query(request);
            return resolveTotal(resp?.attributes?.total);
        } catch (e) {
            console.error(`Failed to fetch count for ${space}/${resource_type}`, e);
            return 0;
        }
    }

    async function load() {
        try {
            isLoading = true;
            error = null;

            const [users, roles, permissions] = await Promise.all([
                fetchCount("management", "users", true),
                fetchCount("management", "roles", true),
                fetchCount("management", "permissions", true),
            ]);

            const spacesObj = await getSpaces();
            let totalOverallRecords = 0;

            const resolvedSpaceStats = await Promise.all(
                spacesObj.records.map(async (spaceData) => {
                    const sn = spaceData.shortname;
                    const [total, schemas, folders] = await Promise.all([
                        fetchCount(sn, "*"),
                        fetchCount(sn, "schema"),
                        fetchCount(sn, "folder"),
                    ]);
                    // "Entries" = everything that is neither a schema nor a folder.
                    const entriesOrFolders = Math.max(0, total - schemas - folders);
                    totalOverallRecords += total;
                    return { shortname: sn, total, entriesOrFolders, schemas, folders };
                }),
            );

            mgmtStats = {
                users,
                roles,
                permissions,
                totalSpaces: resolvedSpaceStats.length,
                totalRecords: totalOverallRecords,
            };
            spaceStats = resolvedSpaceStats;
        } catch (err: unknown) {
            error = err;
        } finally {
            isLoading = false;
        }
    }

    onMount(load);

    const tiles = $derived([
        { key: "total_users", value: mgmtStats.users, icon: UsersSolid, tone: "text-info" },
        { key: "total_roles", value: mgmtStats.roles, icon: ShieldCheckSolid, tone: "text-success" },
        { key: "total_permissions", value: mgmtStats.permissions, icon: LockSolid, tone: "text-danger" },
    ]);
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader
        title={$_("statistics")}
        description={$_("statistics_description")}
        icon={ChartPieOutline}
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    />

    {#if error}
        <ErrorState title={$_("statistics_load_failed")} {error} onRetry={load} />
    {:else if isLoading}
        <LoadingState variant="skeleton" rows={8} />
    {:else}
        <!-- Top-level stat tiles -->
        <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4 mb-8">
            {#each tiles as tile (tile.key)}
                {@const Icon = tile.icon}
                <Card>
                    <div class="flex items-center gap-4">
                        <Icon class="w-8 h-8 shrink-0 {tile.tone}" aria-hidden="true" />
                        <div>
                            <p class="text-3xl font-semibold tabular-nums text-text">{formatNumber(tile.value)}</p>
                            <p class="text-xs text-text-muted">{$_(tile.key)}</p>
                        </div>
                    </div>
                </Card>
            {/each}
            <Card class="bg-primary-soft border-primary/20">
                <div class="flex items-center gap-4">
                    <DatabaseSolid class="w-8 h-8 shrink-0 text-primary" aria-hidden="true" />
                    <div>
                        <p class="text-3xl font-semibold tabular-nums text-primary">{formatNumber(mgmtStats.totalRecords)}</p>
                        <p class="text-xs text-text-muted">
                            {$_("records_in_spaces", { values: { count: formatNumber(mgmtStats.totalSpaces) } })}
                        </p>
                    </div>
                </div>
            </Card>
        </div>

        <!-- Space breakdown -->
        <h2 class="text-lg font-semibold text-text mb-4">{$_("space_statistics")}</h2>
        {#if spaceStats.length === 0}
            <EmptyState title={$_("no_spaces")} />
        {:else}
            <div class="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
                {#each spaceStats as stat (stat.shortname)}
                    <Card>
                        <div class="flex items-center justify-between gap-3 pb-3 mb-3 border-b border-border">
                            <div class="flex items-center gap-3 min-w-0">
                                <div class="p-2 bg-primary-soft rounded-control shrink-0" aria-hidden="true">
                                    <FolderSolid class="w-5 h-5 text-primary" />
                                </div>
                                <h3 class="font-semibold text-text truncate" title={stat.shortname}>{stat.shortname}</h3>
                            </div>
                            <Badge>{$_("total")}: {formatNumber(stat.total)}</Badge>
                        </div>

                        <dl class="grid grid-cols-3 gap-2 text-center">
                            <div class="p-2 rounded-control bg-surface">
                                <FileCodeSolid class="w-4 h-4 mx-auto text-warning mb-1" aria-hidden="true" />
                                <dd class="text-lg font-semibold tabular-nums text-text">{formatNumber(stat.schemas)}</dd>
                                <dt class="text-xs text-text-muted">{$_("schemas")}</dt>
                            </div>
                            <div class="p-2 rounded-control bg-surface">
                                <FolderSolid class="w-4 h-4 mx-auto text-info mb-1" aria-hidden="true" />
                                <dd class="text-lg font-semibold tabular-nums text-text">{formatNumber(stat.folders)}</dd>
                                <dt class="text-xs text-text-muted">{$_("folders")}</dt>
                            </div>
                            <div class="p-2 rounded-control bg-surface">
                                <DatabaseSolid class="w-4 h-4 mx-auto text-success mb-1" aria-hidden="true" />
                                <dd class="text-lg font-semibold tabular-nums text-text">{formatNumber(stat.entriesOrFolders)}</dd>
                                <dt class="text-xs text-text-muted">{$_("entries")}</dt>
                            </div>
                        </dl>
                    </Card>
                {/each}
            </div>
        {/if}
    {/if}
</div>
