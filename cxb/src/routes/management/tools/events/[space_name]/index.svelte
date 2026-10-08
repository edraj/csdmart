<script lang="ts">
    import { QueryType } from "@edraj/tsdmart";
    import { CalendarMonthOutline } from "flowbite-svelte-icons";
    import { params } from "@roxi/routify";
    import ListView from "@/components/management/ListView.svelte";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import cols_events from "@/utils/jsons/list_cols_events.json";
    import { _ } from "@/i18n";

    const spaceName = $derived($params.space_name as string | undefined);
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader
        title="{$_('events')}: {spaceName ?? ''}"
        icon={CalendarMonthOutline}
        backHref="/management/tools/events"
        backLabel={$_("back_to_events")}
    />
    {#key spaceName}
        {#if spaceName}
            <ListView type={QueryType.events} space_name={spaceName} subpath="/" folderColumns={cols_events} />
        {/if}
    {/key}
</div>
