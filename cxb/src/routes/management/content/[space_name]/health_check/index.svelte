<script lang="ts">
    import { spaces } from "@/stores/management/spaces";
    import { _ } from "@/i18n";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import SpaceGrid from "@/components/ui/SpaceGrid.svelte";
    import EmptyState from "@/components/ui/EmptyState.svelte";
    import LoadingState from "@/components/ui/LoadingState.svelte";

    // The reports live in the "management" space under /health_check, one
    // entry per space; the card leads to that entry.
    const list = $derived($spaces ?? []);
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader title={$_("health_check")} description={$_("health_check_select_space")} />
    {#if $spaces === null}
        <LoadingState variant="skeleton" rows={6} />
    {:else if list.length === 0}
        <EmptyState title={$_("no_spaces")} />
    {:else}
        <SpaceGrid spaces={list} href={(space) => `/management/content/management/health_check/${space.shortname}`} />
    {/if}
</div>
