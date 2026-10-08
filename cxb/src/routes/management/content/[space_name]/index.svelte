<script lang="ts">
    import {params} from "@roxi/routify";
    import {Dmart, ResourceType} from "@edraj/tsdmart";
    import {ListPlaceholder} from 'flowbite-svelte';
    import EntryRenderer from "@/components/management/renderers/EntryRenderer.svelte";

    // Derive from the one param that matters, not from `$params` as a whole:
    // the list below rewrites `page`/`sort`/`search` query params in place, and
    // a promise re-created on every `$params` change re-mounted the whole
    // renderer (and re-fetched the entry) on each page click.
    const spaceName = $derived($params.space_name as string | undefined);
    let entryPromise = $derived(
        spaceName
            ? Dmart.retrieveEntry({
                resource_type: ResourceType.space, space_name: spaceName, subpath: "__root__", shortname: spaceName, retrieve_json_payload: true, retrieve_attachments: true, validate_schema: true
            })
            : null
    );
</script>

{#if entryPromise}
    {#await entryPromise}
        <div class="flex flex-col w-full">
            <ListPlaceholder class="m-5" size="lg" style="width: 100vw"/>
        </div>
    {:then entry}
        <EntryRenderer
            entry={entry!}
            resource_type={ResourceType.space}
            space_name={spaceName ?? ""}
            subpath={'/'}
        />
    {:catch error}
        <div class="alert alert-danger text-center m-5">
            <h4 class="alert-heading text-capitalize">{error}</h4>
        </div>
    {/await}
{:else}
    <h4>For some reason ... params doesn't have the needed info</h4>
    <pre>{JSON.stringify($params, null, 2)}</pre>
{/if}

