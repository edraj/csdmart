<script lang="ts">
    import {spaces} from "@/stores/management/spaces";
    import {goto} from "@roxi/routify";
    import SpaceGrid from "@/components/ui/SpaceGrid.svelte";

    function handleSelectedSpace(spaceShortname: string) {
        $goto(`/management/content/[space_name]/health_check/[space_name_health]`, {
            space_name: 'management',
            space_name_health: spaceShortname
        });
    }
</script>

<div class="container mx-auto px-12 py-6">
    <div class="flex justify-between items-center mb-1 px-1">
        <h1 class="text-2xl font-bold mb-6">Select Space to list the events</h1>
    </div>
    <hr class="mb-6 border-gray-300" />
    <SpaceGrid spaces={$spaces ?? []} onSelect={handleSelectedSpace} />
</div>
