<script lang="ts">
    import { _ } from "@/i18n";
    import { formatBytes, formatDate, formatDuration } from "@/utils/format";
    import Badge from "@/components/ui/Badge.svelte";
    import Card from "@/components/ui/Card.svelte";

    // The per-session log the Import and Export tools keep of what they sent
    // or received: one row per attempt, newest first.
    export type TransferEvent = {
        id: string;
        at: Date;
        status: "success" | "error";
        filename: string;
        /** Bytes, or null when unknown (a failed download has no size). */
        bytes: number | null;
        durationMs: number;
    };

    let { title, events }: { title: string; events: TransferEvent[] } = $props();
</script>

{#if events.length}
    <Card padding="none">
        <h2 class="text-base font-semibold text-text px-4 sm:px-5 py-3 border-b border-border">{title}</h2>
        <div class="max-h-64 overflow-auto">
            <table class="w-full text-sm text-start">
                <thead class="text-xs text-text-muted bg-surface sticky top-0">
                    <tr>
                        <th scope="col" class="px-4 py-2 font-semibold text-start">{$_("status")}</th>
                        <th scope="col" class="px-4 py-2 font-semibold text-start">{$_("file")}</th>
                        <th scope="col" class="px-4 py-2 font-semibold text-end">{$_("size")}</th>
                        <th scope="col" class="px-4 py-2 font-semibold text-end">{$_("duration")}</th>
                        <th scope="col" class="px-4 py-2 font-semibold text-start">{$_("time")}</th>
                    </tr>
                </thead>
                <tbody>
                    {#each events as event (event.id)}
                        <tr class="border-t border-border">
                            <td class="px-4 py-2">
                                <Badge variant={event.status === "success" ? "success" : "danger"} size="sm">
                                    {event.status === "success" ? $_("succeeded") : $_("failed")}
                                </Badge>
                            </td>
                            <td class="px-4 py-2 font-mono text-xs break-all" dir="ltr">{event.filename}</td>
                            <td class="px-4 py-2 text-end tabular-nums">{event.bytes === null ? "–" : formatBytes(event.bytes)}</td>
                            <td class="px-4 py-2 text-end tabular-nums">{formatDuration(event.durationMs)}</td>
                            <td class="px-4 py-2 tabular-nums text-text-muted">{formatDate(event.at, "datetime")}</td>
                        </tr>
                    {/each}
                </tbody>
            </table>
        </div>
    </Card>
{/if}
