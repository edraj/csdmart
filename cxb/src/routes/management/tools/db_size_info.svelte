<script lang="ts">
    import { Dmart } from "@edraj/tsdmart";
    import { onMount } from "svelte";
    import { DatabaseSolid } from "flowbite-svelte-icons";
    import { _ } from "@/i18n";
    import { formatNumber } from "@/utils/format";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";
    import Badge from "@/components/ui/Badge.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";

    interface DbSizeEntry {
        table_name: string;
        pretty_size: string;
    }

    let isLoading = $state(true);
    let error: unknown = $state(null);
    let data: DbSizeEntry[] = $state([]);

    /** Convert a pretty_size string (e.g. "8047 MB", "80 kB", "16 bytes") to bytes for sorting. */
    function parseSizeToBytes(pretty: string): number {
        const units: Record<string, number> = {
            bytes: 1,
            byte: 1,
            kb: 1024,
            mb: 1024 ** 2,
            gb: 1024 ** 3,
            tb: 1024 ** 4,
        };
        const match = pretty.trim().match(/^([\d.]+)\s*(\S+)$/i);
        if (!match) return 0;
        const value = parseFloat(match[1]);
        const unit = match[2].toLowerCase();
        return value * (units[unit] ?? 0);
    }

    async function load() {
        try {
            isLoading = true;
            error = null;
            const axiosInstance = Dmart.getAxiosInstance();
            const headers = Dmart.getHeaders();
            const response = await axiosInstance.get("db_size_info/", { headers });
            if (response.data?.status === "success") {
                const raw: DbSizeEntry[] = response.data.data ?? [];
                data = raw.slice().sort((a, b) => parseSizeToBytes(b.pretty_size) - parseSizeToBytes(a.pretty_size));
            } else {
                error = response.data?.error?.message ?? $_("db_size_load_failed");
            }
        } catch (err: unknown) {
            error = err;
        } finally {
            isLoading = false;
        }
    }

    onMount(load);
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader
        title={$_("db_size_info")}
        description={$_("db_size_info_description")}
        icon={DatabaseSolid}
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    />

    {#if error}
        <ErrorState title={$_("db_size_load_failed")} {error} onRetry={load} />
    {:else if isLoading}
        <LoadingState variant="skeleton" rows={8} />
    {:else if data.length === 0}
        <EmptyState title={$_("no_records_found")} />
    {:else}
        <Card padding="none">
            <div class="overflow-x-auto">
                <table class="w-full text-sm text-start text-text">
                    <thead class="text-xs text-text-muted bg-surface border-b border-border">
                        <tr>
                            <th scope="col" class="px-4 sm:px-6 py-3 font-semibold text-start w-12">#</th>
                            <th scope="col" class="px-4 sm:px-6 py-3 font-semibold text-start">{$_("table_name")}</th>
                            <th scope="col" class="px-4 sm:px-6 py-3 font-semibold text-end">{$_("size")}</th>
                        </tr>
                    </thead>
                    <tbody>
                        {#each data as row, index (row.table_name)}
                            <tr class="border-b border-border last:border-0 hover:bg-surface-3 transition-colors">
                                <td class="px-4 sm:px-6 py-2.5 text-text-faint tabular-nums">{formatNumber(index + 1)}</td>
                                <td class="px-4 sm:px-6 py-2.5 font-medium font-mono" dir="ltr">{row.table_name}</td>
                                <td class="px-4 sm:px-6 py-2.5 text-end">
                                    <Badge variant="info" size="sm"><span dir="ltr">{row.pretty_size}</span></Badge>
                                </td>
                            </tr>
                        {/each}
                    </tbody>
                </table>
            </div>
        </Card>
        <p class="text-xs text-text-muted mt-3">{$_("total_tables", { values: { count: formatNumber(data.length) } })}</p>
    {/if}
</div>
