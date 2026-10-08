<script lang="ts">
    import { QueryType } from "@edraj/tsdmart";
    import { TrashBinOutline } from "flowbite-svelte-icons";
    import ListView from "@/components/management/ListView.svelte";
    import PageHeader from "@/components/ui/PageHeader.svelte";
    import { user } from "@/stores/user";
    import { _ } from "@/i18n";

    let space_name = $state("personal");
    let subpath = $state(`people/${$user.shortname}/trash`);
</script>

<div class="container mx-auto px-4 sm:px-6 py-6">
    <PageHeader
        title={$_("trash")}
        description={$_("trash_description")}
        icon={TrashBinOutline}
        iconTone="danger"
        backHref="/management/tools"
        backLabel={$_("back_to_tools")}
    />

    <ListView
        emptyHint={$_("trash_empty_hint")}
        bind:space_name
        bind:subpath
        canDelete={true}
        exact_subpath={false}
        type={QueryType.search}
        sort_by="created_at"
        sort_order="ascending"
    />
</div>
