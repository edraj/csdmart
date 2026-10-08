<script lang="ts">
    import { Dmart } from "@edraj/tsdmart";
    import { onMount } from "svelte";
    import { Modal } from "flowbite-svelte";
    import { ChartLineUpOutline, ExpandOutline, FolderSolid, TableColumnOutline } from "flowbite-svelte-icons";
    import { _ } from "@/i18n";
    import { formatDate, formatNumber } from "@/utils/format";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";
    import Badge from "@/components/ui/Badge.svelte";
    import IconButton from "@/components/ui/IconButton.svelte";
    import ErrorState from "@/components/ui/ErrorState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";

    interface HistoryEntry {
        entries_count: number;
        recorded_at: string;
    }

    interface SpaceHistory {
        spacename: string;
        data: HistoryEntry[];
    }

    let isLoading = $state(true);
    let error: unknown = $state(null);
    let spaces: SpaceHistory[] = $state([]);

    // Per-card view mode: "graph" | "table"
    let viewModes: Record<string, "graph" | "table"> = $state({});

    // Fullscreen chart
    let fullscreenSpace: SpaceHistory | null = $state(null);
    let fullscreenOpen = $state(false);

    function openFullscreen(space: SpaceHistory) {
        fullscreenSpace = space;
        fullscreenOpen = true;
    }

    async function load() {
        try {
            isLoading = true;
            error = null;
            const axiosInstance = Dmart.getAxiosInstance();
            const headers = Dmart.getHeaders();
            const response = await axiosInstance.get("db_entries_count_history/", { headers });
            if (response.data?.status === "success") {
                spaces = response.data.data ?? [];
                // Default: graph mode for spaces with >= 2 points, table otherwise
                const modes: Record<string, "graph" | "table"> = {};
                for (const s of spaces) {
                    modes[s.spacename] = s.data?.length >= 2 ? "graph" : "table";
                }
                viewModes = modes;
            } else {
                error = response.data?.error?.message ?? $_("db_history_load_failed");
            }
        } catch (err: unknown) {
            error = err;
        } finally {
            isLoading = false;
        }
    }

    onMount(load);

    function latestCount(space: SpaceHistory): number {
        if (!space.data || space.data.length === 0) return 0;
        return space.data[space.data.length - 1].entries_count;
    }

    function trend(space: SpaceHistory): "up" | "down" | "flat" {
        if (!space.data || space.data.length < 2) return "flat";
        const delta = space.data[space.data.length - 1].entries_count - space.data[0].entries_count;
        if (delta > 0) return "up";
        if (delta < 0) return "down";
        return "flat";
    }

    // ── SVG chart helpers ──────────────────────────────────────────────────────
    // One series per chart, so one hue (the primary) and no legend; grid and
    // axis text are recessive tokens; the line is 2px and the markers 8px.
    const W = 300;
    const H = 120;
    const PAD = { top: 10, right: 12, bottom: 24, left: 44 };
    const FS_PAD = { top: 10, right: 12, bottom: 24, left: 20 };

    function buildChart(data: HistoryEntry[], pad = PAD) {
        const innerW = W - pad.left - pad.right;
        const innerH = H - pad.top - pad.bottom;

        const counts = data.map((d) => d.entries_count);
        const minY = Math.min(...counts);
        const maxY = Math.max(...counts);
        const rangeY = maxY - minY || 1;

        const scaleX = (i: number) => pad.left + (i / Math.max(1, data.length - 1)) * innerW;
        const scaleY = (v: number) => pad.top + innerH - ((v - minY) / rangeY) * innerH;

        const points = data.map((d, i) => `${scaleX(i)},${scaleY(d.entries_count)}`);
        const polyline = points.join(" ");

        // Fill area under curve
        const areaPoints = [
            `${pad.left},${pad.top + innerH}`,
            ...points,
            `${scaleX(data.length - 1)},${pad.top + innerH}`,
        ].join(" ");

        // Y-axis ticks (3 labels)
        const yTicks = [minY, Math.round((minY + maxY) / 2), maxY];

        // X-axis labels: first and last
        const xLabels = [
            { x: scaleX(0), label: formatDate(data[0].recorded_at, "date") },
            { x: scaleX(data.length - 1), label: formatDate(data[data.length - 1].recorded_at, "date") },
        ];

        // Dot positions for all points
        const dots = data.map((d, i) => ({
            cx: scaleX(i),
            cy: scaleY(d.entries_count),
            count: d.entries_count,
            label: formatDate(d.recorded_at, "datetime"),
        }));

        return { polyline, areaPoints, yTicks, xLabels, dots, scaleX, scaleY, minY, maxY, innerH, pad };
    }

    const trendVariant = { up: "success", down: "danger", flat: "neutral" } as const;
    const trendKey = { up: "trend_growing", down: "trend_shrinking", flat: "trend_stable" } as const;
</script>

{#snippet chart(space: SpaceHistory, pad: typeof PAD, idSuffix: string, fullscreen: boolean)}
    {@const c = buildChart(space.data, pad)}
    <svg
        viewBox="0 0 {W} {H}"
        width="100%"
        height={fullscreen ? "100%" : undefined}
        preserveAspectRatio={fullscreen ? "xMidYMid meet" : undefined}
        class="overflow-visible"
        role="img"
        aria-label={$_("entries_over_time", { values: { space: space.spacename } })}
        direction="ltr"
    >
        <defs>
            <linearGradient id="grad-{idSuffix}" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0%" stop-color="var(--color-primary)" stop-opacity="0.35" />
                <stop offset="100%" stop-color="var(--color-primary)" stop-opacity="0" />
            </linearGradient>
        </defs>

        <polygon points={c.areaPoints} fill="url(#grad-{idSuffix})" />

        {#each c.yTicks as tick, i (i)}
            {@const cy = c.scaleY(tick)}
            <line x1={pad.left} y1={cy} x2={W - pad.right} y2={cy} stroke="var(--color-border)" stroke-width="1" />
            <text x={pad.left - 4} y={cy + 3} text-anchor="end" font-size={fullscreen ? 7 : 9} fill="var(--color-text-faint)">
                {formatNumber(tick)}
            </text>
        {/each}

        <polyline
            points={c.polyline}
            fill="none"
            stroke="var(--color-primary)"
            stroke-width="2"
            stroke-linejoin="round"
            stroke-linecap="round"
            vector-effect="non-scaling-stroke"
        />

        {#each c.dots as dot (dot.cx)}
            <g>
                <title>{dot.label}: {formatNumber(dot.count)}</title>
                <!-- Hit target larger than the mark -->
                <circle cx={dot.cx} cy={dot.cy} r="8" fill="transparent" />
                <circle cx={dot.cx} cy={dot.cy} r="4" fill="var(--color-surface-2)" stroke="var(--color-primary)" stroke-width="2" />
            </g>
        {/each}

        {#if fullscreen}
            {#each space.data as entry, i (entry.recorded_at)}
                <text x={c.scaleX(i)} y={H - 2} text-anchor="middle" font-size="4" fill="var(--color-text-faint)">
                    {formatDate(entry.recorded_at, "date")}
                </text>
            {/each}
        {:else}
            {#each c.xLabels as lbl, i (i)}
                <text x={lbl.x} y={H - 4} text-anchor={i === 0 ? "start" : "end"} font-size="8" fill="var(--color-text-faint)">
                    {lbl.label}
                </text>
            {/each}
        {/if}
    </svg>
{/snippet}

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader
        title={$_("db_entries_count_history")}
        description={$_("db_entries_count_history_description")}
        icon={ChartLineUpOutline}
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    />

    {#if error}
        <ErrorState title={$_("db_history_load_failed")} {error} onRetry={load} />
    {:else if isLoading}
        <LoadingState variant="skeleton" rows={8} />
    {:else if spaces.length === 0}
        <EmptyState title={$_("no_records_found")} />
    {:else}
        <div class="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
            {#each spaces as space (space.spacename)}
                {@const t = trend(space)}
                {@const canGraph = space.data?.length >= 2}
                {@const mode = viewModes[space.spacename] ?? "table"}

                <Card padding="none" class="overflow-hidden">
                    <!-- Header -->
                    <div class="flex items-center justify-between gap-3 px-4 py-3 border-b border-border bg-surface">
                        <div class="flex items-center gap-3 min-w-0">
                            <div class="p-2 bg-primary-soft rounded-control shrink-0" aria-hidden="true">
                                <FolderSolid class="w-4 h-4 text-primary" />
                            </div>
                            <h3 class="text-sm font-semibold text-text truncate" title={space.spacename}>{space.spacename}</h3>
                        </div>

                        <div class="flex items-center gap-2 shrink-0">
                            {#if canGraph}
                                <IconButton
                                    label={mode === "graph" ? $_("switch_to_table") : $_("switch_to_graph")}
                                    variant="outline"
                                    size="sm"
                                    onclick={() => {
                                        viewModes[space.spacename] = mode === "graph" ? "table" : "graph";
                                    }}
                                >
                                    {#if mode === "graph"}
                                        <TableColumnOutline size="sm" />
                                    {:else}
                                        <ChartLineUpOutline size="sm" />
                                    {/if}
                                </IconButton>
                                {#if mode === "graph"}
                                    <IconButton label={$_("fullscreen")} variant="outline" size="sm" onclick={() => openFullscreen(space)}>
                                        <ExpandOutline size="sm" />
                                    </IconButton>
                                {/if}
                            {/if}
                            <div class="flex flex-col items-end leading-tight">
                                <span class="text-xl font-semibold tabular-nums text-text">{formatNumber(latestCount(space))}</span>
                                <span class="text-xs text-text-muted">{$_("entries")}</span>
                            </div>
                        </div>
                    </div>

                    <!-- Body: graph or table -->
                    {#if mode === "graph" && canGraph}
                        <div class="px-2 pt-3 pb-1">
                            {@render chart(space, PAD, space.spacename, false)}
                        </div>
                    {:else}
                        <div class="overflow-y-auto max-h-52">
                            {#if space.data && space.data.length > 0}
                                <table class="w-full text-xs text-start text-text">
                                    <thead class="sticky top-0 bg-surface-2 border-b border-border text-text-muted">
                                        <tr>
                                            <th scope="col" class="px-4 py-2 font-medium text-start">{$_("recorded_at")}</th>
                                            <th scope="col" class="px-4 py-2 font-medium text-end">{$_("count")}</th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        {#each [...space.data].reverse() as entry (entry.recorded_at)}
                                            <tr class="border-b border-border last:border-0 hover:bg-surface-3">
                                                <td class="px-4 py-2 tabular-nums text-text-muted">{formatDate(entry.recorded_at, "datetime")}</td>
                                                <td class="px-4 py-2 text-end font-semibold tabular-nums">{formatNumber(entry.entries_count)}</td>
                                            </tr>
                                        {/each}
                                    </tbody>
                                </table>
                            {:else}
                                <p class="text-center text-text-muted py-6 text-xs">{$_("no_history_records")}</p>
                            {/if}
                        </div>
                    {/if}

                    <!-- Footer: trend badge -->
                    <div class="px-4 py-2.5 border-t border-border flex items-center gap-2">
                        <Badge variant={trendVariant[t]} size="sm">{$_(trendKey[t])}</Badge>
                        <span class="text-xs text-text-muted">
                            {$_("snapshots_count", { values: { count: space.data?.length ?? 0 } })}
                        </span>
                    </div>
                </Card>
            {/each}
        </div>
    {/if}
</div>

<!-- Fullscreen chart: a native dialog, so Escape/overlay close and focus is trapped -->
<Modal bind:open={fullscreenOpen} fullscreen title={fullscreenSpace?.spacename ?? ""} class="rounded-modal shadow-modal">
    {#if fullscreenSpace}
        {@const fs = fullscreenSpace}
        {@const fst = trend(fs)}
        <div class="flex flex-col h-full gap-4">
            <div class="flex flex-wrap items-center gap-3">
                <span class="text-3xl font-semibold tabular-nums text-text">{formatNumber(latestCount(fs))}</span>
                <span class="text-sm text-text-muted">{$_("entries")}</span>
                <Badge variant={trendVariant[fst]}>{$_(trendKey[fst])}</Badge>
                <span class="text-sm text-text-muted">{$_("snapshots_count", { values: { count: fs.data.length } })}</span>
            </div>
            <div class="flex-1 min-h-[50vh]">
                {@render chart(fs, FS_PAD, "fullscreen", true)}
            </div>
        </div>
    {/if}
</Modal>
