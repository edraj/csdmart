<script lang="ts">
    import { Dmart } from "@edraj/tsdmart";
    import { Table, TableBody, TableBodyCell, TableBodyRow, TableHead, TableHeadCell, TabItem, Tabs } from "flowbite-svelte";
    import { CodeOutline, InfoCircleOutline } from "flowbite-svelte-icons";
    import { onMount } from "svelte";
    import { _ } from "@/i18n";
    import Table2Cols from "@/components/management/Table2Cols.svelte";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";

    // Injected by vite.config.ts from `git rev-parse`; "N/A" outside a checkout.
    const gitHash: string = import.meta.env.VITE_GIT_HASH ?? "N/A";

    // Records returned by GET /info/plugins. Each plugin row carries the
    // version it announces from its own binary (assembly attr, .so dlsym,
    // or subprocess info-response — see custom_plugins_sdk/README.md) plus
    // its wire type ("hook" | "api").
    type PluginRow = {
        shortname: string;
        version: string;
        type: string;
    };

    let settings = $state<Record<string, unknown>>({});
    let manifest = $state<Record<string, unknown>>({});
    let plugins = $state<PluginRow[]>([]);
    let settingsError = $state<unknown>(null);
    let manifestError = $state<unknown>(null);
    let pluginsError = $state<unknown>(null);
    let loading = $state(true);

    async function load() {
        loading = true;
        settingsError = manifestError = pluginsError = null;
        // Each tab loads on its own: a failing settings call must not blank
        // the manifest and plugins tabs too.
        await Promise.all([
            Dmart.getSettings()
                .then((r) => {
                    if (r?.status === "success") settings = r.attributes ?? {};
                    else settingsError = $_("settings_load_failed");
                })
                .catch((e: unknown) => (settingsError = e)),
            Dmart.getManifest()
                .then((r) => {
                    if (r?.status === "success") manifest = r.attributes ?? {};
                    else manifestError = $_("manifest_load_failed");
                })
                .catch((e: unknown) => (manifestError = e)),
            Dmart.getPlugins()
                .then((r) => {
                    if (r?.status === "success" && Array.isArray(r.records)) {
                        plugins = r.records.map((rec) => ({
                            shortname: rec.shortname ?? "",
                            version: (rec.attributes?.version as string | undefined) ?? "0.0.0",
                            type: (rec.attributes?.type as string | undefined) ?? "",
                        }));
                    } else {
                        pluginsError = $_("plugins_load_failed");
                    }
                })
                // Older dmart servers (pre-/info/plugins) return 404 here. Surface
                // the failure in the tab body rather than swallowing — operators
                // can then upgrade the server.
                .catch((e: unknown) => (pluginsError = e)),
        ]);
        loading = false;
    }

    onMount(load);
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader
        title={$_("information")}
        description={$_("information_description")}
        icon={InfoCircleOutline}
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    >
        {#snippet actions()}
            <span class="text-xs text-text-muted font-mono" dir="ltr">{$_("build_hash")}: {gitHash}</span>
        {/snippet}
    </PageHeader>

    <Card padding="none">
        <Tabs tabStyle="underline" class="px-4 pt-2" contentClass="p-4">
            <TabItem open title={$_("settings")}>
                {#if loading}
                    <LoadingState variant="skeleton" rows={8} />
                {:else if settingsError}
                    <ErrorState title={$_("settings_load_failed")} error={settingsError} onRetry={load} />
                {:else}
                    <Table2Cols entry={settings} />
                {/if}
            </TabItem>
            <TabItem title={$_("manifest")}>
                {#if loading}
                    <LoadingState variant="skeleton" rows={8} />
                {:else if manifestError}
                    <ErrorState title={$_("manifest_load_failed")} error={manifestError} onRetry={load} />
                {:else}
                    <Table2Cols entry={manifest} />
                {/if}
            </TabItem>
            <TabItem title={$_("plugins")}>
                {#if loading}
                    <LoadingState variant="skeleton" rows={4} />
                {:else if pluginsError}
                    <ErrorState title={$_("plugins_load_failed")} error={pluginsError} onRetry={load} />
                {:else if plugins.length === 0}
                    <EmptyState icon={CodeOutline} title={$_("no_plugins_loaded")} />
                {:else}
                    <!-- Three columns: shortname, version, type. Versions come
                         from the plugin binary itself (assembly attr, .so symbol,
                         or subprocess info JSON) — not a config file. -->
                    <div class="overflow-x-auto">
                        <Table striped>
                            <TableHead>
                                <TableHeadCell>{$_("shortname")}</TableHeadCell>
                                <TableHeadCell>{$_("version")}</TableHeadCell>
                                <TableHeadCell>{$_("type")}</TableHeadCell>
                            </TableHead>
                            <TableBody>
                                {#each plugins as p (p.shortname)}
                                    <TableBodyRow>
                                        <TableBodyCell>{p.shortname}</TableBodyCell>
                                        <TableBodyCell class="tabular-nums" dir="ltr">{p.version}</TableBodyCell>
                                        <TableBodyCell>{p.type}</TableBodyCell>
                                    </TableBodyRow>
                                {/each}
                            </TableBody>
                        </Table>
                    </div>
                {/if}
            </TabItem>
        </Tabs>
    </Card>
</div>
