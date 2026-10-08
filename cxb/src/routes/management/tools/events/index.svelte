<script lang="ts">
    import { spaces } from "@/stores/management/spaces";
    import {
        CalendarMonthOutline,
        ArrowLeftOutline,
    } from "flowbite-svelte-icons";
    import { goto } from "@roxi/routify";
    import SpaceGrid from "@/components/ui/SpaceGrid.svelte";

    function handleSelectedSpace(spaceShortname: string) {
        $goto(`/management/tools/events/[space_name]`, {
            space_name: spaceShortname,
        });
    }
</script>

<div class="container mx-auto p-8">
    <button
        class="flex items-center gap-2 text-gray-600 hover:text-primary-600 mb-6 transition-colors"
        onclick={() => $goto("/management/tools")}
    >
        <ArrowLeftOutline size="sm" />
        <span>Back to Tools</span>
    </button>

    <div class="flex items-center gap-3 mb-8">
        <div class="p-3 bg-primary-100 rounded-full">
            <CalendarMonthOutline class="w-8 h-8 text-primary-600" />
        </div>
        <div>
            <h1 class="text-2xl font-bold">Events</h1>
            <p class="text-gray-500">
                Check all Dmart instance events. Select a space below to view.
            </p>
        </div>
    </div>
    <hr class="mb-6 border-gray-300" />
    <SpaceGrid spaces={$spaces ?? []} onSelect={handleSelectedSpace} />
</div>
