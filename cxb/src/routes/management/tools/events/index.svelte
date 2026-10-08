<script lang="ts">
    import { CalendarMonthOutline } from "flowbite-svelte-icons";
    import { spaces } from "@/stores/management/spaces";
    import { _ } from "@/i18n";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import SpaceGrid from "@/components/ui/SpaceGrid.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";

    const list = $derived($spaces ?? []);
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader
        title={$_("events")}
        description={$_("events_select_space")}
        icon={CalendarMonthOutline}
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    />
    {#if $spaces === null}
        <LoadingState variant="skeleton" rows={6} />
    {:else if list.length === 0}
        <EmptyState title={$_("no_spaces")} />
    {:else}
        <SpaceGrid spaces={list} link={(space) => ({ path: "/management/tools/events/[space_name]", params: { space_name: space.shortname } })} />
    {/if}
</div>
