<script lang="ts">
    import {
        CalendarMonthOutline,
        ChartLineUpOutline,
        ChartPieOutline,
        DatabaseSolid,
        FileExportOutline,
        FileImportOutline,
        InfoCircleOutline,
        PaletteOutline,
        SearchOutline,
        TrashBinOutline,
        UserRemoveOutline,
    } from "flowbite-svelte-icons";
    import { url } from "@roxi/routify";
    import { Dmart } from "@edraj/tsdmart";
    import { onMount } from "svelte";
    import { _ } from "@/i18n";
    import { Level, showToast } from "@/utils/toast";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import Card from "@/components/ui/Card.svelte";

    type Tool = {
        path: string;
        icon: typeof InfoCircleOutline;
        titleKey: string;
        descriptionKey: string;
        tone?: "primary" | "danger";
        /** Only shown when the server lists this plugin in its manifest. */
        plugin?: string;
    };

    const tools: Tool[] = [
        { path: "/management/tools/info", icon: InfoCircleOutline, titleKey: "information", descriptionKey: "information_description" },
        { path: "/management/tools/events", icon: CalendarMonthOutline, titleKey: "events", descriptionKey: "events_description" },
        { path: "/management/tools/query", icon: SearchOutline, titleKey: "query", descriptionKey: "query_description" },
        { path: "/management/tools/import", icon: FileImportOutline, titleKey: "import", descriptionKey: "import_description" },
        { path: "/management/tools/export", icon: FileExportOutline, titleKey: "export", descriptionKey: "export_description" },
        { path: "/management/tools/theme", icon: PaletteOutline, titleKey: "theme", descriptionKey: "theme_description" },
        { path: "/management/tools/statistics", icon: ChartPieOutline, titleKey: "statistics", descriptionKey: "statistics_description" },
        { path: "/management/tools/trash", icon: TrashBinOutline, titleKey: "trash", descriptionKey: "trash_description", tone: "danger" },
        { path: "/management/tools/entry_deletion", icon: UserRemoveOutline, titleKey: "entry_deletion", descriptionKey: "entry_deletion_description", tone: "danger" },
        { path: "/management/tools/db_size_info", icon: DatabaseSolid, titleKey: "db_size_info", descriptionKey: "db_size_info_description", plugin: "db_size_info" },
        { path: "/management/tools/db_entries_count_history", icon: ChartLineUpOutline, titleKey: "db_entries_count_history", descriptionKey: "db_entries_count_history_description", plugin: "db_entries_count_history" },
    ];

    let plugins: string[] = $state([]);

    onMount(async () => {
        try {
            const manifest = await Dmart.getManifest();
            if (manifest?.status === "success") {
                plugins = manifest.attributes?.plugins ?? [];
            }
        } catch {
            // The plugin-gated cards simply stay hidden; say why.
            showToast(Level.warn, $_("manifest_load_failed"));
        }
    });

    const visibleTools = $derived(tools.filter((tool) => !tool.plugin || plugins.includes(tool.plugin)));
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader title={$_("tools")} description={$_("tools_description")} />

    <ul class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4" role="list">
        {#each visibleTools as tool (tool.path)}
            {@const Icon = tool.icon}
            <li class="flex">
                <Card href={$url(tool.path)} class="w-full">
                    <div class="flex items-start gap-4">
                        <div
                            class="shrink-0 w-12 h-12 rounded-full flex items-center justify-center
                                {tool.tone === 'danger' ? 'bg-danger-soft text-danger' : 'bg-primary-soft text-primary'}"
                            aria-hidden="true"
                        >
                            <Icon size="lg" />
                        </div>
                        <div class="min-w-0">
                            <h2 class="text-base font-semibold text-text">{$_(tool.titleKey)}</h2>
                            <p class="mt-1 text-sm text-text-muted">{$_(tool.descriptionKey)}</p>
                        </div>
                    </div>
                </Card>
            </li>
        {/each}
    </ul>
</div>
