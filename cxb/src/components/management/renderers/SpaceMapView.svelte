<script lang="ts">
    import { ArchiveOutline, FolderOutline } from "flowbite-svelte-icons";
    import Badge from "@/components/ui/Badge.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";
    import { _ } from "@/i18n";

    type SubpathInfo = {
        resource_types: string[];
        actions: string[];
        conditions: string[];
        allowed_fields_values: Record<string, unknown>;
        filter_fields_values: string;
    };

    const {
        spaceMap = {},
        loading = false,
    }: {
        spaceMap: Record<string, Record<string, SubpathInfo>>;
        loading: boolean;
    } = $props();

    type Tone = "neutral" | "primary" | "success" | "danger" | "warning" | "info";

    const ACTION_TONES: Record<string, Tone> = {
        create: "success",
        update: "info",
        delete: "danger",
        query: "neutral",
        view: "neutral",
        replace: "warning",
        move: "warning",
        attach: "primary",
    };

    function actionTone(action: string): Tone {
        return ACTION_TONES[action] ?? "neutral";
    }

    function pretty(v: unknown): string {
        return JSON.stringify(v, null, 2);
    }

    function isTruthy(v: unknown): boolean {
        if (v === null || v === undefined || v === "") return false;
        if (Array.isArray(v)) return v.length > 0;
        if (typeof v === "object") return Object.keys(v as object).length > 0;
        return Boolean(v);
    }

    function entries<T>(obj: Record<string, T>): [string, T][] {
        return Object.entries(obj);
    }
</script>

{#if loading}
    <LoadingState label={$_("loading_permission_map")} />
{:else if Object.keys(spaceMap).length === 0}
    <EmptyState title={$_("no_space_permissions")} />
{:else}
    <div class="space-y-4">
        {#each entries(spaceMap) as [space, subpathMap] (space)}
            <section class="rounded-card border border-border bg-surface-2 shadow-card overflow-hidden">
                <header class="flex items-center gap-2 px-4 py-3 bg-surface border-b border-border">
                    <ArchiveOutline size="sm" class="text-primary" aria-hidden="true" />
                    <h3 class="font-semibold text-text text-base">{space}</h3>
                    <span class="ms-auto text-xs text-text-faint tabular-nums">
                        {$_("n_subpaths", { values: { count: Object.keys(subpathMap).length } })}
                    </span>
                </header>

                <div class="divide-y divide-border">
                    {#each entries(subpathMap) as [subpath, info] (subpath)}
                        <div class="px-5 py-3 space-y-1.5">
                            <div class="flex items-center gap-2">
                                <FolderOutline size="sm" class="text-text-faint" aria-hidden="true" />
                                <span class="font-mono text-sm font-semibold text-text">{subpath}</span>
                            </div>

                            {#if info.resource_types.length > 0}
                                <div class="flex flex-wrap items-center gap-1 ms-6">
                                    <span class="text-xs text-text-faint me-1">{$_("types")}:</span>
                                    {#each info.resource_types as rt (rt)}
                                        <Badge variant="primary" size="sm">{rt}</Badge>
                                    {/each}
                                </div>
                            {/if}

                            {#if info.actions.length > 0}
                                <div class="flex flex-wrap items-center gap-1 ms-6">
                                    <span class="text-xs text-text-faint me-1">{$_("actions")}:</span>
                                    {#each info.actions as action (action)}
                                        <Badge variant={actionTone(action)} size="sm">{action}</Badge>
                                    {/each}
                                </div>
                            {/if}

                            {#if isTruthy(info.conditions)}
                                <div class="flex flex-wrap items-center gap-1 ms-6">
                                    <span class="text-xs text-text-faint me-1">{$_("conditions")}:</span>
                                    {#each info.conditions as cond (cond)}
                                        <Badge variant="warning" size="sm">{cond}</Badge>
                                    {/each}
                                </div>
                            {/if}

                            {#if isTruthy(info.allowed_fields_values)}
                                <div class="ms-6">
                                    <span class="text-xs text-text-faint block mb-0.5">{$_("allowed_fields_values")}:</span>
                                    <pre class="text-xs bg-surface border border-border rounded-control p-2 overflow-auto max-h-32 text-text font-mono" dir="ltr">{pretty(info.allowed_fields_values)}</pre>
                                </div>
                            {/if}

                            {#if isTruthy(info.filter_fields_values)}
                                <div class="ms-6">
                                    <span class="text-xs text-text-faint block mb-0.5">{$_("filter_fields_values")}:</span>
                                    <pre class="text-xs bg-surface border border-border rounded-control p-2 overflow-auto max-h-32 text-text font-mono" dir="ltr">{pretty(info.filter_fields_values)}</pre>
                                </div>
                            {/if}
                        </div>
                    {/each}
                </div>
            </section>
        {/each}
    </div>
{/if}
